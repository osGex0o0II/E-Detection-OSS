using CommunityToolkit.Mvvm.ComponentModel;
using EDetection.Desktop.Models;

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

    public void ApplyLocalSnapshot(DesktopDiagnosticsSnapshot snapshot)
    {
        InputText = snapshot.InputMessage;
        OutputText = snapshot.OutputMessage;
        ConfigText = snapshot.ConfigMessage;
        PythonText = snapshot.PythonMessage;
        BackendText = snapshot.BackendMessage;
        PythonSetupCommandText = snapshot.PythonSetupCommand;
        ActionText = snapshot.ActionMessage;
        SummaryText = snapshot.SummaryMessage;
    }

    public void MarkProbeInProgress(string actionText)
    {
        PythonText = "检查中...";
        BackendText = "检查中...";
        ActionText = actionText;
    }

    public void ApplyPythonProbeResult(
        PythonProbeResult result,
        bool isInputReady,
        bool isConfigReady,
        DateTime checkedAt)
    {
        PythonText = result.PythonMessage;
        BackendText = result.BackendMessage;
        ActionText = result.ActionMessage;
        CheckedAtText = $"上次检查: {checkedAt:yyyy-MM-dd HH:mm:ss}";
        SummaryText = result.IsReady && isInputReady && isConfigReady
            ? "运行环境就绪"
            : "运行环境需要处理";
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

        return string.Join(Environment.NewLine, lines);
    }
}
