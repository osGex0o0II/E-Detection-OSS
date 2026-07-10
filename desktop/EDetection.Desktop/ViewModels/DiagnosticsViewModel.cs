using CommunityToolkit.Mvvm.ComponentModel;
using EDetection.Desktop.Models;
using EDetection.Desktop.Services;

namespace EDetection.Desktop.ViewModels;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    [ObservableProperty] public partial string SummaryText { get; set; } = "尚未检查运行环境";
    [ObservableProperty] public partial string BackendText { get; set; } = "Native .NET 检测核心";
    [ObservableProperty] public partial string ConfigText { get; set; } = "待检查";
    [ObservableProperty] public partial string InputText { get; set; } = "待检查";
    [ObservableProperty] public partial string OutputText { get; set; } = "待检查";
    [ObservableProperty] public partial string CheckedAtText { get; set; } = "尚未检查状态";
    [ObservableProperty] public partial string ActionText { get; set; } = "检查状态后显示处理建议";

    public void ApplyLocalSnapshot(DesktopDiagnosticsSnapshot snapshot)
    {
        InputText = snapshot.InputMessage;
        OutputText = snapshot.OutputMessage;
        ConfigText = snapshot.ConfigMessage;
        BackendText = snapshot.BackendMessage;
        SummaryText = snapshot.SummaryMessage;
        ActionText = snapshot.IsInputReady && snapshot.IsConfigReady
            ? "可以开始检测。"
            : "请处理输入目录或阈值配置后重试。";
        CheckedAtText = $"上次检查: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    }

    public void ApplyBlockStart(string message, string action)
    {
        SummaryText = $"无法开始: {message}";
        ActionText = action;
    }

    public string BuildClipboardText() => DiagnosticsRedactor.Redact(string.Join(Environment.NewLine,
    [
        "诊断信息: 已脱敏本机路径和凭据",
        $"状态摘要: {SummaryText}",
        $"检查时间: {CheckedAtText}",
        $"检测核心: {BackendText}",
        $"配置: {ConfigText}",
        $"输入: {InputText}",
        $"输出: {OutputText}",
        $"建议: {ActionText}",
    ]));
}
