using System.Text.Json.Serialization;

namespace EDetection.Desktop.Models;

public sealed class DetectionBackendEvent
{
    [JsonPropertyName("event")]
    public string EventName { get; set; } = "";

    [JsonPropertyName("generated_at")]
    public string? GeneratedAt { get; set; }

    [JsonPropertyName("source_file")]
    public string? SourceFile { get; set; }

    [JsonPropertyName("relative_path")]
    public string? RelativePath { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error_type")]
    public string? ErrorType { get; set; }

    [JsonPropertyName("input_dir")]
    public string? InputDirectory { get; set; }

    [JsonPropertyName("output_dir")]
    public string? OutputDirectory { get; set; }

    [JsonPropertyName("report_path")]
    public string? ReportPath { get; set; }

    [JsonPropertyName("total_files")]
    public int? TotalFiles { get; set; }

    [JsonPropertyName("processed_files")]
    public int? ProcessedFiles { get; set; }

    [JsonPropertyName("normal_files")]
    public int? NormalFiles { get; set; }

    [JsonPropertyName("anomaly_files")]
    public int? AnomalyFiles { get; set; }

    [JsonPropertyName("anomaly_records")]
    public int? AnomalyRecords { get; set; }

    [JsonPropertyName("skipped_files")]
    public int? SkippedFiles { get; set; }

    [JsonPropertyName("percent")]
    public double? Percent { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; set; }

    [JsonPropertyName("device_count")]
    public int? DeviceCount { get; set; }

    [JsonPropertyName("high_risk_devices")]
    public List<ReportDeviceSummary>? HighRiskDevices { get; set; }

    [JsonPropertyName("top_issue_types")]
    public List<ReportIssueType>? TopIssueTypes { get; set; }

    [JsonPropertyName("sensor_overview")]
    public ReportSensorOverview? SensorOverview { get; set; }

    [JsonPropertyName("detail_preview_count")]
    public int? DetailPreviewCount { get; set; }

    [JsonPropertyName("detail_preview")]
    public List<ReportDetailPreview>? DetailPreview { get; set; }
}
