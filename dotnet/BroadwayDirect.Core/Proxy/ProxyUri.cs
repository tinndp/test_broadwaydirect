namespace BroadwayDirect.Core.Proxy;

/// <summary>
/// Parses a proxy value in the standard "scheme://[user:pass@]host:port"
/// format (passed per request - see README_DOTNET.md's "Proxy" section).
/// WebView2 only accepts "--proxy-server=scheme://host:port" (WITHOUT
/// inline user:pass) at the environment level; credentials must be supplied
/// separately via BasicAuthenticationRequested. Lives in Core (not Fetch)
/// since it has no WebView2 dependency - keeps it testable on macOS, same
/// as everything else under Core.
/// </summary>
public static class ProxyUri
{
    public record Parsed(string SwitchValue, string? User, string? Password);

    /// <summary>Accepts either a proper "scheme://[user:pass@]host:port" URI
    /// (returned unchanged) or the raw "host:port:user:pass" format proxy
    /// providers hand out (converted to "http://user:pass@host:port").
    /// Mirrors python/broadwaydirect/proxy_pool.py's normalize_proxy.</summary>
    public static string Normalize(string raw)
    {
        var line = raw.Trim();
        if (line.Contains("://")) return line;

        var parts = line.Split(':', 4);
        if (parts.Length != 4)
            throw new FormatException(
                $"expected host:port:user:pass or scheme://[user:pass@]host:port, got '{raw}'");
        var (host, port, user, pwd) = (parts[0], parts[1], parts[2], parts[3]);
        return $"http://{user}:{pwd}@{host}:{port}";
    }

    public static Parsed Parse(string proxy)
    {
        var uri = new Uri(Normalize(proxy), UriKind.Absolute);
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
