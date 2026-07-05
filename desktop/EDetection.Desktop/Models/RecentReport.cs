using System.Text.Json.Serialization;

namespace EDetection.Desktop.Models;

public sealed class RecentReport
{
    public string Path { get; set; } = "";

    public string CreatedAt { get; set; } = "";

    public string CompletedAt { get; set; } = "";

    public string InputDirectory { get; set; } = "";

    public string OutputDirectory { get; set; } = "";

    public int TotalFiles { get; set; }

    public int ProcessedFiles { get; set; }

    public int AnomalyRecords { get; set; }

    public int AnomalyFiles { get; set; }

    public int SkippedFiles { get; set; }

    public double DurationSeconds { get; set; }

    public int DeviceCount { get; set; }

    public List<ReportDeviceSummary> HighRiskDevices { get; set; } = [];

    public List<ReportIssueType> TopIssueTypes { get; set; } = [];

    public ReportSensorOverview SensorOverview { get; set; } = new();

    public int DetailPreviewCount { get; set; }

    public List<ReportDetailPreview> DetailPreview { get; set; } = [];

    [JsonIgnore]
    public bool IsLatest { get; set; }

    [JsonIgnore]
    public string FileName => string.IsNullOrWhiteSpace(Path)
        ? "未命名报告"
        : System.IO.Path.GetFileName(Path);

    [JsonIgnore]
    public string CreatedAtText => string.IsNullOrWhiteSpace(CompletedAt)
        ? CreatedAt
        : CompletedAt;

    [JsonIgnore]
    public string SourceText => string.IsNullOrWhiteSpace(InputDirectory)
        ? "输入目录未记录"
        : InputDirectory;

    [JsonIgnore]
    public string OutputText => string.IsNullOrWhiteSpace(OutputDirectory)
        ? "输出目录未记录"
        : OutputDirectory;

    [JsonIgnore]
    public string Summary => TotalFiles > 0
        ? $"{AnomalyRecords} 条异常 / {AnomalyFiles} 个异常文件 / {ProcessedFiles}/{TotalFiles} 个文件"
        : $"{AnomalyRecords} 条异常 / {AnomalyFiles} 个异常文件";

    [JsonIgnore]
    public string RunMetaText
    {
        get
        {
            var skipped = SkippedFiles > 0 ? $" · 跳过 {SkippedFiles}" : "";
            var duration = DurationSeconds > 0 ? $" · {DurationSeconds:0.0}s" : "";
            return $"{CreatedAtText}{skipped}{duration}";
        }
    }

    [JsonIgnore]
    public bool HasAnomalies => AnomalyRecords > 0 || AnomalyFiles > 0;

    [JsonIgnore]
    public bool IsAvailable => File.Exists(Path);

    [JsonIgnore]
    public string StatusText
    {
        get
        {
            var status = File.Exists(Path) ? "可打开" : "报告缺失";
            return IsLatest ? $"最新 · {status}" : status;
        }
    }

    public bool Matches(string query, int filterIndex)
    {
        if (filterIndex == 1 && !HasAnomalies)
        {
            return false;
        }

        if (filterIndex == 2 && !IsAvailable)
        {
            return false;
        }

        if (filterIndex == 3 && IsAvailable)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var haystack = string.Join(
            "\n",
            Path,
            FileName,
            CreatedAt,
            CompletedAt,
            InputDirectory,
            OutputDirectory,
            Summary,
            RunMetaText,
            StatusText);
        return haystack.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }
}
