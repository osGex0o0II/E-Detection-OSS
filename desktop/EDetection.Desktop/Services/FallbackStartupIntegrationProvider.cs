using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class FallbackStartupIntegrationProvider : IStartupIntegrationProvider
{
    private readonly IStartupIntegrationProvider _primary;
    private readonly IStartupIntegrationProvider _fallback;

    public FallbackStartupIntegrationProvider(
        IStartupIntegrationProvider primary,
        IStartupIntegrationProvider fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public StartupIntegrationSnapshot GetStatus()
    {
        var primaryStatus = TryGetStatus(_primary);
        if (primaryStatus?.IsEnabled is true)
        {
            return primaryStatus;
        }

        var fallbackStatus = TryGetStatus(_fallback);
        if (fallbackStatus?.IsEnabled is true)
        {
            return fallbackStatus;
        }

        return primaryStatus
            ?? fallbackStatus
            ?? new StartupIntegrationSnapshot(
                IsEnabled: false,
                PointsToCurrentExecutable: false,
                "启动集成不可用",
                EntryName: "",
                ExecutablePath: "",
                RegisteredCommand: null);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            try
            {
                _primary.SetEnabled(true);
                TrySetEnabled(_fallback, enabled: false);
                return;
            }
            catch
            {
                _fallback.SetEnabled(true);
                return;
            }
        }

        TrySetEnabled(_primary, enabled: false);
        _fallback.SetEnabled(false);
    }

    private static StartupIntegrationSnapshot? TryGetStatus(IStartupIntegrationProvider provider)
    {
        try
        {
            return provider.GetStatus();
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetEnabled(IStartupIntegrationProvider provider, bool enabled)
    {
        try
        {
            provider.SetEnabled(enabled);
        }
        catch
        {
        }
    }
}
