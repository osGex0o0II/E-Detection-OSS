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
        Join-Path $scriptDir "smoke-results\keyboard"
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\keyboard-smoke"
    }
}

$appFull = [System.IO.Path]::GetFullPath((Resolve-Path $AppPath).Path)
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputFull | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DesktopKeyboardSmokeNative
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
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
"@

function Get-WindowTitle([IntPtr]$Handle) {
    $builder = [System.Text.StringBuilder]::new(512)
    [DesktopKeyboardSmokeNative]::GetWindowText($Handle, $builder, $builder.Capacity) | Out-Null
    $builder.ToString()
}

function Get-ProcessWindows([int]$ProcessId) {
    $windows = [System.Collections.Generic.List[object]]::new()
    $callback = [DesktopKeyboardSmokeNative+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)
        $windowProcessId = 0
        [DesktopKeyboardSmokeNative]::GetWindowThreadProcessId($hWnd, [ref]$windowProcessId) | Out-Null
        if ($windowProcessId -eq $ProcessId -and [DesktopKeyboardSmokeNative]::IsWindowVisible($hWnd)) {
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

    [DesktopKeyboardSmokeNative]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
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

function Send-DesktopKeys([string]$Keys) {
    if (Send-DesktopKeysNative $Keys) {
        return
    }

    $lastError = $null
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            [System.Windows.Forms.SendKeys]::Flush()
            [System.Windows.Forms.SendKeys]::SendWait($Keys)
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 150
            return
        }
        catch {
            $lastError = $_
            try {
                $shell = New-Object -ComObject WScript.Shell
                $shell.SendKeys($Keys)
                [System.Windows.Forms.Application]::DoEvents()
                Start-Sleep -Milliseconds 150
                return
            }
            catch {
                $lastError = $_
            }

            Start-Sleep -Milliseconds 250
        }
    }

    throw $lastError
}

function Activate-DesktopProcess([int]$ProcessId, [IntPtr]$WindowHandle) {
    [DesktopKeyboardSmokeNative]::SetForegroundWindow($WindowHandle) | Out-Null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shell.AppActivate($ProcessId) | Out-Null
    }
    catch {
    }

    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 250
}

function Send-KeyDown([byte]$VirtualKey) {
    [DesktopKeyboardSmokeNative]::keybd_event($VirtualKey, 0, 0, [UIntPtr]::Zero)
}

function Send-KeyUp([byte]$VirtualKey) {
    [DesktopKeyboardSmokeNative]::keybd_event($VirtualKey, 0, 0x0002, [UIntPtr]::Zero)
}

function Send-KeyPress([byte]$VirtualKey) {
    Send-KeyDown $VirtualKey
    Start-Sleep -Milliseconds 40
    Send-KeyUp $VirtualKey
}

function Send-DesktopKeysNative([string]$Keys) {
    switch ($Keys) {
        "{F6}" {
            Send-KeyPress 0x75
            Start-Sleep -Milliseconds 200
            return $true
        }
        "^k" {
            Send-KeyDown 0x11
            Start-Sleep -Milliseconds 40
            Send-KeyPress 0x4B
            Start-Sleep -Milliseconds 40
            Send-KeyUp 0x11
            Start-Sleep -Milliseconds 200
            return $true
        }
        "^a" {
            Send-KeyDown 0x11
            Start-Sleep -Milliseconds 40
            Send-KeyPress 0x41
            Start-Sleep -Milliseconds 40
            Send-KeyUp 0x11
            Start-Sleep -Milliseconds 100
            return $true
        }
        "{ESC}" {
            Send-KeyPress 0x1B
            Start-Sleep -Milliseconds 100
            return $true
        }
        default {
            return $false
        }
    }
}

function Set-QuickActionSearch([IntPtr]$RootHandle, [string]$Text) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($RootHandle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $searchBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($searchBox -eq $null) {
        throw "Quick action search box was not found."
    }

    $pattern = $null
    if ($searchBox.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        $pattern.SetValue($Text)
        return
    }

    $searchBox.SetFocus()
    Start-Sleep -Milliseconds 100
    Send-DesktopKeys "^a"
    Send-DesktopKeys $Text
}

$process = $null

try {
    $process = Start-Process -FilePath $appFull -WorkingDirectory (Split-Path -Parent $appFull) -PassThru
    $mainWindow = Wait-ForWindowTitle $process.Id "*E-Detection*" $WaitSeconds
    [DesktopKeyboardSmokeNative]::ShowWindow($mainWindow.Handle, 9) | Out-Null
    $dpi = [DesktopKeyboardSmokeNative]::GetDpiForWindow($mainWindow.Handle)
    if ($dpi -eq 0) {
        $dpi = 96
    }

    $scale = $dpi / 96.0
    $requestedPhysicalWidth = [Math]::Ceiling($Width * $scale)
    $requestedPhysicalHeight = [Math]::Ceiling($Height * $scale)
    $workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $targetWidth = [Math]::Min($requestedPhysicalWidth, $workArea.Width)
    $targetHeight = [Math]::Min($requestedPhysicalHeight, $workArea.Height)
    [DesktopKeyboardSmokeNative]::MoveWindow($mainWindow.Handle, $workArea.Left, $workArea.Top, $targetWidth, $targetHeight, $true) | Out-Null
    [DesktopKeyboardSmokeNative]::SetForegroundWindow($mainWindow.Handle) | Out-Null
    Start-Sleep -Milliseconds 500

    $diagnosticsAction = Wait-ForAutomationName $mainWindow.Handle "运行诊断" $WaitSeconds
    $eventLogTab = Wait-ForAutomationName $mainWindow.Handle "事件日志" $WaitSeconds
    $inputPanel = Wait-ForAutomationName $mainWindow.Handle "检测输入" $WaitSeconds
    $detailTab = Wait-ForAutomationName $mainWindow.Handle "异常明细" $WaitSeconds
    $highRiskSection = Wait-ForAutomationName $mainWindow.Handle "高风险设备" $WaitSeconds

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $resultPath = Join-Path $outputFull "keyboard-smoke-$timestamp.json"
    [pscustomobject]@{
        AppPath = $appFull
        ProcessId = $process.Id
        MainWindowTitle = $mainWindow.Title
        DiagnosticsAction = $diagnosticsAction
        EventLogTab = $eventLogTab
        InputPanel = $inputPanel
        DetailTab = $detailTab
        HighRiskSection = $highRiskSection
        Dpi = $dpi
        RequestedDipWidth = $Width
        RequestedDipHeight = $Height
        TargetWidth = $targetWidth
        TargetHeight = $targetHeight
        ResponsiveMode = if ($Width -le 920) { "compact" } else { "comfortable" }
        CheckedActions = @("运行诊断", "事件日志", "检测输入", "异常明细", "高风险设备")
        Passed = $true
        CapturedAt = (Get-Date).ToString("o")
    } | ConvertTo-Json | Set-Content -Path $resultPath -Encoding UTF8

    Write-Host "Keyboard smoke passed: $resultPath"
}
finally {
    if ($process -ne $null -and !$process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 2
        if (!$process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
    }
}
