using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class ReportDetailPreviewService
{
    private const string AllIssueTypesFilter = "全部类型";

    public ReportDetailRefreshResult Refresh(
        IEnumerable<ReportDetailPreview> details,
        ReportDetailPreview? selectedDetail,
        IReadOnlyList<string> currentIssueTypeFilters,
        ReportDetailFilterState filter)
    {
        var issueTypeFilters = BuildIssueTypeFilters(details);
        var selectedIssueType = filter.IssueTypeFilterIndex > 0
            && filter.IssueTypeFilterIndex < currentIssueTypeFilters.Count
                ? currentIssueTypeFilters[filter.IssueTypeFilterIndex]
                : "";
        var nextIssueTypeIndex = string.IsNullOrWhiteSpace(selectedIssueType)
            ? 0
            : Math.Max(0, issueTypeFilters.IndexOf(selectedIssueType));
        var effectiveFilter = filter with { IssueTypeFilterIndex = nextIssueTypeIndex };
        var filtered = Filter(details, issueTypeFilters, effectiveFilter).ToList();
        var nextSelected = selectedDetail is not null && !filtered.Contains(selectedDetail)
            ? null
            : selectedDetail;

        return new ReportDetailRefreshResult(
            filtered,
            issueTypeFilters,
            nextIssueTypeIndex,
            nextSelected);
    }

    public IReadOnlyList<ReportDetailPreview> Filter(
        IEnumerable<ReportDetailPreview> details,
        IReadOnlyList<string> issueTypeFilters,
        ReportDetailFilterState filter)
    {
        var query = filter.SearchText.Trim();
        var severity = filter.SeverityFilterIndex switch
        {
            1 => "高",
            2 => "中",
            3 => "低",
            _ => "",
        };
        var issueType = filter.IssueTypeFilterIndex > 0
            && filter.IssueTypeFilterIndex < issueTypeFilters.Count
                ? issueTypeFilters[filter.IssueTypeFilterIndex]
                : "";

        var filtered = details.Where(item => item.Matches(query, severity, issueType));
        var sorted = filter.SortKey switch
        {
            "severity" => filtered.OrderBy(item => SeverityRank(item.Severity)).ThenBy(item => item.TimeText),
            "device" => filtered.OrderBy(item => item.LocationText).ThenBy(item => item.TimeText),
            "time" => filtered.OrderByDescending(item => item.TimeText).ThenBy(item => item.LocationText),
            "issue" => filtered.OrderBy(item => item.IssueText).ThenBy(item => item.LocationText),
            "value" => filtered.OrderBy(item => item.ValueText).ThenBy(item => item.LocationText),
            _ => filtered,
        };

        return sorted.ToList();
    }

    public string BuildExportText(IEnumerable<ReportDetailPreview> details)
    {
        var rows = details.ToList();
        var lines = new List<string>
        {
            "等级\t设备\t时间\t异常\t异常值\t建议",
        };
        lines.AddRange(rows.Select(detail => string.Join(
            "\t",
            detail.Severity ?? "",
            detail.LocationText,
            detail.TimeText,
            detail.IssueText,
            detail.ValueText,
            detail.RecommendedAction ?? "")));
        return string.Join(Environment.NewLine, lines);
    }

    public List<string> BuildIssueTypeFilters(IEnumerable<ReportDetailPreview> details)
    {
        var issueTypes = details
            .Select(detail => detail.IssueType)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value)
            .ToList();
        issueTypes.Insert(0, AllIssueTypesFilter);
        return issueTypes!;
    }

    private static int SeverityRank(string? severity) => severity switch
    {
        "高" => 0,
        "中" => 1,
        "低" => 2,
        _ => 3,
    };
}
