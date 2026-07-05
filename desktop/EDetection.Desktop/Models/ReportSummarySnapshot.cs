namespace EDetection.Desktop.Models;

public sealed record ReportSummarySnapshot(
    int DeviceCount,
    IReadOnlyList<ReportDeviceSummary> HighRiskDevices,
    IReadOnlyList<ReportIssueType> TopIssueTypes,
    ReportSensorOverview SensorOverview,
    int DetailPreviewCount,
    IReadOnlyList<ReportDetailPreview> DetailPreview);
