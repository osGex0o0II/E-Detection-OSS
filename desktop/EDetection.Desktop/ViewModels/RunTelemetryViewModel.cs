using CommunityToolkit.Mvvm.ComponentModel;
using EDetection.Desktop.Models;
using EDetection.Desktop.Services;

namespace EDetection.Desktop.ViewModels;

public sealed partial class RunTelemetryViewModel(
    RunTelemetryService telemetry) : ObservableObject
{
    [ObservableProperty]
    public partial string CurrentFileText { get; set; } = "";

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "00:00";

    [ObservableProperty]
    public partial string SpeedText { get; set; } = "计算中";

    [ObservableProperty]
    public partial string RemainingText { get; set; } = "计算中";

    [ObservableProperty]
    public partial string ProgressDetailText { get; set; } = "尚未开始";

    public void Start()
    {
        CurrentFileText = "等待 Python 事件";
        Apply(telemetry.InitialSnapshot);
    }

    public void Stop(
        TimeSpan elapsed,
        bool isRunning,
        int processedFiles,
        int totalFiles,
        double progressPercent)
    {
        Update(
            elapsed,
            isRunning,
            processedFiles,
            totalFiles,
            progressPercent);
    }

    public void Update(
        TimeSpan elapsed,
        bool isRunning,
        int processedFiles,
        int totalFiles,
        double progressPercent)
    {
        Apply(telemetry.Build(
            elapsed,
            isRunning,
            processedFiles,
            totalFiles,
            progressPercent));
    }

    public void Reset()
    {
        CurrentFileText = "";
        Apply(telemetry.ResetSnapshot);
    }

    public void ApplyCurrentFile(string text) => CurrentFileText = text;

    private void Apply(RunTelemetrySnapshot snapshot)
    {
        ElapsedText = snapshot.ElapsedText;
        SpeedText = snapshot.SpeedText;
        RemainingText = snapshot.RemainingText;
        ProgressDetailText = snapshot.ProgressDetailText;
    }
}
