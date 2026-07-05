namespace EDetection.Desktop.Models;

public sealed record RunSummarySnapshot(
    int TotalFiles,
    int ProcessedFiles,
    int AnomalyFiles,
    int AnomalyRecords,
    int SkippedFiles,
    string ReportPath);
