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
        Join-Path $scriptDir "smoke-results\global-hotkey"
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\global-hotkey-smoke"
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

public static class DesktopGlobalHotkeySmokeNative
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
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
"@

function Get-WindowTitle([IntPtr]$Handle) {
    $builder = [System.Text.StringBuilder]::new(512)
    [DesktopGlobalHotkeySmokeNative]::GetWindowText($Handle, $builder, $builder.Capacity) | Out-Null
    $builder.ToString()
}

function Get-ProcessWindows([int]$ProcessId) {
    $windows = [System.Collections.Generic.List[object]]::new()
    $callback = [DesktopGlobalHotkeySmokeNative+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)
        $windowProcessId = 0
        [DesktopGlobalHotkeySmokeNative]::GetWindowThreadProcessId($hWnd, [ref]$windowProcessId) | Out-Null
        if ($windowProcessId -eq $ProcessId -and [DesktopGlobalHotkeySmokeNative]::IsWindowVisible($hWnd)) {
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

    [DesktopGlobalHotkeySmokeNative]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
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

function Send-KeyDown([byte]$VirtualKey) {
    [DesktopGlobalHotkeySmokeNative]::keybd_event($VirtualKey, 0, 0, [UIntPtr]::Zero)
}

function Send-KeyUp([byte]$VirtualKey) {
    [DesktopGlobalHotkeySmokeNative]::keybd_event($VirtualKey, 0, 0x0002, [UIntPtr]::Zero)
}

function Send-KeyPress([byte]$VirtualKey) {
    Send-KeyDown $VirtualKey
    Start-Sleep -Milliseconds 40
    Send-KeyUp $VirtualKey
}

function Send-GlobalHotkey([byte]$VirtualKey) {
    Send-KeyDown 0x11
    Start-Sleep -Milliseconds 35
    Send-KeyDown 0x12
    Start-Sleep -Milliseconds 35
    Send-KeyDown 0x10
    Start-Sleep -Milliseconds 35
    Send-KeyPress $VirtualKey
    Start-Sleep -Milliseconds 35
    Send-KeyUp 0x10
    Send-KeyUp 0x12
    Send-KeyUp 0x11
    [System.Windows.Forms.Application]::DoEvents()
}

$process = $null

try {
    New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
    [pscustomobject]@{
        InputDirectory = ""
        OutputDirectory = ""
        ConfigPath = "config.json"
        PythonExecutable = "python"
        WriteReport = $true
        CloseToTrayOnClose = $false
        StartMinimizedToTray = $false
        AutoStartOnSignIn = $false
        EnableDesktopNotifications = $false
        EnableGlobalHotkeys = $true
        EnableQuickActionsShortcut = $true
        SelectedThemeIndex = 0
        SelectedBackdropIndex = 0
        RecentReports = @()
    } | ConvertTo-Json | Set-Content -Path $settingsPath -Encoding UTF8

    $process = Start-Process -FilePath $appFull -WorkingDirectory (Split-Path -Parent $appFull) -PassThru
    $mainWindow = Wait-ForWindowTitle $process.Id "*E-Detection*" $WaitSeconds
    [DesktopGlobalHotkeySmokeNative]::SetForegroundWindow($mainWindow.Handle) | Out-Null
    Start-Sleep -Milliseconds 500

    Send-GlobalHotkey 0x45
    $restoredWindow = Wait-ForWindowTitle $process.Id "*E-Detection*" $WaitSeconds

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $resultPath = Join-Path $outputFull "global-hotkey-smoke-$timestamp.json"
    [pscustomobject]@{
        AppPath = $appFull
        ProcessId = $process.Id
        RestoreGesture = "Ctrl+Alt+Shift+E"
        RestoredWindowTitle = $restoredWindow.Title
        Passed = $true
        CapturedAt = (Get-Date).ToString("o")
    } | ConvertTo-Json | Set-Content -Path $resultPath -Encoding UTF8

    Write-Host "Global-hotkey smoke passed: $resultPath"
}
finally {
    if ($process -ne $null -and !$process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 2
        if (!$process.HasExited) {
            Stop-Process -Id $process.Id -Force
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
