namespace EDetection.Desktop.Models;

public sealed class AppSettings
{
    public int SettingsVersion { get; set; }

    public string InputDirectory { get; set; } = "";

    public string OutputDirectory { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string PythonExecutable { get; set; } = "python";

    public bool WriteReport { get; set; } = true;

    public bool CloseToTrayOnClose { get; set; }

    public bool StartMinimizedToTray { get; set; }

    public bool AutoStartOnSignIn { get; set; }

    public bool EnableDesktopNotifications { get; set; } = true;

    public bool EnableGlobalHotkeys { get; set; } = true;

    public bool EnableQuickActionsShortcut { get; set; }

    public int SelectedQuickActionsShortcutIndex { get; set; } = 2;

    public int SelectedLogRetentionIndex { get; set; } = 1;

    public int SelectedRecentReportLimitIndex { get; set; } = 1;

    public List<RecentReport> RecentReports { get; set; } = [];

    public int SelectedThemeIndex { get; set; } = 0;

    public int SelectedBackdropIndex { get; set; } = 0;

    public int WindowLeft { get; set; } = -1;

    public int WindowTop { get; set; } = -1;

    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    public bool IsWindowMaximized { get; set; }
}
