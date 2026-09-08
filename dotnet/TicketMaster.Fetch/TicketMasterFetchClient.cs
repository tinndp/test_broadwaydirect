using System.Net;
using System.Text.Json;
using BroadwayDirect.Core.Proxy;
using BroadwayDirect.Fetch;
using TicketMaster.Core;
using TicketMaster.Core.Models;

namespace TicketMaster.Fetch;

public sealed class TicketMasterCrawlResult
{
    public string EventId { get; set; } = "";
    public int Total { get; set; }
    public int PageCount { get; set; }
    /// <summary>Raw quickpicks JSON for every page, in order.</summary>
    public List<string> RawPages { get; } = new();
    /// <summary>Deserialized pages, in order.</summary>
    public List<QuickPicksResponse> Pages { get; } = new();
    public bool FellBackToScroll { get; set; }
}

/// <summary>
/// Ticketmaster "giả lập API" fetch, POC of the ETECH bot's
/// <c>TicketMaster.Fetch/TicketMasterFetchClient</c> (default <c>ApiReplay</c> mode):
///
///  1. one real (off-screen) WebView2 through the proxy warms past Kasada and captures the page's
///     own first <c>quickpicks</c> request (URL + headers + cookies + offset-0 body),
///  2. every next page is a plain <see cref="HttpClient"/> GET of the same URL with
///     <c>offset += limit</c>, carrying those cookies/headers, routed through the SAME proxy - this
///     bypasses the page-side Kasada <c>fetch</c>/<c>XHR</c> wrapper (an in-page replay is blocked).
///
/// <c>qty</c> is left exactly as the first request used (the current site does NOT switch qty
/// between pages). Not concurrent - offset pages are fetched sequentially with a small delay, same
/// as the ETECH bot, to keep the request pattern tame.
/// </summary>
public sealed class TicketMasterFetchClient : IAsyncDisposable
{
    private readonly WebView2Host _host = new();

    public int Retries { get; init; } = 3;
    public int TimeoutSeconds { get; init; } = 30;
    public int FirstPageTimeoutSeconds { get; init; } = 180;
    public int InterPageDelayMs { get; init; } = 300;
    public double SleepSeconds { get; init; } = 0.5;

    /// <summary>
    /// eventUrl = the public event page (e.g.
    /// <c>https://www.ticketmaster.com/&lt;slug&gt;/event/{eventId}</c>). proxy = "" or the standard
    /// "scheme://[user:pass@]host:port" / raw "host:port:user:pass" form (Ticketmaster needs a proxy
    /// to show tickets).
    /// </summary>
    public async Task<TicketMasterCrawlResult> GetAllPagesAsync(
        string eventId, string eventUrl, string proxy = "", CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(TimeoutSeconds);

        string? proxySwitch = null, proxyUser = null, proxyPassword = null;
        string? proxyHost = null;
        int proxyPort = 0;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            var normalized = ProxyUri.Normalize(proxy);
            var u = new Uri(normalized, UriKind.Absolute);
            proxySwitch = $"{u.Scheme}://{u.Host}:{u.Port}";
            proxyHost = u.Host;
            proxyPort = u.Port;
            if (!string.IsNullOrEmpty(u.UserInfo))
            {
                var parts = u.UserInfo.Split(':', 2);
                proxyUser = Uri.UnescapeDataString(parts[0]);
                proxyPassword = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            }
        }

        await using var session = new TicketMasterBrowserSession(_host);
        var first = await session.WarmAndCaptureFirstAsync(
            eventUrl, proxySwitch, proxyUser, proxyPassword, timeout, FirstPageTimeoutSeconds);

        var result = new TicketMasterCrawlResult { EventId = eventId };

        var url = QuickPicksUrl.Parse(first.Url);
        var baseUri = BaseUriOrThrow(url.PathBeforeQuery);
        var limit = url.GetInt("limit", 40);

        // offset-0 page the browser already fetched
        AddPage(result, first.Body);

        using var http = BuildHttpClient(first.RequestHeaders, proxyHost, proxyPort, proxyUser, proxyPassword, timeout);

