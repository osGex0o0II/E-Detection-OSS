using System.Diagnostics;
using System.Runtime.InteropServices;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8001;
    private const uint WmNull = 0x0000;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmAppCommandShow = 1001;
    private const uint WmAppCommandExit = 1002;
    private const uint WmAppCommandStart = 1004;
    private const uint WmAppCommandCancel = 1005;
    private const uint WmAppCommandOpenReport = 1006;
    private const uint WmAppCommandOpenReportFolder = 1007;

    private static readonly nint MessageOnlyWindow = new(-3);

    private readonly nint _ownerWindowHandle;
    private readonly WindowProc _wndProc;
    private readonly string _windowClassName = $"EDetectionDesktopTrayWindow-{Guid.NewGuid():N}";
    private readonly nint _instanceHandle;
    private nint _callbackWindowHandle;
    private nint _idleIconHandle;
    private nint _runningIconHandle;
    private bool _showingRunningIcon;
    private ShellStatusSnapshot _status = ShellStatusSnapshot.Idle;
    private bool _canStart;
    private bool _canCancel;
    private bool _canOpenReport;
    private bool _canOpenReportFolder;
    private bool _classRegistered;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIconService(nint ownerWindowHandle, string iconPath, string? runningIconPath = null)
    {
        _ownerWindowHandle = ownerWindowHandle;
        _wndProc = WndProc;
        _instanceHandle = GetModuleHandle(null);

        if (!TryCreateCallbackWindow())
        {
            return;
        }

        _idleIconHandle = LoadIcon(iconPath);
        _runningIconHandle = !string.IsNullOrWhiteSpace(runningIconPath)
            ? LoadIcon(runningIconPath)
            : 0;
        AddIcon();
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler? StartRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler? OpenReportRequested;

    public event EventHandler? OpenReportFolderRequested;

    public bool IsAvailable => _iconAdded;

    public void UpdateStatus(ShellStatusSnapshot status)
    {
        if (!_iconAdded)
        {
            return;
        }

        _status = status;
        var desiredRunningIcon = status.IsRunning && _runningIconHandle != 0;
        var flags = NotifyIconFlags.Tip;
        if (desiredRunningIcon != _showingRunningIcon)
        {
            _showingRunningIcon = desiredRunningIcon;
            flags |= NotifyIconFlags.Icon;
        }

        var data = CreateData(flags);
        data.Tip = Trim(_status.TrayTooltip, 127);
        if ((flags & NotifyIconFlags.Icon) is NotifyIconFlags.Icon)
        {
            data.Icon = ResolveCurrentIconHandle();
        }

        ShellNotifyIcon(NotifyIconMessage.Modify, ref data);
    }

    public void UpdateCommands(
        bool canStart,
        bool canCancel,
        bool canOpenReport,
        bool canOpenReportFolder)
    {
        _canStart = canStart;
        _canCancel = canCancel;
        _canOpenReport = canOpenReport;
        _canOpenReportFolder = canOpenReportFolder;
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateData(NotifyIconFlags.Info);
        data.InfoTitle = Trim(title, 63);
        data.Info = Trim(message, 255);
        data.InfoFlags = NotifyIconInfoFlags.Info;
        ShellNotifyIcon(NotifyIconMessage.Modify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_iconAdded)
        {
            var data = CreateData(0);
            ShellNotifyIcon(NotifyIconMessage.Delete, ref data);
        }

        if (_callbackWindowHandle != 0)
        {
            DestroyWindow(_callbackWindowHandle);
        }

        if (_classRegistered)
        {
            UnregisterClass(_windowClassName, _instanceHandle);
        }

        if (_idleIconHandle != 0)
        {
            DestroyIcon(_idleIconHandle);
        }

        if (_runningIconHandle != 0)
        {
            DestroyIcon(_runningIconHandle);
        }

        _disposed = true;
    }

    private bool TryCreateCallbackWindow()
    {
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = _instanceHandle,
            ClassName = _windowClassName,
        };

        var atom = RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            Debug.WriteLine($"[TrayIconService] RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        _classRegistered = true;
        _callbackWindowHandle = CreateWindowEx(
            0,
            _windowClassName,
            "E-Detection Tray",
            0,
            0,
            0,
            0,
            0,
            MessageOnlyWindow,
            0,
            _instanceHandle,
            0);

        if (_callbackWindowHandle != 0)
        {
            return true;
        }

        Debug.WriteLine($"[TrayIconService] CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        return false;
    }

    private void AddIcon()
    {
        if (_callbackWindowHandle == 0)
        {
            return;
        }

        var data = CreateData(
            NotifyIconFlags.Message
            | NotifyIconFlags.Icon
            | NotifyIconFlags.Tip);
        data.CallbackMessage = CallbackMessage;
        data.Icon = ResolveCurrentIconHandle();
        data.Tip = "E-Detection";
        _iconAdded = ShellNotifyIcon(NotifyIconMessage.Add, ref data);

        if (!_iconAdded)
        {
            Debug.WriteLine($"[TrayIconService] Shell_NotifyIcon Add failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private NotifyIconData CreateData(NotifyIconFlags flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _callbackWindowHandle,
        Id = IconId,
        Flags = flags,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private nint ResolveCurrentIconHandle() =>
        _showingRunningIcon && _runningIconHandle != 0
            ? _runningIconHandle
            : _idleIconHandle;

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage && wParam == IconId)
        {
            var command = unchecked((uint)lParam.ToInt64());
            if (command == WmLButtonDoubleClick)
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
                return 0;
            }

            if (command == WmRButtonUp)
            {
                ShowContextMenu();
                return 0;
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MenuFlags.String | MenuFlags.Grayed, 0, Trim(_status.TrayMenuStatusText, 80));
            AppendMenu(menu, MenuFlags.Separator, 0, null);
            AppendMenu(menu, MenuFlags.String, WmAppCommandShow, "显示工作台");
            AppendMenu(menu, MenuFlags.Separator, 0, null);
            AppendCommand(menu, WmAppCommandStart, "开始检测", _canStart);
            AppendCommand(menu, WmAppCommandCancel, "取消检测", _canCancel);
            AppendMenu(menu, MenuFlags.Separator, 0, null);
            AppendCommand(menu, WmAppCommandOpenReport, "打开最新报告", _canOpenReport);
            AppendCommand(menu, WmAppCommandOpenReportFolder, "打开报告目录", _canOpenReportFolder);
            AppendMenu(menu, MenuFlags.Separator, 0, null);
            AppendMenu(menu, MenuFlags.String, WmAppCommandExit, "退出");

            var owner = _ownerWindowHandle != 0 ? _ownerWindowHandle : _callbackWindowHandle;
            SetForegroundWindow(owner);
            var command = TrackPopupMenu(
                menu,
                TrackPopupMenuFlags.ReturnCommand | TrackPopupMenuFlags.RightButton,
                point.X,
                point.Y,
                0,
                owner,
                0);
            PostMessage(owner, WmNull, 0, 0);

            if (command == WmAppCommandShow)
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (command == WmAppCommandStart)
            {
                StartRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (command == WmAppCommandCancel)
            {
                CancelRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (command == WmAppCommandOpenReport)
            {
                OpenReportRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (command == WmAppCommandOpenReportFolder)
            {
                OpenReportFolderRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (command == WmAppCommandExit)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static nint LoadIcon(string iconPath)
    {
        if (!File.Exists(iconPath))
        {
            return 0;
        }

        return LoadImage(
            0,
            iconPath,
            ImageType.Icon,
            0,
            0,
            LoadImageFlags.LoadFromFile | LoadImageFlags.DefaultSize);
    }

    private static void AppendCommand(nint menu, uint id, string text, bool enabled)
    {
        var flags = enabled
            ? MenuFlags.String
            : MenuFlags.String | MenuFlags.Grayed;
        AppendMenu(menu, flags, id, text);
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";

    private delegate nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parentWindow,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(NotifyIconMessage message, ref NotifyIconData data);

    private static bool ShellNotifyIcon(NotifyIconMessage message, ref NotifyIconData data) =>
        Shell_NotifyIcon(message, ref data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        ImageType type,
        int desiredWidth,
        int desiredHeight,
        LoadImageFlags loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(
        nint menu,
        MenuFlags flags,
        uint newItemId,
        string? newItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(
        nint menu,
        TrackPopupMenuFlags flags,
        int x,
        int y,
        int reserved,
        nint hwnd,
        nint rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public NotifyIconFlags Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public NotifyIconInfoFlags InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [Flags]
    private enum NotifyIconFlags : uint
    {
        Message = 0x1,
        Icon = 0x2,
        Tip = 0x4,
        Info = 0x10,
    }

    private enum NotifyIconMessage : uint
    {
        Add = 0,
        Modify = 1,
        Delete = 2,
    }

    private enum NotifyIconInfoFlags : uint
    {
        None = 0,
        Info = 1,
    }

    private enum ImageType : uint
    {
        Icon = 1,
    }

    [Flags]
    private enum LoadImageFlags : uint
    {
        DefaultSize = 0x40,
        LoadFromFile = 0x10,
    }

    [Flags]
    private enum MenuFlags : uint
    {
        String = 0,
        Grayed = 0x1,
        Separator = 0x800,
    }

    [Flags]
    private enum TrackPopupMenuFlags : uint
    {
        RightButton = 0x2,
        ReturnCommand = 0x100,
    }
}
