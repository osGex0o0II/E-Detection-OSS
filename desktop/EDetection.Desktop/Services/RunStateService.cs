using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class RunStateService
{
    public RunSummarySnapshot ResetSummary { get; } = new(
        0,
        0,
        0,
        0,
        0,
        "");

    public ReportSummarySnapshot ResetReportSummary { get; } = new(
        0,
        [],
        [],
        new ReportSensorOverview(),
        0,
        []);

    public RunSummarySnapshot BuildSummary(
        DetectionBackendEvent evt,
        RunSummarySnapshot current)
    {
        return current with
        {
            TotalFiles = evt.TotalFiles ?? current.TotalFiles,
            ProcessedFiles = evt.ProcessedFiles ?? current.ProcessedFiles,
            AnomalyFiles = evt.AnomalyFiles ?? current.AnomalyFiles,
            AnomalyRecords = evt.AnomalyRecords ?? current.AnomalyRecords,
            SkippedFiles = evt.SkippedFiles ?? current.SkippedFiles,
            ReportPath = evt.ReportPath ?? current.ReportPath,
        };
    }

    public ReportSummarySnapshot BuildReportSummary(DetectionBackendEvent evt)
    {
        var sensor = evt.SensorOverview ?? new ReportSensorOverview();
        return new ReportSummarySnapshot(
            evt.DeviceCount ?? 0,
            evt.HighRiskDevices?.ToList() ?? [],
            evt.TopIssueTypes?.ToList() ?? [],
            new ReportSensorOverview
            {
                OfflineDevices = sensor.OfflineDevices,
                SensorFaultRows = sensor.SensorFaultRows,
                SensorMissingRows = sensor.SensorMissingRows,
                SkippedRows = sensor.SkippedRows,
            },
            evt.DetailPreviewCount ?? evt.DetailPreview?.Count ?? 0,
            evt.DetailPreview?.ToList() ?? []);
    }
}
