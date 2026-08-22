using System.Collections.Concurrent;
using System.Text.Json;
using BroadwayDirect.Core.Models;

namespace BroadwayDirect.Fetch;

/// <summary>
/// Calls the public JSON API of Tixtrack/Broadway Direct, getting past the
/// Cloudflare Managed Challenge via WebView2. Ported from
/// python/broadwaydirect/client.py (the Python version using patchright) - see
/// README_DOTNET.md for the design differences (in-page fetch() instead of
/// context.request, proxy pool per request).
///
/// Retry rule KEPT IDENTICAL to the Python version: doesn't distinguish
/// error type (403/500/timeout are all handled the same way), total
/// attempts = Retries (default 3), each failure reopens the session (with a
/// lock to avoid duplicate reopens) then sleeps Sleep*attempt*2, and raises
/// the last error once attempts are exhausted.
/// </summary>
public sealed class BroadwayDirectFetchClient : IAsyncDisposable
{
    private readonly FetchOptions _options;
    private readonly WebView2Host _host;
    private readonly ProxyEnvironmentPool _pool;

    /// <summary>onRaw(kind, key, raw): optional, called with EVERY raw JSON
    /// received from the API (unmodified) - used to mirror to Mongo, same as
    /// the Python version.</summary>
    public Action<string, IReadOnlyDictionary<string, object>, JsonElement>? OnRaw { get; set; }

    public BroadwayDirectFetchClient(FetchOptions? options = null)
    {
        _options = options ?? new FetchOptions();
        _host = new WebView2Host();
        _pool = new ProxyEnvironmentPool(_host);
    }

    private static bool LessOrEqual((int Year, int Month) a, (int Year, int Month) b) =>
        a.Year < b.Year || (a.Year == b.Year && a.Month <= b.Month);

