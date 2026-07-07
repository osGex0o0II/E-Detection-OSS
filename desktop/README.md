# E-Detection Desktop

This folder contains the WinUI 3 native shell for E-Detection. The desktop app owns the Windows-native experience and launches the Python detection core through the `python -m e_detection --json-events` bridge.

## Architecture

- `EDetection.Desktop/Models` contains the JSONL event contract shared with the Python CLI.
- `EDetection.Desktop/Services/PythonBackendService.cs` starts the Python process and streams stdout JSON Lines into ViewModels.
- `EDetection.Desktop/Services/DesktopDiagnosticsService.cs` owns input/config/output/Python readiness checks and repair-command generation.
- `EDetection.Desktop/Services/DetectionEnvironmentRepairService.cs` owns the user-triggered local detection-core repair path.
- `EDetection.Desktop/Services/DesktopHealthService.cs` summarizes shell health for notifications, startup integration, settings storage, package integrity, Python bridge mode, and install shape.
- `EDetection.Desktop/Services/ReportHistoryService.cs` owns report history filtering, trimming, latest markers, and snapshot item updates.
- `EDetection.Desktop/Services/ReportDetailPreviewService.cs` owns anomaly detail filtering, issue-type filters, sorting, and TSV export text.
- `EDetection.Desktop/Services/RuntimeLogService.cs` owns runtime log retention, kind filters, search filtering, clearing, and TSV export text.
- `EDetection.Desktop/Services/RunTelemetryService.cs` owns elapsed time, speed, remaining time, and run progress text calculation.
- `EDetection.Desktop/Services/RunEventService.cs` translates Python JSONL backend events into UI state actions, logs, taskbar progress, and notification requests.
- `EDetection.Desktop/Services/RunStateService.cs` maps backend run/report summaries into resettable UI state snapshots.
- `EDetection.Desktop/Services/SettingsService.cs` persists versioned desktop preferences, window placement, and recent report history under LocalAppData with atomic writes.
- `EDetection.Desktop/Services/ShellResourceService.cs` trims hidden shell resources after the main window is parked in the tray.
- `EDetection.Desktop/Services/StartupService.cs` owns the startup integration boundary. The default provider is a per-user Task Scheduler logon task with `HKCU Run` kept as a legacy fallback.
- `EDetection.Desktop/Services/TaskbarProgressService.cs` exposes native Windows taskbar progress.
- `EDetection.Desktop/Services/DesktopNotificationService.cs` registers Windows App SDK notifications and handles notification actions when available.
- `EDetection.Desktop/Services/CommandPaletteService.cs` owns quick action catalog construction, search scoring, category ordering, command execution bridges, and recent report actions.
- `EDetection.Desktop/Models/ShellStatusSnapshot.cs` is the shared shell contract used by native surfaces such as the tray menu and Windows taskbar progress.
- `EDetection.Desktop/Models/CommandPaletteAction.cs` is the shared command palette item contract consumed by the shell view.
- `EDetection.Desktop/ViewModels` holds screen state and commands.
- `EDetection.Desktop/ViewModels/DiagnosticsViewModel.cs` owns bindable diagnostics text, Python probe status, repair command text, and clipboard diagnostic export text.
- `EDetection.Desktop/ViewModels/DesktopHealthViewModel.cs` owns the bindable desktop health card state while `DesktopHealthService` owns the actual checks.
- `EDetection.Desktop/ViewModels/ReportHistoryViewModel.cs` owns recent report collections, filtering, selection, retention limits, and status text while `ReportHistoryService` owns report mutation rules.
- `EDetection.Desktop/ViewModels/RunTelemetryViewModel.cs` owns bindable long-running run telemetry such as current file, elapsed time, speed, remaining time, and progress details.
- `EDetection.Desktop/ViewModels/RuntimeLogViewModel.cs` owns runtime log collections, filters, retention settings, status text, clearing, and export text while `RuntimeLogService` owns filtering/export rules.
- `EDetection.Desktop/Views` contains the run setup panel, runtime diagnostics, settings, logs, and detection workbench.
- `EDetection.Desktop/MainWindow.xaml` is the native shell with Mica/Acrylic, custom title bar, app identity, taskbar progress, actionable notifications, tray integration, report history, first-run readiness guidance, diagnostics, and in-app report summaries.

