using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BroadwayDirect.Core.Proxy;
using BroadwayDirect.Fetch;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace StubHub.Fetch;

/// <summary>
/// One real (non-headless) WebView2 window for a single proxy value, used to get
/// past <b>DataDome</b> and then run same-origin <c>fetch()</c> calls in the page
/// context. The .NET equivalent of the patchright browser +
/// <c>context.new_page()</c> handling in python/stubhub/client.py.
///
/// Differences forced by WebView2:
///   - Proxy is set at the environment level, so it's fixed per instance (one
///     <see cref="DataDomeBrowser"/> per proxy - StubHub.Api pools clients by
///     proxy the same way python/stubhub/api.py does).
///   - "Fresh context per event" (Python opens a new context to drop the
///     per-event <c>FilterSortSessionId</c> and the stale <c>datadome</c>
///     cookie) becomes <see cref="OpenFreshAsync"/> = clear ALL cookies, then
///     re-navigate and re-clear the DataDome challenge.
///   - <c>page.evaluate(fn, arg)</c> becomes <see cref="EvaluateJsonAsync"/>,
///     which posts the result back via <c>postMessage</c> because
///     ExecuteScriptAsync doesn't reliably await a returned Promise (see the
///     NOTE in BroadwayDirect.Fetch/ProxyEnvironmentPool.cs).
/// </summary>
public sealed class DataDomeBrowser : IAsyncDisposable
{
    private const char ResultSeparator = '\u0001';
    private static readonly string[] ChallengeMarkers =
        { "Please enable JS and disable", "geo.captcha-delivery.com" };

    private readonly WebView2Host _host;
    private readonly string _proxy;
    private readonly StubHubFetchOptions _opt;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<string, TaskCompletionSource<string>> _pending = new();
    private readonly object _pendingGate = new();

    private CoreWebView2Environment? _env;
    private WebView2? _control;
    private System.Windows.Forms.Form? _form;

    public DataDomeBrowser(WebView2Host host, string proxy, StubHubFetchOptions opt)
    {
        _host = host;
        _proxy = proxy ?? "";
        _opt = opt;
    }

    private TimeSpan Timeout => TimeSpan.FromSeconds(_opt.TimeoutSeconds);

    // -- lifecycle --------------------------------------------------------
    private async Task EnsureControlAsync()
    {
        if (_control != null) return;
        await _host.RunOnUiThreadAsync(async () =>
        {
            var userDataFolder = Path.Combine(
                Path.GetTempPath(), "StubHubFetch",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    string.IsNullOrEmpty(_proxy) ? "__direct__" : _proxy))));
            Directory.CreateDirectory(userDataFolder);

            var envOptions = new CoreWebView2EnvironmentOptions();
            string? proxyUser = null, proxyPassword = null;
            if (!string.IsNullOrEmpty(_proxy))
            {
                var parsed = ProxyUri.Parse(_proxy);
                envOptions.AdditionalBrowserArguments = $"--proxy-server={parsed.SwitchValue}";
                proxyUser = parsed.User;
                proxyPassword = parsed.Password;
            }

