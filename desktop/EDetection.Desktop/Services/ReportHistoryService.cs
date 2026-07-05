using System.Collections.ObjectModel;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class ReportHistoryService
{
    public void Load(
        ObservableCollection<RecentReport> target,
        IEnumerable<RecentReport> reports,
        int limit)
    {
        target.Clear();
        foreach (var report in reports.Where(r => !string.IsNullOrWhiteSpace(r.Path)))
        {
            target.Add(report);
        }

        Trim(target, limit);
        RefreshLatestMarkers(target);
    }

    public ReportHistoryRefreshResult Filter(
        IEnumerable<RecentReport> reports,
        RecentReport? selectedReport,
        string searchText,
        int filterIndex)
    {
        var query = searchText.Trim();
        var filtered = reports
            .Where(item => item.Matches(query, filterIndex))
            .ToList();
        var selected = selectedReport is not null && !filtered.Contains(selectedReport)
            ? null
            : selectedReport;
        return new ReportHistoryRefreshResult(filtered, selected);
    }

    public RecentReport? AddOrUpdate(
        ObservableCollection<RecentReport> reports,
        string path,
        DetectionBackendEvent? evt,
        RecentReportUpdateContext context,
        int limit,
        RecentReport? selectedReport,
        out bool clearedSelectedReport)
    {
        clearedSelectedReport = false;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var existing = reports.FirstOrDefault(
            r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            clearedSelectedReport = ReferenceEquals(selectedReport, existing);
            reports.Remove(existing);
        }

        var now = DateTime.Now;
        var item = existing ?? new RecentReport();
        item.Path = path;
        item.CreatedAt = string.IsNullOrWhiteSpace(item.CreatedAt)
            ? now.ToString("yyyy-MM-dd HH:mm")
            : item.CreatedAt;
        item.CompletedAt = now.ToString("yyyy-MM-dd HH:mm:ss");
        item.InputDirectory = evt?.InputDirectory ?? context.InputDirectory;
        item.OutputDirectory = evt?.OutputDirectory ?? context.OutputDirectory;
        item.TotalFiles = evt?.TotalFiles ?? context.TotalFiles;
        item.ProcessedFiles = evt?.ProcessedFiles ?? context.ProcessedFiles;
        item.AnomalyRecords = evt?.AnomalyRecords ?? context.AnomalyRecords;
        item.AnomalyFiles = evt?.AnomalyFiles ?? context.AnomalyFiles;
        item.SkippedFiles = evt?.SkippedFiles ?? context.SkippedFiles;
        item.DurationSeconds = evt?.DurationSeconds ?? item.DurationSeconds;
        item.DeviceCount = context.DeviceCount;
        item.HighRiskDevices = context.HighRiskDevices.ToList();
        item.TopIssueTypes = context.TopIssueTypes.ToList();
        item.SensorOverview = new ReportSensorOverview
        {
            OfflineDevices = context.SensorOverview.OfflineDevices,
            SensorFaultRows = context.SensorOverview.SensorFaultRows,
            SensorMissingRows = context.SensorOverview.SensorMissingRows,
            SkippedRows = context.SensorOverview.SkippedRows,
        };
        item.DetailPreviewCount = context.DetailPreviewCount;
        item.DetailPreview = context.DetailPreview.ToList();

        foreach (var report in reports)
        {
            report.IsLatest = false;
        }

        item.IsLatest = true;
        reports.Insert(0, item);
        Trim(reports, limit);
        RefreshLatestMarkers(reports);
        return item;
    }

    public bool Remove(
        ObservableCollection<RecentReport> reports,
        RecentReport report,
        int limit)
    {
        var removed = reports.Remove(report);
        if (removed)
        {
            Trim(reports, limit);
            RefreshLatestMarkers(reports);
        }

        return removed;
    }

    public void Clear(ObservableCollection<RecentReport> reports) =>
        reports.Clear();

    public void Trim(
        ObservableCollection<RecentReport> reports,
        int limit)
    {
        while (reports.Count > limit)
        {
            reports.RemoveAt(reports.Count - 1);
        }
    }

    public void RefreshLatestMarkers(IReadOnlyList<RecentReport> reports)
    {
        for (var i = 0; i < reports.Count; i++)
        {
            reports[i].IsLatest = i == 0;
        }
    }
}