## Local Build

Install the .NET 10 SDK and Windows App SDK development workload, then run:

```powershell
dotnet restore .\desktop\EDetection.Desktop.slnx
dotnet build .\desktop\EDetection.Desktop.slnx -c Debug
```

For release packaging, prefer the repo script:

```powershell
.\desktop\scripts\Publish-Desktop.ps1 -RuntimeIdentifier win-x64
```

The script writes a self-contained unpackaged build to `artifacts\desktop\win-x64\publish`, adds `release-info.txt` and `INSTALL.txt`, verifies the app icon and WinUI resources are present, copies the local Python detection core plus install/uninstall scripts, and creates `E-Detection.Desktop-win-x64.zip` unless `-NoZip` is passed.

GitHub Actions builds the same Windows x64 package on pull requests and pushes to `main`. Push a version tag such as `v0.1.0`, or run the `desktop` workflow manually with a `release_tag`, to upload a standard setup wizard (`E-Detection.Desktop-Setup-win-x64.exe`) and the portable zip (`E-Detection.Desktop-win-x64.zip`) to GitHub Releases. Most users should download the setup wizard and update by running the newer setup wizard from the app or the release page.

To build the setup wizard locally, install Inno Setup 6 and run:

```powershell
.\desktop\scripts\Build-DesktopInstaller.ps1 -RuntimeIdentifier win-x64
.\desktop\scripts\Test-DesktopInstallerSmoke.ps1
```

The installer defaults to `%LOCALAPPDATA%\Programs\E-Detection Desktop`, does not require administrator privileges, creates Start Menu integration, offers an optional Desktop shortcut, appears in Windows installed apps, closes the running desktop app during update when needed, and lets the user choose another install directory from the setup wizard.

Useful options:

```powershell
.\desktop\scripts\Publish-Desktop.ps1 -RuntimeIdentifier win-arm64
.\desktop\scripts\Publish-Desktop.ps1 -RuntimeIdentifier win-x64 -NoZip
```

Run a local visual smoke test after publishing:

```powershell
.\desktop\scripts\Test-DesktopVisualSmoke.ps1
.\desktop\scripts\Test-DesktopSingleInstanceSmoke.ps1
.\desktop\scripts\Test-DesktopSessionEndingSmoke.ps1
.\desktop\scripts\Test-DesktopStartupIntegrationSmoke.ps1
.\desktop\scripts\Test-DesktopEnvironmentRepairSmoke.ps1
```

When running against a publish folder, every desktop GUI smoke script also accepts `-PackagePath .\artifacts\desktop\win-x64\publish`.

The visual smoke test starts the published app, verifies the main window title, moves the main window to a stable size, captures a PNG under `artifacts\desktop\visual-smoke`, checks that the image is not blank or undersized, and writes a JSON result next to the screenshot. The settings smoke verifies persisted settings, startup provider status, settings schema migration, and the desktop health card. The single-instance smoke test starts the app hidden with `--startup-minimized`, launches a second copy, verifies the duplicate exits, and confirms the first instance is restored. The session-ending smoke test sends Windows session-ending messages to the main window, verifies a cancelled session does not close the app, then verifies a real end-session message exits cleanly. The startup-integration smoke toggles login startup from the settings UI, verifies the scheduled task or fallback startup entry points to the published app, then verifies disabling removes it. The environment-repair smoke creates a temporary venv, verifies `e_detection` is initially missing, runs the same local editable-install repair path, and verifies the module is importable afterward.

Validate the install/uninstall path without keeping user artifacts:

```powershell
.\artifacts\desktop\win-x64\publish\Test-DesktopInstallSmoke.ps1
```

