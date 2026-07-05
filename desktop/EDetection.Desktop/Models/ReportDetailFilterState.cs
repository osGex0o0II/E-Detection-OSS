namespace EDetection.Desktop.Models;

public sealed record ReportDetailFilterState(
    string SearchText,
    int SeverityFilterIndex,
    int IssueTypeFilterIndex,
    string SortKey);
