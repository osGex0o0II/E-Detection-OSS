using EDetection.NativeCore.Models;

namespace EDetection.NativeCore.Services;

public static class DetectionExecutionService
{
    public static Task<int> RunAsync(
        IDetectionBackendService backend,
        DetectionRequest request,
        IProgress<DetectionBackendEvent> progress,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => backend.RunDetectionAsync(request, progress, cancellationToken),
            cancellationToken);
}
