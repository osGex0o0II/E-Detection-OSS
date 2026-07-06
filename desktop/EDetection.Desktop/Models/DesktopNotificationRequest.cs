namespace EDetection.Desktop.Models;

public sealed record DesktopNotificationRequest(
    DesktopNotificationKind Kind,
    string Title,
    string Message,
    string? ReportPath = null,
    string? ActionUrl = null);
