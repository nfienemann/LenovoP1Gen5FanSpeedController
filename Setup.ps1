[CmdletBinding()]
param([ValidateSet('Check','Install','Remove')][string]$Mode = 'Install')
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit Windows PowerShell.' }
$name = 'ThinkPadP1FanPolicy'
$dir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'ThinkPad P1 Fan Policy'
$exe = Join-Path $dir 'P1FanControl.exe'
$image = '"{0}" service' -f $exe
$moduleHash = 'C38FD116E7AFF4D1FDB0A494E296BE0A6708E5A22FC72F14587442FB7F8F7906'
$driverHash = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
$files = @('P1FanControl.exe','LpcACPIEC.bin')
$sc = Join-Path $env:SystemRoot 'System32\sc.exe'

function Invoke-Controller($Binary,$Command) {
    & $Binary $Command
    if ($LASTEXITCODE) { throw "Controller command '$Command' failed." }
}
function Check-Hash($Path,$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) { throw "SHA-256 verification failed for $Path." }
}
function Log($Message) {
    $line = '[{0:o}] {1}' -f [DateTimeOffset]::Now,$Message
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $dir 'setup.log') -Value $line
}
function Start-Policy {
    Start-Service -Name $name
    (Get-Service $name).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
}
function Clear-Stage($Stage) {
    # The staging directory contains only files created by this invocation.
    foreach ($file in 'P1FanControl.exe','LpcACPIEC.bin','PawnIO_setup.exe','P1FanControl.exe.old','LpcACPIEC.bin.old','controller.log') {
        $path = Join-Path $Stage $file
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    [IO.Directory]::Delete($Stage,$false)
}
if ($Mode -eq 'Check') {
    $stage = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('P1FanCheck-' + [Guid]::NewGuid().ToString('N')))).FullName
    try {
        & (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $stage
        Invoke-Controller (Join-Path $stage 'P1FanControl.exe') identify
    } finally { Clear-Stage $stage }
    return
}
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $child = Start-Process $ps -Verb RunAs -Wait -PassThru -ArgumentList ('-NoProfile -ExecutionPolicy RemoteSigned -File "{0}" -Mode {1}' -f $PSCommandPath,$Mode)
    if ($child.ExitCode) { throw "Setup failed. See the preceding error or $dir\setup.log." }
    return
}
$mutex = New-Object Threading.Mutex($false,'Global\ThinkPadP1FanPolicy.Setup')
try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
if (-not $locked) { $mutex.Dispose(); throw 'Another setup operation is in progress.' }
try {
    $installed = Get-CimInstance Win32_Service -Filter "Name='ThinkPadP1FanPolicy'"
    if ($installed -and $installed.PathName -ne $image) { throw 'The service name is already in use by another executable.' }
    if (Test-Path -LiteralPath $dir) {
        $entries = @(Get-Item -LiteralPath $dir) + @(Get-ChildItem -LiteralPath $dir -Force)
        foreach ($entry in $entries) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected filesystem link in $dir." }
        }
    }
    if ($Mode -eq 'Remove') {
        if ($installed) {
            Stop-Service -Name $name
            & $sc delete $name
            if ($LASTEXITCODE) { throw 'Service removal failed.' }
        }
        foreach ($file in $files) {
            $path = Join-Path $dir $file
            if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
        }
        Write-Host "Controller removed. Logs are in $dir. PawnIO is shared and can be removed from Windows Installed apps."
        return
    }
    $null = New-Item -ItemType Directory -Force -Path $dir
    $stage = [IO.Directory]::CreateDirectory((Join-Path $dir ('Setup-' + [Guid]::NewGuid().ToString('N')))).FullName
    $wasRunning = $installed -and $installed.State -eq 'Running'
    $stopped = $false
    $replacing = $false
    $created = $false
    $keepBackup = $false
    try {
        Log 'Building the unofficial controller and checking hardware compatibility.'
        & (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $stage
        Invoke-Controller (Join-Path $stage 'P1FanControl.exe') identify
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $module = Join-Path $stage 'LpcACPIEC.bin'
        $oldModule = Join-Path $dir 'LpcACPIEC.bin'
        if ((Test-Path -LiteralPath $oldModule) -and (Get-FileHash -LiteralPath $oldModule).Hash -eq $moduleHash) {
            Copy-Item -LiteralPath $oldModule -Destination $module
        } else {
            Invoke-WebRequest -UseBasicParsing 'https://github.com/mews-se/TPFanCtrl2/releases/download/v2.5.1/LpcACPIEC.bin' -OutFile $module
        }
        Check-Hash $module $moduleHash
        if (-not (Get-Service PawnIO -ErrorAction SilentlyContinue)) {
            Log 'Installing signed PawnIO 2.2.0.'
            $driver = Join-Path $stage 'PawnIO_setup.exe'
            Invoke-WebRequest -UseBasicParsing 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe' -OutFile $driver
            Check-Hash $driver $driverHash
            $sig = Get-AuthenticodeSignature -LiteralPath $driver
            if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'CN=namazso\.eu') { throw 'Driver publisher verification failed.' }
            $p = Start-Process -FilePath $driver -ArgumentList '-install','-silent' -WindowStyle Hidden -Wait -PassThru
            if ($p.ExitCode -eq 3010) { throw 'PawnIO requires a reboot. Restart Windows and run setup again.' }
            if ($p.ExitCode) { throw "PawnIO installation returned $($p.ExitCode)." }
        }
        if ((Get-Service PawnIO).Status -ne 'Running') { Start-Service PawnIO }
        foreach ($file in $files) {
            $target = Join-Path $dir $file
            if ([IO.File]::Exists($target)) { Copy-Item -LiteralPath $target -Destination (Join-Path $stage "$file.old") }
        }
        if ($installed) { Stop-Service $name; $stopped = $true }
        Log 'Testing both fans at regulated level 7 and verifying handoff to firmware.'
        try { Invoke-Controller (Join-Path $stage 'P1FanControl.exe') validate }
        finally {
            $testLog = Join-Path $stage 'controller.log'
            if (Test-Path -LiteralPath $testLog) { Get-Content -LiteralPath $testLog | Add-Content -LiteralPath (Join-Path $dir 'setup.log') }
        }
        $replacing = $true
        foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $stage $file) -Destination (Join-Path $dir $file) -Force }
        if (-not $installed) {
            $null = New-Service -Name $name -BinaryPathName $image -DisplayName 'ThinkPad P1 temperature-based fan control' -DependsOn PawnIO -StartupType Automatic
            $created = $true
        } else { Set-Service $name -StartupType Automatic }
        & $sc failure $name reset= 86400 actions= restart/5000/restart/15000/restart/60000
        if ($LASTEXITCODE) { throw 'Could not configure service recovery.' }
        Start-Policy
        Log 'Fan controller is running and configured for automatic startup.'
    } catch {
        Log ("FAILED: " + $_.Exception.Message)
        try {
            if ($replacing) {
                if (Get-Service $name -ErrorAction SilentlyContinue) { Stop-Service $name }
                if ($created) { & $sc delete $name; if ($LASTEXITCODE) { throw 'Service rollback failed.' } }
                foreach ($file in $files) {
                    $old = Join-Path $stage "$file.old"
                    $target = Join-Path $dir $file
                    if ([IO.File]::Exists($old)) { Copy-Item -LiteralPath $old -Destination $target -Force }
                    elseif ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
                }
            }
            if ($stopped -and $wasRunning) { Start-Policy }
        } catch { $keepBackup = $true; Log "Rollback needs attention. Backup in $stage. $_" }
        throw
    } finally { if (-not $keepBackup) { Clear-Stage $stage } }
} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
