namespace EDetection.Desktop.Models;

public sealed class RunEventAction
{
    public int? TotalFiles { get; init; }

    public int? ProcessedFiles { get; init; }

    public double? ProgressPercent { get; init; }

    public string? CurrentFileText { get; init; }

    public string? StatusText { get; init; }

    public string? LastFailureText { get; init; }

    public TaskbarProgressKind? TaskbarKind { get; init; }

    public double? TaskbarPercent { get; init; }

    public string? LogKind { get; init; }

    public string? LogMessage { get; init; }

    public DesktopNotificationRequest? Notification { get; init; }

    public bool StopTelemetry { get; init; }

    public bool ApplySummary { get; init; }

    public bool ApplyReportSummary { get; init; }

    public bool AddRecentReport { get; init; }
}
