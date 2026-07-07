namespace EDetection.Desktop.Models;

public sealed record DetectionEnvironmentRepairResult(
    bool Succeeded,
    int? ExitCode,
    string SummaryMessage,
    string ActionMessage,
    string OutputTail,
    string RepairedPythonExecutable = "");
