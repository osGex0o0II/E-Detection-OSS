namespace EDetection.Desktop.Models;

public sealed record ReportHistoryRefreshResult(
    IReadOnlyList<RecentReport> FilteredReports,
    RecentReport? SelectedReport);
