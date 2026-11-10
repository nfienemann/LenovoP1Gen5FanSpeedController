# Install the unofficial Windows controller

This independent project controls both fans on a verified ThinkPad P1 Gen 5 configuration. Lenovo does not provide or support it. Start with the [tested model and firmware](../README.md#compatibility).

## Before installing

- Use 64-bit Windows PowerShell on Windows 11 x64. Setup uses the Windows .NET Framework 4.x C# compiler. Visual Studio, Git and a separate .NET SDK are optional.
- Download and extract the complete [version 1.0.0 ZIP](https://github.com/nfienemann/LenovoP1Gen5FanSpeedController/archive/refs/tags/v1.0.0.zip). Keep `src`, `Build.ps1` and `Setup.ps1` together. Commands below run from the extracted folder containing those files.
- First installation requires an administrator account or administrator approval and an internet connection for the signed hardware-access components. Exit any other fan controller, including FanControl or TPFanControl, before the fan test.

### Check compatibility first

Open PowerShell in the extracted folder and run

```powershell
.\Setup.ps1 -Mode Check
```

This builds in a temporary folder, exercises the control rules with simulated temperatures, and reads the model and ACPI table already exposed by Windows. A match prints `Matched inspected P1 Gen 5 ACPI table.` The check runs without elevation, driver installation, service registration or fan commands.

If the check reports unsupported hardware, leave firmware cooling active. A similar model name does not establish that its fan registers have the same meaning. A compatibility issue can include the model code, BIOS and embedded-controller versions, and the check's error text. Omit device serial numbers and full system reports.

## Install and verify

Double-click **Install.cmd**, or use PowerShell from the extracted folder.

```powershell
.\Setup.ps1 -Mode Install
```

1. Setup obtains administrator privileges and builds a 64-bit controller from the included source. It checks compatibility before installing a driver.
2. It obtains the pinned LpcACPIEC module and, if needed, installs PawnIO 2.2.0 after checking its digest and Authenticode publisher. A compatible existing PawnIO installation is reused.
3. With firmware in control, it records baseline temperatures and RPM, requests regulated level 7 on both fans, and checks for two distinct, sustained speed responses. It then verifies that both fans accept automatic mode again.
4. After the test passes, setup installs `ThinkPadP1FanPolicy` in `%ProgramFiles%\ThinkPad P1 Fan Policy`. Windows starts it as LocalSystem and loads its PawnIO dependency when required.

```powershell
Get-Service ThinkPadP1FanPolicy | Select-Object Name, Status, StartType
Get-Content "$env:ProgramFiles\ThinkPad P1 Fan Policy\controller.log" -Tail 10
```

Expect `Running` and `Automatic`. The service can show `AUTO` in the log while it waits for a hot reading. After a later reboot, the same commands verify the new session. [Configuration and service commands](configuration.md)

## Upgrade or remove

For an upgrade, extract the new version into its own folder and run `.\Setup.ps1 -Mode Install`. Setup prepares the new files, stops the old service, repeats the hardware check and starts the replacement. It preserves saved thresholds and sets startup to Automatic. If installation fails during replacement, setup attempts to restore the previous files and a previously running service.

To uninstall, run the following from the extracted project folder and approve elevation.

```powershell
.\Setup.ps1 -Mode Remove
```

Removal stops the controller and deletes its service registration, saved policy and installed controller binaries. Logs stay in the install directory. PawnIO stays available to other monitoring software. If nothing else needs PawnIO, you can remove it separately through **Settings → Apps → Installed apps**.

## Troubleshooting

| Symptom | What to do |
| --- | --- |
| PowerShell blocks a downloaded script | Verify that the ZIP came from this repository. In the ZIP's **Properties**, select **Unblock** if shown, then extract it again. The launcher uses a process-scoped RemoteSigned policy. An organization-managed policy takes precedence. |
| A hash or publisher check fails | Stop setup. Download the official project archive again and check the linked upstream release. The installer reports the file that failed verification. |
| Another program owns the fans, or RPM barely changes during the test | Exit other fan-control applications. Let the laptop return to normal idle cooling, then rerun setup. A laptop already at high RPM may produce an inconclusive test. |
| PawnIO requests a restart | Restart Windows when convenient, then rerun setup. |
| A firmware update changes the ACPI table | Run the compatibility check again. A changed table needs review before this version can control the fans. |
| The service or its settings command reports access denied | Use **Administrator PowerShell** for writes. Check `setup.log` for installation failures and `controller.log` for runtime telemetry. Both are in `%ProgramFiles%\ThinkPad P1 Fan Policy`. |

## Build without installing

```powershell
.\Build.ps1
.\build\P1FanControl.exe help
.\build\P1FanControl.exe selftest
.\build\P1FanControl.exe identify
```

The build compiles the three C# files under `src` using the Windows .NET Framework compiler and runs the policy self-test. The commands above leave the service and the fans under their existing control.
