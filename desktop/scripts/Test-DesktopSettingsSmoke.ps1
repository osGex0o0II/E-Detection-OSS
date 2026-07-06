[CmdletBinding()]
param(
    [string]$PackagePath = "",

    [string]$AppPath = "",

    [string]$OutputDirectory = "",

    [int]$Width = 1600,

    [int]$Height = 1000,

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
        Join-Path $scriptDir "smoke-results\settings"
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\settings-smoke"
    }
}

$appFull = [System.IO.Path]::GetFullPath((Resolve-Path $AppPath).Path)
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputFull | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$sandboxDir = Join-Path $outputFull "sandbox-$timestamp"
$inputDir = Join-Path $sandboxDir "input"
New-Item -ItemType Directory -Force -Path $inputDir | Out-Null

$settingsDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "E-Detection\Desktop"
$settingsPath = Join-Path $settingsDirectory "settings.json"
$settingsExisted = Test-Path $settingsPath
$settingsBackup = $null
if ($settingsExisted) {
    $settingsBackup = Get-Content -Path $settingsPath -Raw
}

$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runEntryName = "E-Detection Desktop"
$runEntryExisted = $false
$runEntryBackup = $null
if (Test-Path $runKeyPath) {
    $runEntry = Get-ItemProperty -Path $runKeyPath -Name $runEntryName -ErrorAction SilentlyContinue
    if ($runEntry -ne $null) {
        $runEntryExisted = $true
        $runEntryBackup = $runEntry.PSObject.Properties[$runEntryName].Value
    }
}

$taskName = "E-Detection Desktop Autostart"

function Get-StartupTaskXml {
    $output = & schtasks.exe /Query /TN $taskName /XML 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return ($output -join [Environment]::NewLine)
}

function Remove-StartupTask {
    & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
}

function Restore-StartupTask([string]$TaskXml) {
    if ([string]::IsNullOrWhiteSpace($TaskXml)) {
        return
    }

    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) "EDetectionDesktopTaskRestore-$([Guid]::NewGuid().ToString('N')).xml"
    try {
        Set-Content -Path $tempPath -Value $TaskXml -Encoding Unicode
        & schtasks.exe /Create /TN $taskName /XML $tempPath /F | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to restore scheduled task '$taskName'."
        }
    }
    finally {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
}

$taskBackup = Get-StartupTaskXml

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DesktopSettingsSmokeNative
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

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class DesktopSettingsMouseNative
{
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
}
"@

function Get-WindowTitle([IntPtr]$Handle) {
    $builder = [System.Text.StringBuilder]::new(512)
    [DesktopSettingsSmokeNative]::GetWindowText($Handle, $builder, $builder.Capacity) | Out-Null
    $builder.ToString()
}

