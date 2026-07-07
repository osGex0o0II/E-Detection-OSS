using System.Diagnostics;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class DesktopDiagnosticsService
{
    public DesktopDiagnosticsSnapshot BuildLocalSnapshot(DesktopDiagnosticsRequest request)
    {
        var backendRoot = PythonBackendService.ResolveBackendWorkingDirectory();
        var resolvedConfig = ResolveAgainstBackend(request.ConfigPath, backendRoot);
        var resolvedPython = PythonBackendService.ResolvePythonExecutable(request.PythonExecutable);
        var hasBackendSource = HasBackendSource(backendRoot);
        var isInputReady = IsInputReady(request.InputDirectory);
        var configIssue = DetectionConfigService.ValidateConfigFile(resolvedConfig);
        var isConfigReady = configIssue is null;
        var pythonSetupCommand = hasBackendSource
            ? BuildPythonSetupCommand(request.PythonExecutable, backendRoot)
            : "";

        return new DesktopDiagnosticsSnapshot(
            backendRoot,
            resolvedConfig,
            isInputReady,
            isConfigReady,
            string.IsNullOrWhiteSpace(request.InputDirectory)
                ? "未选择输入目录"
                : Directory.Exists(request.InputDirectory)
                    ? $"可用 · {request.InputDirectory}"
                    : $"不存在 · {request.InputDirectory}",
            string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? "将写入输入目录"
                : Directory.Exists(request.OutputDirectory)
                    ? $"可用 · {request.OutputDirectory}"
                    : request.WriteReport
                        ? $"将自动创建 · {request.OutputDirectory}"
                        : $"未使用 · {request.OutputDirectory}",
            isConfigReady
                ? $"可用 · {resolvedConfig}"
                : configIssue ?? $"未找到 · {resolvedConfig}",
            PythonBackendService.IsBundledPythonExecutable(resolvedPython)
                ? $"待检查 · 内置检测运行时 {resolvedPython}"
                : string.IsNullOrWhiteSpace(request.PythonExecutable)
                    ? "待检查 · 系统 Python"
                    : $"待检查 · {request.PythonExecutable}",
            hasBackendSource
                ? $"待检查 · 本地核心 {backendRoot}"
                : $"待检查 · 发布目录 {backendRoot}",
            hasBackendSource
                ? "如果检测核心不可导入，可复制修复命令安装本地包。"
                : "发布版需要配置一个已经安装 e_detection 的 Python 环境。",
            isInputReady && isConfigReady
                ? "输入和配置已就绪，Python 待检查"
                : "输入或配置需要处理",
            pythonSetupCommand);
    }

    public IReadOnlyList<string> ValidateRunInputs(
        DesktopDiagnosticsRequest request,
        bool prepareOutputDirectory)
    {
        var issues = new List<string>();
        var inputIssue = ValidateInputDirectory(request.InputDirectory);
        if (inputIssue is not null)
        {
            issues.Add(inputIssue);
        }

        var backendRoot = PythonBackendService.ResolveBackendWorkingDirectory();
        var resolvedConfig = ResolveAgainstBackend(request.ConfigPath, backendRoot);
        var configIssue = DetectionConfigService.ValidateConfigFile(resolvedConfig);
        if (configIssue is not null)
        {
            issues.Add(configIssue);
        }

        var resolvedPython = PythonBackendService.ResolvePythonExecutable(request.PythonExecutable);
        if (string.IsNullOrWhiteSpace(resolvedPython))
        {
            issues.Add("Python 可执行文件未设置");
        }

        var outputIssue = ValidateOutputDirectory(
            request.OutputDirectory,
            request.WriteReport,
            prepareOutputDirectory);
        if (outputIssue is not null)
        {
            issues.Add(outputIssue);
        }

        return issues;
    }

    public async Task<PythonProbeResult> ProbePythonAsync(
        string executable,
        string backendRoot)
    {
        var resolvedExecutable = PythonBackendService.ResolvePythonExecutable(executable);
        if (string.IsNullOrWhiteSpace(resolvedExecutable))
        {
            return new PythonProbeResult(
                false,
                false,
                "Python 可执行文件未设置",
                "检测核心未检查",
                "请选择 Python 可执行文件，或输入可在命令行中运行的 python。");
        }

        using var process = new Process();
        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = resolvedExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.Exists(backendRoot)
                    ? backendRoot
                    : AppContext.BaseDirectory,
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(
                "import importlib.util, sys; "
                + "print('python=' + sys.version.split()[0]); "
                + "spec = importlib.util.find_spec('e_detection'); "
                + "sys.exit(20) if spec is None else None; "
                + "import e_detection; "
                + "import e_detection.cli; "
                + "print('module=' + (getattr(e_detection, '__file__', '') or 'unknown'))");

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            var outputLines = output.Split(
                [Environment.NewLine],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var version = outputLines
                .FirstOrDefault(line => line.StartsWith("python=", StringComparison.Ordinal))
                ?.Replace("python=", "Python ", StringComparison.Ordinal);
            var modulePath = outputLines
                .FirstOrDefault(line => line.StartsWith("module=", StringComparison.Ordinal))
                ?.Replace("module=", "", StringComparison.Ordinal);
            var pythonMessage = string.IsNullOrWhiteSpace(version)
                ? "Python 可用"
                : $"Python 可用 · {version}";

            if (process.ExitCode == 0)
            {
                return new PythonProbeResult(
                    true,
                    false,
                    pythonMessage,
                    string.IsNullOrWhiteSpace(modulePath)
                        ? "检测核心可导入"
                        : $"检测核心可导入 · {modulePath}",
                    "运行环境就绪，可以开始检测。");
            }

            var reason = string.IsNullOrWhiteSpace(error)
                ? $"退出码 {process.ExitCode}"
                : error.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? error;
            var canRepair = HasBackendSource(backendRoot)
                && !PythonBackendService.IsBundledPythonExecutable(resolvedExecutable);
            var action = PythonBackendService.IsBundledPythonExecutable(resolvedExecutable)
                ? "内置检测运行时不可用。请重新安装或更新 E-Detection Desktop。"
                : HasBackendSource(backendRoot)
                ? "检测核心不可导入。可点击“修复检测组件”自动安装本地检测核心，或复制修复命令手动处理。"
                : "检测核心不可导入。请将 Python 指向已安装 e_detection 的环境，或使用包含源码的开发目录运行。";
            return new PythonProbeResult(
                false,
                canRepair,
                pythonMessage,
                $"检测核心不可导入 · {reason}",
                action);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            return new PythonProbeResult(
                false,
                false,
                "Python 检查超时",
                "检测核心未检查",
                "请确认该 Python 可执行文件可以正常启动，然后重新检查状态。");
        }
        catch (Exception ex)
        {
            return new PythonProbeResult(
                false,
                false,
                $"Python 不可用: {ex.Message}",
                "检测核心未检查",
                "请重新选择 Python 可执行文件，或输入可在命令行中运行的 python。");
        }
    }

    public static string ResolveAgainstBackend(string path, string backendRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Path.Combine(backendRoot, "config.json");
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(backendRoot, path);
    }

    public static bool HasBackendSource(string backendRoot) =>
        File.Exists(Path.Combine(backendRoot, "pyproject.toml"))
        && Directory.Exists(Path.Combine(backendRoot, "e_detection"));

    public static string BuildPythonSetupCommand(string executable, string backendRoot)
    {
        var python = PythonBackendService.ResolvePythonExecutable(executable);
        return $"{QuoteCommandArgument(python)} -m pip install -e {QuoteCommandArgument(backendRoot)}";
    }

    public static string BuildBlockStartAction(IReadOnlyList<string> issues) =>
        issues.Any(issue => issue.Contains("CSV", StringComparison.OrdinalIgnoreCase)
                            || issue.Contains("输入", StringComparison.OrdinalIgnoreCase))
            ? "请选择包含 CSV 文件的输入目录。"
            : issues.Any(issue => issue.Contains("配置", StringComparison.OrdinalIgnoreCase))
                ? "请在设置中选择有效的阈值配置文件，或留空使用默认阈值配置。"
                : issues.Any(issue => issue.Contains("报告", StringComparison.OrdinalIgnoreCase))
                    ? "请选择可写的报告目录，或留空写入输入目录。"
                    : "请先修复运行环境后再开始检测。";

    public static bool IsInputReady(string inputDirectory) =>
        !string.IsNullOrWhiteSpace(inputDirectory) && Directory.Exists(inputDirectory);

    private static string? ValidateInputDirectory(string inputDirectory)
    {
        if (string.IsNullOrWhiteSpace(inputDirectory))
        {
            return "未选择输入目录";
        }

        if (!Directory.Exists(inputDirectory))
        {
            return $"输入目录不存在: {inputDirectory}";
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            return Directory.EnumerateFiles(inputDirectory, "*.csv", options).Any()
                ? null
                : $"输入目录中未找到 CSV 文件: {inputDirectory}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"无法扫描输入目录: {ex.Message}";
        }
    }

    private static string? ValidateOutputDirectory(
        string outputDirectory,
        bool writeReport,
        bool prepareOutputDirectory)
    {
        if (!writeReport || string.IsNullOrWhiteSpace(outputDirectory) || Directory.Exists(outputDirectory))
        {
            return null;
        }

        if (!prepareOutputDirectory)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"报告目录不可创建: {ex.Message}";
        }
    }

    private static string QuoteCommandArgument(string value) =>
        value.Contains(' ') || value.Contains('[') || value.Contains(']')
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
}
