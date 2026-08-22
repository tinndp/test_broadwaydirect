namespace BroadwayDirect.Fetch;

/// <summary>
/// WebView2 needs a Windows message loop (STA thread) to function - a normal
/// ASP.NET Core/console app runs on the thread pool, with no message loop.
/// This class creates its own background STA thread running a hidden Form
/// (not shown in the taskbar) to "host" the message loop, and provides
/// RunOnUiThreadAsync to marshal every CoreWebView2 call onto that exact
/// thread.
///
/// Why a hidden Form instead of writing a native message loop by hand:
/// simple, stable, and WindowsFormsSynchronizationContext is automatically
/// installed when Application.Run runs, allowing continuations (await) to
/// post back onto the correct UI thread.
/// </summary>
public sealed class WebView2Host : IDisposable
{
    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private SynchronizationContext? _uiContext;
    private System.Windows.Forms.Form? _pumpForm;

    public WebView2Host()
    {
        _uiThread = new Thread(RunMessageLoop) { IsBackground = true };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Name = "WebView2-UI-Thread";
        _uiThread.Start();
        _ready.Wait();
    }

    private void RunMessageLoop()
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        _pumpForm = new System.Windows.Forms.Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            Width = 1,
            Height = 1,
        };
        _pumpForm.Load += (_, _) =>
        {
            _uiContext = SynchronizationContext.Current;
            _ready.Set();
        };
        // Don't call Form.Show() here - this form only exists to "pump" the
        // message loop; the actual WebView2 host forms (with a real window
        // to get past Cloudflare) are created separately in
        // ProxyEnvironmentPool.
        _pumpForm.Load += (_, _) => _pumpForm.Hide();
        System.Windows.Forms.Application.Run(_pumpForm);
    }

    /// <summary>Runs fn on WebView2's own UI thread and returns the result.
    /// Every CoreWebView2 call (creating an environment, navigating,
    /// executing a script...) MUST go through this method.</summary>
    public Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> fn)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext!.Post(async _ =>
        {
            try
            {
                var result = await fn();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    public Task RunOnUiThreadAsync(Func<Task> fn) =>
        RunOnUiThreadAsync(async () => { await fn(); return true; });

    public void Dispose()
    {
        if (_uiContext == null) return;
        _uiContext.Post(_ => System.Windows.Forms.Application.ExitThread(), null);
        _uiThread.Join(TimeSpan.FromSeconds(5));
    }
}
