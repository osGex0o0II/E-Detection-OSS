namespace EDetection.Desktop.Models;

public sealed record DesktopDiagnosticsSnapshot(
    string ResolvedConfigPath,
    bool IsInputReady,
    bool IsConfigReady,
    string InputMessage,
    string OutputMessage,
    string ConfigMessage,
    string BackendMessage,
    string SummaryMessage);