    private async Task<JsonElement> GetJsonAsync(string apiUrl, string bootstrapUrl, string proxy, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        var (control, generation) = await _pool.EnsureWarmSessionAsync(proxy, bootstrapUrl, timeout);

        Exception? lastErr = null;
        for (var attempt = 1; attempt <= _options.Retries; attempt++)
        {
            var genBefore = generation;
            try
            {
                var (status, body) = await _pool.ExecuteFetchAsync(control, apiUrl, timeout);
                if (status == 403)
                    throw new InvalidOperationException($"Blocked by Cloudflare (HTTP {status})");
                if (status >= 400 || status == 0)
                    throw new InvalidOperationException($"HTTP {status}");

                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
            catch (Exception e)
            {
                lastErr = e;
                if (attempt < _options.Retries)
                {
                    await _pool.ReopenIfStillCurrentAsync(proxy, bootstrapUrl, genBefore, timeout);
                    (control, generation) = await _pool.EnsureWarmSessionAsync(proxy, bootstrapUrl, timeout);
                    await Task.Delay(TimeSpan.FromSeconds(_options.Sleep * attempt * 2), ct);
                }
            }
        }

        throw new InvalidOperationException($"Error calling {apiUrl}: {lastErr?.Message}", lastErr);
    }

    /// <summary>bootstrapUrl: the series ticket page (e.g.
    /// https://tickets.broadwaydirect.com/tickets/series/{seriesId}) - MUST be
    /// opened first to get past Cloudflare; the API domain is inferred from
    /// this url.</summary>
    public async Task<JsonElement> GetEventInventoryAsync(
        string eventId, string bootstrapUrl, string proxy = "", CancellationToken ct = default)
    {
        var domain = new Uri(bootstrapUrl).Host;
        var apiUrl = $"https://{domain}/api/consumer/eventinventory/{eventId}";
        var data = await GetJsonAsync(apiUrl, bootstrapUrl, proxy, ct);
        OnRaw?.Invoke("eventinventory", new Dictionary<string, object> { ["event_id"] = eventId }, data);
        return data;
    }

    public async Task<List<JsonElement>> GetEventsByMonthAsync(
        string seriesId, string bootstrapUrl, int year, int month,
        string promoCode = "", string proxy = "", CancellationToken ct = default)
    {
        var domain = new Uri(bootstrapUrl).Host;
        var query = $"requestedTime={Uri.EscapeDataString($"{year}/{month}/01")}" +
                    $"&salesChannel=Web&promoCode={Uri.EscapeDataString(promoCode)}";
        var apiUrl = $"https://{domain}/api/consumer/events/getbymonth/{seriesId}?{query}";
        var data = await GetJsonAsync(apiUrl, bootstrapUrl, proxy, ct);

        OnRaw?.Invoke("events_by_month", new Dictionary<string, object>
        {
            ["series_id"] = seriesId, ["year"] = year, ["month"] = month,
        }, data);

        if (data.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
            return events.EnumerateArray().Select(e => e.Clone()).ToList();
        return new List<JsonElement>();
    }

    /// <summary>Yields deduplicated Events, from start_ym to end_ym (year, month tuples).</summary>
    public async Task<List<Event>> IterEventsAsync(
        string seriesId, string bootstrapUrl, (int Year, int Month) startYm, (int Year, int Month) endYm,
        string promoCode = "", string proxy = "", CancellationToken ct = default)
    {
        var seen = new Dictionary<long, Event>();
        var (y, m) = startYm;
        while (LessOrEqual((y, m), endYm))
        {
            Console.Error.WriteLine($"  [month {y}-{m:D2}] fetching event list...");
            var events = await GetEventsByMonthAsync(seriesId, bootstrapUrl, y, m, promoCode, proxy, ct);
            foreach (var e in events)
            {
                var id = e.GetProperty("id").GetInt64();
                seen[id] = new Event
                {
                    Id = id,
                    LocalDate = GetStr(e, "localDate"),
                    AvailabilityColor = GetStr(e, "availabilityColor"),
                    Name = GetStr(e, "name"),
                    SeriesId = seriesId,
                };
            }
            await Task.Delay(TimeSpan.FromSeconds(_options.Sleep), ct);
            m++;
            if (m > 12) { m = 1; y++; }
        }
        return seen.Values.OrderBy(e => e.LocalDate).ToList();
    }

    private static string GetStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

    /// <summary>events: list of Event. Returns {event_id: raw_inventory_json}. Runs
    /// concurrently (bounded by Concurrency). Errors on a single performance are
    /// skipped (won't fail the whole crawl) - same behavior as get_all_inventory
    /// on the Python side.</summary>
    public async Task<Dictionary<long, JsonElement>> GetAllInventoryAsync(
        IReadOnlyList<Event> events, string bootstrapUrl, string proxy = "", CancellationToken ct = default)
    {
        var outMap = new ConcurrentDictionary<long, JsonElement>();
        var sem = new SemaphoreSlim(Math.Max(1, _options.Concurrency));
        var done = 0;
        var n = events.Count;

        async Task FetchOne(Event e)
        {
            await sem.WaitAsync(ct);
            try
            {
                try
                {
                    var inv = await GetEventInventoryAsync(e.Id.ToString(), bootstrapUrl, proxy, ct);
                    outMap[e.Id] = inv;
                }
                catch (Exception err)
                {
                    Console.Error.WriteLine($"    !! skipping event {e.Id}: {err.Message}");
                }
                var d = Interlocked.Increment(ref done);
                Console.Error.WriteLine($"  [{d}/{n}] eventinventory {e.Id} ({e.LocalDate}) done");
                await Task.Delay(TimeSpan.FromSeconds(_options.Sleep), ct);
            }
            finally
            {
                sem.Release();
            }
        }

        await Task.WhenAll(events.Select(FetchOne));
        return new Dictionary<long, JsonElement>(outMap);
    }

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync();
        _host.Dispose();
    }
}
