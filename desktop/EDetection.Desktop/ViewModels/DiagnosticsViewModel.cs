using CommunityToolkit.Mvvm.ComponentModel;
using EDetection.Desktop.Models;
using EDetection.Desktop.Services;

namespace EDetection.Desktop.ViewModels;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string SummaryText { get; set; } = "尚未检查运行环境";

    [ObservableProperty]
    public partial string PythonText { get; set; } = "待检查";

    [ObservableProperty]
    public partial string BackendText { get; set; } = "待检查";

    [ObservableProperty]
    public partial string ConfigText { get; set; } = "待检查";

    [ObservableProperty]
    public partial string InputText { get; set; } = "待检查";

    [ObservableProperty]
    public partial string OutputText { get; set; } = "待检查";

    [ObservableProperty]
    public partial string CheckedAtText { get; set; } = "尚未检查状态";

    [ObservableProperty]
    public partial string ActionText { get; set; } = "检查状态后显示修复建议";

    [ObservableProperty]
    public partial string PythonSetupCommandText { get; set; } = "";

    [ObservableProperty]
    public partial bool CanRepairDetectionEnvironment { get; set; }

    [ObservableProperty]
    public partial string RepairOutputText { get; set; } = "";

    public void ApplyLocalSnapshot(DesktopDiagnosticsSnapshot snapshot)
    {
        InputText = snapshot.InputMessage;
        OutputText = snapshot.OutputMessage;
        ConfigText = snapshot.ConfigMessage;
        PythonText = snapshot.PythonMessage;
        BackendText = snapshot.BackendMessage;
        PythonSetupCommandText = snapshot.PythonSetupCommand;
        CanRepairDetectionEnvironment = false;
        ActionText = snapshot.ActionMessage;
        SummaryText = snapshot.SummaryMessage;
    }

    public void MarkProbeInProgress(string actionText)
    {
        PythonText = "检查中...";
        BackendText = "检查中...";
        CanRepairDetectionEnvironment = false;
        ActionText = actionText;
    }

    public void MarkRepairInProgress()
    {
        SummaryText = "正在修复检测组件";
        PythonText = "修复中...";
        BackendText = "正在安装本地检测核心...";
        CanRepairDetectionEnvironment = false;
        ActionText = "修复完成后会自动重新检查运行环境。";
        RepairOutputText = "";
    }

    public void ApplyPythonProbeResult(
        PythonProbeResult result,
        bool isInputReady,
        bool isConfigReady,
        DateTime checkedAt)
    {
        PythonText = result.PythonMessage;
        BackendText = result.BackendMessage;
        CanRepairDetectionEnvironment = result.CanRepairDetectionEnvironment;
        ActionText = result.ActionMessage;
        CheckedAtText = $"上次检查: {checkedAt:yyyy-MM-dd HH:mm:ss}";
        SummaryText = result.IsReady && isInputReady && isConfigReady
            ? "运行环境就绪"
            : "运行环境需要处理";
    }

    public void ApplyRepairResult(DetectionEnvironmentRepairResult result)
    {
        SummaryText = result.SummaryMessage;
        ActionText = result.ActionMessage;
        RepairOutputText = result.OutputTail;
    }

    public void ApplyBlockStart(string message, string action)
    {
        SummaryText = $"无法开始: {message}";
        ActionText = action;
    }

    public string BuildClipboardText(
        string pythonExecutable,
        string backendRoot)
    {
        var lines = new List<string>
        {
            "诊断信息: 已脱敏本机路径和凭据",
            $"状态摘要: {SummaryText}",
            $"检查时间: {CheckedAtText}",
            $"Python: {PythonText}",
            $"检测核心: {BackendText}",
            $"配置: {ConfigText}",
            $"输入: {InputText}",
            $"输出: {OutputText}",
            $"建议: {ActionText}",
            $"Python 可执行文件: {pythonExecutable}",
            $"检测核心目录: {backendRoot}",
        };

        if (!string.IsNullOrWhiteSpace(PythonSetupCommandText))
        {
            lines.Add($"修复命令: {PythonSetupCommandText}");
        }

        lines.Add($"自动修复可用: {(CanRepairDetectionEnvironment ? "是" : "否")}");

        if (!string.IsNullOrWhiteSpace(RepairOutputText))
        {
            lines.Add("最近修复输出:");
            lines.Add(RepairOutputText);
        }

        return DiagnosticsRedactor.Redact(
            string.Join(Environment.NewLine, lines),
            pythonExecutable,
            backendRoot);
    }
}
