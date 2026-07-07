namespace EDetection.Desktop.Services;

public static class EndpointSecurity
{
    public static void RequireHttpsOrLocal(Uri uri, string displayName)
    {
        if (uri.Scheme == Uri.UriSchemeHttps || IsLocalHttp(uri))
        {
            return;
        }

        throw new InvalidOperationException($"{displayName} 必须使用 HTTPS；仅本机 localhost/127.0.0.1/::1 调试地址允许 HTTP。");
    }

    private static bool IsLocalHttp(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        return uri.IsLoopback
               || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Host, "[::1]", StringComparison.OrdinalIgnoreCase);
    }
}
