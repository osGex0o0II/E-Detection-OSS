using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public interface IStartupIntegrationProvider
{
    StartupIntegrationSnapshot GetStatus();

    void SetEnabled(bool enabled);
}
