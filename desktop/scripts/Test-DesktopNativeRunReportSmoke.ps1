[CmdletBinding()]
param(
    [string]$PackagePath = "",

    [string]$AppPath = "",

    [string]$OutputDirectory = "",

    [int]$Width = 1600,

    [int]$Height = 1000,

    [int]$WaitSeconds = 30
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
        Join-Path $scriptDir "smoke-results\native-run-report"
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\native-run-report-smoke"
    }
}

$appFull = [System.IO.Path]::GetFullPath((Resolve-Path $AppPath).Path)
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputFull | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$sandboxDir = Join-Path $outputFull "sandbox-$timestamp"
$inputDir = Join-Path $sandboxDir "input"
$reportDir = Join-Path $sandboxDir "reports"
$configPath = Join-Path $sandboxDir "config.json"
New-Item -ItemType Directory -Force -Path $inputDir | Out-Null
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
Set-Content -Path (Join-Path $inputDir "voltage.csv") -Value "time,Uab`n0,320`n" -Encoding UTF8
@"
{
  "V_MIN_THRESHOLD": 353.0,
  "V_MAX_THRESHOLD": 430.0,
  "I_MAX_THRESHOLD": 1000.0,
  "I_UNBALANCE_MAX_THRESHOLD": 0.15,
  "P_ACTIVE_MIN_THRESHOLD": 0.0,
  "PF_MIN_THRESHOLD": 0.9,
  "T_MIN_THRESHOLD": 0.0,
  "T_MAX_THRESHOLD": 70.0,
  "I_MIN_ACTIVE_THRESHOLD": 1.0,
  "FREEZE_COUNT_THRESHOLD": 3,
  "FREEZE_STD_THRESHOLD": 0.01,
  "V_IMBALANCE_THRESHOLD": 0.02,
  "current_overload": true,
  "current_unbalance": false,
  "power_factor": false,
  "detail_output": false
}
"@ | Set-Content -Path $configPath -Encoding UTF8

$settingsEnvironmentVariable = "EDETECTION_DESKTOP_SETTINGS_DIR"
$previousSettingsOverride = $env:EDETECTION_DESKTOP_SETTINGS_DIR
$settingsDirectory = Join-Path $sandboxDir "settings"
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

public static class DesktopNativeRunReportSmokeNative
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
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);
}
"@

function Get-WindowTitle {
    param([IntPtr]$Handle)

    $builder = [System.Text.StringBuilder]::new(512)
    [DesktopNativeRunReportSmokeNative]::GetWindowText($Handle, $builder, $builder.Capacity) | Out-Null
    $builder.ToString()
}

