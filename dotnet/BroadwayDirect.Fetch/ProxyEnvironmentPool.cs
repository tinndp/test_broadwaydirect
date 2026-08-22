using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BroadwayDirect.Fetch;

internal sealed class ProxySession
{
    public required CoreWebView2Environment Environment { get; init; }
    public required WebView2 Control { get; init; }
    public required System.Windows.Forms.Form HostForm { get; init; }
    public string? WarmedUrl { get; set; }
    public int Generation { get; set; }
}

/// <summary>
/// Pool of WebView2 "environments" (each environment = its own profile/
/// cookie-jar + optionally its own proxy) keyed by the proxy value passed
/// per request (see README_DOTNET.md's "Proxy" section - proxy is set at
/// the environment level, not per-call, so it must be pooled by this key).
/// Equivalent to _ensure_session/_open_session + _reopen_lock/_session_gen
/// in the Python client.py, just keyed by "proxy" instead of "series_id".
/// </summary>
public sealed class ProxyEnvironmentPool : IAsyncDisposable
{
    private readonly WebView2Host _host;
    private readonly Dictionary<string, ProxySession> _sessions = new();
    private readonly Dictionary<string, SemaphoreSlim> _locks = new();
    private readonly object _locksGate = new();

    public ProxyEnvironmentPool(WebView2Host host) => _host = host;

    private static string KeyOf(string proxy) => string.IsNullOrEmpty(proxy) ? "__direct__" : proxy;

    private SemaphoreSlim LockFor(string key)
    {
        lock (_locksGate)
        {
            if (!_locks.TryGetValue(key, out var sem))
                _locks[key] = sem = new SemaphoreSlim(1, 1);
            return sem;
        }
    }

    /// <summary>Ensures a "warmed" (already past Cloudflare) session exists for
    /// this (proxy, bootstrapUrl) pair, opening a new/fresh one if none exists
    /// yet or the existing one is warmed for a different url.</summary>
    public async Task<(WebView2 Control, int Generation)> EnsureWarmSessionAsync(
        string proxy, string bootstrapUrl, TimeSpan timeout)
    {
        var key = KeyOf(proxy);
        var sem = LockFor(key);
        await sem.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(key, out var existing) && existing.WarmedUrl == bootstrapUrl)
                return (existing.Control, existing.Generation);

