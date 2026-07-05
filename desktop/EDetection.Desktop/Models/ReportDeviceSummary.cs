using System.Text.Json.Serialization;

namespace EDetection.Desktop.Models;

public sealed class ReportDeviceSummary
{
    [JsonPropertyName("building")]
    public string Building { get; set; } = "";

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
    public string Subtitle => $"{Priority} · {HighestSeverity} · {AnomalyRecords} 条";
}
