using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EDetection.Desktop.ViewModels;

namespace EDetection.Desktop.Services;

public sealed class LlmAssistantService(SecureCredentialService credentials)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public async Task<string> TestConnectionAsync(
        MainViewModel settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.EnableLlmAssistant)
        {
            throw new InvalidOperationException("请先启用智能助手。");
        }

        if (string.IsNullOrWhiteSpace(settings.LlmEndpoint))
        {
            throw new InvalidOperationException("请填写 LLM 服务地址。");
        }

        if (string.IsNullOrWhiteSpace(settings.LlmModel))
        {
            throw new InvalidOperationException("请填写 LLM 模型。");
        }

        var apiKey = credentials.GetLlmApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("请先保存 LLM API Key。");
        }

        using var handler = BuildHandler(settings);
        using var client = new HttpClient(handler)
        {
            Timeout = RequestTimeout,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings.LlmEndpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new StringContent(BuildChatCompletionsPayload(settings.LlmModel), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ExtractAssistantMessage(document.RootElement);
    }

    private HttpClientHandler BuildHandler(MainViewModel settings)
    {
        var handler = new HttpClientHandler();
        if (!settings.EnableNetworkProxy
            || !settings.UseProxyForLlm
            || string.IsNullOrWhiteSpace(settings.ProxyAddress))
        {
            return handler;
        }

        var proxy = new WebProxy(settings.ProxyAddress);
        if (settings.ProxyRequiresAuthentication
            && !string.IsNullOrWhiteSpace(settings.ProxyUserName))
        {
            proxy.Credentials = new NetworkCredential(
                settings.ProxyUserName,
                credentials.GetProxyPassword());
        }

        handler.UseProxy = true;
        handler.Proxy = proxy;
        return handler;
    }

    private static Uri BuildEndpoint(string endpoint)
    {
        var uri = new Uri(endpoint.Trim(), UriKind.Absolute);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var builder = new UriBuilder(uri)
        {
            Query = "",
            Fragment = "",
        };

        builder.Path = string.IsNullOrWhiteSpace(path.Trim('/'))
            ? "/v1/chat/completions"
            : path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? $"{path}/chat/completions"
                : throw new InvalidOperationException("请填写 OpenAI 兼容的基础地址或 /chat/completions 地址。");

        return builder.Uri;
    }

    private static string BuildChatCompletionsPayload(string model) =>
        JsonSerializer.Serialize(new
        {
            model = model.Trim(),
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = "Reply with OK for an E-Detection connection test.",
                },
            },
            max_tokens = 8,
            temperature = 0,
        });

    private static string ExtractAssistantMessage(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()?.Trim() ?? "";
        }

        return "";
    }
}
