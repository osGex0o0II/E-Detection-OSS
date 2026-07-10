namespace EDetection.Desktop.Models;

public sealed record DesktopDiagnosticsRequest(
    string InputDirectory,
    string OutputDirectory,
    string ConfigPath,
    bool WriteReport);
