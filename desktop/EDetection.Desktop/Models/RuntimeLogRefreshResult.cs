namespace EDetection.Desktop.Models;

public sealed record RuntimeLogRefreshResult(
    IReadOnlyList<DetectionLogItem> FilteredItems,
    IReadOnlyList<string> KindFilters,
    int SelectedKindFilterIndex);
