namespace EDetection.Desktop.Models;

public sealed record PythonProbeResult(
    bool IsReady,
    bool CanRepairDetectionEnvironment,
    string PythonMessage,
    string BackendMessage,
    string ActionMessage);
