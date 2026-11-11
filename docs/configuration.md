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
| `--engage-above` | 70°C | Either valid sensor strictly above this value requests the selected speed on both fans. At exactly 70°C the default policy stays in its previous mode. |
| `--release-at` | 65°C | Both sensors must be at or below this value throughout the cooldown period. A warmer sample restarts the timer. |
| `--hold-seconds` | 15 s | Time spent continuously cool before the firmware takes over. Zero releases on the first qualifying sample. |
| `--fan-mode` | `level7` | The speed to request during a hot interval. Choices are `level7` (regulated) and `full-speed` (unregulated). |

Use whole numbers for temperatures and time. The release threshold must be at least 1°C and below the engage threshold. The engage threshold must be at most 126°C. The hold time must be a nonnegative 32-bit integer. The default thresholds are the ones tested on hardware.

## Optional unregulated full speed

**Risk.** In `full-speed` mode the embedded controller disables closed-loop RPM regulation. Fans can run beyond their rated limits, with more noise and possible accelerated wear or hardware damage. Some ThinkPads can report stale RPM during ramp-up. If software loses access to the controller, it may be unable to restore automatic mode immediately. On the tested P1 Gen 5, a 2.3-second trial measured about 4% more RPM on each fan. That measurement does not establish safe sustained operation or a cooling benefit. [ThinkPad fan-level documentation](https://docs.kernel.org/admin-guide/laptops/thinkpad-acpi.html#fan-levels)

**Opt in** from Administrator PowerShell. The command changes only the requested speed. It preserves your thresholds, cooldown duration and any cooldown time already elapsed. If the fans are already in a hot interval, both switch on the next poll. Otherwise the selection takes effect at the next hot interval. The selection persists across reboots.

```powershell
& $fan config set --fan-mode full-speed
```

**Switch back to regulated level 7** while keeping your temperature settings, then verify the saved selection.

```powershell
& $fan config set --fan-mode level7
& $fan config show
```

Stopping the service asks the firmware to take control immediately. The saved mode still applies when the service starts again. To restore both level 7 and all three factory policy values for this utility, use `& $fan config reset`.

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

Restore regulated level 7 and the original 70°C / 65°C / 15-second policy.

```powershell
& $fan config reset
```

Each update saves the settings together. An invalid command exits with an error and preserves the previous settings. Changing a temperature or hold time restarts the cooldown timer. Changing only the fan mode preserves that timer. Settings are stored in the service's `Parameters` registry key and survive an in-place upgrade.

## Check operation and control startup

```powershell
Get-Service ThinkPadP1FanPolicy | Select-Object Name, Status, StartType
Get-Content "$env:ProgramFiles\ThinkPad P1 Fan Policy\controller.log" -Tail 15
```

The log records policy changes, handoffs to firmware, temperatures, RPM and mode readbacks. `MAX` means the service requested regulated level 7. `FULL` means it requested unregulated full speed. `AUTO` means cooling is under firmware control. A hot reading from either available sensor can request the selected speed. With one sensor missing and no valid hot reading, the policy hands control to the firmware.

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
