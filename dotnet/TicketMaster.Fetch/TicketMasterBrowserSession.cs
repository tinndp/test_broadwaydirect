using System.Text;
using BroadwayDirect.Fetch;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TicketMaster.Fetch;

/// <summary>Everything captured from the page's own first <c>quickpicks</c> request - the seed for
/// the HttpClient replay.</summary>
public sealed record FirstQuickPicksCapture(
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> RequestHeaders,
    string Body);

/// <summary>
/// One real (off-screen) WebView2 window through a proxy, used only to get past Ticketmaster's
/// Kasada bot wall and capture the page's own first <c>quickpicks</c> request (URL + headers +
/// cookies + offset-0 body). Everything after that is a plain HttpClient replay
/// (<see cref="TicketMasterFetchClient"/>) - Kasada wraps <c>window.fetch</c>/<c>XHR</c> so an
/// in-page replay does not work (verified live 2026-09).
///
/// Runs on <see cref="WebView2Host"/>'s STA thread (reused from BroadwayDirect.Fetch).
/// </summary>
public sealed class TicketMasterBrowserSession : IAsyncDisposable
{
    private readonly WebView2Host _host;
    private System.Windows.Forms.Form? _form;
    private WebView2? _webView;

    private readonly object _gate = new();
    private FirstQuickPicksCapture? _first;
    private int _quickpicksSeen;               // requests to a quickpicks URL we saw at all
    private int _quickpicksBodyOk;             // ...whose body we managed to read
    private readonly List<string> _diag = new();
    public string? BlockedReason { get; private set; }

    public TicketMasterBrowserSession(WebView2Host host) => _host = host;

