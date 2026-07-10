using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class TaskSchedulerStartupIntegrationProvider : IStartupIntegrationProvider
{
    private const string TaskName = "EDetection Autostart";
    private const string LegacyTaskName = "E-Detection Desktop Autostart";
    private const string StartupArgument = App.BackgroundStartupArgument;
    private const string ProviderName = "Task Scheduler";
    private const int CommandTimeoutMilliseconds = 10000;

    public StartupIntegrationSnapshot GetStatus()
    {
        var executablePath = ResolveExecutablePath();
        var result = RunSchtasks("/Query", "/TN", TaskName, "/XML");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return DisabledSnapshot(executablePath, registeredCommand: null);
        }

        try
        {
            var document = XDocument.Parse(result.StandardOutput);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var command = document.Descendants(ns + "Command").FirstOrDefault()?.Value ?? "";
            var arguments = document.Descendants(ns + "Arguments").FirstOrDefault()?.Value ?? "";
            var registeredCommand = string.Join(
                " ",
                new[] { command, arguments }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var pointsToCurrentExecutable = string.Equals(
                command,
                executablePath,
                StringComparison.OrdinalIgnoreCase);
            var usesBackgroundStartup = arguments.Contains(
                StartupArgument,
                StringComparison.OrdinalIgnoreCase);

            return new StartupIntegrationSnapshot(
                IsEnabled: pointsToCurrentExecutable && usesBackgroundStartup,
                PointsToCurrentExecutable: pointsToCurrentExecutable,
                ProviderName,
                TaskName,
                executablePath,
                string.IsNullOrWhiteSpace(registeredCommand) ? null : registeredCommand);
        }
        catch
        {
            return DisabledSnapshot(executablePath, result.StandardOutput);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            DeleteTaskIfExists();
            DeleteTaskIfExists(LegacyTaskName);
            return;
        }

        DeleteTaskIfExists(LegacyTaskName);

        var executablePath = ResolveExecutablePath();
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"EDetectionDesktopAutostart-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempPath, BuildTaskXml(executablePath), Encoding.Unicode);
            var result = RunSchtasks("/Create", "/TN", TaskName, "/XML", tempPath, "/F");
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"无法注册任务计划程序自启动项: {result.StandardError}{result.StandardOutput}");
            }
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    private static StartupIntegrationSnapshot DisabledSnapshot(
        string executablePath,
        string? registeredCommand) =>
        new(
            IsEnabled: false,
            PointsToCurrentExecutable: false,
            ProviderName,
            TaskName,
            executablePath,
            registeredCommand);

    private static void DeleteTaskIfExists(string taskName = TaskName)
    {
        var query = RunSchtasks("/Query", "/TN", taskName);
        if (query.ExitCode != 0)
        {
            return;
        }

        var delete = RunSchtasks("/Delete", "/TN", taskName, "/F");
        if (delete.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"无法删除任务计划程序自启动项: {delete.StandardError}{delete.StandardOutput}");
        }
    }

    private static string BuildTaskXml(string executablePath)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var workingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(
                ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(
                    ns + "RegistrationInfo",
                    new XElement(ns + "Author", "E-Detection OSS"),
                    new XElement(ns + "Description", "Start EDetection hidden in the tray after user sign-in.")),
                new XElement(
                    ns + "Triggers",
                    new XElement(
                        ns + "LogonTrigger",
                        new XElement(ns + "Enabled", "true"),
                        string.IsNullOrWhiteSpace(sid) ? null : new XElement(ns + "UserId", sid),
                        new XElement(ns + "Delay", "PT5S"))),
                new XElement(
                    ns + "Principals",
                    new XElement(
                        ns + "Principal",
                        new XAttribute("id", "Author"),
                        string.IsNullOrWhiteSpace(sid) ? null : new XElement(ns + "UserId", sid),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(
                    ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "false"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(
                        ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "Priority", "7")),
                new XElement(
                    ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(
                        ns + "Exec",
                        new XElement(ns + "Command", executablePath),
                        new XElement(ns + "Arguments", StartupArgument),
                        new XElement(ns + "WorkingDirectory", workingDirectory)))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string ResolveExecutablePath() =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("无法解析当前应用程序路径。");

    private static SchtasksResult RunSchtasks(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CommandTimeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException("任务计划程序命令超时。");
        }

        return new SchtasksResult(
            process.ExitCode,
            stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult());
    }

    private sealed record SchtasksResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
