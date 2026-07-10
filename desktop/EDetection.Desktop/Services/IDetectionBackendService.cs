using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public interface IDetectionBackendService
{
    Task<int> RunDetectionAsync(
        DetectionRequest request,
        IProgress<DetectionBackendEvent> progress,
        CancellationToken cancellationToken);
}
