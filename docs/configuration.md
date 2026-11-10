# Configure the fan controller

These commands operate on the installed, unofficial ThinkPad P1 Gen 5 service. Open PowerShell and define the executable path once per session.

```powershell
$fan = "$env:ProgramFiles\ThinkPad P1 Fan Policy\P1FanControl.exe"
& $fan help
& $fan config show
```

Reading settings works as a standard user. To save or reset them, open **Administrator PowerShell** and define `$fan` there. Changes persist across reboots and take effect on the running service's next poll, normally within two seconds.

| Option | Default | Behavior |
| --- | ---: | --- |
| `--engage-above` | 70°C | Either valid sensor strictly above this value requests regulated level 7 on both fans. At exactly 70°C the default policy stays in its previous mode. |
| `--release-at` | 65°C | Both sensors must be at or below this value throughout the cooldown period. A warmer sample restarts the timer. |
| `--hold-seconds` | 15 s | Time spent continuously cool before the firmware takes over. Zero releases on the first qualifying sample. |

Use whole numbers. The release threshold must be at least 1°C and below the engage threshold. The engage threshold must be at most 126°C. The hold time must be a nonnegative 32-bit integer. Those limits describe the accepted input format. The default thresholds are the ones tested on hardware.

## Change or reset settings

Change only the cooldown duration. Omitted options retain their current values.

```powershell
& $fan config set --hold-seconds 25
```

Change all three values together and read them back.

```powershell
& $fan config set --engage-above 75 --release-at 68 --hold-seconds 20
& $fan config show
```

Restore the original 70°C / 65°C / 15-second policy.

```powershell
& $fan config reset
```

Each update saves the three settings together. An invalid command exits with an error and preserves the previous settings. The service resets its cooldown timer when it applies a change. The current fan mode carries over until a transition condition is met. Settings are stored in the service's `Parameters` registry key and survive an in-place upgrade.

## Check operation and control startup

```powershell
Get-Service ThinkPadP1FanPolicy | Select-Object Name, Status, StartType
Get-Content "$env:ProgramFiles\ThinkPad P1 Fan Policy\controller.log" -Tail 15
```

The log records policy changes, handoffs to firmware, temperatures, RPM and mode readbacks. `MAX` means the service requested regulated level 7. `AUTO` means cooling is under firmware control. A hot reading from either available sensor can request level 7. With one sensor missing and no valid hot reading, the policy hands control to the firmware.

To return control to the firmware for the current session, stop the service from Administrator PowerShell. Start it again to resume monitoring.

```powershell
Stop-Service ThinkPadP1FanPolicy
Start-Service ThinkPadP1FanPolicy
```

To keep the service off across reboots, stop it and change its startup type. To restore boot operation, set it back to Automatic and start it.

```powershell
Stop-Service ThinkPadP1FanPolicy
Set-Service ThinkPadP1FanPolicy -StartupType Manual

Set-Service ThinkPadP1FanPolicy -StartupType Automatic
Start-Service ThinkPadP1FanPolicy
```

The controller does not need a tray app, a scheduled task or an interactive login. [Installation and removal](installation.md)