            await OpenSessionAsync(key, proxy, bootstrapUrl, timeout);
            var s = _sessions[key];
            return (s.Control, s.Generation);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Reopens the session ONLY IF no other task has already reopened it
    /// after this error (compares generation) - avoids multiple requests that
    /// hit the same error all reopening redundantly. Equivalent to the
    /// "if self._session_gen == gen_before" check in client.py.</summary>
    public async Task ReopenIfStillCurrentAsync(string proxy, string bootstrapUrl, int generationBefore, TimeSpan timeout)
    {
        var key = KeyOf(proxy);
        var sem = LockFor(key);
        await sem.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(key, out var existing) && existing.Generation == generationBefore)
                await OpenSessionAsync(key, proxy, bootstrapUrl, timeout);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task OpenSessionAsync(string key, string proxy, string bootstrapUrl, TimeSpan timeout)
    {
        await _host.RunOnUiThreadAsync(async () =>
        {
            Console.Error.WriteLine("  opening a real WebView2 window to get past the Cloudflare Managed Challenge (once, ~10s)...");

            if (_sessions.TryGetValue(key, out var old))
            {
                old.Control.Dispose();
                old.HostForm.Dispose();
            }

            var userDataFolder = Path.Combine(
                Path.GetTempPath(), "BroadwayDirectFetch",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
            Directory.CreateDirectory(userDataFolder);

            var envOptions = new CoreWebView2EnvironmentOptions();
            string? proxyUser = null, proxyPassword = null;
            if (!string.IsNullOrEmpty(proxy))
            {
                var parsed = ProxyUri.Parse(proxy);
                envOptions.AdditionalBrowserArguments = $"--proxy-server={parsed.SwitchValue}";
                proxyUser = parsed.User;
                proxyPassword = parsed.Password;
            }

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions);

            // The window MUST be a real window (not headless) - Cloudflare still
            // detects and blocks headless, same reason headless=False is required
            // in the Python version (see client.py's docstring). Positioned
            // off-screen so it doesn't bother the user, but it's still a real,
            // rendering window.
            var form = new System.Windows.Forms.Form
            {
                ShowInTaskbar = false,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(-3000, -3000),
                Width = 1280,
                Height = 800,
            };
            var control = new WebView2 { Dock = System.Windows.Forms.DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();

            await control.EnsureCoreWebView2Async(environment);

            if (!string.IsNullOrEmpty(proxyUser))
            {
                control.CoreWebView2.BasicAuthenticationRequested += (_, e) =>
                {
                    e.Response.UserName = proxyUser;
                    e.Response.Password = proxyPassword ?? "";
                };
            }

            await NavigateAndWaitAsync(control, bootstrapUrl, timeout);
            await Task.Delay(8000); // same as page.wait_for_timeout(8000) on the Python side, waits for the JS/cookie challenge to finish

            var host = new Uri(bootstrapUrl).GetLeftPart(UriPartial.Authority);
            var cookies = await control.CoreWebView2.CookieManager.GetCookiesAsync(host);
            if (!cookies.Any(c => c.Name == "cf_clearance"))
            {
                Console.Error.WriteLine("  !! cf_clearance cookie not found - Cloudflare may still be blocking, subsequent requests will error");
            }

            var newGeneration = (_sessions.TryGetValue(key, out var prev) ? prev.Generation : 0) + 1;
            _sessions[key] = new ProxySession
            {
                Environment = environment,
                Control = control,
                HostForm = form,
                WarmedUrl = bootstrapUrl,
                Generation = newGeneration,
            };
            return true;
        });
    }

    private static async Task NavigateAndWaitAsync(WebView2 control, string url, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult();
        control.CoreWebView2.NavigationCompleted += OnCompleted;
        try
        {
            control.CoreWebView2.Navigate(url);
            using var cts = new CancellationTokenSource(timeout);
            using (cts.Token.Register(() => tcs.TrySetException(
                new TimeoutException($"Timeout opening the bootstrap page: {url}"))))
            {
                await tcs.Task;
            }
        }
        finally
        {
            control.CoreWebView2.NavigationCompleted -= OnCompleted;
        }
    }

    // SOH control character (\u0001) used as the delimiter between status and
    // body. Why not use nested JSON (e.g. {"status":200,"body":"..."}) anymore:
    // different WebView2 runtime versions seem inconsistent in how they encode
    // the returned value when the script returns a string (some get JSON-quoted
    // twice, some don't). SOH never realistically appears raw in an API's JSON
    // text response, so it's safe to use as a delimiter. IMPORTANT: the JS side
    // uses the "\u0001" escape (does NOT embed a raw SOH byte in the JS
    // source) - we previously hit a WebView2 bug returning "{}" (== what
    // JSON.stringify(new Error(...)) produces in V8, i.e. the script hit a
    // PARSE error before reaching try/catch), suspected to be caused by
    // ExecuteScriptAsync mis-marshaling a raw control byte when sending it to
    // the renderer process.
    private const char ResultSeparator = '\u0001';

    /// <summary>Runs fetch() directly in the already-loaded page (uses the tab's
    /// real network stack/cookies/TLS fingerprint, not an out-of-band request) -
    /// see README_DOTNET.md for why we don't use an API equivalent to
    /// Playwright's context.request.</summary>
    public Task<(int Status, string Body)> ExecuteFetchAsync(WebView2 control, string url)
    {
        return _host.RunOnUiThreadAsync(async () =>
        {
            var script = $$"""
                (async () => {
                    try {
                        const r = await fetch({{JsonSerializer.Serialize(url)}}, {
                            headers: { "Accept": "application/json, text/plain, */*" }
                        });
                        const body = await r.text();
                        return String(r.status) + "\u0001" + body;
                    } catch (e) {
                        return "0\u0001" + String(e);
                    }
                })()
                """;

            var raw = await control.CoreWebView2.ExecuteScriptAsync(script);

            // Some WebView2 versions wrap the returned string in an extra layer of
            // JSON quoting (e.g. "\"200{...}\""), others return it unwrapped - handle both.
            var trimmed = raw.Trim();
            string text;
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            {
                try { text = JsonSerializer.Deserialize<string>(trimmed) ?? trimmed; }
                catch (JsonException) { text = trimmed; }
            }
            else
            {
                text = trimmed;
            }

            var sep = text.IndexOf(ResultSeparator);
            if (sep < 0 || !int.TryParse(text[..sep], out var status))
            {
                var preview = raw.Length <= 300 ? raw : raw[..300] + "...(truncated)";
                var diag = await DiagnoseAsync(control, url);
                throw new InvalidOperationException(
                    $"Could not parse the ExecuteScriptAsync result (missing delimiter/invalid status). " +
                    $"Raw: {preview} | Diag: {diag}");
            }

            var body = text[(sep + 1)..];
            return (status, body);
        });
    }

    /// <summary>Only called when parsing the main result fails - runs 4 scripts
    /// from simplest to most complex to pinpoint exactly where ExecuteScriptAsync
    /// is failing (basic eval / awaiting a promise / fetch / reading the body),
    /// to avoid guessing blindly across many test rounds on Windows.</summary>
    private static async Task<string> DiagnoseAsync(WebView2 control, string url)
    {
        async Task<string> Probe(string label, string script)
        {
            try
            {
                var r = await control.CoreWebView2.ExecuteScriptAsync(script);
                return $"{label}={r}";
            }
            catch (Exception e)
            {
                return $"{label}=THREW({e.GetType().Name}: {e.Message})";
            }
        }

        var urlJson = JsonSerializer.Serialize(url);
        var p1 = await Probe("sync", "1+1");
        var p2 = await Probe("async-no-fetch", "(async () => { return 'ok'; })()");
        var p3 = await Probe("fetch-status-only",
            $$"""(async () => { try { const r = await fetch({{urlJson}}); return r.status; } catch (e) { return "ERR:" + String(e); } })()""");
        var p4 = await Probe("fetch-body-length",
            $$"""(async () => { try { const r = await fetch({{urlJson}}); const t = await r.text(); return t.length; } catch (e) { return "ERR:" + String(e); } })()""");

        return string.Join("; ", p1, p2, p3, p4);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
        {
            await _host.RunOnUiThreadAsync(() =>
            {
                s.Control.Dispose();
                s.HostForm.Dispose();
                return Task.CompletedTask;
            });
        }
        _sessions.Clear();
    }
}
