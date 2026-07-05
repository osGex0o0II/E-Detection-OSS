namespace EDetection.Desktop.Models;

public sealed record DesktopNotificationActivation(
    string Action,
    string? ReportPath)
{
    public const string OpenWorkbenchAction = "openWorkbench";
    public const string OpenReportAction = "openReport";
}
