[CmdletBinding()]
param(
    [string]$PackagePath = "",

    [string]$AppPath = "",

    [string]$OutputDirectory = "",

    [int]$WaitSeconds = 8
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")

if (![string]::IsNullOrWhiteSpace($PackagePath) -and [string]::IsNullOrWhiteSpace($AppPath)) {
    $packageFull = [System.IO.Path]::GetFullPath((Resolve-Path $PackagePath).Path)
    $AppPath = Join-Path $packageFull "EDetection.Desktop.exe"
}

if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $localAppPath = Join-Path $scriptDir "EDetection.Desktop.exe"
    $AppPath = if (Test-Path $localAppPath) {
        $localAppPath
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\win-x64\publish\EDetection.Desktop.exe"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = if (Test-Path (Join-Path $scriptDir "EDetection.Desktop.exe")) {
        Join-Path $scriptDir "smoke-results\single-instance"
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\single-instance-smoke"
    }
}

$appFull = [System.IO.Path]::GetFullPath((Resolve-Path $AppPath).Path)
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputFull | Out-Null

$settingsDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "E-Detection\Desktop"
$settingsPath = Join-Path $settingsDirectory "settings.json"
$settingsExisted = Test-Path $settingsPath
$settingsBackup = $null
if ($settingsExisted) {
    $settingsBackup = Get-Content -Path $settingsPath -Raw
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DesktopSingleInstanceSmokeNative
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
"@

function Get-WindowTitle([IntPtr]$Handle) {
    $builder = [System.Text.StringBuilder]::new(512)
    [DesktopSingleInstanceSmokeNative]::GetWindowText($Handle, $builder, $builder.Capacity) | Out-Null
    $builder.ToString()
}

function Get-ProcessWindows([int]$ProcessId) {
    $windows = [System.Collections.Generic.List[object]]::new()
    $callback = [DesktopSingleInstanceSmokeNative+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)
        $windowProcessId = 0
        [DesktopSingleInstanceSmokeNative]::GetWindowThreadProcessId($hWnd, [ref]$windowProcessId) | Out-Null
        if ($windowProcessId -eq $ProcessId -and [DesktopSingleInstanceSmokeNative]::IsWindowVisible($hWnd)) {
            $title = Get-WindowTitle $hWnd
            if (![string]::IsNullOrWhiteSpace($title)) {
                $windows.Add([pscustomobject]@{
                    Handle = $hWnd
                    Title = $title
                })
            }
        }

        return $true
    }

    [DesktopSingleInstanceSmokeNative]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    $windows
}

