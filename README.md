# ThinkPad P1 Gen 5 Fan Speed Controller

**Unofficial, independently developed Windows software. Lenovo has no affiliation with this project and does not endorse or support it.**

This Windows service runs both fans at ThinkPad level 7, the highest regulated speed, when either the CPU thermal zone or the NVIDIA GPU exceeds **70°C**. It returns control to the firmware after **both sensors stay at or below 65°C for 15 seconds**. You can change all three values while the service runs.

## Compatibility

| Verified on one laptop | Tested configuration |
| --- | --- |
| Model | ThinkPad P1 Gen 5, product code `21DDS70700` (type `21DD`) |
| Firmware | BIOS `N3JET32W` (1.16), embedded controller 1.09 |
| Hardware | Intel Core i7-12700H and NVIDIA RTX A1000 Laptop GPU |
| Operating system | Windows 11 x64 |

The build checks the product code, system family and SHA-256 fingerprint of the firmware's ACPI table before it can access the embedded controller. A different SKU or firmware table requires separate hardware validation. You can run the compatibility check without installing a driver or changing fan speed. [Installation and compatibility guide](docs/installation.md)

## Install

1. [Download version 1.0.0 for Windows (ZIP)](https://github.com/nfienemann/LenovoP1Gen5FanSpeedController/archive/refs/tags/v1.0.0.zip) and extract it.
2. Open the extracted folder and double-click **Install.cmd**. Approve the Windows administrator prompt. Setup builds the controller using the C# compiler included with Windows .NET Framework, obtains its verified hardware-access dependencies, and tests both fans.
3. Open PowerShell and run `Get-Service ThinkPadP1FanPolicy`. A successful installation shows **Running**. Windows starts the service at boot under LocalSystem, before sign-in.

Setup briefly runs the fans at level 7 and verifies their RPM and the return to firmware control. Allow about a minute for this part. The guide covers the compatibility check, upgrades, removal and common installation errors. [Installation guide](docs/installation.md)

## Configure

Open **Administrator PowerShell** for changes. In this example, either sensor above 75°C turns the fans up. Both must stay at or below 68°C for 20 seconds to release them.

```powershell
$fan = "$env:ProgramFiles\ThinkPad P1 Fan Policy\P1FanControl.exe"
& $fan config show
& $fan config set --engage-above 75 --release-at 68 --hold-seconds 20
```

To return to the controller defaults (70°C, 65°C, 15 seconds), run `& $fan config reset`. You can also change a single option. [Configuration and service commands](docs/configuration.md)

## Measured fan speeds

On the tested laptop, the two fans settled around **5,460 and 5,070 RPM** at level 7. A separate 2.3-second trial of the firmware's unregulated full-speed mode reached 5,671 and 5,272 RPM. The trial ended there and established no absolute peak. The public controller uses regulated level 7. Full-speed mode removes closed-loop RPM regulation and may exceed the fans' rated limits. [ThinkPad fan-level documentation](https://docs.kernel.org/admin-guide/laptops/thinkpad-acpi.html#fan-levels)

## How it works

While Windows is awake, the controller samples the Windows THM0 thermal zone and NVIDIA's GPU temperature roughly every two seconds. It uses the embedded controller for fallback temperatures, fan commands and tachometer readings. It requests firmware control on service stop, shutdown, a suspend notification or a control failure. A loss of power or an inaccessible embedded controller can prevent verification of that handoff.

Hardware access uses the signed PawnIO driver and its signed LpcACPIEC module. Setup obtains these from their upstream releases and checks pinned SHA-256 digests. [PawnIO driver](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0) · [EC module source](https://github.com/namazso/PawnIO.Modules/blob/master/LpcACPIEC.p) · [Pinned module distribution](https://github.com/mews-se/TPFanCtrl2/releases/tag/v2.5.1)
