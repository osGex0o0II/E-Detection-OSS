using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class RunEventService
{
    public RunEventAction BuildAction(
        DetectionBackendEvent evt,
        RunEventState state)
    {
        return evt.EventName switch
        {
            "run_started" => BuildRunStarted(evt),
            "file_result" => BuildFileResult(evt),
            "file_progress" => BuildFileProgress(evt),
            "report_written" => BuildReportWritten(evt),
            "report_summary" => new RunEventAction
            {
                CurrentFileText = "正在汇总报告数据",
                ApplyReportSummary = true,
            },
            "run_completed" => BuildRunCompleted(state),
            "error" => BuildError(evt, state),
            "stderr" => new RunEventAction
            {
                LogKind = "stderr",
                LogMessage = evt.Message ?? "",
            },
            _ => new RunEventAction
            {
                LogKind = evt.EventName,
                LogMessage = evt.Message ?? evt.SourceFile ?? "",
            },
        };
    }

    public string BuildReportSummaryLogMessage(RunEventState state) =>
        $"高风险设备 {state.HighRiskDeviceCount} 个，异常类型 {state.TopIssueTypeCount} 类。";

    public string BuildRunCompletedLogMessage(RunEventState state) =>
        HasFaultResult(state)
            ? $"发现故障: 异常 {state.AnomalyRecords} 条，传感器问题 {BuildSensorIssueCount(state)} 项，跳过 {state.SkippedFiles} 个文件。"
            : HasIncompleteResult(state)
                ? $"检测部分完成: 已处理 {state.ProcessedFiles}/{state.TotalFiles} 个文件，跳过 {state.SkippedFiles} 个文件。"
                : $"检测成功: 已处理 {state.ProcessedFiles}/{state.TotalFiles} 个文件，未发现异常。";

    public DesktopNotificationRequest BuildRunCompletedNotification(
        RunEventState state,
        string? reportPath)
    {
        var normalizedReportPath = string.IsNullOrWhiteSpace(reportPath) ? null : reportPath;
        if (!HasFaultResult(state) && !HasIncompleteResult(state))
        {
            return new DesktopNotificationRequest(
                DesktopNotificationKind.Success,
                "检测成功",
                $"已完成 {state.ProcessedFiles}/{state.TotalFiles} 个文件，未发现异常或传感器故障。",
                normalizedReportPath);
        }

        var sensorIssues = BuildSensorIssueCount(state);
        var allProcessedFilesSkipped = state.ProcessedFiles > 0 && state.SkippedFiles >= state.ProcessedFiles;
        var title = allProcessedFilesSkipped
            ? "检测未完成"
            : HasFaultResult(state)
                ? "检测发现故障"
                : "检测部分完成";
        var message = sensorIssues > 0
            ? $"异常 {state.AnomalyRecords} 条，异常文件 {state.AnomalyFiles} 个，传感器问题 {sensorIssues} 项，跳过 {state.SkippedFiles} 个。"
            : state.SkippedFiles > 0
                ? $"已处理 {state.ProcessedFiles}/{state.TotalFiles} 个文件，其中 {state.SkippedFiles} 个未完成处理。"
                : $"异常 {state.AnomalyRecords} 条，异常文件 {state.AnomalyFiles} 个。";

        return new DesktopNotificationRequest(
            allProcessedFilesSkipped ? DesktopNotificationKind.Error : DesktopNotificationKind.Warning,
            title,
            message,
            normalizedReportPath);
    }

    private static RunEventAction BuildRunStarted(DetectionBackendEvent evt)
    {
        var totalFiles = evt.TotalFiles ?? 0;
        var progressPercent = totalFiles == 0 ? 100 : 0;
        var status = $"开始检测，共 {totalFiles} 个文件";
        return new RunEventAction
        {
            TotalFiles = totalFiles,
            ProcessedFiles = 0,
            ProgressPercent = progressPercent,
            CurrentFileText = totalFiles == 0 ? "没有可处理的 CSV 文件" : "等待首个文件",
            StatusText = status,
            TaskbarKind = TaskbarProgressKind.Normal,
            TaskbarPercent = progressPercent,
            LogKind = "开始",
            LogMessage = status,
        };
    }

    private static RunEventAction BuildFileResult(DetectionBackendEvent evt)
    {
        var sourceText = evt.RelativePath ?? evt.SourceFile;
        return new RunEventAction
        {
            ProcessedFiles = evt.ProcessedFiles,
            TotalFiles = evt.TotalFiles,
            CurrentFileText = string.IsNullOrWhiteSpace(sourceText) ? null : $"已完成: {sourceText}",
            LogKind = evt.Status ?? "文件",
            LogMessage = evt.Message ?? evt.SourceFile ?? "文件处理完成",
        };
    }

    private static RunEventAction BuildFileProgress(DetectionBackendEvent evt)
    {
        var progressPercent = Math.Clamp((evt.Percent ?? 0) * 100, 0, 100);
        var status = evt.SourceFile is null
            ? "处理中..."
            : $"处理中: {evt.SourceFile}";
        return new RunEventAction
        {
            ProcessedFiles = evt.ProcessedFiles,
            TotalFiles = evt.TotalFiles,
            ProgressPercent = progressPercent,
            CurrentFileText = evt.RelativePath ?? evt.SourceFile ?? "处理中...",
            StatusText = status,
            TaskbarKind = TaskbarProgressKind.Normal,
            TaskbarPercent = progressPercent,
        };
    }

    private static RunEventAction BuildReportWritten(DetectionBackendEvent evt) =>
        new()
        {
            CurrentFileText = "报告已写入",
            LogKind = "报告",
            LogMessage = string.IsNullOrWhiteSpace(evt.ReportPath) ? "报告已生成" : evt.ReportPath,
            AddRecentReport = true,
        };

    private static RunEventAction BuildRunCompleted(RunEventState state) =>
        new()
        {
            ProgressPercent = 100,
            CurrentFileText = "检测完成",
            StatusText = "检测完成",
            StopTelemetry = true,
            TaskbarKind = TaskbarProgressKind.Normal,
            TaskbarPercent = 100,
            ApplySummary = true,
        };

    private static RunEventAction BuildError(
        DetectionBackendEvent evt,
        RunEventState state)
    {
        var failure = evt.Message ?? evt.ErrorType ?? "原生检测核心返回错误。";
        return new RunEventAction
        {
            StatusText = "检测失败",
            CurrentFileText = "检测失败",
            LastFailureText = failure,
            StopTelemetry = true,
            TaskbarKind = TaskbarProgressKind.Error,
            TaskbarPercent = Math.Max(state.ProgressPercent, 100),
            LogKind = "错误",
            LogMessage = failure,
            Notification = new DesktopNotificationRequest(
                DesktopNotificationKind.Error,
                "检测失败",
                failure),
        };
    }

    private static bool HasFaultResult(RunEventState state) =>
        state.AnomalyRecords > 0
        || state.AnomalyFiles > 0
        || state.SensorOfflineDevices > 0
        || state.SensorFaultRows > 0
        || state.SensorMissingRows > 0;

    private static bool HasIncompleteResult(RunEventState state) =>
        state.SkippedFiles > 0;

    private static int BuildSensorIssueCount(RunEventState state) =>
        state.SensorOfflineDevices
        + state.SensorFaultRows
        + state.SensorMissingRows;
}
