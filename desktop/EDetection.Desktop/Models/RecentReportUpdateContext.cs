namespace EDetection.Desktop.Models;

public sealed record RecentReportUpdateContext(
    string InputDirectory,
    string OutputDirectory,
    int TotalFiles,
    int ProcessedFiles,
    int AnomalyRecords,
    int AnomalyFiles,
    int SkippedFiles,
    int DeviceCount,
    IReadOnlyList<ReportDeviceSummary> HighRiskDevices,
    IReadOnlyList<ReportIssueType> TopIssueTypes,
    ReportSensorOverview SensorOverview,
    int DetailPreviewCount,
    IReadOnlyList<ReportDetailPreview> DetailPreview);
