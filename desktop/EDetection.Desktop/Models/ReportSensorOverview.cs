using System.Text.Json.Serialization;

namespace EDetection.Desktop.Models;

public sealed class ReportSensorOverview
{
    [JsonPropertyName("total_rows")]
    public int TotalRows { get; set; }

    [JsonPropertyName("offline_devices")]
    public int OfflineDevices { get; set; }

    [JsonPropertyName("sensor_fault_rows")]
    public int SensorFaultRows { get; set; }

    [JsonPropertyName("sensor_missing_rows")]
    public int SensorMissingRows { get; set; }

    [JsonPropertyName("skipped_rows")]
    public int SkippedRows { get; set; }
}
