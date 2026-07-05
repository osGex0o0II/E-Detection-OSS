namespace EDetection.Desktop.Models;

public sealed record DesktopDiagnosticsSnapshot(
    string BackendRoot,
    string ResolvedConfigPath,
    bool IsInputReady,
    bool IsConfigReady,
    string InputMessage,
    string OutputMessage,
    string ConfigMessage,
    string PythonMessage,
    string BackendMessage,
    string ActionMessage,
    string SummaryMessage,
    string PythonSetupCommand);
