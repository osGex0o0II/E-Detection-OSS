using CommunityToolkit.Mvvm.ComponentModel;
using EDetection.Desktop.Models;
using EDetection.Desktop.Services;

namespace EDetection.Desktop.ViewModels;

public sealed partial class DesktopHealthViewModel(
    DesktopHealthService health,
    StartupService startup,
    SettingsService settings) : ObservableObject
{
    [ObservableProperty]
    public partial string SummaryText { get; set; } = "应用状态待检查";

    [ObservableProperty]
    public partial string NotificationText { get; set; } = "系统通知待检查";

    [ObservableProperty]
    public partial string StartupText { get; set; } = "启动集成待检查";

    [ObservableProperty]
    public partial string SettingsText { get; set; } = "设置存储待检查";

    [ObservableProperty]
    public partial string PackageText { get; set; } = "包完整性待检查";

    [ObservableProperty]
    public partial string BackendText { get; set; } = "检测核心待检查";

    [ObservableProperty]
    public partial string InstallText { get; set; } = "安装形态待检查";

    public void Refresh()
    {
        var snapshot = health.Build(
            startup.GetStatus(), settings);
        Apply(snapshot);
    }

    private void Apply(DesktopHealthSnapshot snapshot)
    {
        SummaryText = snapshot.SummaryText;
        NotificationText = snapshot.NotificationText;
        StartupText = snapshot.StartupText;
        SettingsText = snapshot.SettingsText;
        PackageText = snapshot.PackageText;
        BackendText = snapshot.BackendText;
        InstallText = snapshot.InstallText;
    }
}
