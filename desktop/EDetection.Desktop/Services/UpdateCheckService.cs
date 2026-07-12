using System.Text.Json;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class UpdateCheckService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task<UpdateCheckResult> CheckLatestAsync(
        string feedUrl,
        string currentVersion,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            throw new ArgumentException("更新源不能为空。", nameof(feedUrl));
        }

        using var client = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = RequestTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EDetection");
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
        var installerAsset = FindRecommendedInstallerAsset(root);
        var checksumAsset = FindChecksumAsset(root);
        var publishedAt = TryGetDateTimeOffset(root, "published_at");
        return new UpdateCheckResult(
            NormalizeVersionText(tagName),
            string.IsNullOrWhiteSpace(releaseName) ? tagName : releaseName,
            releaseUrl,
            installerAsset.Name,
            installerAsset.DownloadUrl,
            installerAsset.Digest,
            checksumAsset.Name,
            checksumAsset.DownloadUrl,
            publishedAt,
            IsNewer(tagName, currentVersion));
    }

    private static Uri ResolveLatestReleaseEndpoint(string feedUrl)
    {
        var uri = new Uri(feedUrl, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("更新源必须使用 HTTPS。");
        }

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
        if (!SemanticVersionComparer.TryCompare(latestVersion, currentVersion, out var comparison))
        {
            return !string.Equals(
                NormalizeVersionText(latestVersion),
                NormalizeVersionText(currentVersion),
                StringComparison.OrdinalIgnoreCase);
        }

        return comparison > 0;
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

    private static (string Name, string DownloadUrl, string Digest) FindRecommendedInstallerAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets)
            || assets.ValueKind is not JsonValueKind.Array)
        {
            return ("", "", "");
        }

        var candidates = new List<(string Name, string DownloadUrl, string Digest, int Score)>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(asset, "name");
            var downloadUrl = GetString(asset, "browser_download_url");
            var digest = GetString(asset, "digest");
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(downloadUrl)
                || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = ScoreInstallerAsset(name);
            if (score > 0)
            {
                candidates.Add((name, downloadUrl, digest, score));
            }
        }

        var selected = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return selected.Score > 0
            ? (selected.Name ?? "", selected.DownloadUrl ?? "", selected.Digest ?? "")
            : ("", "", "");
    }

    private static (string Name, string DownloadUrl) FindChecksumAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets)
            || assets.ValueKind is not JsonValueKind.Array)
        {
            return ("", "");
        }

        var candidates = new List<(string Name, string DownloadUrl, int Score)>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(asset, "name");
            var downloadUrl = GetString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(downloadUrl)
                || !name.EndsWith(".sha256.txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = 10;
            if (name.Contains("EDetection", StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }

            if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                || name.Contains("x64", StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            candidates.Add((name, downloadUrl, score));
        }

        var selected = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return selected.Score > 0
            ? (selected.Name ?? "", selected.DownloadUrl ?? "")
            : ("", "");
    }

    private static int ScoreInstallerAsset(string name)
    {
        const string installerPrefix = "EDetection-Setup-";
        if (!name.StartsWith(installerPrefix, StringComparison.OrdinalIgnoreCase)
            || (!name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("x64", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        var score = 0;
        // The production native installer keeps the stable unsuffixed name.
        if (name.Equals("EDetection-Setup-win-x64.exe", StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }

        if (name.Contains("-native-default", StringComparison.OrdinalIgnoreCase))
        {
            score += 500;
        }

        if (name.Contains("EDetection", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Installer", StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
            || name.Contains("x64", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (name.Contains("portable", StringComparison.OrdinalIgnoreCase)
            || name.Contains("zip", StringComparison.OrdinalIgnoreCase))
        {
            score -= 50;
        }

        return score;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
}
