namespace EDetection.Desktop.Models;

public sealed record PythonProbeResult(
    bool IsReady,
    string PythonMessage,
    string BackendMessage,
    string ActionMessage);
