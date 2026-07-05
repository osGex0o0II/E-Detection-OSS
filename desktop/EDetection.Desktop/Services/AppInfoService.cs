using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class AppInfoService
{
    public AppInfo GetInfo()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var versionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);
        var version = versionInfo.ProductVersion
            ?? versionInfo.FileVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        return new AppInfo(
            string.IsNullOrWhiteSpace(versionInfo.ProductName)
                ? "E-Detection Desktop"
                : versionInfo.ProductName,
            version,
            string.IsNullOrWhiteSpace(versionInfo.FileDescription)
                ? "Windows native workbench for electrical anomaly analysis."
                : versionInfo.FileDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString());
    }
}
