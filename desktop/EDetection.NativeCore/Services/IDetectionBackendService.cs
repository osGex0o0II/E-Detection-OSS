using EDetection.NativeCore.Models;

namespace EDetection.NativeCore.Services;

public interface IDetectionBackendService
{
    Task<int> RunDetectionAsync(
        DetectionRequest request,
        IProgress<DetectionBackendEvent> progress,
        CancellationToken cancellationToken);
}