function Get-ProcessWindows([int]$ProcessId) {
    $windows = [System.Collections.Generic.List[object]]::new()
    $callback = [DesktopSettingsSmokeNative+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)
        $windowProcessId = 0
        [DesktopSettingsSmokeNative]::GetWindowThreadProcessId($hWnd, [ref]$windowProcessId) | Out-Null
        if ($windowProcessId -eq $ProcessId -and [DesktopSettingsSmokeNative]::IsWindowVisible($hWnd)) {
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

    [DesktopSettingsSmokeNative]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
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

function Wait-ForAutomationName([IntPtr]$RootHandle, [string]$Name, [int]$TimeoutSeconds) {
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

function Wait-ForAutomationNameLike([IntPtr]$RootHandle, [string]$Pattern, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($RootHandle)

    do {
        $matches = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($match in $matches) {
            if ($match.Current.Name -like $Pattern) {
                return $match.Current.Name
            }
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for automation element pattern '$Pattern'."
}

function Invoke-AutomationElement([IntPtr]$RootHandle, [string]$Name, [int]$TimeoutSeconds) {
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

function Invoke-SettingsButtonByPosition([IntPtr]$RootHandle) {
    $rect = New-Object DesktopSettingsSmokeNative+RECT
    if (![DesktopSettingsSmokeNative]::GetWindowRect($RootHandle, [ref]$rect)) {
        return
    }

    $settingsX = $rect.Right - 328
    $settingsY = $rect.Top + 32
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($settingsX, $settingsY)
    [System.Windows.Forms.SendKeys]::Flush()
    [System.Windows.Forms.Application]::DoEvents()
    [DesktopSettingsMouseNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [DesktopSettingsMouseNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

$process = $null

try {
    New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
    if (!(Test-Path $runKeyPath)) {
        New-Item -Path $runKeyPath -Force | Out-Null
    }

    Remove-ItemProperty -Path $runKeyPath -Name $runEntryName -ErrorAction SilentlyContinue
    Remove-StartupTask

    [pscustomobject]@{
        InputDirectory = $inputDir
        OutputDirectory = ""
        ConfigPath = "config.json"
        PythonExecutable = "python"
        WriteReport = $false
        CloseToTrayOnClose = $true
        StartMinimizedToTray = $false
        AutoStartOnSignIn = $false
        EnableDesktopNotifications = $false
        EnableGlobalHotkeys = $true
        EnableQuickActionsShortcut = $true
        SelectedQuickActionsShortcutIndex = 1
        SelectedLogRetentionIndex = 2
        SelectedRecentReportLimitIndex = 2
        SelectedThemeIndex = 0
        SelectedBackdropIndex = 0
        RecentReports = @()
    } | ConvertTo-Json | Set-Content -Path $settingsPath -Encoding UTF8

    $process = Start-Process -FilePath $appFull -WorkingDirectory (Split-Path -Parent $appFull) -PassThru
    $mainWindow = Wait-ForWindowTitle $process.Id "*E-Detection*" $WaitSeconds
    [DesktopSettingsSmokeNative]::ShowWindow($mainWindow.Handle, 9) | Out-Null

    $dpi = [DesktopSettingsSmokeNative]::GetDpiForWindow($mainWindow.Handle)
    if ($dpi -eq 0) {
        $dpi = 96
    }

    $scale = $dpi / 96.0
    $requestedPhysicalWidth = [Math]::Ceiling($Width * $scale)
    $requestedPhysicalHeight = [Math]::Ceiling($Height * $scale)
    $workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $targetWidth = [Math]::Min($requestedPhysicalWidth, $workArea.Width)
    $targetHeight = [Math]::Min($requestedPhysicalHeight, $workArea.Height)
    [DesktopSettingsSmokeNative]::MoveWindow($mainWindow.Handle, $workArea.Left, $workArea.Top, $targetWidth, $targetHeight, $true) | Out-Null
    [DesktopSettingsSmokeNative]::SetForegroundWindow($mainWindow.Handle) | Out-Null
    Start-Sleep -Milliseconds 500

    $settingsButton = Invoke-AutomationElement $mainWindow.Handle "设置" 2
    if ($settingsButton -eq $null) {
        Invoke-SettingsButtonByPosition $mainWindow.Handle
    }

    $settingsTitle = Wait-ForAutomationName $mainWindow.Handle "设置" $WaitSeconds
    $defaultsSection = Wait-ForAutomationName $mainWindow.Handle "检测默认值" $WaitSeconds
    $reportsSection = Wait-ForAutomationName $mainWindow.Handle "报告与历史" $WaitSeconds
    $logsSection = Wait-ForAutomationName $mainWindow.Handle "日志" $WaitSeconds
    $startupTrayToggle = Wait-ForAutomationName $mainWindow.Handle "启动时隐藏到托盘" $WaitSeconds
    $autoStartToggle = Wait-ForAutomationName $mainWindow.Handle "登录后自动启动" $WaitSeconds
    $globalHotkeyToggle = Wait-ForAutomationName $mainWindow.Handle "全局热键" $WaitSeconds
    $globalHotkeyText = Wait-ForAutomationName $mainWindow.Handle "全局热键已启用 · Ctrl+Alt+Shift+E 恢复工作台" $WaitSeconds
    $quickActionsToggle = Wait-ForAutomationName $mainWindow.Handle "快速操作快捷键" $WaitSeconds
    $quickActionsText = Wait-ForAutomationNameLike $mainWindow.Handle "*Ctrl+Shift+P*" $WaitSeconds
    $startupIntegrationText = Wait-ForAutomationName $mainWindow.Handle "登录后自动启动未启用 · Task Scheduler" $WaitSeconds
    $recentLimitText = Wait-ForAutomationName $mainWindow.Handle "保留最近 50 个" $WaitSeconds
    $logLimitText = Wait-ForAutomationName $mainWindow.Handle "保留最近 1000 条" $WaitSeconds
    $desktopHealthSection = Wait-ForAutomationName $mainWindow.Handle "桌面健康" $WaitSeconds
    $packageHealthText = Wait-ForAutomationName $mainWindow.Handle "包完整性可用 · 27/27" $WaitSeconds
    $pythonBridgeText = Wait-ForAutomationName $mainWindow.Handle "检测组件 · 本地源码 · python" $WaitSeconds
    $installShapeText = Wait-ForAutomationName $mainWindow.Handle "安装形态: 便携/开发版" $WaitSeconds
    $settingsJson = Get-Content -Path $settingsPath -Raw | ConvertFrom-Json
    if ($settingsJson.SettingsVersion -ne 3) {
        throw "Settings smoke failed: SettingsVersion was '$($settingsJson.SettingsVersion)', expected '3'."
    }

    if ($settingsJson.SelectedQuickActionsShortcutIndex -ne 1) {
        throw "Settings smoke failed: SelectedQuickActionsShortcutIndex was '$($settingsJson.SelectedQuickActionsShortcutIndex)', expected '1'."
    }

    $resultPath = Join-Path $outputFull "settings-smoke-$timestamp.json"
    [pscustomobject]@{
        AppPath = $appFull
        ProcessId = $process.Id
        MainWindowTitle = $mainWindow.Title
        SettingsTitle = $settingsTitle
        DefaultsSection = $defaultsSection
        ReportsSection = $reportsSection
        LogsSection = $logsSection
        StartupTrayToggle = $startupTrayToggle
        AutoStartToggle = $autoStartToggle
        GlobalHotkeyToggle = $globalHotkeyToggle
        GlobalHotkeyText = $globalHotkeyText
        QuickActionsToggle = $quickActionsToggle
        QuickActionsText = $quickActionsText
        QuickActionsShortcutIndex = $settingsJson.SelectedQuickActionsShortcutIndex
        StartupIntegrationText = $startupIntegrationText
        SettingsVersion = $settingsJson.SettingsVersion
        DesktopHealthSection = $desktopHealthSection
        PackageHealthText = $packageHealthText
        PythonBridgeText = $pythonBridgeText
        InstallShapeText = $installShapeText
        RecentLimitText = $recentLimitText
        LogLimitText = $logLimitText
        Dpi = $dpi
        RequestedDipWidth = $Width
        RequestedDipHeight = $Height
        TargetWidth = $targetWidth
        TargetHeight = $targetHeight
        ResponsiveMode = if ($Width -le 700) { "compact" } else { "comfortable" }
        Trigger = "Ctrl+S after writing persisted settings"
        Passed = $true
        CapturedAt = (Get-Date).ToString("o")
    } | ConvertTo-Json | Set-Content -Path $resultPath -Encoding UTF8

    Write-Host "Settings smoke passed: $resultPath"
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

    Remove-StartupTask
    Restore-StartupTask $taskBackup

    if ($runEntryExisted) {
        New-Item -Path $runKeyPath -Force | Out-Null
        Set-ItemProperty -Path $runKeyPath -Name $runEntryName -Value $runEntryBackup
    }
    else {
        Remove-ItemProperty -Path $runKeyPath -Name $runEntryName -ErrorAction SilentlyContinue
    }
}
