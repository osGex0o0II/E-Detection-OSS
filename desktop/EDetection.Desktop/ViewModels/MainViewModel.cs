using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDetection.Desktop.Models;
using EDetection.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace EDetection.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public const int MinWindowWidthDip = 1100;
    public const int MinWindowHeightDip = 740;
    public const int DefaultWindowWidthDip = 1240;
    public const int DefaultWindowHeightDip = 760;
    private const string WindowsNotificationSettingsUri = "ms-settings:notifications";
    private const string WindowsProxySettingsUri = "ms-settings:network-proxy";
    private const string WindowsStartupAppsSettingsUri = "ms-settings:startupapps";

    private readonly PythonBackendService _backend;
    private readonly SettingsService _settings;
    private readonly DetectionConfigService _detectionConfig;
    private readonly DesktopDiagnosticsService _diagnostics;
    private readonly RunEventService _runEvents;
    private readonly ReportDetailPreviewService _detailPreview;
    private readonly RunStateService _runState;
    private readonly StartupService _startup;
    private readonly SecureCredentialService _credentials;
    private readonly NtfyNotificationService _ntfyNotifications;
    private readonly LlmAssistantService _llmAssistant;
    private readonly NetworkProxyService _networkProxy;
    private readonly UpdateCheckService _updateCheck;
    private readonly PoetryStatusService _poetryStatus;
    private readonly Stopwatch _runStopwatch = new();
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _cancelPromptCts;
    private bool _settingsLoaded;
    private bool _desktopNotificationSent;
    private bool _syncingStartupPreference;
    private bool _isShellShutdownRequested;
    private bool _suppressConfigPathReload;
    private string _lastNotifiedUpdateVersion = "";

    public MainViewModel(
        PythonBackendService backend,
        SettingsService settings,
        DetectionConfigService detectionConfig,
        DesktopDiagnosticsService diagnostics,
        ReportHistoryService reportHistory,
        RuntimeLogService runtimeLogs,
        RunTelemetryService runTelemetry,
        RunEventService runEvents,
        ReportDetailPreviewService detailPreview,
        RunStateService runState,
        StartupService startup,
        SecureCredentialService? credentials = null,
        NtfyNotificationService? ntfyNotifications = null,
        LlmAssistantService? llmAssistant = null,
        NetworkProxyService? networkProxy = null,
        UpdateCheckService? updateCheck = null,
        PoetryStatusService? poetryStatus = null,
        DesktopHealthService? desktopHealth = null)
    {
        _backend = backend;
        _settings = settings;
        _detectionConfig = detectionConfig;
        _diagnostics = diagnostics;
        _runEvents = runEvents;
        _detailPreview = detailPreview;
        _runState = runState;
        _startup = startup;
        _credentials = credentials ?? new SecureCredentialService();
        _ntfyNotifications = ntfyNotifications ?? new NtfyNotificationService(_credentials);
        _llmAssistant = llmAssistant ?? new LlmAssistantService(_credentials);
        _networkProxy = networkProxy ?? new NetworkProxyService(_credentials);
        _updateCheck = updateCheck ?? new UpdateCheckService();
        _poetryStatus = poetryStatus ?? new PoetryStatusService();
        Diagnostics = new DiagnosticsViewModel();
        RunTelemetry = new RunTelemetryViewModel(runTelemetry);
        RuntimeLogs = new RuntimeLogViewModel(runtimeLogs);
        ReportHistory = new ReportHistoryViewModel(reportHistory);
        DesktopHealth = new DesktopHealthViewModel(
            desktopHealth ?? new DesktopHealthService(),
            _startup,
            _settings,
            () => PythonExecutable);
        var saved = _settings.Load();
        InputDirectory = saved.InputDirectory;
        OutputDirectory = saved.OutputDirectory;
        ConfigPath = _detectionConfig.EnsureUserConfig(saved.ConfigPath);
        ApplyDetectionConfig(_detectionConfig.Load(ConfigPath));
        PythonExecutable = string.IsNullOrWhiteSpace(saved.PythonExecutable)
            ? "python"
            : saved.PythonExecutable;
        WriteReport = saved.WriteReport;
        CloseToTrayOnClose = saved.CloseToTrayOnClose;
        StartMinimizedToTray = saved.StartMinimizedToTray;
        var startupStatus = _startup.GetStatus();
        AutoStartOnSignIn = startupStatus.IsEnabled;
        UpdateStartupIntegrationStatus(startupStatus);
        EnableDesktopNotifications = saved.EnableDesktopNotifications;
        EnableLlmAssistant = saved.EnableLlmAssistant;
        LlmEndpoint = saved.LlmEndpoint;
        LlmModel = saved.LlmModel;
        UseProxyForLlm = saved.UseProxyForLlm;
        EnableNtfyNotifications = saved.EnableNtfyNotifications;
        NtfyServerUrl = saved.NtfyServerUrl;
        NtfyTopic = saved.NtfyTopic;
        SelectedNtfyPriorityIndex = Math.Clamp(saved.SelectedNtfyPriorityIndex, 0, 4);
        UseProxyForNotifications = saved.UseProxyForNotifications;
        EnableNetworkProxy = saved.EnableNetworkProxy;
        ProxyAddress = saved.ProxyAddress;
        ProxyRequiresAuthentication = saved.ProxyRequiresAuthentication;
        ProxyUserName = saved.ProxyUserName;
        EnableUpdateChecks = saved.EnableUpdateChecks;
        UseProxyForUpdates = saved.UseProxyForUpdates;
        SelectedUpdateChannelIndex = Math.Clamp(saved.SelectedUpdateChannelIndex, 0, 2);
        UpdateFeedUrl = saved.UpdateFeedUrl;
        UpdateStatusText = $"当前版本 {new AppInfoService().GetInfo().Version}";
        RefreshSecureCredentialStatus();
        EnableGlobalHotkeys = false;
        SelectedQuickActionsShortcutIndex = 2;
        EnableQuickActionsShortcut = false;
        RuntimeLogs.SelectedRetentionIndex = Math.Clamp(saved.SelectedLogRetentionIndex, 0, 3);
        SelectedThemeIndex = Math.Clamp(saved.SelectedThemeIndex, 0, 2);
        SelectedBackdropIndex = Math.Clamp(saved.SelectedBackdropIndex, 0, 2);
        EnablePoetryStatus = saved.EnablePoetryStatus;
        PoetryServiceUrl = string.IsNullOrWhiteSpace(saved.PoetryServiceUrl)
            ? "https://poetry.palemoky.com/"
            : saved.PoetryServiceUrl;
        SelectedPoetryLanguageIndex = Math.Clamp(saved.SelectedPoetryLanguageIndex, 0, 1);
        WindowLeft = saved.WindowLeft;
        WindowTop = saved.WindowTop;
        WindowWidth = saved.WindowWidth;
        WindowHeight = saved.WindowHeight;
        IsWindowMaximized = saved.IsWindowMaximized;
        ReportHistory.Load(saved.RecentReports, saved.SelectedRecentReportLimitIndex);
        ReportHistory.PropertyChanged += ReportHistory_PropertyChanged;
        Diagnostics.PropertyChanged += Diagnostics_PropertyChanged;
        RunTelemetry.PropertyChanged += RunTelemetry_PropertyChanged;
        RuntimeLogs.PropertyChanged += RuntimeLogs_PropertyChanged;
        RefreshLocalDiagnostics();
        RefreshDesktopHealth();

        _settingsLoaded = true;
    }

    public ObservableCollection<DetectionLogItem> LogItems => RuntimeLogs.LogItems;

    public ObservableCollection<DetectionLogItem> FilteredLogItems => RuntimeLogs.FilteredLogItems;

    public ObservableCollection<string> LogKindFilters => RuntimeLogs.LogKindFilters;

    public ObservableCollection<RecentReport> RecentReports => ReportHistory.RecentReports;

    public ObservableCollection<RecentReport> FilteredRecentReports => ReportHistory.FilteredRecentReports;

    public ObservableCollection<ReportDeviceSummary> HighRiskDevices { get; } = [];

    public ObservableCollection<ReportIssueType> TopIssueTypes { get; } = [];

    public ObservableCollection<ReportDetailPreview> DetailPreview { get; } = [];

    public ObservableCollection<ReportDetailPreview> FilteredDetailPreview { get; } = [];

    public ObservableCollection<string> DetailIssueTypeFilters { get; } = ["全部类型"];

    public bool HasSelectedDetail => SelectedDetail is not null;

    public bool IsIdle => !IsRunning;

    public bool CanEditRunConfiguration => !IsRunning;

    public Visibility CancelCommandVisibility => IsRunning
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility StatusProgressVisibility => IsRunning
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RunTelemetryVisibility =>
        SelectedReport is null
        && (IsRunning
            || TotalFiles > 0
            || ProcessedFiles > 0
            || !string.IsNullOrWhiteSpace(CurrentFileText))
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility CancelConfirmationVisibility => IsCancelConfirmationPending
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string CancelButtonText => IsCancelConfirmationPending ? "确认取消" : "取消";

    public string CancelConfirmationText =>
        "再次按 Esc 或点击“确认取消”停止检测。当前检测任务会被终止。";

    public Visibility CompletionActionsVisibility => ShouldShowCompletionActions
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string CompletionActionTitleText => AnomalyRecords > 0
        ? $"检测完成，发现 {AnomalyRecords} 条异常"
        : "检测完成，未发现异常";

    public string CompletionActionBodyText
    {
        get
        {
            var reportText = string.IsNullOrWhiteSpace(ReportPath)
                ? "未生成 Excel 报告"
                : $"报告已生成: {Path.GetFileName(ReportPath)}";
            return $"{ProcessedSummary} 个文件 · 异常文件 {AnomalyFiles} · 跳过 {SkippedFiles} · {reportText}";
        }
    }

    private bool ShouldShowCompletionActions =>
        !IsRunning && SelectedReport is null && StatusText == "检测完成";

    public string ProcessedSummary => TotalFiles > 0
        ? $"{ProcessedFiles}/{TotalFiles}"
        : ProcessedFiles.ToString();

    public string ProgressText => $"{ProgressPercent:0}%";

    public string SensorOverviewText =>
        $"离线 {SensorOfflineDevices} · 传感器故障 {SensorFaultRows} · 未配置 {SensorMissingRows} · 跳过 {SensorSkippedRows}";

    public string DetailPreviewStatusText => DetailPreviewTotalCount > 0
        ? $"{FilteredDetailPreview.Count}/{DetailPreviewTotalCount} 条预览"
        : "暂无异常明细";

    public string DetailSortStatusText => SelectedDetailSortKey switch
    {
        "severity" => "按等级排序",
        "device" => "按设备排序",
        "time" => "按时间排序",
        "issue" => "按异常排序",
        "value" => "按异常值排序",
        _ => "默认顺序",
    };

    public string LogStatusText => RuntimeLogs.StatusText;

    public int LogRetentionLimit => RuntimeLogs.RetentionLimit;

    public string LogRetentionText => RuntimeLogs.RetentionText;

    public int RecentReportLimit => ReportHistory.RecentReportLimit;

    public string RecentReportLimitText => ReportHistory.RecentReportLimitText;

    public int WorkbenchReportDeviceCount => SelectedReport?.DeviceCount ?? ReportDeviceCount;

    public IEnumerable<ReportDeviceSummary> WorkbenchHighRiskDevices =>
        SelectedReport is { } report ? report.HighRiskDevices : HighRiskDevices;

    public IEnumerable<ReportIssueType> WorkbenchTopIssueTypes =>
        SelectedReport is { } report ? report.TopIssueTypes : TopIssueTypes;

    public string WorkbenchSensorOverviewText => SelectedReport is { } report
        ? SensorOverviewTextFrom(report.SensorOverview)
        : SensorOverviewText;

    public int WorkbenchDetailPreviewTotalCount =>
        SelectedReport?.DetailPreviewCount ?? DetailPreviewTotalCount;

    public IEnumerable<ReportDetailPreview> WorkbenchFilteredDetailPreview =>
        SelectedReport is { } report
            ? _detailPreview.Filter(
                report.DetailPreview,
                DetailIssueTypeFilters,
                BuildDetailFilterState())
            : FilteredDetailPreview;

    public string WorkbenchDetailPreviewStatusText => WorkbenchDetailPreviewTotalCount > 0
        ? $"{WorkbenchFilteredDetailPreview.Count()}/{WorkbenchDetailPreviewTotalCount} 条预览"
        : "暂无异常明细";

    public string ReportHistoryStatusText => ReportHistory.StatusText;

    public bool IsShowingReportSnapshot => SelectedReport is not null;

    public string WorkbenchModeText => IsShowingReportSnapshot ? "历史报告快照" : "当前检测状态";

    public string WorkbenchStatusText => SelectedReport is { } report
        ? report.FileName
        : StatusText;

    public string WorkbenchProgressText => SelectedReport is { TotalFiles: > 0 }
        ? "历史"
        : ProgressText;

    public double WorkbenchProgressPercent => SelectedReport is { TotalFiles: > 0 }
        ? 100
        : ProgressPercent;

    public string WorkbenchProcessedSummary => SelectedReport is { } report
        ? (report.TotalFiles > 0 ? $"{report.ProcessedFiles}/{report.TotalFiles}" : "-")
        : ProcessedSummary;

    public int WorkbenchAnomalyFiles => SelectedReport?.AnomalyFiles ?? AnomalyFiles;

    public int WorkbenchAnomalyRecords => SelectedReport?.AnomalyRecords ?? AnomalyRecords;

    public int WorkbenchSkippedFiles => SelectedReport?.SkippedFiles ?? SkippedFiles;

    public string WorkbenchReportPath => SelectedReport?.Path ?? ReportPath;

    public string WorkbenchReportPathText => string.IsNullOrWhiteSpace(WorkbenchReportPath)
        ? "尚未生成报告"
        : WorkbenchReportPath;

    public string WorkbenchReportButtonText => IsShowingReportSnapshot ? "打开选中报告" : "打开最新报告";

    public string WorkbenchReportFolderButtonText => IsShowingReportSnapshot ? "选中报告目录" : "打开所在目录";

    public Visibility ReportActionsVisibility => string.IsNullOrWhiteSpace(WorkbenchReportPath)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility WorkbenchActivityVisibility =>
        IsRunning
        || IsCancelConfirmationPending
        || ShouldShowCompletionActions
        || HasActionableFailure
        || IsShowingReportSnapshot
        || !string.IsNullOrWhiteSpace(WorkbenchReportPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string WorkbenchActivityTitleText => IsShowingReportSnapshot
        ? "报告快照"
        : IsRunning
            ? "运行详情"
            : HasActionableFailure
                ? "处理建议"
                : ShouldShowCompletionActions
                    ? "完成摘要"
                    : "报告操作";

    public string WorkbenchContextText => SelectedReport is { } report
        ? $"{report.SourceText} · {report.RunMetaText}"
        : IsRunning && !string.IsNullOrWhiteSpace(ActiveRunSummaryText)
            ? ActiveRunSummaryText
            : !string.IsNullOrWhiteSpace(ReportPath)
                ? $"最新运行结果 · {Path.GetFileName(ReportPath)}"
                : "等待检测任务";

    public string WorkbenchStatusBadgeText => SelectedReport is not null
        ? ReportHistoryStatusText
        : IsRunning
            ? CurrentFileText
            : !string.IsNullOrWhiteSpace(ReportPath)
                ? "报告已生成"
                : "待开始";

    public Visibility WorkbenchStatusBadgeVisibility =>
        SelectedReport is not null
        || IsRunning
        || !string.IsNullOrWhiteSpace(ReportPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string RunSetupReadinessText
    {
        get
        {
            if (IsRunning)
            {
                return "正在检测";
            }

            if (StatusText == "无法开始检测")
            {
                return "需要处理后再开始";
            }

            if (StatusText == "检测失败")
            {
                return "检测未完成";
            }

            if (!string.IsNullOrWhiteSpace(ReportPath))
            {
                return "检测完成";
            }

            if (!IsLocalInputReady())
            {
                return "选择检测数据";
            }

            if (!IsConfigReady())
            {
                return "确认阈值设置";
            }

            return "准备开始检测";
        }
    }

    public string RunSetupReadinessDetailText
    {
        get
        {
            if (IsRunning)
            {
                return string.IsNullOrWhiteSpace(CurrentFileText)
                    ? "检测正在运行，完成后可在右侧查看结果。"
                    : CurrentFileText;
            }

            if (StatusText == "无法开始检测" || StatusText == "检测失败")
            {
                return LastFailureText == "暂无失败"
                    ? DiagnosticActionText
                    : LastFailureText;
            }

            if (!string.IsNullOrWhiteSpace(ReportPath))
            {
                return $"最新报告已生成: {Path.GetFileName(ReportPath)}";
            }

            if (!IsLocalInputReady())
            {
                return "选择包含 CSV 文件的目录后即可开始。";
            }

            if (!IsConfigReady())
            {
                return "请在设置中确认阈值设置和检测规则。";
            }

            return "选择完成后可直接开始，必要检查会自动执行。";
        }
    }

    public ShellStatusSnapshot ShellStatus => new(
        StatusText,
        IsRunning,
        TaskbarProgressKind,
        TaskbarProgressPercent);

    public Visibility ActiveRunSummaryVisibility => string.IsNullOrWhiteSpace(ActiveRunSummaryText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility FailureActionsVisibility => HasActionableFailure
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string FailureActionTitleText => StatusText == "无法开始检测"
        ? "运行前检查未通过"
        : "检测失败";

    public string FailureActionBodyText => LastFailureText == "暂无失败"
        ? DiagnosticActionText
        : LastFailureText;

    public Visibility SnapshotActionsVisibility => IsShowingReportSnapshot
        ? Visibility.Visible
        : Visibility.Collapsed;

    private bool HasActionableFailure =>
        !IsRunning && (StatusText == "检测失败" || StatusText == "无法开始检测");

    public Visibility FirstRunGuideVisibility =>
        ShouldShowFirstRunGuide ? Visibility.Visible : Visibility.Collapsed;

    public string FirstRunGuideTitleText => StatusText == "无法开始检测"
        ? "运行前检查未通过"
        : IsLocalInputReady()
            ? "准备开始检测"
            : "选择检测数据";

    public string FirstRunGuideSubtitleText
    {
        get
        {
            if (StatusText == "无法开始检测")
            {
                return DiagnosticActionText;
            }

            if (!IsLocalInputReady())
            {
                return "左侧选择 CSV 根目录后即可进行就绪检查。";
            }

            if (!IsConfigReady())
            {
                return "确认阈值设置和检测规则后检查状态。";
            }

            if (BackendDiagnosticText.Contains("可导入", StringComparison.Ordinal))
            {
                return "运行环境就绪，可以开始检测。";
            }

            return DiagnosticActionText;
        }
    }

    public string FirstRunInputStepText => IsLocalInputReady()
        ? InputDiagnosticText
        : "未选择可用的 CSV 根目录";

    public string FirstRunConfigStepText => ConfigDiagnosticText;

    public string FirstRunPythonStepText => BackendDiagnosticText.Contains("可导入", StringComparison.Ordinal)
        ? BackendDiagnosticText
        : PythonDiagnosticText;

    public string FirstRunOutputStepText => OutputDiagnosticText;

    private bool ShouldShowFirstRunGuide =>
        !IsRunning
        && SelectedReport is null
        && TotalFiles == 0
        && string.IsNullOrWhiteSpace(ReportPath);

    public string ThemeMode => SelectedThemeIndex switch
    {
        1 => "Light",
        2 => "Dark",
        _ => "Default",
    };

    public string BackdropMode => SelectedBackdropIndex switch
    {
        1 => "Acrylic",
        2 => "None",
        _ => "Mica",
    };

    public event EventHandler? AppearanceChanged;

    public event EventHandler<DesktopNotificationRequest>? DesktopNotificationRequested;

    public string SettingsStoreStatusText => _settings.StoreStatusText;

    public DesktopHealthViewModel DesktopHealth { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public RunTelemetryViewModel RunTelemetry { get; }

    public RuntimeLogViewModel RuntimeLogs { get; }

    public ReportHistoryViewModel ReportHistory { get; }

    [ObservableProperty]
    public partial bool EnableGlobalHotkeys { get; set; }

    [ObservableProperty]
    public partial bool EnableQuickActionsShortcut { get; set; }

    [ObservableProperty]
    public partial int SelectedQuickActionsShortcutIndex { get; set; } = 2;

    public void PrepareForShellShutdown()
    {
        _isShellShutdownRequested = true;
        _cancelPromptCts?.Cancel();
        _runCts?.Cancel();

        if (_runStopwatch.IsRunning)
        {
            StopRunTelemetry();
        }

        if (IsRunning)
        {
            StatusText = "正在退出...";
            SetTaskbarProgress(TaskbarProgressKind.Paused, ProgressPercent);
        }

        SaveSettings();
    }

    public int WindowLeft { get; private set; } = -1;

    public int WindowTop { get; private set; } = -1;

    public int WindowWidth { get; private set; }

    public int WindowHeight { get; private set; }

    public bool IsWindowMaximized { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShellStatus))]
    public partial TaskbarProgressKind TaskbarProgressKind { get; set; } = TaskbarProgressKind.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShellStatus))]
    public partial double TaskbarProgressPercent { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessText))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessDetailText))]
    public partial string InputDirectory { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessDetailText))]
    public partial string OutputDirectory { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessText))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessDetailText))]
    public partial string ConfigPath { get; set; } = "config.json";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string PythonExecutable { get; set; } = "python";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool WriteReport { get; set; } = true;

    [ObservableProperty]
    public partial bool CloseToTrayOnClose { get; set; }

    [ObservableProperty]
    public partial bool StartMinimizedToTray { get; set; }

    [ObservableProperty]
    public partial bool AutoStartOnSignIn { get; set; }

    [ObservableProperty]
    public partial string StartupIntegrationStatusText { get; set; } = "登录后自动启动未启用";

    [ObservableProperty]
    public partial bool EnableDesktopNotifications { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableLlmAssistant { get; set; }

    [ObservableProperty]
    public partial string LlmEndpoint { get; set; } = "";

    [ObservableProperty]
    public partial string LlmModel { get; set; } = "";

    [ObservableProperty]
    public partial bool UseProxyForLlm { get; set; }

    [ObservableProperty]
    public partial string PendingLlmApiKey { get; set; } = "";

    [ObservableProperty]
    public partial string LlmApiKeyStatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestLlmConnectionCommand))]
    public partial bool IsTestingLlmConnection { get; set; }

    [ObservableProperty]
    public partial bool EnableNtfyNotifications { get; set; }

    [ObservableProperty]
    public partial string NtfyServerUrl { get; set; } = "https://ntfy.sh";

    [ObservableProperty]
    public partial string NtfyTopic { get; set; } = "";

    [ObservableProperty]
    public partial int SelectedNtfyPriorityIndex { get; set; } = 2;

    [ObservableProperty]
    public partial bool UseProxyForNotifications { get; set; }

    [ObservableProperty]
    public partial string PendingNtfyToken { get; set; } = "";

    [ObservableProperty]
    public partial string NtfyTokenStatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendTestNtfyNotificationCommand))]
    public partial bool IsSendingTestNtfyNotification { get; set; }

    [ObservableProperty]
    public partial bool EnableNetworkProxy { get; set; }

    [ObservableProperty]
    public partial string ProxyAddress { get; set; } = "";

    [ObservableProperty]
    public partial bool ProxyRequiresAuthentication { get; set; }

    [ObservableProperty]
    public partial string ProxyUserName { get; set; } = "";

    [ObservableProperty]
    public partial string PendingProxyPassword { get; set; } = "";

    [ObservableProperty]
    public partial string ProxyPasswordStatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestNetworkProxyCommand))]
    public partial bool IsTestingNetworkProxy { get; set; }

    [ObservableProperty]
    public partial bool EnableUpdateChecks { get; set; } = true;

    [ObservableProperty]
    public partial bool UseProxyForUpdates { get; set; }

    [ObservableProperty]
    public partial int SelectedUpdateChannelIndex { get; set; }

    [ObservableProperty]
    public partial string UpdateFeedUrl { get; set; } = "https://github.com/osGex0o0II/E-Detection-OSS/releases/latest";

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = "尚未检查更新";

    [ObservableProperty]
    public partial string LatestReleaseUrl { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    public partial bool IsCheckingForUpdates { get; set; }

    public bool ShouldCheckForUpdatesOnStartup =>
        EnableUpdateChecks && SelectedUpdateChannelIndex != 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShellStatus))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanEditRunConfiguration))]
    [NotifyPropertyChangedFor(nameof(CancelCommandVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusProgressVisibility))]
    [NotifyPropertyChangedFor(nameof(RunTelemetryVisibility))]
    [NotifyPropertyChangedFor(nameof(CompletionActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(FirstRunGuideVisibility))]
    [NotifyPropertyChangedFor(nameof(FailureActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(WorkbenchActivityVisibility))]
    [NotifyPropertyChangedFor(nameof(WorkbenchContextText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchStatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchStatusBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessText))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessDetailText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseSelectedReportDirectoriesCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCompletionSummaryCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShellStatus))]
    [NotifyPropertyChangedFor(nameof(WorkbenchStatusText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchStatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(FirstRunGuideTitleText))]
    [NotifyPropertyChangedFor(nameof(FirstRunGuideSubtitleText))]
    [NotifyPropertyChangedFor(nameof(FailureActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(WorkbenchActivityVisibility))]
    [NotifyPropertyChangedFor(nameof(FailureActionTitleText))]
    [NotifyPropertyChangedFor(nameof(FailureActionBodyText))]
    [NotifyPropertyChangedFor(nameof(CompletionActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(WorkbenchActivityTitleText))]
    [NotifyPropertyChangedFor(nameof(CompletionActionTitleText))]
    [NotifyPropertyChangedFor(nameof(CompletionActionBodyText))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessText))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessDetailText))]
    [NotifyCanExecuteChangedFor(nameof(CopyCompletionSummaryCommand))]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProcessedSummary))]
    [NotifyPropertyChangedFor(nameof(WorkbenchProcessedSummary))]
    [NotifyPropertyChangedFor(nameof(CompletionActionBodyText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchProgressText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchProgressPercent))]
    [NotifyPropertyChangedFor(nameof(RunTelemetryVisibility))]
    [NotifyPropertyChangedFor(nameof(FirstRunGuideVisibility))]
    public partial int TotalFiles { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProcessedSummary))]
    [NotifyPropertyChangedFor(nameof(WorkbenchProcessedSummary))]
    [NotifyPropertyChangedFor(nameof(CompletionActionBodyText))]
    [NotifyPropertyChangedFor(nameof(RunTelemetryVisibility))]
    public partial int ProcessedFiles { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkbenchAnomalyFiles))]
    [NotifyPropertyChangedFor(nameof(CompletionActionTitleText))]
    [NotifyPropertyChangedFor(nameof(CompletionActionBodyText))]
    public partial int AnomalyFiles { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkbenchAnomalyRecords))]
    [NotifyPropertyChangedFor(nameof(CompletionActionTitleText))]
    [NotifyPropertyChangedFor(nameof(CompletionActionBodyText))]
    public partial int AnomalyRecords { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkbenchSkippedFiles))]
    [NotifyPropertyChangedFor(nameof(CompletionActionBodyText))]
    public partial int SkippedFiles { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchProgressText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchProgressPercent))]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkbenchReportPath))]
    [NotifyPropertyChangedFor(nameof(WorkbenchReportPathText))]
    [NotifyPropertyChangedFor(nameof(ReportActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(WorkbenchActivityVisibility))]
    [NotifyPropertyChangedFor(nameof(WorkbenchActivityTitleText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchContextText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchStatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchStatusBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(CompletionActionBodyText))]
    [NotifyPropertyChangedFor(nameof(FirstRunGuideVisibility))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessText))]
    [NotifyPropertyChangedFor(nameof(RunSetupReadinessDetailText))]
    [NotifyCanExecuteChangedFor(nameof(OpenCurrentReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCurrentReportFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCurrentReportPathCommand))]
    public partial string ReportPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedDetail))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedDetailCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedDetailSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedDetailSourcePathCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExplainSelectedDetailCommand))]
    public partial ReportDetailPreview? SelectedDetail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingReportSnapshot))]
    [NotifyPropertyChangedFor(nameof(WorkbenchModeText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchStatusText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchProgressText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchProgressPercent))]
    [NotifyPropertyChangedFor(nameof(WorkbenchProcessedSummary))]
    [NotifyPropertyChangedFor(nameof(WorkbenchAnomalyFiles))]
    [NotifyPropertyChangedFor(nameof(WorkbenchAnomalyRecords))]
    [NotifyPropertyChangedFor(nameof(WorkbenchSkippedFiles))]
    [NotifyPropertyChangedFor(nameof(WorkbenchReportPath))]
    [NotifyPropertyChangedFor(nameof(WorkbenchReportPathText))]
    [NotifyPropertyChangedFor(nameof(ReportActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(WorkbenchActivityVisibility))]
    [NotifyPropertyChangedFor(nameof(WorkbenchActivityTitleText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchReportButtonText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchReportFolderButtonText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchContextText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchStatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchStatusBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(WorkbenchReportDeviceCount))]
    [NotifyPropertyChangedFor(nameof(WorkbenchHighRiskDevices))]
    [NotifyPropertyChangedFor(nameof(WorkbenchTopIssueTypes))]
    [NotifyPropertyChangedFor(nameof(WorkbenchSensorOverviewText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchDetailPreviewTotalCount))]
    [NotifyPropertyChangedFor(nameof(WorkbenchFilteredDetailPreview))]
    [NotifyPropertyChangedFor(nameof(WorkbenchDetailPreviewStatusText))]
    [NotifyPropertyChangedFor(nameof(FirstRunGuideVisibility))]
    [NotifyPropertyChangedFor(nameof(SnapshotActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(RunTelemetryVisibility))]
    [NotifyPropertyChangedFor(nameof(CompletionActionsVisibility))]
    [NotifyCanExecuteChangedFor(nameof(OpenCurrentReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCurrentReportFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCurrentReportPathCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowCurrentRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCompletionSummaryCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFirstAnomalySourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedReportFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseSelectedReportDirectoriesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedReportCommand))]
    public partial RecentReport? SelectedReport { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkbenchReportDeviceCount))]
    public partial int ReportDeviceCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SensorOverviewText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchSensorOverviewText))]
    public partial int SensorOfflineDevices { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SensorOverviewText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchSensorOverviewText))]
    public partial int SensorFaultRows { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SensorOverviewText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchSensorOverviewText))]
    public partial int SensorMissingRows { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SensorOverviewText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchSensorOverviewText))]
    public partial int SensorSkippedRows { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkbenchFilteredDetailPreview))]
    [NotifyPropertyChangedFor(nameof(WorkbenchDetailPreviewStatusText))]
    public partial string DetailSearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkbenchFilteredDetailPreview))]
    [NotifyPropertyChangedFor(nameof(WorkbenchDetailPreviewStatusText))]
    public partial int SelectedSeverityFilterIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkbenchFilteredDetailPreview))]
    [NotifyPropertyChangedFor(nameof(WorkbenchDetailPreviewStatusText))]
    public partial int SelectedIssueTypeFilterIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailSortStatusText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchFilteredDetailPreview))]
    [NotifyPropertyChangedFor(nameof(WorkbenchDetailPreviewStatusText))]
    public partial string SelectedDetailSortKey { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailPreviewStatusText))]
    [NotifyPropertyChangedFor(nameof(WorkbenchDetailPreviewTotalCount))]
    [NotifyPropertyChangedFor(nameof(WorkbenchDetailPreviewStatusText))]
    public partial int DetailPreviewTotalCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LlmDetailExplanationVisibility))]
    public partial string LlmDetailExplanationText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExplainSelectedDetailCommand))]
    public partial bool IsExplainingSelectedDetail { get; set; }

    public Visibility LlmDetailExplanationVisibility =>
        string.IsNullOrWhiteSpace(LlmDetailExplanationText) ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeMode))]
    public partial int SelectedThemeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackdropMode))]
    public partial int SelectedBackdropIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PoetryStatusVisibility))]
    [NotifyCanExecuteChangedFor(nameof(RefreshPoetryStatusCommand))]
    public partial bool EnablePoetryStatus { get; set; } = true;

    [ObservableProperty]
    public partial string PoetryServiceUrl { get; set; } = "https://poetry.palemoky.com/";

    [ObservableProperty]
    public partial int SelectedPoetryLanguageIndex { get; set; }

    [ObservableProperty]
    public partial string PoetryStatusText { get; set; } = "山重水复疑无路，柳暗花明又一村。";

    [ObservableProperty]
    public partial string PoetryStatusSourceText { get; set; } = "陆游 · 游山西村";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshPoetryStatusCommand))]
    public partial bool IsRefreshingPoetryStatus { get; set; }

    public Visibility PoetryStatusVisibility =>
        EnablePoetryStatus ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial bool IsSettingsFeedbackOpen { get; set; }

    [ObservableProperty]
    public partial string SettingsFeedbackText { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity SettingsFeedbackSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial double VoltageMinThreshold { get; set; } = 353.0;

    [ObservableProperty]
    public partial double VoltageMaxThreshold { get; set; } = 430.0;

    [ObservableProperty]
    public partial double CurrentMaxThreshold { get; set; } = 1000.0;

    [ObservableProperty]
    public partial double CurrentUnbalanceMaxThreshold { get; set; } = 0.15;

    [ObservableProperty]
    public partial double ActivePowerMinThreshold { get; set; }

    [ObservableProperty]
    public partial double PowerFactorMinThreshold { get; set; } = 0.9;

    [ObservableProperty]
    public partial double TemperatureMinThreshold { get; set; }

    [ObservableProperty]
    public partial double TemperatureMaxThreshold { get; set; } = 70.0;

    [ObservableProperty]
    public partial double CurrentActiveMinThreshold { get; set; } = 1.0;

    [ObservableProperty]
    public partial double FreezeCountThreshold { get; set; } = 3;

    [ObservableProperty]
    public partial double FreezeStdThreshold { get; set; } = 0.01;

    [ObservableProperty]
    public partial double VoltageImbalanceThreshold { get; set; } = 0.02;

    [ObservableProperty]
    public partial bool CurrentOverloadEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool CurrentUnbalanceEnabled { get; set; }

    [ObservableProperty]
    public partial bool PowerFactorEnabled { get; set; }

    [ObservableProperty]
    public partial bool DetailOutputEnabled { get; set; }

    public string DiagnosticSummaryText => Diagnostics.SummaryText;

    public string PythonDiagnosticText => Diagnostics.PythonText;

    public string BackendDiagnosticText => Diagnostics.BackendText;

    public string ConfigDiagnosticText => Diagnostics.ConfigText;

    public string InputDiagnosticText => Diagnostics.InputText;

    public string OutputDiagnosticText => Diagnostics.OutputText;

    public string DiagnosticCheckedAtText => Diagnostics.CheckedAtText;

    public string DiagnosticActionText => Diagnostics.ActionText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRunSummaryVisibility))]
    [NotifyPropertyChangedFor(nameof(WorkbenchContextText))]
    public partial string ActiveRunSummaryText { get; set; } = "";

    public string CurrentFileText => RunTelemetry.CurrentFileText;

    public string RunElapsedText => RunTelemetry.ElapsedText;

    public string RunSpeedText => RunTelemetry.SpeedText;

    public string RunRemainingText => RunTelemetry.RemainingText;

    public string RunProgressDetailText => RunTelemetry.ProgressDetailText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelConfirmationVisibility))]
    [NotifyPropertyChangedFor(nameof(CancelButtonText))]
    public partial bool IsCancelConfirmationPending { get; set; }

    public string PythonSetupCommandText => Diagnostics.PythonSetupCommandText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FailureActionBodyText))]
    [NotifyPropertyChangedFor(nameof(FailureActionsVisibility))]
    public partial string LastFailureText { get; set; } = "暂无失败";

    public int SelectedLogRetentionIndex => RuntimeLogs.SelectedRetentionIndex;

    public string LogSearchText => RuntimeLogs.SearchText;

    public int SelectedLogKindFilterIndex => RuntimeLogs.SelectedKindFilterIndex;

    partial void OnSelectedThemeIndexChanged(int value) => SaveAppearanceSettings();

    partial void OnSelectedBackdropIndexChanged(int value) => SaveAppearanceSettings();

    partial void OnEnablePoetryStatusChanged(bool value)
    {
        SaveAppearanceSettings();
        if (value)
        {
            _ = RefreshPoetryStatusAsync();
        }
    }

    partial void OnPoetryServiceUrlChanged(string value) => SaveAppearanceSettings();

    partial void OnSelectedPoetryLanguageIndexChanged(int value)
    {
        var normalized = Math.Clamp(value, 0, 1);
        if (normalized != value)
        {
            SelectedPoetryLanguageIndex = normalized;
            return;
        }

        SaveAppearanceSettings();
        if (EnablePoetryStatus)
        {
            _ = RefreshPoetryStatusAsync();
        }
    }

    partial void OnCloseToTrayOnCloseChanged(bool value) => SavePreferenceSettings();

    partial void OnStartMinimizedToTrayChanged(bool value) => SavePreferenceSettings();

    partial void OnAutoStartOnSignInChanged(bool value)
    {
        if (!_settingsLoaded || _syncingStartupPreference)
        {
            UpdateStartupIntegrationStatus(_startup.GetStatus());
            return;
        }

        try
        {
            _startup.SetEnabled(value);
            UpdateStartupIntegrationStatus(_startup.GetStatus());
            RefreshDesktopHealth();
            SavePreferenceSettings();
        }
        catch (Exception ex)
        {
            StartupIntegrationStatusText = value
                ? $"启用登录自启动失败: {ex.Message}"
                : $"关闭登录自启动失败: {ex.Message}";
            _syncingStartupPreference = true;
            var startupStatus = _startup.GetStatus();
            AutoStartOnSignIn = startupStatus.IsEnabled;
            _syncingStartupPreference = false;
            RefreshDesktopHealth();
            SavePreferenceSettings();
        }
    }

    partial void OnEnableDesktopNotificationsChanged(bool value)
    {
        RefreshDesktopHealth();
        SavePreferenceSettings();
    }

    partial void OnEnableLlmAssistantChanged(bool value) => SavePreferenceSettings();

    partial void OnLlmEndpointChanged(string value) => SavePreferenceSettings();

    partial void OnLlmModelChanged(string value) => SavePreferenceSettings();

    partial void OnUseProxyForLlmChanged(bool value) => SavePreferenceSettings();

    partial void OnEnableNtfyNotificationsChanged(bool value) => SavePreferenceSettings();

    partial void OnNtfyServerUrlChanged(string value) => SavePreferenceSettings();

    partial void OnNtfyTopicChanged(string value) => SavePreferenceSettings();

    partial void OnSelectedNtfyPriorityIndexChanged(int value)
    {
        var normalized = Math.Clamp(value, 0, 4);
        if (normalized != value)
        {
            SelectedNtfyPriorityIndex = normalized;
            return;
        }

        SavePreferenceSettings();
    }

    partial void OnUseProxyForNotificationsChanged(bool value) => SavePreferenceSettings();

    partial void OnEnableNetworkProxyChanged(bool value) => SavePreferenceSettings();

    partial void OnProxyAddressChanged(string value) => SavePreferenceSettings();

    partial void OnProxyRequiresAuthenticationChanged(bool value) => SavePreferenceSettings();

    partial void OnProxyUserNameChanged(string value) => SavePreferenceSettings();

    partial void OnEnableUpdateChecksChanged(bool value) => SavePreferenceSettings();

    partial void OnUseProxyForUpdatesChanged(bool value) => SavePreferenceSettings();

    partial void OnSelectedUpdateChannelIndexChanged(int value)
    {
        var normalized = Math.Clamp(value, 0, 2);
        if (normalized != value)
        {
            SelectedUpdateChannelIndex = normalized;
            return;
        }

        SavePreferenceSettings();
    }

    partial void OnUpdateFeedUrlChanged(string value) => SavePreferenceSettings();

    partial void OnEnableGlobalHotkeysChanged(bool value)
    {
        if (value)
        {
            EnableGlobalHotkeys = false;
            return;
        }

        SavePreferenceSettings();
    }

    partial void OnEnableQuickActionsShortcutChanged(bool value)
    {
        if (value)
        {
            EnableQuickActionsShortcut = false;
            return;
        }

        if (SelectedQuickActionsShortcutIndex != 2)
        {
            SelectedQuickActionsShortcutIndex = 2;
            return;
        }

        SavePreferenceSettings();
    }

    partial void OnSelectedQuickActionsShortcutIndexChanged(int value)
    {
        if (value != 2)
        {
            SelectedQuickActionsShortcutIndex = 2;
            return;
        }

        if (EnableQuickActionsShortcut)
        {
            EnableQuickActionsShortcut = false;
            return;
        }

        SavePreferenceSettings();
    }

    partial void OnWriteReportChanged(bool value)
    {
        RefreshLocalDiagnostics();
        SavePreferenceSettings();
    }

    partial void OnInputDirectoryChanged(string value)
    {
        RefreshLocalDiagnostics();
        RefreshFirstRunGuide();
        SavePreferenceSettings();
    }

    partial void OnOutputDirectoryChanged(string value)
    {
        RefreshLocalDiagnostics();
        RefreshFirstRunGuide();
        SavePreferenceSettings();
    }

    partial void OnConfigPathChanged(string value)
    {
        if (!_suppressConfigPathReload)
        {
            ApplyDetectionConfig(_detectionConfig.Load(value));
        }

        RefreshLocalDiagnostics();
        RefreshFirstRunGuide();
        SavePreferenceSettings();
    }

    partial void OnPythonExecutableChanged(string value)
    {
        RefreshLocalDiagnostics();
        RefreshDesktopHealth();
        RefreshFirstRunGuide();
        SavePreferenceSettings();
    }

    partial void OnSelectedReportChanged(RecentReport? value)
    {
        if (!ReferenceEquals(ReportHistory.SelectedReport, value))
        {
            ReportHistory.SelectedReport = value;
        }

        SelectedDetail = null;
        RefreshDetailPreview();
    }

    partial void OnDetailSearchTextChanged(string value) => RefreshDetailPreview();

    partial void OnSelectedSeverityFilterIndexChanged(int value) => RefreshDetailPreview();

    partial void OnSelectedIssueTypeFilterIndexChanged(int value) => RefreshDetailPreview();

    partial void OnSelectedDetailSortKeyChanged(string value) => RefreshDetailPreview();

    partial void OnSelectedDetailChanged(ReportDetailPreview? value)
    {
        if (!IsExplainingSelectedDetail)
        {
            LlmDetailExplanationText = "";
        }
    }

    [RelayCommand]
    private void SaveSettingsFromPage()
    {
        SaveSettings();
        var thresholdsSaved = SaveDetectionConfig();
        RefreshDesktopHealth();
        RefreshLocalDiagnostics();
        if (thresholdsSaved)
        {
            ShowSettingsFeedback("设置已保存。", InfoBarSeverity.Success);
            AddLog("设置", "设置已保存。");
        }
    }

    [RelayCommand]
    private void ResetSettingsToDefaults()
    {
        ApplySettingsDefaults(_settings.CreateDefault());
        ApplyDetectionConfig(_detectionConfig.CreateDefault());
        var thresholdsSaved = SaveDetectionConfig();
        SaveSettings();
        RefreshDesktopHealth();
        RefreshLocalDiagnostics();
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
        if (thresholdsSaved)
        {
            ShowSettingsFeedback("设置已恢复默认值。", InfoBarSeverity.Success);
            AddLog("设置", "设置已恢复默认值。");
        }
    }

    [RelayCommand]
    private void SaveLlmApiKey()
    {
        SaveSecureSecret(PendingLlmApiKey, _credentials.SaveLlmApiKey, () => PendingLlmApiKey = "");
    }

    [RelayCommand]
    private void ClearLlmApiKey()
    {
        ClearSecureSecret(_credentials.ClearLlmApiKey, "LLM API Key 已清除。");
    }

    private bool CanTestLlmConnection() => !IsTestingLlmConnection;

    private bool CanRefreshPoetryStatus() => EnablePoetryStatus && !IsRefreshingPoetryStatus;

    [RelayCommand(CanExecute = nameof(CanRefreshPoetryStatus))]
    public async Task RefreshPoetryStatusAsync()
    {
        if (!EnablePoetryStatus || IsRefreshingPoetryStatus)
        {
            return;
        }

        IsRefreshingPoetryStatus = true;
        try
        {
            var snapshot = await _poetryStatus.GetRandomAsync(
                PoetryServiceUrl,
                SelectedPoetryLanguageIndex);
            PoetryStatusText = snapshot.Text;
            PoetryStatusSourceText = string.IsNullOrWhiteSpace(snapshot.Source)
                ? "诗泉"
                : snapshot.Source;
        }
        finally
        {
            IsRefreshingPoetryStatus = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestLlmConnection))]
    private async Task TestLlmConnectionAsync()
    {
        IsTestingLlmConnection = true;
        try
        {
            var response = await _llmAssistant.TestConnectionAsync(this);
            var detail = string.IsNullOrWhiteSpace(response)
                ? "服务已响应。"
                : $"服务已响应: {response}";
            ShowSettingsFeedback($"LLM 连接测试成功，{detail}", InfoBarSeverity.Success);
            AddLog("智能助手", "LLM 连接测试成功。");
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or InvalidOperationException
                                   or TaskCanceledException
                                   or UriFormatException
                                   or JsonException)
        {
            ShowSettingsFeedback($"LLM 连接测试失败: {ex.Message}", InfoBarSeverity.Warning);
            AddLog("智能助手提醒", $"LLM 连接测试失败: {ex.Message}");
        }
        finally
        {
            IsTestingLlmConnection = false;
        }
    }

    [RelayCommand]
    private void SaveNtfyToken()
    {
        SaveSecureSecret(PendingNtfyToken, _credentials.SaveNtfyToken, () => PendingNtfyToken = "");
    }

    [RelayCommand]
    private void ClearNtfyToken()
    {
        ClearSecureSecret(_credentials.ClearNtfyToken, "ntfy Token 已清除。");
    }

    [RelayCommand]
    private void TestDesktopNotification()
    {
        if (!EnableDesktopNotifications)
        {
            ShowSettingsFeedback("请先启用桌面通知。", InfoBarSeverity.Warning);
            return;
        }

        DesktopNotificationRequested?.Invoke(
            this,
            new DesktopNotificationRequest(
                DesktopNotificationKind.Success,
                "E-Detection 桌面通知",
                $"桌面通知可以正常显示 · {DateTimeOffset.Now:HH:mm}",
                ForwardToRemoteNotifications: false));
        ShowSettingsFeedback("已发送桌面通知测试。", InfoBarSeverity.Success);
        AddLog("桌面通知", "已发送桌面通知测试。");
    }

    [RelayCommand]
    private void OpenSystemNotificationSettings()
    {
        OpenUri(WindowsNotificationSettingsUri);
        ShowSettingsFeedback("已打开 Windows 通知设置。", InfoBarSeverity.Informational);
        AddLog("桌面通知", "已打开 Windows 通知设置。");
    }

    [RelayCommand]
    private void OpenSystemStartupSettings()
    {
        OpenUri(WindowsStartupAppsSettingsUri);
        ShowSettingsFeedback("已打开 Windows 启动应用设置。", InfoBarSeverity.Informational);
        AddLog("窗口", "已打开 Windows 启动应用设置。");
    }

    private bool CanSendTestNtfyNotification() => !IsSendingTestNtfyNotification;

    [RelayCommand(CanExecute = nameof(CanSendTestNtfyNotification))]
    private async Task SendTestNtfyNotificationAsync()
    {
        if (!EnableNtfyNotifications)
        {
            ShowSettingsFeedback("请先启用 ntfy 推送。", InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(NtfyServerUrl) || string.IsNullOrWhiteSpace(NtfyTopic))
        {
            ShowSettingsFeedback("请填写 ntfy 服务地址和主题。", InfoBarSeverity.Warning);
            return;
        }

        IsSendingTestNtfyNotification = true;
        try
        {
            var request = new DesktopNotificationRequest(
                DesktopNotificationKind.Success,
                "E-Detection 测试推送",
                $"这是一条测试消息 · {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");

            if (await _ntfyNotifications.TrySendAsync(request, this))
            {
                ShowSettingsFeedback("测试推送已发送。", InfoBarSeverity.Success);
                AddLog("消息推送", "已发送 ntfy 测试推送。");
                return;
            }

            ShowSettingsFeedback("ntfy 推送未启用或配置不完整。", InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or InvalidOperationException
                                   or TaskCanceledException
                                   or UriFormatException)
        {
            ShowSettingsFeedback($"测试推送失败: {ex.Message}", InfoBarSeverity.Warning);
            AddLog("推送提醒", $"ntfy 测试推送失败: {ex.Message}");
        }
        finally
        {
            IsSendingTestNtfyNotification = false;
        }
    }

    [RelayCommand]
    private void SaveProxyPassword()
    {
        SaveSecureSecret(PendingProxyPassword, _credentials.SaveProxyPassword, () => PendingProxyPassword = "");
    }

    [RelayCommand]
    private void ClearProxyPassword()
    {
        ClearSecureSecret(_credentials.ClearProxyPassword, "代理密码已清除。");
    }

    [RelayCommand]
    private void OpenSystemProxySettings()
    {
        OpenUri(WindowsProxySettingsUri);
        ShowSettingsFeedback("已打开 Windows 代理设置。", InfoBarSeverity.Informational);
        AddLog("网络代理", "已打开 Windows 代理设置。");
    }

    private bool CanTestNetworkProxy() => !IsTestingNetworkProxy;

    [RelayCommand(CanExecute = nameof(CanTestNetworkProxy))]
    private async Task TestNetworkProxyAsync()
    {
        IsTestingNetworkProxy = true;
        try
        {
            await _networkProxy.TestProxyAsync(this);
            ShowSettingsFeedback("代理连接测试成功。", InfoBarSeverity.Success);
            AddLog("网络代理", "代理连接测试成功。");
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or InvalidOperationException
                                   or TaskCanceledException
                                   or UriFormatException)
        {
            ShowSettingsFeedback($"代理连接测试失败: {ex.Message}", InfoBarSeverity.Warning);
            AddLog("网络代理提醒", $"代理连接测试失败: {ex.Message}");
        }
        finally
        {
            IsTestingNetworkProxy = false;
        }
    }

    private void SaveSecureSecret(
        string value,
        Action<string> save,
        Action clearInput)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowSettingsFeedback("请输入要保存的凭据。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            save(value);
            clearInput();
            RefreshSecureCredentialStatus();
            ShowSettingsFeedback("凭据已保存到 Windows 凭据。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowSettingsFeedback($"保存凭据失败: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void ClearSecureSecret(Action clear, string successMessage)
    {
        try
        {
            clear();
            RefreshSecureCredentialStatus();
            ShowSettingsFeedback(successMessage, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowSettingsFeedback($"清除凭据失败: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void RefreshSecureCredentialStatus()
    {
        LlmApiKeyStatusText = _credentials.LlmApiKeyStatusText;
        NtfyTokenStatusText = _credentials.NtfyTokenStatusText;
        ProxyPasswordStatusText = _credentials.ProxyPasswordStatusText;
    }

    private void ReportHistory_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReportHistoryViewModel.SelectedReport)
            && !ReferenceEquals(SelectedReport, ReportHistory.SelectedReport))
        {
            SelectedReport = ReportHistory.SelectedReport;
        }

        if (e.PropertyName is nameof(ReportHistoryViewModel.StatusText))
        {
            OnPropertyChanged(nameof(ReportHistoryStatusText));
            OnPropertyChanged(nameof(WorkbenchStatusBadgeText));
        }

        if (e.PropertyName is nameof(ReportHistoryViewModel.RecentReportLimit)
            or nameof(ReportHistoryViewModel.RecentReportLimitText))
        {
            OnPropertyChanged(nameof(RecentReportLimit));
            OnPropertyChanged(nameof(RecentReportLimitText));
            ClearRecentReportsCommand.NotifyCanExecuteChanged();
            SavePreferenceSettings();
        }
    }

    private void Diagnostics_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DiagnosticsViewModel.SummaryText):
                OnPropertyChanged(nameof(DiagnosticSummaryText));
                OnPropertyChanged(nameof(FirstRunGuideSubtitleText));
                OnPropertyChanged(nameof(RunSetupReadinessText));
                OnPropertyChanged(nameof(RunSetupReadinessDetailText));
                break;
            case nameof(DiagnosticsViewModel.PythonText):
                OnPropertyChanged(nameof(PythonDiagnosticText));
                OnPropertyChanged(nameof(FirstRunPythonStepText));
                break;
            case nameof(DiagnosticsViewModel.BackendText):
                OnPropertyChanged(nameof(BackendDiagnosticText));
                OnPropertyChanged(nameof(FirstRunPythonStepText));
                OnPropertyChanged(nameof(FirstRunGuideSubtitleText));
                break;
            case nameof(DiagnosticsViewModel.ConfigText):
                OnPropertyChanged(nameof(ConfigDiagnosticText));
                OnPropertyChanged(nameof(FirstRunConfigStepText));
                break;
            case nameof(DiagnosticsViewModel.InputText):
                OnPropertyChanged(nameof(InputDiagnosticText));
                OnPropertyChanged(nameof(FirstRunInputStepText));
                break;
            case nameof(DiagnosticsViewModel.OutputText):
                OnPropertyChanged(nameof(OutputDiagnosticText));
                OnPropertyChanged(nameof(FirstRunOutputStepText));
                break;
            case nameof(DiagnosticsViewModel.CheckedAtText):
                OnPropertyChanged(nameof(DiagnosticCheckedAtText));
                break;
            case nameof(DiagnosticsViewModel.ActionText):
                OnPropertyChanged(nameof(DiagnosticActionText));
                OnPropertyChanged(nameof(FirstRunGuideSubtitleText));
                OnPropertyChanged(nameof(RunSetupReadinessDetailText));
                break;
            case nameof(DiagnosticsViewModel.PythonSetupCommandText):
                OnPropertyChanged(nameof(PythonSetupCommandText));
                CopyPythonSetupCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    private void RunTelemetry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RunTelemetryViewModel.CurrentFileText):
                OnPropertyChanged(nameof(CurrentFileText));
                OnPropertyChanged(nameof(WorkbenchStatusBadgeText));
                OnPropertyChanged(nameof(RunSetupReadinessDetailText));
                OnPropertyChanged(nameof(RunTelemetryVisibility));
                break;
            case nameof(RunTelemetryViewModel.ElapsedText):
                OnPropertyChanged(nameof(RunElapsedText));
                break;
            case nameof(RunTelemetryViewModel.SpeedText):
                OnPropertyChanged(nameof(RunSpeedText));
                break;
            case nameof(RunTelemetryViewModel.RemainingText):
                OnPropertyChanged(nameof(RunRemainingText));
                break;
            case nameof(RunTelemetryViewModel.ProgressDetailText):
                OnPropertyChanged(nameof(RunProgressDetailText));
                break;
        }
    }

    private void RuntimeLogs_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RuntimeLogViewModel.StatusText):
                OnPropertyChanged(nameof(LogStatusText));
                ClearRuntimeLogsCommand.NotifyCanExecuteChanged();
                CopyFilteredLogsCommand.NotifyCanExecuteChanged();
                break;
            case nameof(RuntimeLogViewModel.RetentionLimit):
            case nameof(RuntimeLogViewModel.RetentionText):
                OnPropertyChanged(nameof(LogRetentionLimit));
                OnPropertyChanged(nameof(LogRetentionText));
                SavePreferenceSettings();
                break;
            case nameof(RuntimeLogViewModel.SelectedRetentionIndex):
                OnPropertyChanged(nameof(SelectedLogRetentionIndex));
                break;
            case nameof(RuntimeLogViewModel.SearchText):
                OnPropertyChanged(nameof(LogSearchText));
                break;
            case nameof(RuntimeLogViewModel.SelectedKindFilterIndex):
                OnPropertyChanged(nameof(SelectedLogKindFilterIndex));
                break;
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!await EnsureReadyToStartAsync())
        {
            return;
        }

        SaveDetectionConfig();
        var configPath = EnsureEffectiveConfigPath();
        ResetRunState();
        var request = new DetectionRequest
        {
            InputDirectory = InputDirectory,
            OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory,
            ConfigPath = configPath,
            PythonExecutable = string.IsNullOrWhiteSpace(PythonExecutable) ? "python" : PythonExecutable,
            WriteReport = WriteReport,
            WorkingDirectory = PythonBackendService.ResolveBackendWorkingDirectory(),
        };
        ActiveRunSummaryText = BuildActiveRunSummaryText(request);
        StartRunTelemetry();
        IsRunning = true;
        _desktopNotificationSent = false;
        StatusText = "正在准备检测...";
        SetTaskbarProgress(TaskbarProgressKind.Indeterminate, 0);
        _runCts = new CancellationTokenSource();
        SaveSettings();

        var progress = new Progress<DetectionBackendEvent>(HandleBackendEvent);

        try
        {
            var exitCode = await _backend.RunDetectionAsync(
                request,
                progress,
                _runCts.Token);

            if (exitCode != 0)
            {
                RunTelemetry.ApplyCurrentFile("检测失败");
                StopRunTelemetry();
                LastFailureText = $"检测组件退出码: {exitCode}";
                AddLog("错误", LastFailureText);
                StatusText = "检测失败";
                SetTaskbarProgress(TaskbarProgressKind.Error, Math.Max(ProgressPercent, 100));
                RequestDesktopNotification(
                    DesktopNotificationKind.Error,
                    "检测失败",
                    LastFailureText,
                    ReportPath);
            }
        }
        catch (OperationCanceledException)
        {
            ClearCancelConfirmation();
            RunTelemetry.ApplyCurrentFile("检测已取消");
            StopRunTelemetry();
            AddLog("取消", "检测已取消。");
            StatusText = "检测已取消";
            SetTaskbarProgress(TaskbarProgressKind.Paused, ProgressPercent);
            RequestDesktopNotification(
                DesktopNotificationKind.Warning,
                "检测已取消",
                $"已处理 {ProcessedFiles}/{TotalFiles} 个文件。",
                ReportPath);
        }
        catch (Exception ex)
        {
            ClearCancelConfirmation();
            RunTelemetry.ApplyCurrentFile("检测失败");
            StopRunTelemetry();
            LastFailureText = ex.Message;
            AddLog("错误", ex.Message);
            StatusText = "检测失败";
            SetTaskbarProgress(TaskbarProgressKind.Error, 100);
            RequestDesktopNotification(
                DesktopNotificationKind.Error,
                "检测失败",
                ex.Message,
                ReportPath);
        }
        finally
        {
            if (_runStopwatch.IsRunning)
            {
                StopRunTelemetry();
            }

            IsRunning = false;
            ClearCancelConfirmation();
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!IsCancelConfirmationPending)
        {
            ArmCancelConfirmation();
            return;
        }

        ClearCancelConfirmation(restoreRunningTaskbar: false);
        _runCts?.Cancel();
        StatusText = "正在取消...";
    }

    private void HandleBackendEvent(DetectionBackendEvent evt)
    {
        var action = _runEvents.BuildAction(evt, BuildRunEventState());
        ApplyRunEventAction(action, evt);
    }

    private void ApplyRunEventAction(
        RunEventAction action,
        DetectionBackendEvent evt)
    {
        if (action.ApplySummary)
        {
            ApplySummary(evt);
        }

        if (action.TotalFiles is { } totalFiles)
        {
            TotalFiles = totalFiles;
        }

        if (action.ProcessedFiles is { } processedFiles)
        {
            ProcessedFiles = processedFiles;
        }

        if (action.ProgressPercent is { } progressPercent)
        {
            ProgressPercent = progressPercent;
        }

        if (action.CurrentFileText is not null)
        {
            RunTelemetry.ApplyCurrentFile(action.CurrentFileText);
        }

        if (action.StatusText is not null)
        {
            StatusText = action.StatusText;
        }

        if (action.LastFailureText is not null)
        {
            LastFailureText = action.LastFailureText;
        }

        if (evt.EventName == "report_written")
        {
            ReportPath = evt.ReportPath ?? "";
        }

        if (action.ApplyReportSummary)
        {
            ApplyReportSummary(evt);
            AddLog("摘要", _runEvents.BuildReportSummaryLogMessage(BuildRunEventState()));
        }

        if (action.AddRecentReport)
        {
            AddRecentReport(ReportPath, evt);
        }

        if (action.StopTelemetry)
        {
            StopRunTelemetry();
        }
        else if (ShouldUpdateTelemetry(evt.EventName))
        {
            UpdateRunTelemetry();
        }

        if (action.TaskbarKind is { } taskbarKind)
        {
            SetTaskbarProgress(taskbarKind, action.TaskbarPercent ?? ProgressPercent);
        }

        if (action.LogKind is not null)
        {
            AddLog(action.LogKind, action.LogMessage ?? "");
        }

        if (action.ApplySummary)
        {
            AddLog("完成", _runEvents.BuildRunCompletedLogMessage(BuildRunEventState()));
            var notification = _runEvents.BuildRunCompletedNotification(BuildRunEventState(), ReportPath);
            RequestDesktopNotification(
                notification.Kind,
                notification.Title,
                notification.Message,
                notification.ReportPath);
        }

        if (action.Notification is { } notificationRequest)
        {
            RequestDesktopNotification(
                notificationRequest.Kind,
                notificationRequest.Title,
                notificationRequest.Message,
                notificationRequest.ReportPath);
        }
    }

    private static bool ShouldUpdateTelemetry(string eventName) =>
        eventName is "run_started"
            or "file_result"
            or "file_progress"
            or "report_written"
            or "report_summary";

    private RunEventState BuildRunEventState() =>
        new(
            TotalFiles,
            ProcessedFiles,
            AnomalyFiles,
            AnomalyRecords,
            SkippedFiles,
            ProgressPercent,
            HighRiskDevices.Count,
            TopIssueTypes.Count,
            SensorOfflineDevices,
            SensorFaultRows,
            SensorMissingRows,
            SensorSkippedRows);

    private void ApplySummary(DetectionBackendEvent evt)
    {
        ApplyRunSummarySnapshot(_runState.BuildSummary(evt, BuildRunSummarySnapshot()));
        if (!string.IsNullOrWhiteSpace(ReportPath))
        {
            AddRecentReport(ReportPath, evt);
        }
    }

    private RunSummarySnapshot BuildRunSummarySnapshot() =>
        new(
            TotalFiles,
            ProcessedFiles,
            AnomalyFiles,
            AnomalyRecords,
            SkippedFiles,
            ReportPath);

    private void ApplyRunSummarySnapshot(RunSummarySnapshot snapshot)
    {
        TotalFiles = snapshot.TotalFiles;
        ProcessedFiles = snapshot.ProcessedFiles;
        AnomalyFiles = snapshot.AnomalyFiles;
        AnomalyRecords = snapshot.AnomalyRecords;
        SkippedFiles = snapshot.SkippedFiles;
        ReportPath = snapshot.ReportPath;
    }

    private static string BuildActiveRunSummaryText(DetectionRequest request)
    {
        var input = FormatPathLeaf(request.InputDirectory, "未选择数据");
        var output = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? "输入目录"
            : FormatPathLeaf(request.OutputDirectory, "报告目录");
        return $"本次运行 · 数据 {input} · 报告 {output}";
    }

    private static string FormatPathLeaf(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return fallback;
        }

        var trimmed = path.Trim().TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return fallback;
        }

        var fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
    }

    private void StartRunTelemetry()
    {
        _runStopwatch.Restart();
        RunTelemetry.Start();
        OnPropertyChanged(nameof(RunTelemetryVisibility));
    }

    private void StopRunTelemetry()
    {
        UpdateRunTelemetry();
        _runStopwatch.Stop();
    }

    private void UpdateRunTelemetry()
    {
        RunTelemetry.Update(
            _runStopwatch.Elapsed,
            IsRunning,
            ProcessedFiles,
            TotalFiles,
            ProgressPercent);
    }

    private void ArmCancelConfirmation()
    {
        if (!IsRunning)
        {
            return;
        }

        _cancelPromptCts?.Cancel();
        var promptCts = new CancellationTokenSource();
        _cancelPromptCts = promptCts;
        IsCancelConfirmationPending = true;
        StatusText = "再次确认取消";
        SetTaskbarProgress(TaskbarProgressKind.Paused, ProgressPercent);
        AddLog("取消确认", "再次按 Esc 或点击确认取消停止检测。");
        _ = ClearCancelConfirmationAfterDelayAsync(promptCts);
    }

    private async Task ClearCancelConfirmationAfterDelayAsync(CancellationTokenSource promptCts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), promptCts.Token);
            if (ReferenceEquals(_cancelPromptCts, promptCts) && IsRunning)
            {
                IsCancelConfirmationPending = false;
                _cancelPromptCts = null;
                StatusText = string.IsNullOrWhiteSpace(CurrentFileText)
                    ? "处理中..."
                    : CurrentFileText;
                SetTaskbarProgress(TaskbarProgressKind.Normal, ProgressPercent);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            promptCts.Dispose();
        }
    }

    private void ClearCancelConfirmation(bool restoreRunningTaskbar = true)
    {
        _cancelPromptCts?.Cancel();
        _cancelPromptCts = null;
        IsCancelConfirmationPending = false;
        if (restoreRunningTaskbar && IsRunning)
        {
            SetTaskbarProgress(TaskbarProgressKind.Normal, ProgressPercent);
        }
    }

    private void ResetRunState()
    {
        ClearCancelConfirmation();
        _runStopwatch.Reset();
        SelectedReport = null;
        ApplyRunSummarySnapshot(_runState.ResetSummary);
        ProgressPercent = 0;
        SetTaskbarProgress(TaskbarProgressKind.None, 0);
        ApplyReportSummarySnapshot(_runState.ResetReportSummary);
        DetailSearchText = "";
        SelectedSeverityFilterIndex = 0;
        SelectedIssueTypeFilterIndex = 0;
        SelectedDetailSortKey = "";
        DetailIssueTypeFilters.Clear();
        DetailIssueTypeFilters.Add("全部类型");
        ActiveRunSummaryText = "";
        RunTelemetry.Reset();
        FilteredDetailPreview.Clear();
        RuntimeLogs.Clear();
        OnPropertyChanged(nameof(DetailPreviewStatusText));
        CopyFilteredDetailsCommand.NotifyCanExecuteChanged();
    }

    private void ApplyReportSummary(DetectionBackendEvent evt)
    {
        ApplyReportSummarySnapshot(_runState.BuildReportSummary(evt));
        RefreshDetailPreview();
        SelectFirstVisibleDetailIfAvailable();
    }

    private void ApplyReportSummarySnapshot(ReportSummarySnapshot snapshot)
    {
        ReportDeviceCount = snapshot.DeviceCount;
        HighRiskDevices.Clear();
        foreach (var device in snapshot.HighRiskDevices)
        {
            HighRiskDevices.Add(device);
        }

        TopIssueTypes.Clear();
        foreach (var issue in snapshot.TopIssueTypes)
        {
            TopIssueTypes.Add(issue);
        }

        SensorOfflineDevices = snapshot.SensorOverview.OfflineDevices;
        SensorFaultRows = snapshot.SensorOverview.SensorFaultRows;
        SensorMissingRows = snapshot.SensorOverview.SensorMissingRows;
        SensorSkippedRows = snapshot.SensorOverview.SkippedRows;

        DetailPreview.Clear();
        foreach (var detail in snapshot.DetailPreview)
        {
            DetailPreview.Add(detail);
        }

        DetailPreviewTotalCount = snapshot.DetailPreviewCount;
    }

    private void RefreshDetailPreview()
    {
        IEnumerable<ReportDetailPreview> source = SelectedReport is { } report
            ? report.DetailPreview
            : DetailPreview;
        var result = _detailPreview.Refresh(
            source,
            SelectedDetail,
            DetailIssueTypeFilters,
            BuildDetailFilterState());
        DetailIssueTypeFilters.Clear();
        foreach (var issueType in result.IssueTypeFilters)
        {
            DetailIssueTypeFilters.Add(issueType);
        }

        if (SelectedIssueTypeFilterIndex != result.SelectedIssueTypeFilterIndex)
        {
            SelectedIssueTypeFilterIndex = result.SelectedIssueTypeFilterIndex;
        }

        FilteredDetailPreview.Clear();
        foreach (var detail in result.FilteredDetails)
        {
            FilteredDetailPreview.Add(detail);
        }

        if (!ReferenceEquals(SelectedDetail, result.SelectedDetail))
        {
            SelectedDetail = result.SelectedDetail;
        }

        NotifyDetailPreviewChanged();
        CopyFilteredDetailsCommand.NotifyCanExecuteChanged();
        OpenFirstAnomalySourceCommand.NotifyCanExecuteChanged();
    }

    private void NotifyDetailPreviewChanged()
    {
        OnPropertyChanged(nameof(DetailPreviewStatusText));
        OnPropertyChanged(nameof(WorkbenchFilteredDetailPreview));
        OnPropertyChanged(nameof(WorkbenchDetailPreviewStatusText));
        OnPropertyChanged(nameof(DetailSortStatusText));
    }

    private void SelectFirstVisibleDetailIfAvailable()
    {
        if (SelectedDetail is null && FilteredDetailPreview.Count > 0)
        {
            SelectedDetail = FilteredDetailPreview[0];
        }
    }

    private ReportDetailFilterState BuildDetailFilterState() =>
        new(
            DetailSearchText,
            SelectedSeverityFilterIndex,
            SelectedIssueTypeFilterIndex,
            SelectedDetailSortKey);

    private static string SensorOverviewTextFrom(ReportSensorOverview sensor) =>
        $"离线 {sensor.OfflineDevices} · 传感器故障 {sensor.SensorFaultRows} · 未配置 {sensor.SensorMissingRows} · 跳过 {sensor.SkippedRows}";

    private void RefreshReportHistory()
    {
        ReportHistory.SelectedReport = SelectedReport;
        ReportHistory.Refresh();
        OnPropertyChanged(nameof(ReportHistoryStatusText));
    }

    private void NotifyWorkbenchSnapshotChanged()
    {
        OnPropertyChanged(nameof(IsShowingReportSnapshot));
        OnPropertyChanged(nameof(WorkbenchModeText));
        OnPropertyChanged(nameof(WorkbenchStatusText));
        OnPropertyChanged(nameof(WorkbenchProgressText));
        OnPropertyChanged(nameof(WorkbenchProgressPercent));
        OnPropertyChanged(nameof(WorkbenchProcessedSummary));
        OnPropertyChanged(nameof(WorkbenchAnomalyFiles));
        OnPropertyChanged(nameof(WorkbenchAnomalyRecords));
        OnPropertyChanged(nameof(WorkbenchSkippedFiles));
        OnPropertyChanged(nameof(WorkbenchReportPath));
        OnPropertyChanged(nameof(WorkbenchReportPathText));
        OnPropertyChanged(nameof(WorkbenchReportButtonText));
        OnPropertyChanged(nameof(WorkbenchReportFolderButtonText));
        OnPropertyChanged(nameof(WorkbenchContextText));
        OnPropertyChanged(nameof(WorkbenchReportDeviceCount));
        OnPropertyChanged(nameof(WorkbenchHighRiskDevices));
        OnPropertyChanged(nameof(WorkbenchTopIssueTypes));
        OnPropertyChanged(nameof(WorkbenchSensorOverviewText));
        OnPropertyChanged(nameof(WorkbenchDetailPreviewTotalCount));
        OnPropertyChanged(nameof(WorkbenchFilteredDetailPreview));
        OnPropertyChanged(nameof(WorkbenchDetailPreviewStatusText));
        RefreshFirstRunGuide();
        OpenCurrentReportCommand.NotifyCanExecuteChanged();
        OpenCurrentReportFolderCommand.NotifyCanExecuteChanged();
        CopyCurrentReportPathCommand.NotifyCanExecuteChanged();
        CopyFilteredDetailsCommand.NotifyCanExecuteChanged();
        CopyCompletionSummaryCommand.NotifyCanExecuteChanged();
        OpenFirstAnomalySourceCommand.NotifyCanExecuteChanged();
    }

    private bool CanShowCurrentRun() => SelectedReport is not null;

    [RelayCommand(CanExecute = nameof(CanShowCurrentRun))]
    private void ShowCurrentRun()
    {
        SelectedReport = null;
        AddLog("视图", "已返回当前检测状态。");
    }

    private bool CanCopyCompletionSummary() => ShouldShowCompletionActions;

    [RelayCommand(CanExecute = nameof(CanCopyCompletionSummary))]
    private void CopyCompletionSummary()
    {
        CopyTextToClipboard(BuildCompletionSummaryText());
        AddLog("复制", "已复制检测完成摘要。");
    }

    private string BuildCompletionSummaryText()
    {
        var lines = new List<string>
        {
            "E-Detection 检测摘要",
            $"状态: {StatusText}",
            $"文件: {ProcessedSummary}",
            $"异常文件: {AnomalyFiles}",
            $"异常记录: {AnomalyRecords}",
            $"跳过文件: {SkippedFiles}",
            $"设备数: {ReportDeviceCount}",
            $"传感器: {SensorOverviewText}",
            $"耗时: {RunElapsedText}",
            $"输入目录: {InputDirectory}",
            $"报告: {(string.IsNullOrWhiteSpace(ReportPath) ? "未生成" : ReportPath)}",
        };

        var issueTypes = TopIssueTypes.Take(5).ToList();
        if (issueTypes.Count > 0)
        {
            lines.Add("异常类型:");
            lines.AddRange(issueTypes.Select(issue => $"- {issue.Name}: {issue.Count}"));
        }

        var devices = HighRiskDevices.Take(5).ToList();
        if (devices.Count > 0)
        {
            lines.Add("高风险设备:");
            lines.AddRange(devices.Select(device => $"- {device.Title}: {device.Subtitle}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private bool CanOpenFirstAnomalySource()
    {
        var detail = WorkbenchFilteredDetailPreview.FirstOrDefault();
        return detail is not null && File.Exists(ResolveDetailSourcePath(detail));
    }

    [RelayCommand(CanExecute = nameof(CanOpenFirstAnomalySource))]
    private void OpenFirstAnomalySource()
    {
        var detail = WorkbenchFilteredDetailPreview.FirstOrDefault();
        if (detail is null)
        {
            return;
        }

        SelectedDetail = detail;
        var path = ResolveDetailSourcePath(detail);
        if (!File.Exists(path))
        {
            return;
        }

        OpenContainingFolder(path);
        AddLog("定位", "已打开首个异常的源文件位置。");
    }

    private void AddLog(string kind, string message)
    {
        RuntimeLogs.Add(kind, message);
    }

    public void AddRuntimeLog(string kind, string message) => AddLog(kind, message);

    public string BuildLogExportText()
    {
        return RuntimeLogs.BuildExportText();
    }

    private bool CanCopyFilteredLogs() => FilteredLogItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyFilteredLogs))]
    private void CopyFilteredLogs()
    {
        if (FilteredLogItems.Count == 0)
        {
            return;
        }

        CopyTextToClipboard(BuildLogExportText());
    }

    [RelayCommand]
    private void ClearLogFilters()
    {
        RuntimeLogs.ClearFilters();
    }

    private bool CanClearRuntimeLogs() => LogItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearRuntimeLogs))]
    private void ClearRuntimeLogs()
    {
        RuntimeLogs.Clear();
        ClearRuntimeLogsCommand.NotifyCanExecuteChanged();
        CopyFilteredLogsCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> EnsureReadyToStartAsync()
    {
        RefreshLocalDiagnostics();

        var issues = _diagnostics.ValidateRunInputs(
            BuildDiagnosticsRequest(),
            prepareOutputDirectory: true);
        if (issues.Count > 0)
        {
            BlockStart(issues);
            return false;
        }

        RefreshLocalDiagnostics();
        Diagnostics.MarkProbeInProgress("正在执行运行前就绪检查...");

        var backendRoot = PythonBackendService.ResolveBackendWorkingDirectory();
        var result = await _diagnostics.ProbePythonAsync(PythonExecutable, backendRoot);
        ApplyPythonProbeResult(result);
        if (!result.IsReady)
        {
            BlockStart(
                "检测组件未就绪",
                result.ActionMessage);
            return false;
        }

        LastFailureText = "暂无失败";
        return true;
    }

    [RelayCommand]
    private async Task RefreshDiagnosticsAsync()
    {
        RefreshLocalDiagnostics();
        Diagnostics.MarkProbeInProgress("正在验证检测组件...");

        var backendRoot = PythonBackendService.ResolveBackendWorkingDirectory();
        var result = await _diagnostics.ProbePythonAsync(PythonExecutable, backendRoot);
        ApplyPythonProbeResult(result);
        AddLog(result.IsReady ? "状态检查" : "状态提醒", $"{result.PythonMessage}；{result.BackendMessage}");
    }

    [RelayCommand]
    private void RefreshDesktopHealth() => DesktopHealth.Refresh();

    private void RefreshLocalDiagnostics()
    {
        var snapshot = _diagnostics.BuildLocalSnapshot(BuildDiagnosticsRequest());
        Diagnostics.ApplyLocalSnapshot(snapshot);
        RefreshFirstRunGuide();
    }

    private DesktopDiagnosticsRequest BuildDiagnosticsRequest() =>
        new(
            InputDirectory,
            OutputDirectory,
            DetectionConfigService.ResolveEffectiveConfigPath(ConfigPath),
            PythonExecutable,
            WriteReport);

    private void ApplyPythonProbeResult(PythonProbeResult result)
    {
        Diagnostics.ApplyPythonProbeResult(
            result,
            IsLocalInputReady(),
            IsConfigReady(),
            DateTime.Now);
        RefreshFirstRunGuide();
    }

    private void RefreshFirstRunGuide()
    {
        OnPropertyChanged(nameof(FirstRunGuideVisibility));
        OnPropertyChanged(nameof(FirstRunGuideTitleText));
        OnPropertyChanged(nameof(FirstRunGuideSubtitleText));
        OnPropertyChanged(nameof(FirstRunInputStepText));
        OnPropertyChanged(nameof(FirstRunConfigStepText));
        OnPropertyChanged(nameof(FirstRunPythonStepText));
        OnPropertyChanged(nameof(FirstRunOutputStepText));
        OnPropertyChanged(nameof(RunSetupReadinessText));
        OnPropertyChanged(nameof(RunSetupReadinessDetailText));
    }

    private void BlockStart(IReadOnlyList<string> issues)
    {
        var message = string.Join("；", issues);
        var action = DesktopDiagnosticsService.BuildBlockStartAction(issues);
        BlockStart(message, action);
    }

    private void BlockStart(string message, string action)
    {
        StatusText = "无法开始检测";
        LastFailureText = message;
        Diagnostics.ApplyBlockStart(message, action);
        SetTaskbarProgress(TaskbarProgressKind.None, 0);
        AddLog("就绪检查", message);
    }

    private bool IsLocalInputReady() =>
        DesktopDiagnosticsService.IsInputReady(InputDirectory);

    private bool IsConfigReady()
        => File.Exists(DetectionConfigService.ResolveEffectiveConfigPath(ConfigPath));

    private bool CanCopyPythonSetupCommand() =>
        !string.IsNullOrWhiteSpace(PythonSetupCommandText);

    [RelayCommand(CanExecute = nameof(CanCopyPythonSetupCommand))]
    private void CopyPythonSetup()
    {
        CopyTextToClipboard(PythonSetupCommandText);
        AddLog("修复", "已复制检测组件修复命令。");
    }

    [RelayCommand]
    private void OpenBackendDirectory()
    {
        OpenFolder(PythonBackendService.ResolveBackendWorkingDirectory());
    }

    [RelayCommand]
    private void OpenUpdateFeed()
    {
        var targetUrl = string.IsNullOrWhiteSpace(LatestReleaseUrl)
            ? UpdateFeedUrl
            : LatestReleaseUrl;
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            ShowSettingsFeedback("请先填写更新源。", InfoBarSeverity.Warning);
            return;
        }

        OpenUri(targetUrl);
    }

    private bool CanCheckForUpdates() => !IsCheckingForUpdates;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        await CheckForUpdatesCoreAsync(showProgress: true);
    }

    public async Task CheckForUpdatesInBackgroundAsync()
    {
        if (!ShouldCheckForUpdatesOnStartup || IsCheckingForUpdates)
        {
            return;
        }

        var result = await CheckForUpdatesCoreAsync(showProgress: false);
        NotifyUpdateAvailableIfNeeded(result);
    }

    private async Task<UpdateCheckResult?> CheckForUpdatesCoreAsync(bool showProgress)
    {
        IsCheckingForUpdates = true;
        if (showProgress)
        {
            UpdateStatusText = "正在检查更新...";
        }

        try
        {
            var currentVersion = new AppInfoService().GetInfo().Version;
            using var handler = _networkProxy.BuildHandler(this, UseProxyForUpdates);
            var result = await _updateCheck.CheckLatestAsync(UpdateFeedUrl, currentVersion, handler);
            LatestReleaseUrl = result.ReleaseUrl;
            var publishedText = result.PublishedAt is { } publishedAt
                ? $" · {publishedAt:yyyy-MM-dd}"
                : "";
            UpdateStatusText = result.IsUpdateAvailable
                ? $"发现新版本 {result.LatestVersion}{publishedText}"
                : $"已是最新版本 {currentVersion}";
            AddLog("更新", result.IsUpdateAvailable
                ? $"发现新版本 {result.LatestVersion}: {result.ReleaseName}"
                : $"已是最新版本 {currentVersion}");
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or JsonException
                                   or ArgumentException
                                   or InvalidOperationException
                                   or TaskCanceledException)
        {
            UpdateStatusText = showProgress
                ? $"检查更新失败: {ex.Message}"
                : "自动检查更新失败";
            AddLog("更新提醒", showProgress ? UpdateStatusText : $"自动检查更新失败: {ex.Message}");
            return null;
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private void NotifyUpdateAvailableIfNeeded(UpdateCheckResult? result)
    {
        if (result is not { IsUpdateAvailable: true })
        {
            return;
        }

        if (string.Equals(_lastNotifiedUpdateVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!EnableDesktopNotifications)
        {
            return;
        }

        _lastNotifiedUpdateVersion = result.LatestVersion;
        var targetUrl = string.IsNullOrWhiteSpace(result.ReleaseUrl)
            ? UpdateFeedUrl
            : result.ReleaseUrl;
        DesktopNotificationRequested?.Invoke(
            this,
            new DesktopNotificationRequest(
                DesktopNotificationKind.Update,
                "发现 E-Detection 新版本",
                $"可更新到 {result.LatestVersion} · {result.ReleaseName}",
                ActionUrl: targetUrl));
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        CopyTextToClipboard(Diagnostics.BuildClipboardText(
            PythonExecutable,
            PythonBackendService.ResolveBackendWorkingDirectory()));
        AddLog("状态检查", "已复制检测组件状态详情。");
    }

    private bool CanCopySelectedDetail() => SelectedDetail is not null;

    [RelayCommand(CanExecute = nameof(CanCopySelectedDetail))]
    private void CopySelectedDetail()
    {
        if (SelectedDetail is null)
        {
            return;
        }

        CopyTextToClipboard(SelectedDetail.ToClipboardText());
        AddLog("复制", "已复制选中的异常明细。");
    }

    private bool CanExplainSelectedDetail() =>
        SelectedDetail is not null && !IsExplainingSelectedDetail;

    [RelayCommand(CanExecute = nameof(CanExplainSelectedDetail))]
    private async Task ExplainSelectedDetailAsync()
    {
        if (SelectedDetail is not { } detail)
        {
            return;
        }

        IsExplainingSelectedDetail = true;
        LlmDetailExplanationText = "正在生成智能解读...";
        try
        {
            var response = await _llmAssistant.ExplainDetailAsync(this, detail.ToClipboardText());
            if (ReferenceEquals(SelectedDetail, detail))
            {
                LlmDetailExplanationText = string.IsNullOrWhiteSpace(response)
                    ? "智能助手已响应，但未返回可显示的解读。"
                    : response;
            }
            AddLog("智能助手", "已生成选中异常明细解读。");
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or InvalidOperationException
                                   or TaskCanceledException
                                   or UriFormatException
                                   or JsonException)
        {
            LlmDetailExplanationText = $"智能解读失败: {ex.Message}";
            AddLog("智能助手提醒", $"异常明细解读失败: {ex.Message}");
        }
        finally
        {
            IsExplainingSelectedDetail = false;
        }
    }

    private bool CanOpenSelectedDetailSource() =>
        SelectedDetail is not null && File.Exists(ResolveSelectedDetailSourcePath());

    [RelayCommand(CanExecute = nameof(CanOpenSelectedDetailSource))]
    private void OpenSelectedDetailSource()
    {
        var path = ResolveSelectedDetailSourcePath();
        if (!File.Exists(path))
        {
            return;
        }

        OpenContainingFolder(path);
        AddLog("定位", "已打开选中异常的源文件位置。");
    }

    private bool CanCopySelectedDetailSourcePath() =>
        SelectedDetail is not null && !string.IsNullOrWhiteSpace(ResolveSelectedDetailSourcePath());

    [RelayCommand(CanExecute = nameof(CanCopySelectedDetailSourcePath))]
    private void CopySelectedDetailSourcePath()
    {
        var path = ResolveSelectedDetailSourcePath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        CopyTextToClipboard(path);
        AddLog("复制", "已复制源文件路径。");
    }

    private string ResolveSelectedDetailSourcePath()
    {
        if (SelectedDetail is not { } detail)
        {
            return "";
        }

        return ResolveDetailSourcePath(detail);
    }

    private string ResolveDetailSourcePath(ReportDetailPreview detail)
    {
        var rawPath = !string.IsNullOrWhiteSpace(detail.RelativePath)
            ? detail.RelativePath
            : detail.SourceFile;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return "";
        }

        if (Path.IsPathRooted(rawPath))
        {
            return rawPath;
        }

        var inputRoot = SelectedReport?.InputDirectory ?? InputDirectory;
        return string.IsNullOrWhiteSpace(inputRoot)
            ? rawPath
            : Path.Combine(inputRoot, rawPath);
    }

    private bool CanCopyFilteredDetails() => WorkbenchFilteredDetailPreview.Any();

    [RelayCommand(CanExecute = nameof(CanCopyFilteredDetails))]
    private void CopyFilteredDetails()
    {
        var rows = WorkbenchFilteredDetailPreview.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        CopyTextToClipboard(_detailPreview.BuildExportText(rows));
        AddLog("复制", $"已复制 {rows.Count} 条异常明细。");
    }

    [RelayCommand]
    private void ClearDetailFilters()
    {
        DetailSearchText = "";
        SelectedSeverityFilterIndex = 0;
        SelectedIssueTypeFilterIndex = 0;
        SelectedDetailSortKey = "";
    }

    [RelayCommand]
    private void SortDetails(string key)
    {
        SelectedDetailSortKey = string.Equals(SelectedDetailSortKey, key, StringComparison.Ordinal)
            ? ""
            : key;
    }

    private static void CopyTextToClipboard(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private void SetTaskbarProgress(TaskbarProgressKind kind, double percent)
    {
        TaskbarProgressKind = kind;
        TaskbarProgressPercent = Math.Clamp(percent, 0, 100);
    }

    private void RequestDesktopNotification(
        DesktopNotificationKind kind,
        string title,
        string message,
        string? reportPath = null)
    {
        if (_isShellShutdownRequested)
        {
            return;
        }

        if (_desktopNotificationSent)
        {
            return;
        }

        if (!EnableDesktopNotifications)
        {
            return;
        }

        _desktopNotificationSent = true;
        DesktopNotificationRequested?.Invoke(
            this,
            new DesktopNotificationRequest(kind, title, message, reportPath));
    }

    private void SaveSettings()
    {
        _settings.Save(new AppSettings
        {
            InputDirectory = InputDirectory,
            OutputDirectory = OutputDirectory,
            ConfigPath = ConfigPath,
            PythonExecutable = PythonExecutable,
            WriteReport = WriteReport,
            CloseToTrayOnClose = CloseToTrayOnClose,
            StartMinimizedToTray = StartMinimizedToTray,
            AutoStartOnSignIn = AutoStartOnSignIn,
            EnableDesktopNotifications = EnableDesktopNotifications,
            EnableLlmAssistant = EnableLlmAssistant,
            LlmEndpoint = LlmEndpoint,
            LlmModel = LlmModel,
            UseProxyForLlm = UseProxyForLlm,
            EnableNtfyNotifications = EnableNtfyNotifications,
            NtfyServerUrl = NtfyServerUrl,
            NtfyTopic = NtfyTopic,
            SelectedNtfyPriorityIndex = SelectedNtfyPriorityIndex,
            UseProxyForNotifications = UseProxyForNotifications,
            EnableNetworkProxy = EnableNetworkProxy,
            ProxyAddress = ProxyAddress,
            ProxyRequiresAuthentication = ProxyRequiresAuthentication,
            ProxyUserName = ProxyUserName,
            EnableUpdateChecks = EnableUpdateChecks,
            UseProxyForUpdates = UseProxyForUpdates,
            SelectedUpdateChannelIndex = SelectedUpdateChannelIndex,
            UpdateFeedUrl = UpdateFeedUrl,
            EnableGlobalHotkeys = false,
            EnableQuickActionsShortcut = false,
            SelectedQuickActionsShortcutIndex = 2,
            SelectedLogRetentionIndex = SelectedLogRetentionIndex,
            SelectedRecentReportLimitIndex = ReportHistory.SelectedRecentReportLimitIndex,
            RecentReports = RecentReports.ToList(),
            SelectedThemeIndex = SelectedThemeIndex,
            SelectedBackdropIndex = SelectedBackdropIndex,
            EnablePoetryStatus = EnablePoetryStatus,
            PoetryServiceUrl = PoetryServiceUrl,
            SelectedPoetryLanguageIndex = SelectedPoetryLanguageIndex,
            WindowLeft = WindowLeft,
            WindowTop = WindowTop,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            IsWindowMaximized = IsWindowMaximized,
        });
    }

    private void ApplySettingsDefaults(AppSettings defaults)
    {
        InputDirectory = defaults.InputDirectory;
        OutputDirectory = defaults.OutputDirectory;
        ConfigPath = _detectionConfig.EnsureUserConfig(defaults.ConfigPath);
        PythonExecutable = defaults.PythonExecutable;
        WriteReport = defaults.WriteReport;
        CloseToTrayOnClose = defaults.CloseToTrayOnClose;
        StartMinimizedToTray = defaults.StartMinimizedToTray;
        EnableDesktopNotifications = defaults.EnableDesktopNotifications;
        EnableLlmAssistant = defaults.EnableLlmAssistant;
        LlmEndpoint = defaults.LlmEndpoint;
        LlmModel = defaults.LlmModel;
        UseProxyForLlm = defaults.UseProxyForLlm;
        EnableNtfyNotifications = defaults.EnableNtfyNotifications;
        NtfyServerUrl = defaults.NtfyServerUrl;
        NtfyTopic = defaults.NtfyTopic;
        SelectedNtfyPriorityIndex = defaults.SelectedNtfyPriorityIndex;
        UseProxyForNotifications = defaults.UseProxyForNotifications;
        EnableNetworkProxy = defaults.EnableNetworkProxy;
        ProxyAddress = defaults.ProxyAddress;
        ProxyRequiresAuthentication = defaults.ProxyRequiresAuthentication;
        ProxyUserName = defaults.ProxyUserName;
        EnableUpdateChecks = defaults.EnableUpdateChecks;
        UseProxyForUpdates = defaults.UseProxyForUpdates;
        SelectedUpdateChannelIndex = defaults.SelectedUpdateChannelIndex;
        UpdateFeedUrl = defaults.UpdateFeedUrl;
        EnableGlobalHotkeys = false;
        SelectedQuickActionsShortcutIndex = 2;
        EnableQuickActionsShortcut = false;
        RuntimeLogs.SelectedRetentionIndex = defaults.SelectedLogRetentionIndex;
        ReportHistory.SelectedRecentReportLimitIndex = defaults.SelectedRecentReportLimitIndex;
        SelectedThemeIndex = defaults.SelectedThemeIndex;
        SelectedBackdropIndex = defaults.SelectedBackdropIndex;
        EnablePoetryStatus = defaults.EnablePoetryStatus;
        PoetryServiceUrl = defaults.PoetryServiceUrl;
        SelectedPoetryLanguageIndex = defaults.SelectedPoetryLanguageIndex;

        try
        {
            _startup.SetEnabled(defaults.AutoStartOnSignIn);
        }
        catch (Exception ex)
        {
            StartupIntegrationStatusText = $"重置登录自启动失败: {ex.Message}";
        }

        _syncingStartupPreference = true;
        var startupStatus = _startup.GetStatus();
        AutoStartOnSignIn = startupStatus.IsEnabled;
        _syncingStartupPreference = false;
        UpdateStartupIntegrationStatus(startupStatus);
    }

    private void ApplyDetectionConfig(DetectionConfigSettings config)
    {
        VoltageMinThreshold = config.VoltageMinThreshold;
        VoltageMaxThreshold = config.VoltageMaxThreshold;
        CurrentMaxThreshold = config.CurrentMaxThreshold;
        CurrentUnbalanceMaxThreshold = config.CurrentUnbalanceMaxThreshold;
        ActivePowerMinThreshold = config.ActivePowerMinThreshold;
        PowerFactorMinThreshold = config.PowerFactorMinThreshold;
        TemperatureMinThreshold = config.TemperatureMinThreshold;
        TemperatureMaxThreshold = config.TemperatureMaxThreshold;
        CurrentActiveMinThreshold = config.CurrentActiveMinThreshold;
        FreezeCountThreshold = config.FreezeCountThreshold;
        FreezeStdThreshold = config.FreezeStdThreshold;
        VoltageImbalanceThreshold = config.VoltageImbalanceThreshold;
        CurrentOverloadEnabled = config.CurrentOverloadEnabled;
        CurrentUnbalanceEnabled = config.CurrentUnbalanceEnabled;
        PowerFactorEnabled = config.PowerFactorEnabled;
        DetailOutputEnabled = config.DetailOutputEnabled;
    }

    private DetectionConfigSettings CaptureDetectionConfig() => new()
    {
        VoltageMinThreshold = VoltageMinThreshold,
        VoltageMaxThreshold = VoltageMaxThreshold,
        CurrentMaxThreshold = CurrentMaxThreshold,
        CurrentUnbalanceMaxThreshold = CurrentUnbalanceMaxThreshold,
        ActivePowerMinThreshold = ActivePowerMinThreshold,
        PowerFactorMinThreshold = PowerFactorMinThreshold,
        TemperatureMinThreshold = TemperatureMinThreshold,
        TemperatureMaxThreshold = TemperatureMaxThreshold,
        CurrentActiveMinThreshold = CurrentActiveMinThreshold,
        FreezeCountThreshold = FreezeCountThreshold,
        FreezeStdThreshold = FreezeStdThreshold,
        VoltageImbalanceThreshold = VoltageImbalanceThreshold,
        CurrentOverloadEnabled = CurrentOverloadEnabled,
        CurrentUnbalanceEnabled = CurrentUnbalanceEnabled,
        PowerFactorEnabled = PowerFactorEnabled,
        DetailOutputEnabled = DetailOutputEnabled,
    };

    private bool SaveDetectionConfig()
    {
        try
        {
            var configPath = EnsureEffectiveConfigPath();
            _detectionConfig.Save(configPath, CaptureDetectionConfig());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var message = $"检测阈值保存失败: {ex.Message}";
            ShowSettingsFeedback(message, InfoBarSeverity.Warning);
            AddLog("设置警告", message);
            return false;
        }
    }

    private string EnsureEffectiveConfigPath()
    {
        var configPath = _detectionConfig.EnsureUserConfig(ConfigPath);
        if (!string.Equals(ConfigPath, configPath, StringComparison.OrdinalIgnoreCase))
        {
            _suppressConfigPathReload = true;
            try
            {
                ConfigPath = configPath;
            }
            finally
            {
                _suppressConfigPathReload = false;
            }
        }

        return configPath;
    }

    private void ShowSettingsFeedback(string message, InfoBarSeverity severity)
    {
        SettingsFeedbackText = message;
        SettingsFeedbackSeverity = severity;
        IsSettingsFeedbackOpen = true;
    }

    public void SaveWindowPlacement(int left, int top, int width, int height, bool isMaximized)
    {
        WindowLeft = left;
        WindowTop = top;
        WindowWidth = width;
        WindowHeight = height;
        IsWindowMaximized = isMaximized;
        SaveSettings();
    }

    private void SaveAppearanceSettings()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        SaveSettings();
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SavePreferenceSettings()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        SaveSettings();
    }

    private void UpdateStartupIntegrationStatus(StartupIntegrationSnapshot status)
    {
        StartupIntegrationStatusText = status.StatusText;
    }

    private void AddRecentReport(string path, DetectionBackendEvent? evt)
    {
        var added = ReportHistory.AddOrUpdate(
            path,
            evt,
            BuildRecentReportUpdateContext());
        if (!added)
        {
            return;
        }

        NotifyWorkbenchSnapshotChanged();
        ClearRecentReportsCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    private RecentReportUpdateContext BuildRecentReportUpdateContext() =>
        new(
            InputDirectory,
            OutputDirectory,
            TotalFiles,
            ProcessedFiles,
            AnomalyRecords,
            AnomalyFiles,
            SkippedFiles,
            ReportDeviceCount,
            HighRiskDevices.ToList(),
            TopIssueTypes.ToList(),
            new ReportSensorOverview
            {
                OfflineDevices = SensorOfflineDevices,
                SensorFaultRows = SensorFaultRows,
                SensorMissingRows = SensorMissingRows,
                SkippedRows = SensorSkippedRows,
            },
            DetailPreviewTotalCount,
            DetailPreview.ToList());

    private bool CanOpenCurrentReport() =>
        !string.IsNullOrWhiteSpace(WorkbenchReportPath) && File.Exists(WorkbenchReportPath);

    [RelayCommand(CanExecute = nameof(CanOpenCurrentReport))]
    private void OpenCurrentReport() => OpenPath(WorkbenchReportPath);

    [RelayCommand(CanExecute = nameof(CanOpenCurrentReport))]
    private void OpenCurrentReportFolder() => OpenContainingFolder(WorkbenchReportPath);

    private bool CanCopyCurrentReportPath() =>
        !string.IsNullOrWhiteSpace(WorkbenchReportPath);

    [RelayCommand(CanExecute = nameof(CanCopyCurrentReportPath))]
    private void CopyCurrentReportPath()
    {
        CopyTextToClipboard(WorkbenchReportPath);
        AddLog("复制", "已复制报告路径。");
    }

    private bool CanOpenSelectedReport() =>
        SelectedReport is not null && File.Exists(SelectedReport.Path);

    [RelayCommand(CanExecute = nameof(CanOpenSelectedReport))]
    private void OpenSelectedReport()
    {
        if (SelectedReport is not null)
        {
            OpenPath(SelectedReport.Path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedReport))]
    private void OpenSelectedReportFolder()
    {
        if (SelectedReport is not null)
        {
            OpenContainingFolder(SelectedReport.Path);
        }
    }

    private bool CanUseSelectedReportDirectories() =>
        !IsRunning && SelectedReport is not null && !string.IsNullOrWhiteSpace(SelectedReport.InputDirectory);

    [RelayCommand(CanExecute = nameof(CanUseSelectedReportDirectories))]
    private void UseSelectedReportDirectories()
    {
        if (SelectedReport is null)
        {
            return;
        }

        InputDirectory = SelectedReport.InputDirectory;
        if (!string.IsNullOrWhiteSpace(SelectedReport.OutputDirectory))
        {
            OutputDirectory = SelectedReport.OutputDirectory;
        }

        StatusText = $"已复用路径: {SelectedReport.FileName}";
        SaveSettings();
    }

    private bool CanRemoveSelectedReport() => SelectedReport is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedReport))]
    private void RemoveSelectedReport()
    {
        if (!ReportHistory.RemoveSelected())
        {
            return;
        }

        NotifyWorkbenchSnapshotChanged();
        ClearRecentReportsCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    private bool CanClearRecentReports() => RecentReports.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearRecentReports))]
    private void ClearRecentReports()
    {
        ReportHistory.Clear();
        NotifyWorkbenchSnapshotChanged();
        ClearRecentReportsCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    private static void OpenPath(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private static void OpenContainingFolder(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { "/select,", path },
            UseShellExecute = true,
        });
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowSettingsFeedback($"打开外部链接失败: {ex.Message}", InfoBarSeverity.Warning);
        }
    }
}
