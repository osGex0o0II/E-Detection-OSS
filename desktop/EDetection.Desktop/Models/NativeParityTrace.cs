using System.Text.Json.Serialization;

namespace EDetection.Desktop.Models;

/// <summary>
/// Opt-in, privacy-preserving diagnostic data for comparing the native rule
/// pipeline with another implementation. It contains counts and HMAC-SHA-256
/// digests only; no source path, file name, telemetry value, or rule label is
/// included in this object.
/// </summary>
public sealed class NativeParityTrace
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    // "analyzed" or "not_analyzed". The file_result event already carries
    // the public status; this distinguishes an unavailable structural trace.
    [JsonPropertyName("analysis_state")]
    public string AnalysisState { get; init; } = "analyzed";

    [JsonPropertyName("data_row_count")]
    public int DataRowCount { get; init; }

    [JsonPropertyName("raw_row_label_count")]
    public int RawRowLabelCount { get; init; }

    [JsonPropertyName("reportable_row_label_count")]
    public int ReportableRowLabelCount { get; init; }

    [JsonPropertyName("structured_detail_count")]
    public int StructuredDetailCount { get; init; }

    [JsonPropertyName("raw_row_label_hashes")]
    public List<string> RawRowLabelHashes { get; init; } = [];

    [JsonPropertyName("reportable_row_label_hashes")]
    public List<string> ReportableRowLabelHashes { get; init; } = [];

    [JsonPropertyName("structured_detail_hashes")]
    public List<string> StructuredDetailHashes { get; init; } = [];

    [JsonPropertyName("final_anomaly_types_hash")]
    public string FinalAnomalyTypesHash { get; init; } = "";
}
