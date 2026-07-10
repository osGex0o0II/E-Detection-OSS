namespace EDetection.NativeCore.Services;

public static class DetectionBackendServiceFactory
{
    public const string NativeMode = "native";

    public static IDetectionBackendService CreateDefault() => CreateNative();

    // The desktop application has one implementation of the detection contract.
    // Keep the argument for compatibility with callers that persisted an older
    // backend selection, but deliberately ignore it.
    public static IDetectionBackendService Create(string? backendMode) => CreateNative();

    public static IDetectionBackendService CreateNative() => new NativeDetectionBackendService();

    public static string GetDefaultBackendMode(string? baseDirectory = null) => NativeMode;
}
