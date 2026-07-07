using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EDetection.Desktop.Models;
using EDetection.Desktop.Services;
using EDetection.Desktop.ViewModels;
using EDetection.Desktop.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using WinUIEx.Messaging;

namespace EDetection.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly TaskbarProgressService _taskbarProgress = new();
    private readonly AppInfoService _appInfo = new();
    private readonly DesktopNotificationService _desktopNotifications = new();
    private readonly SecureCredentialService _credentials = new();
    private readonly NetworkProxyService _networkProxy;
    private readonly NtfyNotificationService _ntfyNotifications;
    private readonly LlmAssistantService _llmAssistant;
    private readonly ShellResourceService _shellResources = new();
    private readonly TrayIconService _trayIcon;
    private readonly WindowMessageMonitor _windowMessageMonitor;
    private readonly nint _windowHandle;
    private readonly string _idleIconPath;
    private readonly string _runningIconPath;
    private bool _windowShowsRunningIcon;
    private bool _isExitRequested;
    private bool _isHiddenToTray;
    private bool _isSessionEnding;
    private bool _shellResourcesCleanedUp;
    private bool _isClosed;

    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const double MaxRestoredWindowWorkAreaRatio = 0.92;
    private const int MinimumVisiblePlacementWidth = 320;
    private const int MinimumVisiblePlacementHeight = 220;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        _networkProxy = new NetworkProxyService(_credentials);
        _ntfyNotifications = new NtfyNotificationService(_credentials, _networkProxy);
        _llmAssistant = new LlmAssistantService(_credentials, _networkProxy);
        ViewModel = new MainViewModel(
            new PythonBackendService(),
            new SettingsService(),
            new DetectionConfigService(),
            new DesktopDiagnosticsService(),
            new DetectionEnvironmentRepairService(),
            new ReportHistoryService(),
            new RuntimeLogService(),
            new RunTelemetryService(),
            new RunEventService(),
            new ReportDetailPreviewService(),
            new RunStateService(),
            new StartupService(),
            _credentials,
            _ntfyNotifications,
            _llmAssistant,
            _networkProxy);
        InitializeComponent();
        Shell.DataContext = ViewModel;
        _windowHandle = WindowNative.GetWindowHandle(this);
        _idleIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "app.ico");
        _runningIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "running.ico");
        _trayIcon = new TrayIconService(
            _windowHandle,
            _idleIconPath,
            _runningIconPath);
        _windowMessageMonitor = new WindowMessageMonitor(this);
        _windowMessageMonitor.WindowMessageReceived += OnWindowMessageReceived;
        _trayIcon.ShowRequested += (_, _) => ShowFromTray();
        _trayIcon.ExitRequested += (_, _) => ExitFromTray();
        _trayIcon.StartRequested += (_, _) => ExecuteTrayCommand(ViewModel.StartCommand);
        _trayIcon.CancelRequested += (_, _) => ExecuteTrayCommand(ViewModel.CancelCommand);
        _trayIcon.OpenReportRequested += (_, _) => ExecuteTrayCommand(ViewModel.OpenCurrentReportCommand);
        _trayIcon.OpenReportFolderRequested += (_, _) => ExecuteTrayCommand(ViewModel.OpenCurrentReportFolderCommand);
        _desktopNotifications.Activated += DesktopNotifications_Activated;
        _desktopNotifications.Register();
        Shell.AboutRequested += (_, _) => ShowAboutDialog();
        Activated += MainWindow_Activated;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Shell.TitleBarElement);
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Closing += AppWindow_Closing;
        ApplyWindowSizeConstraints();
        RestoreWindowPlacement();
        ViewModel.AppearanceChanged += (_, _) => ApplyAppearance();
        ViewModel.DesktopNotificationRequested += ViewModel_DesktopNotificationRequested;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Closed += (_, _) =>
        {
            _isClosed = true;
            CleanupShellResources(savePlacement: true);
        };
        ApplyAppearance();
        UpdateShellStatus();
        UpdateTrayCommands();
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (!_isClosed)
        {
            await ViewModel.RefreshPoetryStatusAsync();
            await ViewModel.CheckForUpdatesInBackgroundAsync();
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExitRequested || _isSessionEnding || !ViewModel.CloseToTrayOnClose)
        {
            return;
        }

        args.Cancel = HideToTray();
    }

    public bool PrepareForStartupHidden()
    {
        if (!_trayIcon.IsAvailable)
        {
            AppWindow.IsShownInSwitchers = true;
            ViewModel.AddRuntimeLog("托盘状态", "系统托盘不可用，启动时保持主窗口可见。");
            return false;
        }

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Move(new PointInt32(-32000, -32000));
        return true;
    }

    public void HideForStartup() => HideToTray(savePlacement: false);

    public void RestoreFromExternalActivation() => ShowFromTray();

    private void DesktopNotifications_Activated(
        object? sender,
        DesktopNotificationActivation e)
    {
        DispatcherQueue.TryEnqueue(() => HandleDesktopNotificationActivation(e));
    }

    private void OnWindowMessageReceived(object? sender, WindowMessageEventArgs e)
    {
        if (e.Message.MessageId == WmGetMinMaxInfo)
        {
            ApplyMinMaxInfo(e.Message.LParam);
            e.Handled = true;
            return;
        }

        if (e.Message.MessageId == WmQueryEndSession)
        {
            MarkSessionEnding();
            e.Result = 1;
            e.Handled = true;
            return;
        }

        if (e.Message.MessageId != WmEndSession)
        {
            return;
        }

        if (e.Message.WParam == 0)
        {
            _isSessionEnding = false;
            _isExitRequested = false;
            return;
        }

        BeginShellShutdown();
        CleanupShellResources(savePlacement: true);
        Environment.Exit(0);
    }

    private void MarkSessionEnding()
    {
        _isSessionEnding = true;
        _isExitRequested = true;
        _isHiddenToTray = false;
    }

    private void BeginShellShutdown()
    {
        MarkSessionEnding();
        ViewModel.PrepareForShellShutdown();
    }

    private void HandleDesktopNotificationActivation(DesktopNotificationActivation activation)
    {
        ShowFromTray();
        if (ViewModel.ShowCurrentRunCommand.CanExecute(null))
        {
            ViewModel.ShowCurrentRunCommand.Execute(null);
        }

        if (activation.Action == DesktopNotificationActivation.OpenReportAction
            && !string.IsNullOrWhiteSpace(activation.ReportPath)
            && File.Exists(activation.ReportPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = activation.ReportPath,
                UseShellExecute = true,
            });
        }

        if (activation.Action == DesktopNotificationActivation.OpenUpdateAction
            && !string.IsNullOrWhiteSpace(activation.ActionUrl))
        {
            TryOpenExternalUri(activation.ActionUrl);
        }
    }

    private static void TryOpenExternalUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or Win32Exception
                                   or ArgumentException
                                   or NotSupportedException)
        {
        }
    }

    private async void ViewModel_DesktopNotificationRequested(
        object? sender,
        DesktopNotificationRequest e)
    {
        var shown = _desktopNotifications.Show(e);
        ViewModel.ReportDesktopNotificationResult(e, shown);
        if (!e.ForwardToRemoteNotifications)
        {
            return;
        }

        try
        {
            if (await _ntfyNotifications.TrySendAsync(e, ViewModel))
            {
                ViewModel.AddRuntimeLog("消息推送", "已发送 ntfy 推送。");
            }
        }
        catch (Exception ex)
        {
            ViewModel.AddRuntimeLog("推送提醒", $"ntfy 推送失败: {ex.Message}");
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShellStatus))
        {
            UpdateShellStatus();
            UpdateTrayCommands();
        }

        if (e.PropertyName is nameof(MainViewModel.ReportPath)
            or nameof(MainViewModel.SelectedReport))
        {
            UpdateTrayCommands();
        }
    }

    private void UpdateShellStatus()
    {
        var status = ViewModel.ShellStatus;
        _taskbarProgress.Update(_windowHandle, status);
        _trayIcon.UpdateStatus(status);
        UpdateWindowIcon(status.IsRunning);
    }

    private void UpdateWindowIcon(bool isRunning)
    {
        if (_windowShowsRunningIcon == isRunning)
        {
            return;
        }

        var iconPath = isRunning && File.Exists(_runningIconPath)
            ? _runningIconPath
            : _idleIconPath;
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
            _windowShowsRunningIcon = isRunning;
        }
    }

    private void UpdateTrayCommands() =>
        _trayIcon.UpdateCommands(
            ViewModel.StartCommand.CanExecute(null),
            ViewModel.CancelCommand.CanExecute(null),
            ViewModel.OpenCurrentReportCommand.CanExecute(null),
            ViewModel.OpenCurrentReportFolderCommand.CanExecute(null));

    private static void ExecuteTrayCommand(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private async void ShowAboutDialog()
    {
        var info = _appInfo.GetInfo();
        var content = new StackPanel
        {
            Spacing = 8,
        };
        content.Children.Add(new TextBlock
        {
            Text = info.Description,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"版本: {info.Version}",
        });
        content.Children.Add(new TextBlock
        {
            Text = $"运行时: {info.Runtime}",
        });
        content.Children.Add(new TextBlock
        {
            Text = $"架构: {info.ProcessArchitecture}",
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = info.ProductName,
            Content = content,
            PrimaryButtonText = "软件更新",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await dialog.ShowAsync();
        if (result is ContentDialogResult.Primary)
        {
            Shell.OpenSettingsSection("UpdatesSection");
        }
    }

    private bool HideToTray(bool savePlacement = true)
    {
        if (_isHiddenToTray)
        {
            return true;
        }

        if (!_trayIcon.IsAvailable)
        {
            _isHiddenToTray = false;
            AppWindow.IsShownInSwitchers = true;
            RestoreWindowPlacement();
            AppWindow.Show();
            ShowWindow(_windowHandle, ShowWindowCommand.Restore);
            SetForegroundWindow(_windowHandle);
            ViewModel.AddRuntimeLog("托盘状态", "系统托盘不可用，已保持主窗口可见。");
            return true;
        }

        if (savePlacement)
        {
            SaveWindowPlacement();
        }

        _isHiddenToTray = true;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Hide();
        ShowWindow(_windowHandle, ShowWindowCommand.Hide);
        _shellResources.ReleaseAfterHideToTray();
        return true;
    }

    private void ShowFromTray()
    {
        _isHiddenToTray = false;
        AppWindow.IsShownInSwitchers = true;
        RestoreWindowPlacement();

        AppWindow.Show();
        ShowWindow(_windowHandle, ShowWindowCommand.Show);
        ShowWindow(_windowHandle, ShowWindowCommand.Restore);
        SetForegroundWindow(_windowHandle);
    }

    private void ExitFromTray()
    {
        _isExitRequested = true;
        ViewModel.PrepareForShellShutdown();
        Close();
    }

    private void CleanupShellResources(bool savePlacement)
    {
        if (_shellResourcesCleanedUp)
        {
            return;
        }

        _shellResourcesCleanedUp = true;
        ViewModel.PrepareForShellShutdown();

        if (savePlacement)
        {
            SaveWindowPlacement();
        }

        _taskbarProgress.Update(_windowHandle, TaskbarProgressKind.None, 0);
        _desktopNotifications.Activated -= DesktopNotifications_Activated;
        _desktopNotifications.Unregister();
        _windowMessageMonitor.WindowMessageReceived -= OnWindowMessageReceived;
        _windowMessageMonitor.Dispose();
        _trayIcon.Dispose();
    }

    private void RestoreWindowPlacement()
    {
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var minSize = GetMinWindowSizePixels(workArea);
        var defaultSize = GetDefaultWindowSizePixels(workArea, minSize);
        var maxRestoredSize = GetMaxRestoredWindowSizePixels(workArea, minSize);
        var requestedWidth = ViewModel.WindowWidth > 0
            ? ViewModel.WindowWidth
            : defaultSize.Width;
        var requestedHeight = ViewModel.WindowHeight > 0
            ? ViewModel.WindowHeight
            : defaultSize.Height;
        var savedSizeLooksLikeWorkArea = requestedWidth >= workArea.Width * 0.96
            || requestedHeight >= workArea.Height * 0.96;
        var width = savedSizeLooksLikeWorkArea && !ViewModel.IsWindowMaximized
            ? defaultSize.Width
            : Math.Clamp(requestedWidth, minSize.Width, maxRestoredSize.Width);
        var height = savedSizeLooksLikeWorkArea && !ViewModel.IsWindowMaximized
            ? defaultSize.Height
            : Math.Clamp(requestedHeight, minSize.Height, maxRestoredSize.Height);
        if (IsSavedPlacementUsable(workArea, ViewModel.WindowLeft, ViewModel.WindowTop, width, height))
        {
            var left = Math.Clamp(ViewModel.WindowLeft, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - width));
            var top = Math.Clamp(ViewModel.WindowTop, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - height));
            AppWindow.MoveAndResize(new RectInt32(
                left,
                top,
                width,
                height));
        }
        else
        {
            AppWindow.MoveAndResize(GetVisibleDefaultBounds(workArea, width, height));
        }

        if (ViewModel.IsWindowMaximized
            && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private static bool IsSavedPlacementUsable(RectInt32 workArea, int left, int top, int width, int height)
    {
        if (left <= -30000 || top <= -30000 || width <= 0 || height <= 0)
        {
            return false;
        }

        var right = left + width;
        var bottom = top + height;
        var visibleLeft = Math.Max(left, workArea.X);
        var visibleTop = Math.Max(top, workArea.Y);
        var visibleRight = Math.Min(right, workArea.X + workArea.Width);
        var visibleBottom = Math.Min(bottom, workArea.Y + workArea.Height);
        return visibleRight - visibleLeft >= Math.Min(MinimumVisiblePlacementWidth, width)
            && visibleBottom - visibleTop >= Math.Min(MinimumVisiblePlacementHeight, height);
    }

    private static RectInt32 GetVisibleDefaultBounds(RectInt32 workArea, int width, int height)
    {
        var safeWidth = Math.Min(width, Math.Max(1, workArea.Width));
        var safeHeight = Math.Min(height, Math.Max(1, workArea.Height));
        var left = workArea.X + Math.Max(0, (workArea.Width - safeWidth) / 2);
        var top = workArea.Y + Math.Max(0, (workArea.Height - safeHeight) / 3);
        return new RectInt32(left, top, safeWidth, safeHeight);
    }

    private void SaveWindowPlacement()
    {
        if (_isHiddenToTray
            || AppWindow.Position.X <= -30000
            || AppWindow.Position.Y <= -30000)
        {
            return;
        }

        var isMaximized = AppWindow.Presenter is OverlappedPresenter
        {
            State: OverlappedPresenterState.Maximized,
        };
        ViewModel.SaveWindowPlacement(
            AppWindow.Position.X,
            AppWindow.Position.Y,
            AppWindow.Size.Width,
            AppWindow.Size.Height,
            isMaximized);
    }

    private void ApplyAppearance()
    {
        Shell.RootElement.RequestedTheme = ViewModel.ThemeMode switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppWindow.TitleBar.PreferredTheme = ViewModel.ThemeMode switch
        {
            "Light" => TitleBarTheme.Light,
            "Dark" => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
        SystemBackdrop = ViewModel.BackdropMode switch
        {
            "Acrylic" => new DesktopAcrylicBackdrop(),
            "None" => null,
            _ => new MicaBackdrop(),
        };
    }

    private void ApplyMinMaxInfo(nint lParam)
    {
        var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var minSize = GetMinWindowSizePixels(workArea);
        minMaxInfo.ptMinTrackSize.X = minSize.Width;
        minMaxInfo.ptMinTrackSize.Y = minSize.Height;
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
    }

    private void ApplyWindowSizeConstraints()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var minSize = GetMinWindowSizePixels(workArea);
        presenter.PreferredMinimumWidth = minSize.Width;
        presenter.PreferredMinimumHeight = minSize.Height;
    }

    private SizeInt32 GetDefaultWindowSizePixels(RectInt32 workArea, SizeInt32 minSize)
    {
        var width = Math.Max(minSize.Width, DipToPixels(MainViewModel.DefaultWindowWidthDip));
        var height = Math.Max(minSize.Height, DipToPixels(MainViewModel.DefaultWindowHeightDip));
        var maxSize = GetMaxRestoredWindowSizePixels(workArea, minSize);
        return new SizeInt32(
            Math.Min(width, maxSize.Width),
            Math.Min(height, maxSize.Height));
    }

    private SizeInt32 GetMaxRestoredWindowSizePixels(RectInt32 workArea, SizeInt32 minSize)
    {
        var width = Math.Max(minSize.Width, (int)Math.Floor(workArea.Width * MaxRestoredWindowWorkAreaRatio));
        var height = Math.Max(minSize.Height, (int)Math.Floor(workArea.Height * MaxRestoredWindowWorkAreaRatio));
        return new SizeInt32(
            Math.Min(width, Math.Max(minSize.Width, workArea.Width)),
            Math.Min(height, Math.Max(minSize.Height, workArea.Height)));
    }

    private SizeInt32 GetMinWindowSizePixels(RectInt32 workArea)
    {
        var width = DipToPixels(MainViewModel.MinWindowWidthDip);
        var height = DipToPixels(MainViewModel.MinWindowHeightDip);
        return new SizeInt32(
            Math.Min(width, Math.Max(800, workArea.Width)),
            Math.Min(height, Math.Max(560, workArea.Height)));
    }

    private int DipToPixels(double value) =>
        Math.Max(1, (int)Math.Ceiling(value * GetWindowScale()));

    private double GetWindowScale()
    {
        var dpi = GetDpiForWindow(_windowHandle);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, ShowWindowCommand command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private enum ShowWindowCommand
    {
        Hide = 0,
        Show = 5,
        Restore = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
