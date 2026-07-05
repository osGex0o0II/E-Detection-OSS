namespace EDetection.Desktop.Models;

public sealed record ReportDetailRefreshResult(
    IReadOnlyList<ReportDetailPreview> FilteredDetails,
    IReadOnlyList<string> IssueTypeFilters,
    int SelectedIssueTypeFilterIndex,
    ReportDetailPreview? SelectedDetail);
