using System.Collections.ObjectModel;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class RuntimeLogService
{
    private const string AllKindsFilter = "全部类型";

    public DetectionLogItem Add(
        ObservableCollection<DetectionLogItem> items,
        string kind,
        string message,
        int retentionLimit)
    {
        var item = new DetectionLogItem(kind, message);
        items.Insert(0, item);
        Trim(items, retentionLimit);
        return item;
    }

    public void Trim(
        ObservableCollection<DetectionLogItem> items,
        int retentionLimit)
    {
        while (items.Count > retentionLimit)
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    public RuntimeLogRefreshResult Refresh(
        IEnumerable<DetectionLogItem> items,
        IReadOnlyList<string> currentKindFilters,
        int selectedKindFilterIndex,
        string searchText)
    {
        var selected = selectedKindFilterIndex > 0
            && selectedKindFilterIndex < currentKindFilters.Count
                ? currentKindFilters[selectedKindFilterIndex]
                : "";
        var kindFilters = BuildKindFilters(items);
        var nextSelectedIndex = string.IsNullOrWhiteSpace(selected)
            ? 0
            : Math.Max(0, kindFilters.IndexOf(selected));
        var kind = nextSelectedIndex > 0
            && nextSelectedIndex < kindFilters.Count
                ? kindFilters[nextSelectedIndex]
                : "";
        var query = searchText.Trim();
        var filtered = items
            .Where(item => item.Matches(query, kind))
            .ToList();
        return new RuntimeLogRefreshResult(filtered, kindFilters, nextSelectedIndex);
    }

    public void Clear(
        ObservableCollection<DetectionLogItem> items,
        ObservableCollection<DetectionLogItem> filteredItems,
        ObservableCollection<string> kindFilters)
    {
        items.Clear();
        filteredItems.Clear();
        kindFilters.Clear();
        kindFilters.Add(AllKindsFilter);
    }

    public string BuildExportText(IEnumerable<DetectionLogItem> items)
    {
        var lines = new List<string>
        {
            "时间\t类型\t消息",
        };
        lines.AddRange(items.Select(item => item.ToTsv()));
        return string.Join(Environment.NewLine, lines);
    }

    private static List<string> BuildKindFilters(IEnumerable<DetectionLogItem> items)
    {
        var kinds = items
            .Select(item => item.Kind)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(kind => kind)
            .ToList();
        kinds.Insert(0, AllKindsFilter);
        return kinds;
    }
}
