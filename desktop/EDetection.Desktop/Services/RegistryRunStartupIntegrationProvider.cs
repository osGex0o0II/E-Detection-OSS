using System.Diagnostics;
using EDetection.Desktop.Models;
using Microsoft.Win32;

namespace EDetection.Desktop.Services;

public sealed class RegistryRunStartupIntegrationProvider : IStartupIntegrationProvider
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "E-Detection Desktop";
    private const string StartupArgument = "--startup-minimized";
    private const string ProviderName = "HKCU Run";

    public StartupIntegrationSnapshot GetStatus()
    {
        var executablePath = ResolveExecutablePath();
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = runKey?.GetValue(EntryName) as string;
        var pointsToCurrentExecutable = !string.IsNullOrWhiteSpace(command)
            && command.Contains(executablePath, StringComparison.OrdinalIgnoreCase);

        return new StartupIntegrationSnapshot(
            IsEnabled: pointsToCurrentExecutable,
            PointsToCurrentExecutable: pointsToCurrentExecutable,
            ProviderName,
            EntryName,
            executablePath,
            command);
    }

    public void SetEnabled(bool enabled)
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (runKey is null)
        {
            throw new InvalidOperationException("无法打开当前用户的 Windows 启动项注册表。");
        }

        if (enabled)
        {
            runKey.SetValue(
                EntryName,
                $"{Quote(ResolveExecutablePath())} {StartupArgument}",
                RegistryValueKind.String);
        }
        else
        {
            runKey.DeleteValue(EntryName, throwOnMissingValue: false);
        }
    }

    private static string ResolveExecutablePath() =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("无法解析当前应用程序路径。");

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
