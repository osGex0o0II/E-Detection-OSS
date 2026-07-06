namespace EDetection.Desktop.Models;

public sealed record UpdateCheckResult(
    string LatestVersion,
    string ReleaseName,
    string ReleaseUrl,
    DateTimeOffset? PublishedAt,
    bool IsUpdateAvailable);
