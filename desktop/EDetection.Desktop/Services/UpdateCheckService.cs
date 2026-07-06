using System.Text.Json;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class UpdateCheckService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task<UpdateCheckResult> CheckLatestAsync(
        string feedUrl,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            throw new ArgumentException("更新源不能为空。", nameof(feedUrl));
        }

        using var client = new HttpClient
        {
            Timeout = RequestTimeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("E-Detection-Desktop");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var endpoint = ResolveLatestReleaseEndpoint(feedUrl);
        using var response = await client.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var tagName = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tagName))
        {
            throw new InvalidOperationException("更新源未返回版本号。");
        }

        var releaseUrl = GetString(root, "html_url");
        var releaseName = GetString(root, "name");
        var publishedAt = TryGetDateTimeOffset(root, "published_at");
        return new UpdateCheckResult(
            NormalizeVersionText(tagName),
            string.IsNullOrWhiteSpace(releaseName) ? tagName : releaseName,
            releaseUrl,
            publishedAt,
            IsNewer(tagName, currentVersion));
    }

    private static Uri ResolveLatestReleaseEndpoint(string feedUrl)
    {
        var uri = new Uri(feedUrl, UriKind.Absolute);
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return new Uri($"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/latest");
            }
        }

        return uri;
    }

    private static bool IsNewer(string latestVersion, string currentVersion)
    {
        if (!TryParseVersion(latestVersion, out var latest)
            || !TryParseVersion(currentVersion, out var current))
        {
            return !string.Equals(
                NormalizeVersionText(latestVersion),
                NormalizeVersionText(currentVersion),
                StringComparison.OrdinalIgnoreCase);
        }

        return latest.CompareTo(current) > 0;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = NormalizeVersionText(value);
        var metadataIndex = normalized.IndexOfAny(['-', '+']);
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        if (Version.TryParse(normalized, out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static string NormalizeVersionText(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? normalized[1..]
            : normalized;
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
}
