[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'build' }
if (-not [Environment]::Is64BitProcess) { throw 'Open 64-bit Windows PowerShell.' }
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework 4.x C# compiler is required.' }
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$exe = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path 'P1FanControl.exe'
$source = foreach ($file in 'Controller.cs','Hardware.cs','Settings.cs') { Join-Path $PSScriptRoot "src\$file" }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /r:System.Management.dll /r:System.ServiceProcess.dll "/out:$exe" $source
if ($LASTEXITCODE) { throw 'Build failed.' }
& $exe selftest
if ($LASTEXITCODE) { throw 'Policy self-test failed.' }
