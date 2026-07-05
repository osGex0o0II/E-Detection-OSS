using System.Text.Json.Serialization;

namespace EDetection.Desktop.Models;

public sealed class ReportIssueType
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }
}
