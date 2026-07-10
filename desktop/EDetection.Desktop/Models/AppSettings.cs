namespace EDetection.Desktop.Models;

public sealed class AppSettings
{
    public int SettingsVersion { get; set; }

    public string InputDirectory { get; set; } = "";

    public string OutputDirectory { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public bool WriteReport { get; set; } = true;

    public bool CloseToTrayOnClose { get; set; }

    public bool StartMinimizedToTray { get; set; }

    public bool AutoStartOnSignIn { get; set; }

    public bool EnableDesktopNotifications { get; set; } = true;

    public bool EnableLlmAssistant { get; set; }

    public string LlmEndpoint { get; set; } = "";

    public string LlmModel { get; set; } = "";

    public bool UseProxyForLlm { get; set; }

    public bool EnableNtfyNotifications { get; set; }

    public string NtfyServerUrl { get; set; } = "https://ntfy.sh";

    public string NtfyTopic { get; set; } = "";

    public int SelectedNtfyPriorityIndex { get; set; } = 2;

    public bool UseProxyForNotifications { get; set; }

    public bool EnableNetworkProxy { get; set; }

    public string ProxyAddress { get; set; } = "";

    public bool ProxyRequiresAuthentication { get; set; }

    public string ProxyUserName { get; set; } = "";

    public bool EnableUpdateChecks { get; set; } = true;

    public bool UseProxyForUpdates { get; set; }

    public int SelectedUpdateChannelIndex { get; set; }

    public string UpdateFeedUrl { get; set; } = "https://github.com/osGex0o0II/E-Detection-OSS/releases/latest";

    public int SelectedLogRetentionIndex { get; set; } = 1;

    public int SelectedRecentReportLimitIndex { get; set; } = 1;

    public List<RecentReport> RecentReports { get; set; } = [];

    public int SelectedThemeIndex { get; set; } = 0;

    public int SelectedBackdropIndex { get; set; } = 0;

    public bool EnablePoetryStatus { get; set; }

    public string PoetryServiceUrl { get; set; } = "https://poetry.palemoky.com/";

    public int SelectedPoetryLanguageIndex { get; set; }

    public int WindowLeft { get; set; } = -1;

    public int WindowTop { get; set; } = -1;

    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    public bool IsWindowMaximized { get; set; }
}
