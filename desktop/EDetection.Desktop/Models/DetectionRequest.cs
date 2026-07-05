namespace EDetection.Desktop.Models;

public sealed class DetectionRequest
{
    public required string InputDirectory { get; init; }

    public string? OutputDirectory { get; init; }

    public string? ConfigPath { get; init; }

    public string PythonExecutable { get; init; } = "python";

    public bool WriteReport { get; init; } = true;

    public string? WorkingDirectory { get; init; }
}
