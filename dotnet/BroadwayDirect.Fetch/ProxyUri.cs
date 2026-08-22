namespace BroadwayDirect.Fetch;

/// <summary>
/// Parses a proxy value in the standard "scheme://[user:pass@]host:port"
/// format (passed per request - see README_DOTNET.md's "Proxy" section).
/// WebView2 only accepts "--proxy-server=scheme://host:port" (WITHOUT
/// inline user:pass) at the environment level; credentials must be supplied
/// separately via BasicAuthenticationRequested.
/// </summary>
public static class ProxyUri
{
    public record Parsed(string SwitchValue, string? User, string? Password);

    public static Parsed Parse(string proxy)
    {
        var uri = new Uri(proxy, UriKind.Absolute);
        string? user = null, password = null;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }

        var switchValue = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        return new Parsed(switchValue, user, password);
    }
}
