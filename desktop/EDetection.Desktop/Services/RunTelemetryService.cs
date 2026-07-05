using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class RunTelemetryService
{
    public RunTelemetrySnapshot Build(
        TimeSpan elapsed,
        bool isRunning,
        int processedFiles,
        int totalFiles,
        double progressPercent)
    {
        var elapsedText = FormatDuration(elapsed);
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var speed = processedFiles > 0 ? processedFiles / seconds : 0;
        var speedText = speed > 0
            ? $"{speed:0.0} 文件/秒"
            : "计算中";

        string remainingText;
        if (totalFiles > 0 && processedFiles >= totalFiles)
        {
            remainingText = "0秒";
        }
        else if (totalFiles > 0 && speed > 0)
        {
            var remainingSeconds = Math.Max(0, (totalFiles - processedFiles) / speed);
            remainingText = $"约 {FormatDuration(TimeSpan.FromSeconds(remainingSeconds))}";
        }
        else
        {
            remainingText = "计算中";
        }

        var progressDetailText = totalFiles > 0
            ? $"已处理 {processedFiles}/{totalFiles} · {progressPercent:0}%"
            : processedFiles > 0
                ? $"已处理 {processedFiles} 个文件"
                : isRunning
                    ? "等待文件事件"
                    : "尚未开始";

        return new RunTelemetrySnapshot(
            elapsedText,
            speedText,
            remainingText,
            progressDetailText);
    }

    public RunTelemetrySnapshot InitialSnapshot { get; } = new(
        "0秒",
        "计算中",
        "计算中",
        "等待文件事件");

    public RunTelemetrySnapshot ResetSnapshot { get; } = new(
        "0秒",
        "计算中",
        "计算中",
        "尚未开始");

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}小时 {duration.Minutes}分";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}分 {duration.Seconds}秒";
        }

        return $"{Math.Max(0, (int)Math.Round(duration.TotalSeconds))}秒";
    }
}
