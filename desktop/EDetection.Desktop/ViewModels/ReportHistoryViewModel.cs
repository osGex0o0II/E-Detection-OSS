using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EDetection.Desktop.Models;
using EDetection.Desktop.Services;

namespace EDetection.Desktop.ViewModels;

public sealed partial class ReportHistoryViewModel(
    ReportHistoryService history) : ObservableObject
{
    private bool _loading;

    public ObservableCollection<RecentReport> RecentReports { get; } = [];

    public ObservableCollection<RecentReport> FilteredRecentReports { get; } = [];

    public int RecentReportLimit => SelectedRecentReportLimitIndex switch
    {
        0 => 10,
        2 => 50,
        _ => 20,
    };

    public string RecentReportLimitText => $"保留最近 {RecentReportLimit} 个";

    public string StatusText => RecentReports.Count > 0
        ? $"{FilteredRecentReports.Count}/{RecentReports.Count} 个报告"
        : "暂无报告历史";

    [ObservableProperty]
    public partial RecentReport? SelectedReport { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecentReportLimit))]
    [NotifyPropertyChangedFor(nameof(RecentReportLimitText))]
    public partial int SelectedRecentReportLimitIndex { get; set; } = 1;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial int SelectedFilterIndex { get; set; }

    public void Load(
        IEnumerable<RecentReport> reports,
        int selectedRecentReportLimitIndex)
    {
        _loading = true;
        SelectedRecentReportLimitIndex = Math.Clamp(selectedRecentReportLimitIndex, 0, 2);
        _loading = false;

        history.Load(RecentReports, reports, RecentReportLimit);
        Refresh();
        NotifyStatusChanged();
    }

    public bool AddOrUpdate(
        string path,
        DetectionBackendEvent? evt,
        RecentReportUpdateContext context)
    {
        var item = history.AddOrUpdate(
            RecentReports,
            path,
            evt,
            context,
            RecentReportLimit,
            SelectedReport,
            out var clearedSelectedReport);
        if (item is null)
        {
            return false;
        }

        if (clearedSelectedReport)
        {
            SelectedReport = null;
        }

        Refresh();
        NotifyStatusChanged();
        return true;
    }

    public bool RemoveSelected()
    {
        if (SelectedReport is null)
        {
            return false;
        }

        var removed = history.Remove(RecentReports, SelectedReport, RecentReportLimit);
        if (!removed)
        {
            return false;
        }

        SelectedReport = null;
        Refresh();
        NotifyStatusChanged();
        return true;
    }

    public void Clear()
    {
        history.Clear(RecentReports);
        FilteredRecentReports.Clear();
        SelectedReport = null;
        NotifyStatusChanged();
    }

    public void Refresh()
    {
        var result = history.Filter(
            RecentReports,
            SelectedReport,
            SearchText,
            SelectedFilterIndex);
        FilteredRecentReports.Clear();
        foreach (var report in result.FilteredReports)
        {
            FilteredRecentReports.Add(report);
        }

        if (!ReferenceEquals(SelectedReport, result.SelectedReport))
        {
            SelectedReport = result.SelectedReport;
        }

        NotifyStatusChanged();
    }

    partial void OnSelectedRecentReportLimitIndexChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        history.Trim(RecentReports, RecentReportLimit);
        history.RefreshLatestMarkers(RecentReports);
        Refresh();
        NotifyStatusChanged();
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedFilterIndexChanged(int value) => Refresh();

    private void NotifyStatusChanged() =>
        OnPropertyChanged(nameof(StatusText));
}
