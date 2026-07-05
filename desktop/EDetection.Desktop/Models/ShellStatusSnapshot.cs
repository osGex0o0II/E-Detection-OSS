namespace EDetection.Desktop.Models;

public sealed record ShellStatusSnapshot(
    string StatusText,
    bool IsRunning,
    TaskbarProgressKind TaskbarProgressKind,
    double TaskbarProgressPercent)
{
    public static ShellStatusSnapshot Idle { get; } = new(
        "就绪",
        IsRunning: false,
        TaskbarProgressKind.None,
        TaskbarProgressPercent: 0);

    public string NormalizedStatusText =>
        string.IsNullOrWhiteSpace(StatusText) ? "就绪" : StatusText;

    public string TrayTooltip =>
        $"{(IsRunning ? "E-Detection - 运行中" : "E-Detection")}: {NormalizedStatusText}";

    public string TrayMenuStatusText =>
        IsRunning ? $"运行中: {NormalizedStatusText}" : $"状态: {NormalizedStatusText}";
}
