namespace EDetection.Desktop.Models;

public sealed record AppInfo(
    string ProductName,
    string Version,
    string Description,
    string Runtime,
    string ProcessArchitecture);
