using System.Net;
using EDetection.Desktop.ViewModels;

namespace EDetection.Desktop.Services;

public sealed class NetworkProxyService(SecureCredentialService credentials)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    public HttpClientHandler BuildHandler(MainViewModel settings, bool useProxy)
    {
        var handler = new HttpClientHandler();
        if (!useProxy
            || !settings.EnableNetworkProxy
            || string.IsNullOrWhiteSpace(settings.ProxyAddress))
        {
            return handler;
        }

        handler.UseProxy = true;
        handler.Proxy = BuildProxy(settings);
        return handler;
    }

    public async Task TestProxyAsync(
        MainViewModel settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.EnableNetworkProxy)
        {
            throw new InvalidOperationException("请先启用网络代理。");
        }

        if (string.IsNullOrWhiteSpace(settings.ProxyAddress))
        {
            throw new InvalidOperationException("请填写代理地址。");
        }

        using var handler = BuildHandler(settings, useProxy: true);
        using var client = new HttpClient(handler)
        {
            Timeout = TestTimeout,
        };
        using var response = await client.GetAsync("https://www.msftconnecttest.com/connecttest.txt", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!body.Contains("Microsoft Connect Test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("代理返回了非预期的连接测试响应。");
        }
    }

    private WebProxy BuildProxy(MainViewModel settings)
    {
        var proxy = new WebProxy(settings.ProxyAddress);
        if (settings.ProxyRequiresAuthentication
            && !string.IsNullOrWhiteSpace(settings.ProxyUserName))
        {
            proxy.Credentials = new NetworkCredential(
                settings.ProxyUserName,
                credentials.GetProxyPassword());
        }

        return proxy;
    }
}
