using System.Diagnostics;
using System.Text.Json;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class PythonBackendService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<int> RunDetectionAsync(
        DetectionRequest request,
        IProgress<DetectionBackendEvent> progress,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = BuildStartInfo(request),
            EnableRaisingEvents = true,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 Python 检测进程。");
        }

        using var killOnCancel = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        var stdoutTask = ReadJsonEventsAsync(process, progress, cancellationToken);
        var stderrTask = ReadErrorsAsync(process, progress, cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return process.ExitCode;
    }

    public static string ResolveBackendWorkingDirectory()
    {
        if (HasBackendFiles(AppContext.BaseDirectory))
        {
            return AppContext.BaseDirectory;
        }

        if (!IsDevelopmentBuild())
        {
            return AppContext.BaseDirectory;
        }

        var current = Directory.GetParent(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (HasBackendFiles(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static bool HasBackendFiles(string directory) =>
        File.Exists(Path.Combine(directory, "pyproject.toml"))
        && Directory.Exists(Path.Combine(directory, "e_detection"));

    private static bool IsDevelopmentBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    public static string ResolveBundledPythonExecutable()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "python-runtime", "python.exe");
        return File.Exists(candidate) ? candidate : "";
    }

    public static bool IsBundledPythonExecutable(string executable)
    {
        var bundled = ResolveBundledPythonExecutable();
        if (string.IsNullOrWhiteSpace(bundled) || string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(executable),
                Path.GetFullPath(bundled),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string ResolvePythonExecutable(string configuredExecutable)
    {
        var bundled = ResolveBundledPythonExecutable();
        if ((string.IsNullOrWhiteSpace(configuredExecutable)
             || string.Equals(configuredExecutable.Trim(), "python", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(bundled))
        {
            return bundled;
        }

        return string.IsNullOrWhiteSpace(configuredExecutable)
            ? "python"
            : configuredExecutable;
    }

    private static ProcessStartInfo BuildStartInfo(DetectionRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePythonExecutable(request.PythonExecutable),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? ResolveBackendWorkingDirectory()
                : request.WorkingDirectory,
        };

        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("e_detection");
        startInfo.ArgumentList.Add("--json-events");

        if (!request.WriteReport)
        {
            startInfo.ArgumentList.Add("--no-report");
        }

        if (!string.IsNullOrWhiteSpace(request.ConfigPath))
        {
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(request.ConfigPath);
        }

        if (!string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            startInfo.ArgumentList.Add("--output-dir");
            startInfo.ArgumentList.Add(request.OutputDirectory);
        }

        startInfo.ArgumentList.Add(request.InputDirectory);
        return startInfo;
    }

    private static async Task ReadJsonEventsAsync(
        Process process,
        IProgress<DetectionBackendEvent> progress,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken)
                   .ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var evt = JsonSerializer.Deserialize<DetectionBackendEvent>(line, JsonOptions);
                if (evt is not null)
                {
                    progress.Report(evt);
                }
            }
            catch (JsonException ex)
            {
                progress.Report(new DetectionBackendEvent
                {
                    EventName = "bridge_parse_error",
                    ErrorType = nameof(JsonException),
                    Message = $"无法解析 Python 输出: {ex.Message}",
                });
            }
        }
    }

    private static async Task ReadErrorsAsync(
        Process process,
        IProgress<DetectionBackendEvent> progress,
        CancellationToken cancellationToken)
    {
        while (await process.StandardError.ReadLineAsync(cancellationToken)
                   .ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                progress.Report(new DetectionBackendEvent
                {
                    EventName = "stderr",
                    Message = line,
                });
            }
        }
    }
}
