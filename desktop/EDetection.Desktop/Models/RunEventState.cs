namespace EDetection.Desktop.Models;

public sealed record RunEventState(
    int TotalFiles,
    int ProcessedFiles,
    int AnomalyFiles,
    int AnomalyRecords,
    int SkippedFiles,
    double ProgressPercent,
    int HighRiskDeviceCount,
    int TopIssueTypeCount,
    int SensorOfflineDevices,
    int SensorFaultRows,
    int SensorMissingRows,
    int SensorSkippedRows);