function Get-ProcessWindows {
    param([int]$ProcessId)

    $windows = [System.Collections.Generic.List[object]]::new()
    $callback = [DesktopNativeRunReportSmokeNative+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)
        $windowProcessId = 0
        [DesktopNativeRunReportSmokeNative]::GetWindowThreadProcessId($hWnd, [ref]$windowProcessId) | Out-Null
        if ($windowProcessId -eq $ProcessId -and [DesktopNativeRunReportSmokeNative]::IsWindowVisible($hWnd)) {
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

    [DesktopNativeRunReportSmokeNative]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    $windows
}

function Wait-ForWindowTitle {
    param(
        [int]$ProcessId,
        [string]$Pattern,
        [int]$TimeoutSeconds
    )

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

function Wait-ForAutomationName {
    param(
        [IntPtr]$RootHandle,
        [string]$Name,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($RootHandle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)

    do {
        $match = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($match -ne $null) {
            return $match.Current.Name
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for automation element '$Name'."
}

function Invoke-AutomationElement {
    param(
        [IntPtr]$RootHandle,
        [string]$Name,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($RootHandle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)

    do {
        $match = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($match -ne $null) {
            $pattern = $match.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $pattern.Invoke()
            return $match.Current.Name
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    return $null
}

function Wait-ForReportFile {
    param(
        [string]$Directory,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $report = Get-ChildItem -Path $Directory -Filter "*.xlsx" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($report -ne $null -and $report.Length -gt 0) {
            return $report.FullName
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for native Excel report in '$Directory'."
}

$process = $null

try {
    $env:EDETECTION_DESKTOP_SETTINGS_DIR = $settingsDirectory
    New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
    [pscustomobject]@{
        SettingsVersion = 9
        InputDirectory = $inputDir
        OutputDirectory = $reportDir
        ConfigPath = $configPath
        WriteReport = $true
        CloseToTrayOnClose = $false
        StartMinimizedToTray = $false
        AutoStartOnSignIn = $false
        EnableDesktopNotifications = $false
        SelectedThemeIndex = 0
        SelectedBackdropIndex = 0
        SelectedRecentReportLimitIndex = 1
        RecentReports = @()
    } | ConvertTo-Json | Set-Content -Path $settingsPath -Encoding UTF8

    $process = Start-Process -FilePath $appFull -WorkingDirectory (Split-Path -Parent $appFull) -PassThru
    $mainWindow = Wait-ForWindowTitle $process.Id "*E-Detection*" $WaitSeconds
    [DesktopNativeRunReportSmokeNative]::ShowWindow($mainWindow.Handle, 9) | Out-Null

    $dpi = [DesktopNativeRunReportSmokeNative]::GetDpiForWindow($mainWindow.Handle)
    if ($dpi -eq 0) {
        $dpi = 96
    }

    $scale = $dpi / 96.0
    $requestedPhysicalWidth = [Math]::Ceiling($Width * $scale)
    $requestedPhysicalHeight = [Math]::Ceiling($Height * $scale)
    $workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $targetWidth = [Math]::Min($requestedPhysicalWidth, $workArea.Width)
    $targetHeight = [Math]::Min($requestedPhysicalHeight, $workArea.Height)
    [DesktopNativeRunReportSmokeNative]::MoveWindow($mainWindow.Handle, $workArea.Left, $workArea.Top, $targetWidth, $targetHeight, $true) | Out-Null
    [DesktopNativeRunReportSmokeNative]::SetForegroundWindow($mainWindow.Handle) | Out-Null
    Start-Sleep -Milliseconds 500

    $startAction = Invoke-AutomationElement $mainWindow.Handle "开始检测" 5
    if ($startAction -eq $null) {
        [System.Windows.Forms.SendKeys]::SendWait('{F5}')
    }

    $completionTitle = Wait-ForAutomationName $mainWindow.Handle "检测完成" $WaitSeconds
    $openReportAction = Wait-ForAutomationName $mainWindow.Handle "打开报告" $WaitSeconds
    $reportPath = Wait-ForReportFile $reportDir $WaitSeconds

    $currentSettings = Get-Content -Path $settingsPath -Raw | ConvertFrom-Json
    if ($currentSettings.WriteReport -ne $true) {
        throw "Native run/report smoke failed: WriteReport was '$($currentSettings.WriteReport)', expected 'True'."
    }

    if ($currentSettings.RecentReports.Count -lt 1 -or $currentSettings.RecentReports[0].Path -ne $reportPath) {
        throw "Native run/report smoke failed: latest recent report did not match generated report '$reportPath'."
    }

    $resultPath = Join-Path $outputFull "native-run-report-smoke-$timestamp.json"
    [pscustomobject]@{
        AppPath = $appFull
        SettingsDirectory = $settingsDirectory
        SettingsPath = $settingsPath
        ProcessId = $process.Id
        MainWindowTitle = $mainWindow.Title
        InputDirectory = $inputDir
        OutputDirectory = $reportDir
        ConfigPath = $configPath
        CompletionTitle = $completionTitle
        OpenReportAction = $openReportAction
        ReportPath = $reportPath
        ReportLength = (Get-Item -LiteralPath $reportPath).Length
        RecentReportPath = $currentSettings.RecentReports[0].Path
        WriteReport = $currentSettings.WriteReport
        Dpi = $dpi
        RequestedDipWidth = $Width
        RequestedDipHeight = $Height
        TargetWidth = $targetWidth
        TargetHeight = $targetHeight
        Trigger = "Native backend with WriteReport=true and a one-file CSV input"
        Passed = $true
        CapturedAt = (Get-Date).ToString("o")
    } | ConvertTo-Json | Set-Content -Path $resultPath -Encoding UTF8

    Write-Host "Native run/report smoke passed: $resultPath"
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

    if ($null -eq $previousSettingsOverride) {
        Remove-Item -Path "Env:$settingsEnvironmentVariable" -ErrorAction SilentlyContinue
    }
    else {
        $env:EDETECTION_DESKTOP_SETTINGS_DIR = $previousSettingsOverride
    }
}
