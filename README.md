# WinTune

A simple, AI vibe-coded program I made to speed up all the tweaks and settings I use on Windows.

It is a small WPF app (C# / .NET 10, [WPF UI](https://github.com/lepoco/wpfui)) that lets you see, apply and undo Windows optimizations from one place, and reinstall your usual apps after a fresh setup. It always runs as administrator, because it changes system settings. Use it at your own risk: create a restore point first (there is a button for that).

## Build and run

Double-click `build.bat`. The script:

1. installs the .NET 10 SDK with winget, if it is missing;
2. builds the program into `dist\WinTune.exe` (a single self-contained file, nothing else to install);
3. writes the result to `build.log`.

## Features

- **Overview**: profile and differences, system checks (RAM profile, display refresh rate, activation, Secure Boot and TPM, TRIM, disk health, BIOS), storage cards per drive, temperatures, manual reminders, OC notes, start with Windows, minimize to tray on close.
- **Optimizations**: gaming and performance, privacy and telemetry, File Explorer, taskbar and Start, appearance and accessibility, power, system, network, browser, services (safe and aggressive lists). Compact switches, category filter, "Apply recommended", one-time actions.
- **Apps**: apps by category, install/uninstall with winget, search as you type, detect installed apps, update apps (with background update notifications), remove preinstalled apps, startup programs.
- **Tools**: FanControl, RivaTuner Statistics Server, SpaceSniffer, LibreHardwareMonitor and more (install, start with Windows, save/restore configuration, switch profiles on the fly).
- **Sensors**: live temperatures, voltages, fan speeds and loads (LibreHardwareMonitorLib), before/after snapshots.
- **Drivers**: detected hardware with links to the driver pages, automatic Windows Update drivers on/off (also timed).
- **Updates**: Windows Update modes (on, notify only, paused, off).
- **Diagnostics**: crash and instability events by category, dump files, Windows tools, sfc and DISM checks.
- **Utilities**: timed shutdown, restore point, profile (save, export, import), disk cleanup.

## Credits

Part of the optimizations (`src/WinTune/Core/WinUtilTweaks.cs` and `WinUtilExtras.cs`) reuses registry values and actions from
[Chris Titus Tech's Windows Utility](https://github.com/ChrisTitusTech/winutil), released under the MIT license,
Copyright (c) 2022 CT Tech Group LLC. The file is regenerated with `tools/gen_winutil.py`.

Sensors: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).

## Where the data lives

Folder `%AppData%\WinTune`:

- `profile.json`: the profile;
- `changes.log`: log of every change applied;
- `errors.log`: unexpected errors, if any;
- `original-services.json`: original start type of the services that were disabled, so they can be restored as they were;
- `settings.json`, `notifications.json`: app settings and update-notification state.

## Code layout

Everything is in `src\WinTune`:

- `Core\TweakCatalog.cs`: list of the optimizations. To add one, add an entry here.
- `Core\Tweak.cs`, `Core\TweakEngine.cs`: model and application of the optimizations.
- `Core\ProfileService.cs`: profile and comparison with the current state.
- `Core\WingetService.cs`, `Core\AppxService.cs`, `Core\StartupService.cs`, `Core\ServiceUtil.cs`: winget, preinstalled apps, startup, services.
- `Core\SensorService.cs`, `Core\BackgroundUpdates.cs`, `TrayService.cs`: sensors, background update checks, tray icon.
- `Core\RestorePointService.cs`, `Core\ShutdownService.cs`, `Core\PowerUtil.cs`, `Core\ExplorerUtil.cs`: system functions.
- `ViewModels`, `Views\Pages`: user interface (WPF with WPF UI).

## License

[MIT](LICENSE)