    public async Task<FirstQuickPicksCapture> WarmAndCaptureFirstAsync(
        string eventUrl, string? proxySwitch, string? proxyUser, string? proxyPassword,
        TimeSpan navTimeout, int firstPageTimeoutSeconds)
    {
        await _host.RunOnUiThreadAsync(async () =>
        {
            var options = new CoreWebView2EnvironmentOptions();
            if (!string.IsNullOrWhiteSpace(proxySwitch))
                options.AdditionalBrowserArguments = "--proxy-server=" + proxySwitch;

            var userDataFolder = Path.Combine(Path.GetTempPath(), "TicketMasterFetchPoc",
                Guid.NewGuid().ToString("N"));
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);

            _form = new System.Windows.Forms.Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(-3000, -3000),
                Width = 1280,
                Height = 900,
            };
            _webView = new WebView2 { Dock = System.Windows.Forms.DockStyle.Fill };
            _form.Controls.Add(_webView);
            _form.Show();
            _form.Hide();

            await _webView.EnsureCoreWebView2Async(env);

            // A fresh WebView2 announces itself as Edge ("... Edg/xxx"); Kasada fingerprints the UA
            // hard. Drop the Edge token so it reads as plain Chrome (keeps the real engine version).
            try
            {
                var ua = _webView.CoreWebView2.Settings.UserAgent ?? "";
                var i = ua.IndexOf(" Edg/", StringComparison.Ordinal);
                if (i > 0) _webView.CoreWebView2.Settings.UserAgent = ua[..i];
            }
            catch { }

            if (!string.IsNullOrEmpty(proxyUser))
            {
                _webView.CoreWebView2.BasicAuthenticationRequested += (_, args) =>
                {
                    args.Response.UserName = proxyUser;
                    args.Response.Password = proxyPassword ?? "";
                };
            }

            _webView.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;

            var navDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNav(object? _, CoreWebView2NavigationCompletedEventArgs __) => navDone.TrySetResult(true);
            _webView.CoreWebView2.NavigationCompleted += OnNav;
            try
            {
                _webView.CoreWebView2.Navigate(eventUrl);
                using var cts = new CancellationTokenSource(navTimeout);
                using (cts.Token.Register(() => navDone.TrySetException(new TimeoutException("nav timeout: " + eventUrl))))
                    await navDone.Task;
            }
            finally { _webView.CoreWebView2.NavigationCompleted -= OnNav; }

            await Task.Delay(8000); // Kasada challenge

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var marker = await ReadPageMarkerAsync();
                if (marker == "BOTBLOCK") { BlockedReason = "Kasada bot wall (challenge / access denied page)."; break; }
                if (marker == "CAPTCHA") { BlockedReason = "Interactive bot challenge (press-and-hold / verify-human) - needs a residential proxy or manual solve."; break; }
                if (marker == "SOLDOUT") { BlockedReason = "Sold out event."; break; }
                if (marker == "NOTONLINE") { BlockedReason = "Tickets not currently available online."; break; }
                if (marker == "INTERRUPTION")
                {
                    var reload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnR(object? _, CoreWebView2NavigationCompletedEventArgs __) => reload.TrySetResult(true);
                    _webView.CoreWebView2.NavigationCompleted += OnR;
                    _webView.CoreWebView2.Navigate(eventUrl);
                    await Task.WhenAny(reload.Task, Task.Delay(navTimeout));
                    _webView.CoreWebView2.NavigationCompleted -= OnR;
                    await Task.Delay(6000);
                }
                await Task.Delay(500);
            }
        });

        if (BlockedReason != null)
            throw new InvalidOperationException("[BLOCKED] " + BlockedReason + "\n" + await BuildDiagnosticsAsync());

        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(firstPageTimeoutSeconds);
        while (DateTime.UtcNow < stop)
        {
            lock (_gate) { if (_first != null) return _first; }
            if (BlockedReason != null)
                throw new InvalidOperationException("[BLOCKED] " + BlockedReason + "\n" + await BuildDiagnosticsAsync());
            await _host.RunOnUiThreadAsync(TryNudgeAsync);
            await Task.Delay(1500);
        }

        throw new InvalidOperationException(
            $"No usable quickpicks response within {firstPageTimeoutSeconds}s.\n" + await BuildDiagnosticsAsync());
    }

    private async Task<string> BuildDiagnosticsAsync()
    {
        var page = "(webview gone)";
        try { page = await _host.RunOnUiThreadAsync(DumpPageAsync); } catch { }

        string net;
        int seen, ok;
        lock (_gate)
        {
            seen = _quickpicksSeen;
            ok = _quickpicksBodyOk;
            net = _diag.Count == 0
                ? "(NOTHING hit offeradapter/epsf/kasada - proxy not working or navigation failed?)"
                : string.Join("\n  ", _diag);
        }

        return
            $"page: {page}\n" +
            $"quickpicks requests seen: {seen}, body readable: {ok}\n" +
            (seen > 0 && ok == 0
                ? "  -> quickpicks fired but the body couldn't be read (GetContentAsync returned null - " +
                  "usually a 3xx/opaque response or the proxy stripped it).\n"
                : "") +
            $"network (offeradapter / epsf / kasada):\n  {net}";
    }

    private async Task<string> DumpPageAsync()
    {
        try
        {
            var raw = await _webView!.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({title: document.title, url: location.href, " +
                "seatMap: !!document.querySelector('[data-bdd*=\\\"quick-pick\\\"], .quick-picks__listings-scroll, [class*=\\\"seatmap\\\" i]'), " +
                "text: (document.body ? document.body.innerText : '').replace(/\\s+/g,' ').slice(0, 500)})");
            return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch (Exception e) { return "(could not read page: " + e.Message + ")"; }
    }

    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var req = e.Request;
            var uri = req.Uri ?? "";
            var url = uri.ToLowerInvariant();
            var status = e.Response?.StatusCode ?? 0;

            if (url.Contains("offeradapter.ticketmaster.com") || url.Contains("/epsf/")
                || url.Contains("quickpicks") || url.Contains("kasada") || url.Contains("/api/ismds/"))
            {
                lock (_gate)
                {
                    if (_diag.Count < 60)
                        _diag.Add($"[{status}] {req.Method} {Trunc(StripQuery(uri), 150)}");
                }
            }

            if ((url.Contains("/epsf/") || url.Contains("kasada")) && status is 429 or 403)
            {
                BlockedReason = $"Kasada blocked (HTTP {status}) on {StripQuery(uri)}";
                return;
            }

            if (!string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase)) return;
            if (!url.Contains("/quickpicks")) return;

            lock (_gate) { _quickpicksSeen++; }
            if (status is >= 300 and < 400) { lock (_gate) { _diag.Add($"[{status}] quickpicks redirect - not usable"); } return; }
            if (status is 403 or 429) { BlockedReason = $"quickpicks rejected (HTTP {status})"; return; }

            var reqUri = uri;
            var headers = req.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList();

            _ = ReadBodyAsync(e.Response).ContinueWith(t =>
            {
                var body = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                if (string.IsNullOrEmpty(body)) return;
                lock (_gate)
                {
                    _quickpicksBodyOk++;
                    _first ??= new FirstQuickPicksCapture(reqUri, headers, body);
                }
            });
        }
        catch { /* never break navigation on a capture error */ }
    }

    private static async Task<string?> ReadBodyAsync(CoreWebView2WebResourceResponseView? response)
    {
        try
        {
            if (response == null) return null;
            await using var stream = await response.GetContentAsync();
            if (stream == null) return null;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return null; }
    }

    private async Task<string> ReadPageMarkerAsync()
    {
        const string js = @"(function(){try{
  var t1=document.getElementById('t1');
  if(t1 && (t1.innerText||'').indexOf('Pardon the Interruption')!==-1) return 'INTERRUPTION';
  var b=((document.body?document.body.innerText:'')||'').toLowerCase();
  if(b.indexOf('press & hold')!==-1 || b.indexOf('press and hold')!==-1
     || b.indexOf('verify you are a human')!==-1 || b.indexOf('are you a human')!==-1) return 'CAPTCHA';
  if(b.indexOf('your browsing activity has been paused')!==-1 || b.indexOf('unusual activity')!==-1
     || b.indexOf('access denied')!==-1 || b.indexOf('pardon our interruption')!==-1) return 'BOTBLOCK';
  if(document.querySelector('iframe[src*=""kasada""],#kpsdk-challenge,[id*=""px-captcha""]')) return 'CAPTCHA';
  var so=document.querySelector('[data-bdd=""canceled-event-header-title""]');
  if(so){var s=(so.textContent||'').toLowerCase();
    if(s.indexOf('not currently available online')!==-1) return 'NOTONLINE';
    if(s.indexOf('sold out')!==-1) return 'SOLDOUT';}
  return 'NONE';
}catch(e){return 'NONE';}})();";
        try
        {
            var raw = await _webView!.CoreWebView2.ExecuteScriptAsync(js);
            return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "NONE";
        }
        catch { return "NONE"; }
    }

    private async Task TryNudgeAsync()
    {
        const string js = @"(function(){try{
  var b=document.querySelector('button[data-bdd=""accept-modal-accept-button""]'); if(b) b.click();
  var m=document.getElementsByClassName('modal-dialog__button button'); if(m&&m.length) m[0].click();
  // click a 'Find Tickets' / 'See Tickets' style CTA if the listings panel hasn't mounted
  var cta=[].slice.call(document.querySelectorAll('a,button')).filter(function(x){
    var t=(x.textContent||'').trim().toLowerCase();
    return t==='find tickets'||t==='see tickets'||t==='buy tickets'||t==='get tickets';
  })[0];
  if(cta) cta.click();
  var el=document.getElementsByClassName('quick-picks__listings-scroll')[0]||document.scrollingElement;
  if(el) el.scrollBy({top:600,left:0});
}catch(e){}})();";
        try { if (_webView != null) await _webView.CoreWebView2.ExecuteScriptAsync(js); } catch { }
    }

    private static string StripQuery(string url)
    {
        var i = url.IndexOf('?');
        return i < 0 ? url : url[..i];
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.RunOnUiThreadAsync(() =>
            {
                try { if (_webView != null) _webView.CoreWebView2.WebResourceResponseReceived -= OnWebResourceResponseReceived; } catch { }
                try { _webView?.Dispose(); } catch { }
                try { _form?.Close(); _form?.Dispose(); } catch { }
                return Task.CompletedTask;
            });
        }
        catch { }
    }
}
