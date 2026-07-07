namespace EDetection.Desktop.Models;

public sealed record UpdateCheckResult(
    string LatestVersion,
    string ReleaseName,
    string ReleaseUrl,
    string InstallerName,
    string InstallerDownloadUrl,
    string InstallerDigest,
    DateTimeOffset? PublishedAt,
    bool IsUpdateAvailable)
{
    public bool HasInstallerDownload => !string.IsNullOrWhiteSpace(InstallerDownloadUrl);

    public string PreferredActionUrl =>
        HasInstallerDownload || string.IsNullOrWhiteSpace(ReleaseUrl)
            ? InstallerDownloadUrl
            : ReleaseUrl;
}
