using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class PoetryStatusService
{
    private static readonly PoetryStatusSnapshot FallbackPoem = new(
        "山重水复疑无路，柳暗花明又一村。",
        "陆游 · 游山西村",
        true);

    public async Task<PoetryStatusSnapshot> GetRandomAsync(
        string serviceUrl,
        int languageIndex,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(serviceUrl, languageIndex);
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8),
            };
            var response = await client.GetFromJsonAsync<PoetryResponse>(endpoint, cancellationToken);
            var poem = response?.Data;
            var line = poem?.Content?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                return FallbackPoem;
            }

            var author = poem?.Author?.Name?.Trim();
            var dynasty = poem?.Dynasty?.Name?.Trim();
            var title = poem?.Title?.Trim();
            var sourceParts = new[] { dynasty, author, title }
                .Where(value => !string.IsNullOrWhiteSpace(value));

            return new PoetryStatusSnapshot(
                TrimLine(line),
                string.Join(" · ", sourceParts),
                false);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or TaskCanceledException
                                   or OperationCanceledException
                                   or NotSupportedException
                                   or System.Text.Json.JsonException
                                   or UriFormatException)
        {
            return FallbackPoem;
        }
    }

    private static Uri BuildEndpoint(string serviceUrl, int languageIndex)
    {
        var baseUrl = string.IsNullOrWhiteSpace(serviceUrl)
            ? "https://poetry.palemoky.com/"
            : serviceUrl.Trim();
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        var language = languageIndex == 1 ? "zh-Hant" : "zh-Hans";
        return new Uri(new Uri(baseUrl), $"api/poems/random?lang={language}");
    }

    private static string TrimLine(string value)
    {
        var normalized = value
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 48
            ? normalized
            : $"{normalized[..48]}...";
    }

    private sealed class PoetryResponse
    {
        [JsonPropertyName("data")]
        public PoetryData? Data { get; set; }
    }

    private sealed class PoetryData
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("content")]
        public List<string>? Content { get; set; }

        [JsonPropertyName("author")]
        public PoetryNamedValue? Author { get; set; }

        [JsonPropertyName("dynasty")]
        public PoetryNamedValue? Dynasty { get; set; }
    }

    private sealed class PoetryNamedValue
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
