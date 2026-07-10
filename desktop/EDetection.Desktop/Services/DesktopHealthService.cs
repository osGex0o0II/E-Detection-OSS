using EDetection.Desktop.Models;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;

namespace EDetection.Desktop.Services;

public sealed class DesktopHealthService
{
    private static readonly string[] CommonRequiredPackageFiles =
    [
        "EDetection.Desktop.exe",
        "config.json",
        Path.Combine("Assets", "Icons", "app.ico"),
        Path.Combine("Assets", "Icons", "running.ico"),
        "App.xbf",
        "MainWindow.xbf",
        "EDetection.Desktop.pri",
        Path.Combine("Styles", "Common.xbf"),
        Path.Combine("Views", "AppShellView.xbf"),
        Path.Combine("Views", "DetectionWorkbenchView.xbf"),
        Path.Combine("Views", "RunSetupView.xbf"),
        Path.Combine("Views", "SettingsView.xbf"),
        "Install-Desktop.ps1",
        "Uninstall-Desktop.ps1",
        "Test-DesktopPackageHealth.ps1",
        "Test-DesktopVisualSmoke.ps1",
        "Test-DesktopTrayMenuSmoke.ps1",
        "Test-DesktopSingleInstanceSmoke.ps1",
        "Test-DesktopSessionEndingSmoke.ps1",
        "Test-DesktopStartupIntegrationSmoke.ps1",
        "Test-DesktopRunStateSmoke.ps1",
        "Test-DesktopRunCompletionSmoke.ps1",
        "Test-DesktopNativeRunReportSmoke.ps1",
        "Test-DesktopSettingsSmoke.ps1",
        "Test-DesktopInstallSmoke.ps1",
        "Test-DesktopDiagnosticsRedactionSmoke.ps1",
        "Test-DesktopSignatureStatus.ps1",
        "release-info.txt",
        "install-files.txt",
        "INSTALL.txt",
    ];

    public DesktopHealthSnapshot Build(
        StartupIntegrationSnapshot startupStatus,
        SettingsService settings)
    {
        var notificationText = BuildNotificationText();
        var startupText = startupStatus.StatusText;
        var settingsText = BuildSettingsText(settings);
        var packageText = BuildPackageText();
        const string backendText = "Native .NET 检测核心";
        var installText = BuildInstallText();
        var attentionCount = CountAttention(
            notificationText,
            startupText,
            settingsText,
            packageText,
            backendText,
            installText);

        return new DesktopHealthSnapshot(
            attentionCount == 0
                ? "应用状态正常"
                : $"应用状态有 {attentionCount} 项需要关注",
            notificationText,
            startupText,
            settingsText,
            packageText,
            backendText,
            installText);
    }

    private static string BuildNotificationText()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return "系统通知不可用 · 当前 Windows/App SDK 环境不支持";
            }

            return AppNotificationManager.Default.Setting switch
            {
                AppNotificationSetting.Enabled => "系统通知可用",
                AppNotificationSetting.DisabledForApplication => "系统通知被应用设置关闭",
                AppNotificationSetting.DisabledForUser => "系统通知被用户关闭",
                AppNotificationSetting.DisabledByGroupPolicy => "系统通知被组策略关闭",
                AppNotificationSetting.DisabledByManifest => "系统通知被清单配置关闭",
                _ => $"系统通知状态: {AppNotificationManager.Default.Setting}",
            };
        }
        catch (Exception ex)
        {
            return $"系统通知检查失败 · {ex.Message}";
        }
    }

    private static string BuildSettingsText(SettingsService settings)
    {
        var exists = File.Exists(settings.StorePath);
        return exists
            ? $"{settings.StoreStatusText} · 已创建"
            : $"{settings.StoreStatusText} · 首次保存时创建";
    }

    public static string BuildPackageText(string? baseDirectory = null)
    {
        var packageRoot = baseDirectory ?? AppContext.BaseDirectory;
        var requiredPackageFiles = CommonRequiredPackageFiles;
        var missing = requiredPackageFiles
            .Where(file => !File.Exists(Path.Combine(packageRoot, file)))
            .ToArray();
        return missing.Length == 0
            ? $"包完整性可用 · {requiredPackageFiles.Length}/{requiredPackageFiles.Length}"
            : $"包完整性缺失 {missing.Length} 项 · {string.Join(", ", missing.Take(3))}";
    }

    private static string BuildInstallText()
    {
        var appPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\EDetection.Desktop.exe";
        using var key = Registry.CurrentUser.OpenSubKey(appPathsKey, writable: false);
        var registeredPath = key?.GetValue("") as string;
        var processPath = Environment.ProcessPath ?? "";
        if (!string.IsNullOrWhiteSpace(registeredPath)
            && string.Equals(registeredPath, processPath, StringComparison.OrdinalIgnoreCase))
        {
            return "安装形态: 当前用户安装版";
        }

        if (!string.IsNullOrWhiteSpace(registeredPath))
        {
            return "安装形态: 便携/开发版 · App Paths 指向其他位置";
        }

        return "安装形态: 便携/开发版";
    }

    private static int CountAttention(params string[] values) =>
        values.Count(value =>
            value.Contains("不可用", StringComparison.OrdinalIgnoreCase)
            || value.Contains("关闭", StringComparison.OrdinalIgnoreCase)
            || value.Contains("失败", StringComparison.OrdinalIgnoreCase)
            || value.Contains("缺失", StringComparison.OrdinalIgnoreCase)
            || value.Contains("其他位置", StringComparison.OrdinalIgnoreCase));
}
