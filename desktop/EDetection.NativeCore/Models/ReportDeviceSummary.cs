using System.Text.Json.Serialization;

namespace EDetection.NativeCore.Models;

public sealed class ReportDeviceSummary
{
    [JsonPropertyName("building")]
    public string Building { get; set; } = "";

    [JsonPropertyName("device_path")]
    public string DevicePath { get; set; } = "根目录";

    [JsonPropertyName("transformer")]
    public string Transformer { get; set; } = "";

    [JsonPropertyName("anomaly_records")]
    public int AnomalyRecords { get; set; }

    [JsonPropertyName("main_issue_types")]
    public string MainIssueTypes { get; set; } = "";

    [JsonPropertyName("highest_severity")]
    public string HighestSeverity { get; set; } = "";

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "";

    [JsonIgnore]
    public string Title => $"{Building} / {Transformer}";

    [JsonIgnore]
    public string Subtitle => $"{Priority} · {HighestSeverity} · {AnomalyRecords} 条 · {DevicePath}";
}
