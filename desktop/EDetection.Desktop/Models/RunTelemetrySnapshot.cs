namespace EDetection.Desktop.Models;

public sealed record RunTelemetrySnapshot(
    string ElapsedText,
    string SpeedText,
    string RemainingText,
    string ProgressDetailText);