            _env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions);

            // A real, rendering window (off-screen) - DataDome blocks headless
            // Chromium, same reason headless=False is required on the Python side.
            _form = new System.Windows.Forms.Form
            {
                ShowInTaskbar = false,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(-3000, -3000),
                Width = 1280,
                Height = 900,
            };
            _control = new WebView2 { Dock = System.Windows.Forms.DockStyle.Fill };
            _form.Controls.Add(_control);
            _form.Show();

            await _control.EnsureCoreWebView2Async(_env);
            _control.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            if (!string.IsNullOrEmpty(proxyUser))
            {
                _control.CoreWebView2.BasicAuthenticationRequested += (_, e) =>
                {
                    e.Response.UserName = proxyUser;
                    e.Response.Password = proxyPassword ?? "";
                };
            }
            return true;
        });
    }

    /// <summary>Clear every cookie (drops the per-event FilterSortSessionId and
    /// the stale datadome cookie), navigate to <paramref name="url"/>, and wait
    /// for DataDome's JS challenge to clear. Throws if it never clears - that's a
    /// DataDome-flagged IP, not something more waiting fixes.</summary>
    public async Task OpenFreshAsync(string url)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureControlAsync();
            await _host.RunOnUiThreadAsync(async () =>
            {
                _control!.CoreWebView2.CookieManager.DeleteAllCookies();

                var last = "";
                for (var attempt = 1; attempt <= _opt.NavRetries; attempt++)
                {
                    try
                    {
                        await NavigateAsync(url, Timeout);
                    }
                    catch (Exception e)
                    {
                        last = e.Message;
                        await Task.Delay(2000 * attempt);
                        continue;
                    }

                    var polls = (int)_opt.ChallengeWaitSeconds + 4 * attempt;
                    for (var p = 0; p < polls; p++)
                    {
                        await Task.Delay(1000);
                        string head;
                        try
                        {
                            head = await ReadStringAsync(
                                "document.documentElement.outerHTML.slice(0,6000)");
                        }
                        catch
                        {
                            head = "";   // mid-navigation (challenge reloading) - keep polling
                        }

                        if (head.Length > 0 && !ChallengeMarkers.Any(head.Contains))
                        {
                            // DataDome reloads the page when it clears; let it settle,
                            // then confirm the page is live before handing it back.
                            await Task.Delay(2500);
                            try
                            {
                                await _control.CoreWebView2.ExecuteScriptAsync("1");
                                return true;
                            }
                            catch (Exception e)
                            {
                                last = $"page not stable after challenge: {e.Message}";
                                break;
                            }
                        }
                    }

                    if (last.Length == 0) last = "DataDome challenge page did not clear";
                    await Task.Delay(3000 * attempt);
                }

                throw new InvalidOperationException(
                    $"stubhub: could not load {url} ({last}). The IP is likely " +
                    $"DataDome-flagged - retry later or pass a rotating proxy.");
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs <paramref name="asyncFnExpr"/> (one <c>async (arg) =&gt; {...}</c>
    /// expression) with <paramref name="arg"/> injected as JSON, and returns its
    /// JSON result. Equivalent to patchright's <c>page.evaluate(fn, arg)</c>.</summary>
    public Task<JsonElement> EvaluateJsonAsync(string asyncFnExpr, object arg, TimeSpan? timeout = null)
    {
        var eff = timeout ?? Timeout;
        return _host.RunOnUiThreadAsync(async () =>
        {
            var requestId = Guid.NewGuid().ToString("N");
            var idJson = JsonSerializer.Serialize(requestId);
            var argJson = JsonSerializer.Serialize(arg);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingGate) { _pending[requestId] = tcs; }

            try
            {
                var script = $$"""
                    (async () => {
                      try {
                        const __fn = ({{asyncFnExpr}});
                        const __v = await __fn({{argJson}});
                        window.chrome.webview.postMessage({{idJson}} + "\u0001" + JSON.stringify(__v === undefined ? null : __v));
                      } catch (e) {
                        window.chrome.webview.postMessage({{idJson}} + "\u0001" + JSON.stringify({ __evalError: String((e && e.stack) || e) }));
                      }
                    })();
                    """;

                await _control!.CoreWebView2.ExecuteScriptAsync(script);

                using var cts = new CancellationTokenSource(eff);
                await using var reg = cts.Token.Register(() => tcs.TrySetException(
                    new TimeoutException($"stubhub: timed out waiting for the in-page evaluate result")));

                var payload = await tcs.Task;
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("__evalError", out var err))
                {
                    throw new InvalidOperationException($"stubhub: in-page evaluate threw: {err.GetString()}");
                }
                return root.Clone();
            }
            finally
            {
                lock (_pendingGate) { _pending.Remove(requestId); }
            }
        });
    }

    // -- internals ------------------------------------------------------
    private async Task NavigateAsync(string url, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult();
        _control!.CoreWebView2.NavigationCompleted += OnCompleted;
        try
        {
            _control.CoreWebView2.Navigate(url);
            using var cts = new CancellationTokenSource(timeout);
            await using (cts.Token.Register(() => tcs.TrySetException(
                             new TimeoutException($"stubhub: timeout navigating to {url}"))))
            {
                await tcs.Task;
            }
        }
        finally
        {
            _control.CoreWebView2.NavigationCompleted -= OnCompleted;
        }
    }

    private async Task<string> ReadStringAsync(string expression)
    {
        var json = await _control!.CoreWebView2.ExecuteScriptAsync(expression);
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = e.TryGetWebMessageAsString();
        if (msg == null) return;
        var sep = msg.IndexOf(ResultSeparator);
        if (sep < 0) return;
        var requestId = msg[..sep];
        var payload = msg[(sep + 1)..];

        TaskCompletionSource<string>? tcs;
        lock (_pendingGate)
        {
            if (!_pending.Remove(requestId, out tcs)) return;
        }
        tcs.TrySetResult(payload);
    }

    public async ValueTask DisposeAsync()
    {
        if (_control == null) return;
        await _host.RunOnUiThreadAsync(() =>
        {
            _control.Dispose();
            _form?.Dispose();
            return Task.CompletedTask;
        });
        _control = null;
        _form = null;
        _env = null;
    }
}
