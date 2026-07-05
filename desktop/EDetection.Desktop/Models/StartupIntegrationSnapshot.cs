namespace EDetection.Desktop.Models;

public sealed record StartupIntegrationSnapshot(
    bool IsEnabled,
    bool PointsToCurrentExecutable,
    string ProviderName,
    string EntryName,
    string ExecutablePath,
    string? RegisteredCommand)
{
    public string StatusText
    {
        get
        {
            if (IsEnabled)
            {
                return $"登录后自动启动已启用 · {ProviderName}";
            }

            if (!string.IsNullOrWhiteSpace(RegisteredCommand) && !PointsToCurrentExecutable)
            {
                return $"登录后自动启动未启用 · {ProviderName} 指向其他安装位置";
            }

            return $"登录后自动启动未启用 · {ProviderName}";
        }
    }
}
