using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class StartupService
{
    private readonly IStartupIntegrationProvider _provider;

    public StartupService()
        : this(new FallbackStartupIntegrationProvider(
            new TaskSchedulerStartupIntegrationProvider(),
            new RegistryRunStartupIntegrationProvider()))
    {
    }

    public StartupService(IStartupIntegrationProvider provider)
    {
        _provider = provider;
    }

    public StartupIntegrationSnapshot GetStatus() => _provider.GetStatus();

    public bool IsEnabled() => GetStatus().IsEnabled;

    public void SetEnabled(bool enabled) => _provider.SetEnabled(enabled);
}
