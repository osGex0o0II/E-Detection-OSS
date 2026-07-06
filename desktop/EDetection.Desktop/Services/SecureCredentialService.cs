using Windows.Security.Credentials;

namespace EDetection.Desktop.Services;

public sealed class SecureCredentialService
{
    private const string ResourcePrefix = "E-Detection.Desktop";
    private const string LlmApiKeyResource = $"{ResourcePrefix}.LLM.ApiKey";
    private const string NtfyTokenResource = $"{ResourcePrefix}.Ntfy.Token";
    private const string ProxyPasswordResource = $"{ResourcePrefix}.Proxy.Password";
    private const string DefaultUserName = "default";

    private readonly PasswordVault _vault = new();

    public string LlmApiKeyStatusText => BuildStatusText(LlmApiKeyResource, "LLM API Key");

    public string NtfyTokenStatusText => BuildStatusText(NtfyTokenResource, "ntfy Token");

    public string ProxyPasswordStatusText => BuildStatusText(ProxyPasswordResource, "代理密码");

    public void SaveLlmApiKey(string secret) => SaveSecret(LlmApiKeyResource, secret);

    public void SaveNtfyToken(string secret) => SaveSecret(NtfyTokenResource, secret);

    public void SaveProxyPassword(string secret) => SaveSecret(ProxyPasswordResource, secret);

    public void ClearLlmApiKey() => ClearSecret(LlmApiKeyResource);

    public void ClearNtfyToken() => ClearSecret(NtfyTokenResource);

    public void ClearProxyPassword() => ClearSecret(ProxyPasswordResource);

    public string GetLlmApiKey() => GetSecret(LlmApiKeyResource);

    public string GetNtfyToken() => GetSecret(NtfyTokenResource);

    public string GetProxyPassword() => GetSecret(ProxyPasswordResource);

    private string BuildStatusText(string resource, string label) =>
        HasSecret(resource)
            ? $"{label} 已保存到 Windows 凭据"
            : $"{label} 未保存";

    private bool HasSecret(string resource)
    {
        try
        {
            _vault.Retrieve(resource, DefaultUserName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveSecret(string resource, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        ClearSecret(resource);
        _vault.Add(new PasswordCredential(resource, DefaultUserName, secret));
    }

    private string GetSecret(string resource)
    {
        try
        {
            var credential = _vault.Retrieve(resource, DefaultUserName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch
        {
            return "";
        }
    }

    private void ClearSecret(string resource)
    {
        try
        {
            _vault.Remove(_vault.Retrieve(resource, DefaultUserName));
        }
        catch
        {
        }
    }
}
