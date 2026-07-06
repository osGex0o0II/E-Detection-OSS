namespace EDetection.Desktop.Models;

public sealed record DesktopNotificationActivation(
    string Action,
    string? ReportPath,
    string? ActionUrl = null)
{
    public const string OpenWorkbenchAction = "openWorkbench";
    public const string OpenReportAction = "openReport";
    public const string OpenUpdateAction = "openUpdate";
}
