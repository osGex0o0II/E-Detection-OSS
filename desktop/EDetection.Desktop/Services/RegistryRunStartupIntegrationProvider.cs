using System.Diagnostics;
using EDetection.Desktop.Models;
using Microsoft.Win32;

namespace EDetection.Desktop.Services;

public sealed class RegistryRunStartupIntegrationProvider : IStartupIntegrationProvider
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "EDetection";
    private const string LegacyEntryName = "E-Detection Desktop";
    private const string StartupArgument = App.BackgroundStartupArgument;
    private const string ProviderName = "HKCU Run";

    public StartupIntegrationSnapshot GetStatus()
    {
        var executablePath = ResolveExecutablePath();
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = runKey?.GetValue(EntryName) as string;
        var pointsToCurrentExecutable = !string.IsNullOrWhiteSpace(command)
            && command.Contains(executablePath, StringComparison.OrdinalIgnoreCase);
        var usesBackgroundStartup = !string.IsNullOrWhiteSpace(command)
            && command.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase);

        return new StartupIntegrationSnapshot(
            IsEnabled: pointsToCurrentExecutable && usesBackgroundStartup,
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
            runKey.DeleteValue(LegacyEntryName, throwOnMissingValue: false);
        }
        else
        {
            runKey.DeleteValue(EntryName, throwOnMissingValue: false);
            runKey.DeleteValue(LegacyEntryName, throwOnMissingValue: false);
        }
    }

    private static string ResolveExecutablePath() =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("无法解析当前应用程序路径。");

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