        var offset = 0;
        while (!ct.IsCancellationRequested)
        {
            var q = url.Clone();
            q.Set("offset", offset.ToString());
            if (q.Has("includeResale")) q.Set("includeResale", "false");
            var pageUrl = q.BuildUrl();

            QuickPicksResponse? page = null;
            Exception? lastErr = null;
            for (var attempt = 1; attempt <= Retries; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                    ApplyHeaders(req, first.RequestHeaders);
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
                    var code = (int)resp.StatusCode;
                    if (code is 403 or 429)
                        throw new InvalidOperationException(
                            $"HTTP {code} - replayed quickpicks rejected (Kasada tightened, or proxy IP flagged). " +
                            "The ETECH bot auto-falls back to scroll-capture here.");
                    if (code >= 400) throw new InvalidOperationException($"HTTP {code}");
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    page = JsonSerializer.Deserialize<QuickPicksResponse>(body);
                    if (page == null) throw new InvalidOperationException("empty/invalid quickpicks JSON");
                    result.RawPages.Add(body);
                    result.Pages.Add(page);
                    break;
                }
                catch (Exception e)
                {
                    lastErr = e;
                    if (attempt < Retries)
                        await Task.Delay(TimeSpan.FromSeconds(SleepSeconds * attempt * 2), ct);
                }
            }
            if (page == null)
                throw new InvalidOperationException($"Failed quickpicks offset {offset}: {lastErr?.Message}", lastErr);

            result.Total = page.Total;
            if (page.Offset + limit >= page.Total) break;
            offset = page.Offset + limit;
            if (InterPageDelayMs > 0) await Task.Delay(InterPageDelayMs, ct);
        }

        result.PageCount = result.Pages.Count;
        return result;
    }

    /// <summary>Fetch + group in one call. Mirrors what the ETECH bot's SessionForm does
    /// (crawl every page -&gt; ListingBuilder -&gt; persist).</summary>
    public async Task<(TicketMasterCrawlResult Crawl, List<TicketMasterListing> Listings)> GetListingsAsync(
        string eventId, string eventUrl, string proxy = "", CancellationToken ct = default)
    {
        var crawl = await GetAllPagesAsync(eventId, eventUrl, proxy, ct);
        var builder = new ListingBuilder();
        foreach (var p in crawl.Pages) builder.AddPage(p);
        return (crawl, builder.Build());
    }

    private static void AddPage(TicketMasterCrawlResult result, string body)
    {
        if (string.IsNullOrEmpty(body)) return;
        var page = JsonSerializer.Deserialize<QuickPicksResponse>(body);
        if (page == null) return;
        result.RawPages.Add(body);
        result.Pages.Add(page);
        result.Total = page.Total;
    }

    private static HttpClient BuildHttpClient(
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string? proxyHost, int proxyPort, string? proxyUser, string? proxyPassword, TimeSpan timeout)
    {
        var cookies = new CookieContainer();
        var cookieHeader = headers.FirstOrDefault(
            h => string.Equals(h.Key, "cookie", StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            foreach (var part in cookieHeader.Split(';'))
            {
                var p = part.Trim();
                var eq = p.IndexOf('=');
                if (eq <= 0) continue;
                try { cookies.Add(new Cookie(p[..eq].Trim(), p[(eq + 1)..].Trim().Replace(",", "%2C"), "/", ".ticketmaster.com")); }
                catch { }
            }
        }

        var handler = new SocketsHttpHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (!string.IsNullOrWhiteSpace(proxyHost))
        {
            var proxy = new WebProxy(proxyHost, proxyPort) { BypassProxyOnLocal = false };
            if (!string.IsNullOrEmpty(proxyUser))
                proxy.Credentials = new NetworkCredential(proxyUser, proxyPassword ?? "");
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        return new HttpClient(handler) { Timeout = timeout };
    }

    private static void ApplyHeaders(HttpRequestMessage req, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        foreach (var h in headers)
        {
            var n = h.Key.ToLowerInvariant();
            if (n is "cookie" or "host" or "content-length" or "connection" or "accept-encoding"
                || n.StartsWith("if-"))
                continue;
            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
    }

    private static string BaseUriOrThrow(string pathBeforeQuery)
    {
        var uri = new Uri(pathBeforeQuery);
        var baseUri = $"{uri.Scheme}://{uri.Host}";
        if (!baseUri.Contains("ticketmaster.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("[BLOCKED] redirected to " + baseUri.Replace("offeradapter.", ""));
        return baseUri;
    }

    public async ValueTask DisposeAsync()
    {
        try { _host.Dispose(); } catch { }
        await Task.CompletedTask;
    }
}
