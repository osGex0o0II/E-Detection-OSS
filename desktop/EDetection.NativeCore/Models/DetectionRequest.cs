namespace EDetection.NativeCore.Models;

public sealed class DetectionRequest
{
    public required string InputDirectory { get; init; }

    public string? OutputDirectory { get; init; }

    public string? ConfigPath { get; init; }

    public bool WriteReport { get; init; } = true;
}
