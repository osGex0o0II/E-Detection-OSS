using System.Diagnostics;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class DetectionEnvironmentRepairService
{
    private static readonly TimeSpan RepairTimeout = TimeSpan.FromMinutes(5);
    private static readonly string PrivateEnvironmentRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "E-Detection",
        "Desktop",
        "python-env");

    public bool CanRepair(DetectionEnvironmentRepairRequest request) =>
        !string.IsNullOrWhiteSpace(PythonBackendService.ResolvePythonExecutable(request.PythonExecutable))
        && !PythonBackendService.IsBundledPythonExecutable(PythonBackendService.ResolvePythonExecutable(request.PythonExecutable))
        && DesktopDiagnosticsService.HasBackendSource(request.BackendRoot);

    public async Task<DetectionEnvironmentRepairResult> RepairAsync(
        DetectionEnvironmentRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        var repairPython = PythonBackendService.ResolvePythonExecutable(request.PythonExecutable);
        if (string.IsNullOrWhiteSpace(repairPython))
        {
            return new DetectionEnvironmentRepairResult(
                false,
                null,
                "未设置检测组件运行程序",
                "请先在设置中选择 Python 可执行文件，然后重新修复。",
                "");
        }

        if (PythonBackendService.IsBundledPythonExecutable(repairPython))
        {
            return new DetectionEnvironmentRepairResult(
                false,
                null,
                "内置检测运行时不需要修复",
                "内置运行时损坏时，请重新安装或更新 E-Detection Desktop。",
                "");
        }

        if (!DesktopDiagnosticsService.HasBackendSource(request.BackendRoot))
        {
            return new DetectionEnvironmentRepairResult(
                false,
                null,
                "当前安装包不包含可修复的本地检测核心",
                "请通过安装向导更新到包含检测组件的版本，或选择已安装 e_detection 的 Python 环境。",
                "");
        }

        try
        {
            Directory.CreateDirectory(PrivateEnvironmentRoot);
            var venvPython = Path.Combine(PrivateEnvironmentRoot, "Scripts", "python.exe");
            if (!File.Exists(venvPython))
            {
                var createResult = await RunProcessAsync(
                    repairPython,
                    ["-m", "venv", PrivateEnvironmentRoot],
                    request.BackendRoot,
                    cancellationToken);
                if (createResult.ExitCode != 0 || !File.Exists(venvPython))
                {
                    return new DetectionEnvironmentRepairResult(
                        false,
                        createResult.ExitCode,
                        $"私有检测环境创建失败 · 退出码 {createResult.ExitCode}",
                        "无法创建应用私有 Python 环境。请确认 Python 支持 venv 后重试。",
                        createResult.OutputTail);
                }
            }

            var installResults = new List<ProcessRunResult>();
            var wheelhouse = Path.Combine(request.BackendRoot, "python-wheelhouse");
            var runtimeRequirements = Path.Combine(request.BackendRoot, "requirements-runtime.lock");
            var requirements = File.Exists(runtimeRequirements)
                ? runtimeRequirements
                : Path.Combine(request.BackendRoot, "requirements.txt");
            var useOfflineWheelhouse = false;
            if (Directory.Exists(wheelhouse) && File.Exists(requirements))
            {
                var pythonTag = await GetPythonTagAsync(venvPython, request.BackendRoot, cancellationToken);
                useOfflineWheelhouse = IsWheelhouseCompatible(wheelhouse, pythonTag);
                if (useOfflineWheelhouse)
                {
                    installResults.Add(await RunProcessAsync(
                        venvPython,
                        ["-m", "pip", "install", "--no-index", "--find-links", wheelhouse, "-r", requirements],
                        request.BackendRoot,
                        cancellationToken));
                }
            }

            if (installResults.Count == 0)
            {
                return new DetectionEnvironmentRepairResult(
                    false,
                    null,
                    "离线检测依赖不可用",
                    "安装包未包含兼容当前 Python 的离线依赖。请通过安装向导更新，或在设置中改用内置检测运行时。",
                    "");
            }

            if (installResults[^1].ExitCode != 0)
            {
                return new DetectionEnvironmentRepairResult(
                    false,
                    installResults[^1].ExitCode,
                    $"检测依赖安装失败 · 退出码 {installResults[^1].ExitCode}",
                    "私有检测环境已创建，但离线依赖安装失败。请通过安装向导更新，或在设置中改用内置检测运行时。",
                    BuildOutputTail(installResults));
            }

            var coreInstallArguments = new[]
            {
                "-m",
                "pip",
                "install",
                "--no-index",
                "--find-links",
                wheelhouse,
                "--no-deps",
                "-e",
                request.BackendRoot,
            };
            var coreInstall = await RunProcessAsync(
                venvPython,
                coreInstallArguments,
                request.BackendRoot,
                cancellationToken);
            installResults.Add(coreInstall);

            if (coreInstall.ExitCode == 0)
            {
                return new DetectionEnvironmentRepairResult(
                    true,
                    coreInstall.ExitCode,
                    "检测组件修复完成",
                    "已创建应用私有检测环境，不会修改系统 Python。正在重新检查运行环境。",
                    BuildOutputTail(installResults),
                    venvPython);
            }

            return new DetectionEnvironmentRepairResult(
                false,
                coreInstall.ExitCode,
                $"检测组件修复失败 · 退出码 {coreInstall.ExitCode}",
                "私有环境已创建，但检测核心安装未完成。请复制状态详情交给维护人员。",
                BuildOutputTail(installResults));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DetectionEnvironmentRepairResult(
                false,
                null,
                "检测组件修复超时",
                "修复过程超过 5 分钟未完成。请检查 Python 包安装状态后重试。",
                "");
        }
        catch (OperationCanceledException)
        {
            return new DetectionEnvironmentRepairResult(
                false,
                null,
                "检测组件修复已取消",
                "修复过程已停止，请重新检查运行环境。",
                "");
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or System.ComponentModel.Win32Exception)
        {
            return new DetectionEnvironmentRepairResult(
                false,
                null,
                $"检测组件修复无法启动: {ex.Message}",
                "请确认检测组件运行程序可启动，并且当前用户有权限访问检测核心目录。",
                "");
        }
    }

    private static async Task<ProcessRunResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RepairTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            KillProcessTree(process);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        return new ProcessRunResult(
            process.ExitCode,
            string.Join(" ", arguments),
            BuildOutputTail(output, error));
    }

    private static async Task<string> GetPythonTagAsync(
        string pythonExecutable,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            pythonExecutable,
            ["-c", "import sys; print(f'cp{sys.version_info.major}{sys.version_info.minor}')"],
            workingDirectory,
            cancellationToken);
        return result.ExitCode == 0
            ? result.OutputTail.Trim()
            : "";
    }

    private static bool IsWheelhouseCompatible(string wheelhouse, string pythonTag)
    {
        if (string.IsNullOrWhiteSpace(pythonTag))
        {
            return false;
        }

        var wheels = Directory.GetFiles(wheelhouse, "*.whl");
        return wheels.Length > 0
            && wheels.All(path =>
            {
                var name = Path.GetFileName(path);
                return name.Contains("-py3-none-any.whl", StringComparison.OrdinalIgnoreCase)
                    || name.Contains($"-{pythonTag}-", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string BuildOutputTail(IEnumerable<ProcessRunResult> results)
    {
        var lines = new List<string>();
        foreach (var result in results)
        {
            lines.Add($"> {result.Command}");
            AppendLines(lines, result.OutputTail);
        }

        return string.Join(Environment.NewLine, lines.TakeLast(80));
    }

    private static string BuildOutputTail(string output, string error)
    {
        var lines = new List<string>();
        AppendLines(lines, output);
        AppendLines(lines, error);

        if (lines.Count == 0)
        {
            return "";
        }

        return string.Join(Environment.NewLine, lines.TakeLast(40));
    }

    private static void AppendLines(List<string> lines, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lines.AddRange(text.Split(
            [Environment.NewLine],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static void KillProcessTree(Process process)
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
    }

    private sealed record ProcessRunResult(int ExitCode, string Command, string OutputTail);
}
