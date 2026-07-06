using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EDetection.Desktop.ViewModels;

namespace EDetection.Desktop.Services;

public sealed class LlmAssistantService(
    SecureCredentialService credentials,
    NetworkProxyService? networkProxy = null)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly NetworkProxyService _networkProxy = networkProxy ?? new NetworkProxyService(credentials);

    public async Task<string> TestConnectionAsync(
        MainViewModel settings,
        CancellationToken cancellationToken = default)
    {
        return await SendUserPromptAsync(
            settings,
            "Reply with OK for an E-Detection connection test.",
            maxTokens: 8,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExplainDetailAsync(
        MainViewModel settings,
        string detailText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(detailText))
        {
            throw new InvalidOperationException("请选择一条异常明细。");
        }

        var prompt = string.Join(
            Environment.NewLine,
            "你是电气异常检测软件 E-Detection 的助手。请用中文解释下面这条异常明细。",
            "要求：",
            "1. 用 3-5 条短句说明可能含义、风险和下一步排查建议。",
            "2. 不要编造未提供的数据。",
            "3. 如果证据不足，明确说明需要结合现场和原始数据确认。",
            "",
            detailText);

        return await SendUserPromptAsync(
            settings,
            prompt,
            maxTokens: 260,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendUserPromptAsync(
        MainViewModel settings,
        string prompt,
        int maxTokens,
        CancellationToken cancellationToken)
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

        using var handler = _networkProxy.BuildHandler(settings, settings.UseProxyForLlm);
        using var client = new HttpClient(handler)
        {
            Timeout = RequestTimeout,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings.LlmEndpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new StringContent(BuildChatCompletionsPayload(settings.LlmModel, prompt, maxTokens), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ExtractAssistantMessage(document.RootElement);
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

    private static string BuildChatCompletionsPayload(string model, string prompt, int maxTokens) =>
        JsonSerializer.Serialize(new
        {
            model = model.Trim(),
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = prompt,
                },
            },
            max_tokens = maxTokens,
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
