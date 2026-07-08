using System.Net.Http.Headers;
using System.Text;
using EDetection.Desktop.Models;
using EDetection.Desktop.ViewModels;

namespace EDetection.Desktop.Services;

public sealed class NtfyNotificationService(
    SecureCredentialService credentials,
    NetworkProxyService? networkProxy = null)
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(8);
    private readonly NetworkProxyService _networkProxy = networkProxy ?? new NetworkProxyService(credentials);

    public async Task<bool> TrySendAsync(
        DesktopNotificationRequest request,
        MainViewModel settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.EnableNtfyNotifications
            || string.IsNullOrWhiteSpace(settings.NtfyServerUrl)
            || string.IsNullOrWhiteSpace(settings.NtfyTopic))
        {
            return false;
        }

        var endpoint = BuildTopicEndpoint(settings.NtfyServerUrl, settings.NtfyTopic);
        EndpointSecurity.RequireHttpsOrLocal(endpoint, "ntfy 服务地址");
        using var handler = _networkProxy.BuildHandler(settings, settings.UseProxyForNotifications);
        using var client = new HttpClient(handler)
        {
            Timeout = SendTimeout,
        };
        var body = BuildRemoteNotificationBody(request, settings);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };

        message.Headers.TryAddWithoutValidation("Title", DiagnosticsRedactor.Redact(request.Title));
        message.Headers.TryAddWithoutValidation("Priority", BuildPriority(settings.SelectedNtfyPriorityIndex));
        message.Headers.TryAddWithoutValidation("Tags", BuildTags(request.Kind));
        if (request.Kind is DesktopNotificationKind.Update && !string.IsNullOrWhiteSpace(request.ActionUrl))
        {
            message.Headers.TryAddWithoutValidation("Click", request.ActionUrl);
        }

        var token = credentials.GetNtfyToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return true;
    }

    private static string BuildRemoteNotificationBody(
        DesktopNotificationRequest request,
        MainViewModel settings)
    {
        if (request.Kind is DesktopNotificationKind.Update)
        {
            return string.IsNullOrWhiteSpace(request.ActionUrl)
                ? DiagnosticsRedactor.Redact(request.Message, settings.PythonExecutable, PythonBackendService.ResolveBackendWorkingDirectory())
                : $"{DiagnosticsRedactor.Redact(request.Message, settings.PythonExecutable, PythonBackendService.ResolveBackendWorkingDirectory())}{Environment.NewLine}{request.ActionUrl}";
        }

        return request.Kind switch
        {
            DesktopNotificationKind.Success => "检测已完成，请回到本机查看报告和详情。",
            DesktopNotificationKind.Error => "检测未完成，请回到本机查看失败原因。",
            DesktopNotificationKind.Warning => "检测需要处理，请回到本机查看详情。",
            _ => "E-Detection 有新的状态，请回到本机查看详情。",
        };
    }

    private static Uri BuildTopicEndpoint(string serverUrl, string topic)
    {
        var baseUri = serverUrl.EndsWith("/", StringComparison.Ordinal)
            ? new Uri(serverUrl, UriKind.Absolute)
            : new Uri($"{serverUrl}/", UriKind.Absolute);
        return new Uri(baseUri, Uri.EscapeDataString(topic.Trim()));
    }

    private static string BuildPriority(int index) => Math.Clamp(index, 0, 4) switch
    {
        0 => "1",
        1 => "2",
        3 => "4",
        4 => "5",
        _ => "3",
    };

    private static string BuildTags(DesktopNotificationKind kind) => kind switch
    {
        DesktopNotificationKind.Success => "white_check_mark",
        DesktopNotificationKind.Warning => "warning",
        DesktopNotificationKind.Error => "rotating_light",
        DesktopNotificationKind.Update => "arrow_up",
        _ => "information_source",
    };
}
