[CmdletBinding()]
param(
    [string]$PackagePath = "",

    [string]$AppPath = "",

    [string]$OutputDirectory = "",

    [int]$Width = 1600,

    [int]$Height = 1000,

    [int]$WaitSeconds = 10
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
        Join-Path $scriptDir "smoke-results\startup-integration"
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\startup-integration-smoke"
    }
}

$appFull = [System.IO.Path]::GetFullPath((Resolve-Path $AppPath).Path)
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputFull | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
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

function Wait-ForTaskContains([string]$ExpectedText, [bool]$ShouldExist, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $xml = Get-StartupTaskXml
        $contains = ![string]::IsNullOrWhiteSpace($xml) `
            -and $xml.IndexOf($ExpectedText, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        if ($ShouldExist -and $contains) {
            return $xml
        }

        if (!$ShouldExist -and !$contains) {
            return $xml
        }

        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for scheduled task '$taskName' ShouldExist=$ShouldExist."
}

function Get-RunStartupValue {
    if (!(Test-Path $runKeyPath)) {
        return $null
    }

    $entry = Get-ItemProperty -Path $runKeyPath -Name $runEntryName -ErrorAction SilentlyContinue
    if ($entry -eq $null) {
        return $null
    }

    return $entry.PSObject.Properties[$runEntryName].Value
}

function Wait-ForStartupIntegration([string]$ExpectedText, [bool]$ShouldExist, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $xml = Get-StartupTaskXml
        $taskContains = ![string]::IsNullOrWhiteSpace($xml) `
            -and $xml.IndexOf($ExpectedText, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        $runValue = Get-RunStartupValue
        $runContains = $runValue -is [string] `
            -and $runValue.IndexOf($ExpectedText, [System.StringComparison]::OrdinalIgnoreCase) -ge 0

        if ($ShouldExist -and $taskContains) {
            return [pscustomobject]@{
                Provider = "Task Scheduler"
                TaskXml = $xml
                RunValue = $runValue
            }
        }

        if ($ShouldExist -and $runContains) {
            return [pscustomobject]@{
                Provider = "HKCU Run"
                TaskXml = $xml
                RunValue = $runValue
            }
        }

        if (!$ShouldExist -and !$taskContains -and !$runContains) {
            return [pscustomobject]@{
                Provider = "None"
                TaskXml = $xml
                RunValue = $runValue
            }
        }

        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for startup integration ShouldExist=$ShouldExist."
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

public static class DesktopStartupIntegrationSmokeNative
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

function Get-WindowTitle([IntPtr]$Handle) {
    $builder = [System.Text.StringBuilder]::new(512)
    [DesktopStartupIntegrationSmokeNative]::GetWindowText($Handle, $builder, $builder.Capacity) | Out-Null
    $builder.ToString()
}

function Get-ProcessWindows([int]$ProcessId) {
    $windows = [System.Collections.Generic.List[object]]::new()
    $callback = [DesktopStartupIntegrationSmokeNative+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)
        $windowProcessId = 0
        [DesktopStartupIntegrationSmokeNative]::GetWindowThreadProcessId($hWnd, [ref]$windowProcessId) | Out-Null
        if ($windowProcessId -eq $ProcessId -and [DesktopStartupIntegrationSmokeNative]::IsWindowVisible($hWnd)) {
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

    [DesktopStartupIntegrationSmokeNative]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
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

function Find-AutomationElement([IntPtr]$RootHandle, [string]$Name, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($RootHandle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)

    do {
        $match = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($match -ne $null) {
            return $match
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for automation element '$Name'."
}

function Try-FindAutomationElement([IntPtr]$RootHandle, [string]$Name) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($RootHandle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ForAnyAutomationName([IntPtr]$RootHandle, [string[]]$Names, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        foreach ($name in $Names) {
            $match = Try-FindAutomationElement $RootHandle $name
            if ($match -ne $null) {
                return $name
            }
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for automation names '$($Names -join ', ')'."
}

function Invoke-AutomationElement([IntPtr]$RootHandle, [string]$Name, [int]$TimeoutSeconds) {
    $match = Find-AutomationElement $RootHandle $Name $TimeoutSeconds
    $pattern = $match.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Toggle-AutomationElement([System.Windows.Automation.AutomationElement]$Element) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $pattern.Toggle()
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
        InputDirectory = ""
        OutputDirectory = ""
        ConfigPath = "config.json"
        PythonExecutable = "python"
        WriteReport = $true
        CloseToTrayOnClose = $false
        StartMinimizedToTray = $false
        AutoStartOnSignIn = $false
        EnableDesktopNotifications = $false
        SelectedThemeIndex = 0
        SelectedBackdropIndex = 0
        RecentReports = @()
    } | ConvertTo-Json | Set-Content -Path $settingsPath -Encoding UTF8

    $process = Start-Process -FilePath $appFull -WorkingDirectory (Split-Path -Parent $appFull) -PassThru
    $mainWindow = Wait-ForWindowTitle $process.Id "*E-Detection*" $WaitSeconds
    [DesktopStartupIntegrationSmokeNative]::ShowWindow($mainWindow.Handle, 9) | Out-Null

    $dpi = [DesktopStartupIntegrationSmokeNative]::GetDpiForWindow($mainWindow.Handle)
    if ($dpi -eq 0) {
        $dpi = 96
    }

    $scale = $dpi / 96.0
    $requestedPhysicalWidth = [Math]::Ceiling($Width * $scale)
    $requestedPhysicalHeight = [Math]::Ceiling($Height * $scale)
    $workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $targetWidth = [Math]::Min($requestedPhysicalWidth, $workArea.Width)
    $targetHeight = [Math]::Min($requestedPhysicalHeight, $workArea.Height)
    [DesktopStartupIntegrationSmokeNative]::MoveWindow($mainWindow.Handle, $workArea.Left, $workArea.Top, $targetWidth, $targetHeight, $true) | Out-Null
    [DesktopStartupIntegrationSmokeNative]::SetForegroundWindow($mainWindow.Handle) | Out-Null
    Start-Sleep -Milliseconds 500

    Invoke-AutomationElement $mainWindow.Handle "设置" $WaitSeconds
    Find-AutomationElement $mainWindow.Handle "登录后自动启动未启用 · Task Scheduler" $WaitSeconds | Out-Null
    $autoStartToggle = Find-AutomationElement $mainWindow.Handle "登录后自动启动" $WaitSeconds

    Toggle-AutomationElement $autoStartToggle
    $enabledIntegration = Wait-ForStartupIntegration $appFull $true $WaitSeconds
    $enabledStatusText = Wait-ForAnyAutomationName $mainWindow.Handle @(
        "登录后自动启动已启用 · Task Scheduler",
        "登录后自动启动已启用 · HKCU Run"
    ) $WaitSeconds

    Toggle-AutomationElement $autoStartToggle
    Wait-ForStartupIntegration $appFull $false $WaitSeconds | Out-Null
    $disabledStatusText = Wait-ForAnyAutomationName $mainWindow.Handle @(
        "登录后自动启动未启用 · Task Scheduler",
        "登录后自动启动未启用 · HKCU Run"
    ) $WaitSeconds

    $resultPath = Join-Path $outputFull "startup-integration-smoke-$timestamp.json"
    [pscustomobject]@{
        AppPath = $appFull
        ProcessId = $process.Id
        Provider = $enabledIntegration.Provider
        TaskName = $taskName
        TaskCreated = ($enabledIntegration.Provider -eq "Task Scheduler")
        RunEntryCreated = ($enabledIntegration.Provider -eq "HKCU Run")
        TaskRemoved = $true
        EnabledStatusText = $enabledStatusText
        DisabledStatusText = $disabledStatusText
        TaskXmlContainsAppPath = ($enabledIntegration.TaskXml -is [string] -and $enabledIntegration.TaskXml.IndexOf($appFull, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
        RunEntryContainsAppPath = ($enabledIntegration.RunValue -is [string] -and $enabledIntegration.RunValue.IndexOf($appFull, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
        Dpi = $dpi
        Passed = $true
        CapturedAt = (Get-Date).ToString("o")
    } | ConvertTo-Json | Set-Content -Path $resultPath -Encoding UTF8

    Write-Host "Startup-integration smoke passed: $resultPath"
}
finally {
    if ($process -ne $null -and !$process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 2
        if (!$process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
    }

    Remove-StartupTask
    Restore-StartupTask $taskBackup

    if ($settingsExisted) {
        New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
        Set-Content -Path $settingsPath -Value $settingsBackup -Encoding UTF8
    }
    elseif (Test-Path $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }

    if ($runEntryExisted) {
        New-Item -Path $runKeyPath -Force | Out-Null
        Set-ItemProperty -Path $runKeyPath -Name $runEntryName -Value $runEntryBackup
    }
    else {
        Remove-ItemProperty -Path $runKeyPath -Name $runEntryName -ErrorAction SilentlyContinue
    }
}
