[CmdletBinding()]
param(
    [string]$PackagePath = "",

    [string]$AppPath = "",

    [string]$OutputDirectory = "",

    [int]$Width = 1600,

    [int]$Height = 1000,

    [int]$WaitSeconds = 5
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
        Join-Path $scriptDir "smoke-results\visual"
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\visual-smoke"
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

public static class DesktopSmokeNative
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out RECT rect, int cbAttribute);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

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

$HWND_TOPMOST = [IntPtr](-1)
$HWND_NOTOPMOST = [IntPtr](-2)
$SWP_NOACTIVATE = 0x0010
$SWP_SHOWWINDOW = 0x0040
$DWMWA_EXTENDED_FRAME_BOUNDS = 9
$PW_RENDERFULLCONTENT = 0x00000002

function Get-MainWindowHandle([System.Diagnostics.Process]$Process, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Application exited early with code $($Process.ExitCode)"
        }

        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $Process.MainWindowHandle
        }

        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for the main window handle."
}

function Get-WindowTitle([IntPtr]$Handle) {
    $builder = [System.Text.StringBuilder]::new(512)
    [DesktopSmokeNative]::GetWindowText($Handle, $builder, $builder.Capacity) | Out-Null
    $builder.ToString()
}

function Get-VisibleWindowRect([IntPtr]$Handle) {
    $rect = New-Object DesktopSmokeNative+RECT
    $result = [DesktopSmokeNative]::DwmGetWindowAttribute(
        $Handle,
        $DWMWA_EXTENDED_FRAME_BOUNDS,
        [ref]$rect,
        [System.Runtime.InteropServices.Marshal]::SizeOf([type][DesktopSmokeNative+RECT]))
    if ($result -eq 0) {
        return $rect
    }

    if (![DesktopSmokeNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Failed to read window rectangle."
    }

    $rect
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

function Test-BitmapIsUseful([System.Drawing.Bitmap]$Bitmap) {
    $samples = 0
    $unique = New-Object 'System.Collections.Generic.HashSet[int]'
    $stepX = [Math]::Max(1, [Math]::Floor($Bitmap.Width / 64))
    $stepY = [Math]::Max(1, [Math]::Floor($Bitmap.Height / 64))

    for ($y = 0; $y -lt $Bitmap.Height; $y += $stepY) {
        for ($x = 0; $x -lt $Bitmap.Width; $x += $stepX) {
            [void]$unique.Add($Bitmap.GetPixel($x, $y).ToArgb())
            $samples += 1
        }
    }

    [pscustomobject]@{
        Samples = $samples
        UniqueColors = $unique.Count
        IsUseful = $Bitmap.Width -ge 900 -and $Bitmap.Height -ge 620 -and $unique.Count -gt 12
    }
}

function Capture-WindowBitmap([IntPtr]$Handle, [DesktopSmokeNative+RECT]$Rect) {
    $captureWidth = $Rect.Right - $Rect.Left
    $captureHeight = $Rect.Bottom - $Rect.Top
    if ($captureWidth -le 0 -or $captureHeight -le 0) {
        throw "Invalid window rectangle: $captureWidth x $captureHeight"
    }

    $bufferBitmap = New-Object System.Drawing.Bitmap $captureWidth, $captureHeight
    $bitmap = $null
    $windowDc = [DesktopSmokeNative]::GetWindowDC($Handle)
    $memoryDc = [IntPtr]::Zero
    $hBitmap = [IntPtr]::Zero
    $oldObject = [IntPtr]::Zero

    try {
        if ($windowDc -eq [IntPtr]::Zero) {
            throw "GetWindowDC returned null."
        }

        $memoryDc = [DesktopSmokeNative]::CreateCompatibleDC($windowDc)
        if ($memoryDc -eq [IntPtr]::Zero) {
            throw "CreateCompatibleDC returned null."
        }

        $hBitmap = $bufferBitmap.GetHbitmap()
        $oldObject = [DesktopSmokeNative]::SelectObject($memoryDc, $hBitmap)
        $printed = [DesktopSmokeNative]::PrintWindow($Handle, $memoryDc, $PW_RENDERFULLCONTENT)
        if (!$printed) {
            $printed = [DesktopSmokeNative]::PrintWindow($Handle, $memoryDc, 0)
        }

        if (!$printed) {
            $bitmap = $bufferBitmap
            $bufferBitmap = $null
            return [pscustomobject]@{
                Bitmap = $bitmap
                Method = "PrintWindowFailed"
            }
        }

        $bitmap = [System.Drawing.Image]::FromHbitmap($hBitmap)
    }
    finally {
        if ($oldObject -ne [IntPtr]::Zero -and $memoryDc -ne [IntPtr]::Zero) {
            [DesktopSmokeNative]::SelectObject($memoryDc, $oldObject) | Out-Null
        }

        if ($hBitmap -ne [IntPtr]::Zero) {
            [DesktopSmokeNative]::DeleteObject($hBitmap) | Out-Null
        }

        if ($memoryDc -ne [IntPtr]::Zero) {
            [DesktopSmokeNative]::DeleteDC($memoryDc) | Out-Null
        }

        if ($windowDc -ne [IntPtr]::Zero) {
            [DesktopSmokeNative]::ReleaseDC($Handle, $windowDc) | Out-Null
        }

        if ($bufferBitmap -ne $null) {
            $bufferBitmap.Dispose()
        }
    }

    [pscustomobject]@{
        Bitmap = $bitmap
        Method = "PrintWindow"
    }
}

function Capture-ScreenBitmap([DesktopSmokeNative+RECT]$Rect) {
    $captureWidth = $Rect.Right - $Rect.Left
    $captureHeight = $Rect.Bottom - $Rect.Top
    if ($captureWidth -le 0 -or $captureHeight -le 0) {
        throw "Invalid window rectangle: $captureWidth x $captureHeight"
    }

    $bitmap = New-Object System.Drawing.Bitmap $captureWidth, $captureHeight
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($Rect.Left, $Rect.Top, 0, 0, $bitmap.Size)
    $graphics.Dispose()
    [pscustomobject]@{
        Bitmap = $bitmap
        Method = "CopyFromScreen"
    }
}

$process = $null
$handle = [IntPtr]::Zero
$bitmap = $null
$graphics = $null

try {
    $process = Start-Process -FilePath $appFull -WorkingDirectory (Split-Path -Parent $appFull) -PassThru
    $handle = Get-MainWindowHandle $process $WaitSeconds
    $windowTitle = Get-WindowTitle $handle
    if ($windowTitle -notlike "*E-Detection*") {
        throw "Unexpected main window title '$windowTitle'. Expected an E-Detection window."
    }

    $dpi = [DesktopSmokeNative]::GetDpiForWindow($handle)
    if ($dpi -eq 0) {
        $dpi = 96
    }

    $scale = $dpi / 96.0
    $requestedPhysicalWidth = [Math]::Ceiling($Width * $scale)
    $requestedPhysicalHeight = [Math]::Ceiling($Height * $scale)
    $workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $targetX = $workArea.Left
    $targetY = $workArea.Top
    $targetWidth = [Math]::Min($requestedPhysicalWidth, $workArea.Width)
    $targetHeight = [Math]::Min($requestedPhysicalHeight, $workArea.Height)

    [DesktopSmokeNative]::ShowWindow($handle, 9) | Out-Null
    [DesktopSmokeNative]::MoveWindow($handle, $targetX, $targetY, $targetWidth, $targetHeight, $true) | Out-Null
    [DesktopSmokeNative]::SetWindowPos($handle, $HWND_TOPMOST, $targetX, $targetY, $targetWidth, $targetHeight, $SWP_SHOWWINDOW) | Out-Null
    [DesktopSmokeNative]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Seconds 2
    $foreground = [DesktopSmokeNative]::GetForegroundWindow()
    $foregroundTitle = if ($foreground -eq [IntPtr]::Zero) { "" } else { Get-WindowTitle $foreground }

    $startAction = Wait-ForAutomationName $handle "开始检测" $WaitSeconds
    $settingsAction = Wait-ForAutomationName $handle "设置" $WaitSeconds
    $eventLogTab = Wait-ForAutomationName $handle "运行记录" $WaitSeconds

    $rect = Get-VisibleWindowRect $handle

    $capture = Capture-WindowBitmap $handle $rect
    $bitmap = $capture.Bitmap
    $analysis = Test-BitmapIsUseful $bitmap
    if (!$analysis.IsUseful) {
        $bitmap.Dispose()
        $capture = Capture-ScreenBitmap $rect
        $bitmap = $capture.Bitmap
        $analysis = Test-BitmapIsUseful $bitmap
    }

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $screenshotPath = Join-Path $outputFull "main-window-$timestamp.png"
    $bitmap.Save($screenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)

    $result = [pscustomobject]@{
        AppPath = $appFull
        ProcessId = $process.Id
        WindowTitle = $windowTitle
        ForegroundTitle = $foregroundTitle
        WasForeground = ($foreground -eq $handle)
        CaptureMethod = $capture.Method
        ScreenshotPath = $screenshotPath
        Dpi = $dpi
        RequestedDipWidth = $Width
        RequestedDipHeight = $Height
        TargetWidth = $targetWidth
        TargetHeight = $targetHeight
        Width = $bitmap.Width
        Height = $bitmap.Height
        StartAction = $startAction
        SettingsAction = $settingsAction
        EventLogTab = $eventLogTab
        Samples = $analysis.Samples
        UniqueColors = $analysis.UniqueColors
        Passed = $analysis.IsUseful
        CapturedAt = (Get-Date).ToString("o")
    }

    $resultPath = Join-Path $outputFull "visual-smoke-$timestamp.json"
    $result | ConvertTo-Json | Set-Content -Path $resultPath -Encoding UTF8

    if (!$analysis.IsUseful) {
        throw "Visual smoke failed: screenshot appears blank or undersized. See $screenshotPath"
    }

    Write-Host "Visual smoke passed: $screenshotPath"
    Write-Host "Result: $resultPath"
}
finally {
    if ($process -ne $null -and !$process.HasExited -and $handle -ne [IntPtr]::Zero) {
        [DesktopSmokeNative]::SetWindowPos($handle, $HWND_NOTOPMOST, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor $SWP_NOACTIVATE) | Out-Null
    }

    if ($bitmap -ne $null) {
        $bitmap.Dispose()
    }

    if ($process -ne $null -and !$process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 2
        if (!$process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
    }
}
