namespace EDetection.Desktop.Models;

public sealed record DesktopHealthSnapshot(
    string SummaryText,
    string NotificationText,
    string StartupText,
    string SettingsText,
    string PackageText,
    string PythonBridgeText,
    string InstallText,
    string HotkeyText);
