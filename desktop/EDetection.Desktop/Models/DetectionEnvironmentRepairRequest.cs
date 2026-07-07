namespace EDetection.Desktop.Models;

public sealed record DetectionEnvironmentRepairRequest(
    string PythonExecutable,
    string BackendRoot);
