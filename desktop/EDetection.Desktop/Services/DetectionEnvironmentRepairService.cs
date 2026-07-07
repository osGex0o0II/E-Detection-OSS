using System.Diagnostics;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class DetectionEnvironmentRepairService
{
    private static readonly TimeSpan RepairTimeout = TimeSpan.FromMinutes(5);

    public bool CanRepair(DetectionEnvironmentRepairRequest request) =>
        !string.IsNullOrWhiteSpace(request.PythonExecutable)
        && DesktopDiagnosticsService.HasBackendSource(request.BackendRoot);

    public async Task<DetectionEnvironmentRepairResult> RepairAsync(
        DetectionEnvironmentRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PythonExecutable))
        {
            return new DetectionEnvironmentRepairResult(
                false,
                null,
                "未设置检测组件运行程序",
                "请先在设置中选择 Python 可执行文件，然后重新修复。",
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

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = request.PythonExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = request.BackendRoot,
        };
        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add("pip");
        process.StartInfo.ArgumentList.Add("install");
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(request.BackendRoot);

        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RepairTimeout);

            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            var combinedTail = BuildOutputTail(output, error);

            if (process.ExitCode == 0)
            {
                return new DetectionEnvironmentRepairResult(
                    true,
                    process.ExitCode,
                    "检测组件修复完成",
                    "已完成本地检测核心安装，正在重新检查运行环境。",
                    combinedTail);
            }

            return new DetectionEnvironmentRepairResult(
                false,
                process.ExitCode,
                $"检测组件修复失败 · 退出码 {process.ExitCode}",
                "修复未完成。请复制状态详情交给维护人员，或检查网络、权限和 Python 环境后重试。",
                combinedTail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            return new DetectionEnvironmentRepairResult(
                false,
                null,
                "检测组件修复超时",
                "修复过程超过 5 分钟未完成。请检查网络、代理或 Python 包安装状态后重试。",
                "");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
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
}