The install smoke test installs to a temporary smoke directory, verifies the installed package and per-user App Paths registration, uninstalls the app, and restores any pre-existing shortcuts or App Paths entry.

Published packages include a bundled CPython runtime under `python-runtime`, the local Python detection core source, and an offline `python-wheelhouse`. Ordinary users do not need to install or select Python before running detection. Advanced users can still select a custom Python executable; the repair flow then creates an app-private environment under `%LOCALAPPDATA%\E-Detection\Desktop\python-env` instead of modifying the system Python. Starting a run performs the same readiness gate before launching the backend process: missing input/config, empty CSV input, unavailable runtime, an unimportable detection core, or an uncreatable report directory are reported in the shell instead of surfacing as a late process failure. During development, run `python -m pip install -e .` from the repository root when using a custom Python.

The quick action palette is available from the title bar search button. It exposes entries for copying diagnostics, refreshing desktop health, opening settings sections, opening or copying the current report path, opening the report folder, and opening up to five recent reports.

## Installable User Build

For ordinary Windows users, download `E-Detection.Desktop-Setup-win-x64.exe` from GitHub Releases, run it, choose the install location in the wizard if needed, and launch the app from the Start menu. To update, use the app's update settings or download the newer setup wizard and run it again.

After publishing, the `publish` folder can still be used as a portable app or installed by script for development and advanced troubleshooting:

```powershell
cd .\artifacts\desktop\win-x64\publish
powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1
```

The installer copies files to `%LOCALAPPDATA%\Programs\E-Detection Desktop`, creates Start Menu and Desktop shortcuts, and registers a per-user App Paths entry for `EDetection.Desktop.exe`.

Useful install options:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1 -NoDesktopShortcut
powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1 -NoDesktopShortcut -NoStartMenuShortcut
powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1 -Launch
```

Uninstall:

```powershell
powershell -ExecutionPolicy Bypass -File .\Uninstall-Desktop.ps1
```

By default uninstall keeps `%LOCALAPPDATA%\E-Detection\Desktop\settings.json`. Use `-RemoveSettings` only when you also want to remove local preferences and report history.

## Runtime Notes

- The app stores settings under `%LOCALAPPDATA%\E-Detection\Desktop\settings.json`.
- The settings file is versioned (`SettingsVersion=8` currently). Older settings without a version are migrated and written back on load.
- The settings page includes a desktop health card covering notification availability, startup provider, settings store, package integrity, Python JSONL bridge mode, and install shape.
- The tray menu is command-aware: it shows the current run state and can restore the workbench, start/cancel detection when available, and open the latest report or its folder. The tray and window/taskbar icon switch to `running.ico` while detection is active.
- System notifications are registered on a best-effort basis for the unpackaged app. Completion/error notifications can restore the workbench, and completed runs can open the generated report directly when a report path is available. If Windows notification policy or registration blocks them, detection continues normally.
- Native taskbar progress mirrors the run lifecycle: indeterminate while starting, normal while processing, paused while cancel confirmation is armed or a run is cancelled, and error when a backend failure occurs.
- The published desktop app bundles CPython in `python-runtime` and uses it by default. Custom Python remains available for advanced troubleshooting.
- Launching `EDetection.Desktop.exe --startup-minimized` starts the primary instance hidden in the tray. A normal second launch restores the existing instance instead of creating another window.
- Startup-hidden launches park the first window frame off-screen before activation, then restore the saved placement when the user reopens the workbench.
- When the main window is hidden to the tray, the shell schedules a best-effort resource release pass so long-running sessions keep a smaller desktop footprint.
- Windows logoff/shutdown messages are handled explicitly: query messages are accepted quickly, cancelled session endings keep the app alive, and real session endings cancel backend work, save settings, clear taskbar progress, unregister notifications, and remove the tray icon.
- The "登录后自动启动" setting creates a per-user Task Scheduler logon task with `--startup-minimized`, a short logon delay, no battery-start restriction, and no 72-hour execution limit. Legacy `HKCU Run` entries are still recognized as a fallback and removed when Task Scheduler registration succeeds.