function Wait-ForWindowTitle([int]$ProcessId, [string]$Pattern, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $match = Get-ProcessWindows $ProcessId | Where-Object { $_.Title -like $Pattern } | Select-Object -First 1
        if ($match -ne $null) {
            return $match
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for window title pattern '$Pattern'."
}

function Test-WindowTitle([int]$ProcessId, [string]$Pattern) {
    Get-ProcessWindows $ProcessId | Where-Object { $_.Title -like $Pattern } | Select-Object -First 1
}

function Assert-WindowVisibleInWorkArea([IntPtr]$Handle) {
    $rect = New-Object DesktopSingleInstanceSmokeNative+RECT
    if (![DesktopSingleInstanceSmokeNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Single-instance smoke failed: could not read restored window rectangle."
    }

    $workArea = [System.Windows.Forms.Screen]::FromHandle($Handle).WorkingArea
    $visibleLeft = [Math]::Max($rect.Left, $workArea.Left)
    $visibleTop = [Math]::Max($rect.Top, $workArea.Top)
    $visibleRight = [Math]::Min($rect.Right, $workArea.Right)
    $visibleBottom = [Math]::Min($rect.Bottom, $workArea.Bottom)
    $visibleWidth = $visibleRight - $visibleLeft
    $visibleHeight = $visibleBottom - $visibleTop
    if ($visibleWidth -lt 320 -or $visibleHeight -lt 220) {
        throw "Single-instance smoke failed: restored window is not sufficiently visible. Rect=($($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)); WorkArea=($($workArea.Left),$($workArea.Top),$($workArea.Right),$($workArea.Bottom))."
    }

    [pscustomobject]@{
        Left = $rect.Left
        Top = $rect.Top
        Right = $rect.Right
        Bottom = $rect.Bottom
        VisibleWidth = $visibleWidth
        VisibleHeight = $visibleHeight
    }
}

function Wait-ForProcessExit([System.Diagnostics.Process]$Process, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for duplicate instance $($Process.Id) to exit."
}

$primary = $null
$backgroundDuplicate = $null
$normalDuplicate = $null
$legacyDuplicate = $null

try {
    New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
    [pscustomobject]@{
        InputDirectory = ""
        OutputDirectory = ""
        ConfigPath = "config.json"
        WriteReport = $true
        CloseToTrayOnClose = $false
        StartMinimizedToTray = $false
        AutoStartOnSignIn = $false
        EnableDesktopNotifications = $false
        SelectedThemeIndex = 0
        SelectedBackdropIndex = 0
        WindowLeft = -32000
        WindowTop = -32000
        WindowWidth = 4096
        WindowHeight = 2304
        IsWindowMaximized = $false
        RecentReports = @()
    } | ConvertTo-Json | Set-Content -Path $settingsPath -Encoding UTF8

    $primary = Start-Process -FilePath $appFull `
        -WorkingDirectory (Split-Path -Parent $appFull) `
        -ArgumentList "--background-startup" `
        -PassThru

    Start-Sleep -Seconds 2
    $primary.Refresh()
    if ($primary.HasExited) {
        throw "Single-instance smoke failed: background-startup process exited early with code $($primary.ExitCode)."
    }

    $hiddenWindow = Test-WindowTitle $primary.Id "*E-Detection*"
    if ($hiddenWindow -ne $null) {
        throw "Single-instance smoke failed: background-startup launch left a visible window '$($hiddenWindow.Title)'."
    }

    $backgroundDuplicate = Start-Process -FilePath $appFull `
        -WorkingDirectory (Split-Path -Parent $appFull) `
        -ArgumentList "--background-startup" `
        -PassThru
    Wait-ForProcessExit $backgroundDuplicate $WaitSeconds

    $hiddenWindow = Test-WindowTitle $primary.Id "*E-Detection*"
    if ($hiddenWindow -ne $null) {
        throw "Single-instance smoke failed: duplicate background-startup restored a visible window '$($hiddenWindow.Title)'."
    }

    $normalDuplicate = Start-Process -FilePath $appFull `
        -WorkingDirectory (Split-Path -Parent $appFull) `
        -PassThru
    Wait-ForProcessExit $normalDuplicate $WaitSeconds

    $mainWindow = Wait-ForWindowTitle $primary.Id "*E-Detection*" $WaitSeconds
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($mainWindow.Handle)
    if ($root.Current.Name -notlike "*E-Detection*") {
        throw "Single-instance smoke failed: normal duplicate restored window automation name was '$($root.Current.Name)'."
    }
    $restoredBounds = Assert-WindowVisibleInWorkArea $mainWindow.Handle

    $legacyDuplicate = Start-Process -FilePath $appFull `
        -WorkingDirectory (Split-Path -Parent $appFull) `
        -ArgumentList "--startup-minimized" `
        -PassThru
    Wait-ForProcessExit $legacyDuplicate $WaitSeconds

    $mainWindow = Wait-ForWindowTitle $primary.Id "*E-Detection*" $WaitSeconds
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($mainWindow.Handle)
    if ($root.Current.Name -notlike "*E-Detection*") {
        throw "Single-instance smoke failed: legacy duplicate restored window automation name was '$($root.Current.Name)'."
    }
    $legacyRestoredBounds = Assert-WindowVisibleInWorkArea $mainWindow.Handle

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $resultPath = Join-Path $outputFull "single-instance-smoke-$timestamp.json"
    [pscustomobject]@{
        AppPath = $appFull
        PrimaryProcessId = $primary.Id
        BackgroundDuplicateProcessId = $backgroundDuplicate.Id
        BackgroundDuplicateExitCode = $backgroundDuplicate.ExitCode
        NormalDuplicateProcessId = $normalDuplicate.Id
        NormalDuplicateExitCode = $normalDuplicate.ExitCode
        LegacyDuplicateProcessId = $legacyDuplicate.Id
        LegacyDuplicateExitCode = $legacyDuplicate.ExitCode
        MainWindowTitle = $mainWindow.Title
        RestoredWindowBounds = $restoredBounds
        LegacyRestoredWindowBounds = $legacyRestoredBounds
        StartupArgument = "--background-startup"
        BackgroundDuplicateStartupArgument = "--background-startup"
        LegacyDuplicateStartupArgument = "--startup-minimized"
        HiddenOnStartup = $true
        BackgroundDuplicateKeptHidden = $true
        RestoredByNormalLaunch = $true
        RestoredByLegacyStartupMinimizedLaunch = $true
        Passed = $true
        CapturedAt = (Get-Date).ToString("o")
    } | ConvertTo-Json | Set-Content -Path $resultPath -Encoding UTF8

    Write-Host "Single-instance smoke passed: $resultPath"
}
finally {
    if ($legacyDuplicate -ne $null -and !$legacyDuplicate.HasExited) {
        Stop-Process -Id $legacyDuplicate.Id -Force
    }

    if ($backgroundDuplicate -ne $null -and !$backgroundDuplicate.HasExited) {
        Stop-Process -Id $backgroundDuplicate.Id -Force
    }

    if ($normalDuplicate -ne $null -and !$normalDuplicate.HasExited) {
        Stop-Process -Id $normalDuplicate.Id -Force
    }

    if ($primary -ne $null -and !$primary.HasExited) {
        $primary.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 2
        if (!$primary.HasExited) {
            Stop-Process -Id $primary.Id -Force
        }
    }

    if ($settingsExisted) {
        New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
        Set-Content -Path $settingsPath -Value $settingsBackup -Encoding UTF8
    }
    elseif (Test-Path $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
