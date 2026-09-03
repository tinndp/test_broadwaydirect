using System.Text.Json;
using System.Text.Json.Nodes;
using BroadwayDirect.Fetch;
using StubHub.Core.Json;

namespace StubHub.Fetch;

/// <summary>
/// Fetches StubHub event-listing data through a real browser (WebView2), getting
/// past <b>DataDome</b>. 1:1 port of python/stubhub/client.py's
/// <c>StubHubClient</c> - see that file's module docstring for the full strategy.
///
/// Strategy: open a fresh (cookie-cleared) page per event, bootstrap off the SSR
/// HTML, then
///   1. PRIMARY: one <c>POST /event/&lt;id&gt;/grid</c> with a large PageSize -
///      ideally the whole event in ~1 request, which keeps one IP under
///      DataDome's rate limit;
///   2. FALLBACK / gap-fill: sweep one SSR request per stadium section, batched
///      with adaptive backoff + fresh-context retry passes for 429'd sections.
///
/// One <see cref="WebView2Host"/> (STA pump) + one <see cref="DataDomeBrowser"/>
/// per client; StubHub.Api pools one client per proxy, exactly like
/// python/stubhub/api.py's <c>_get_client</c>.
/// </summary>
public sealed class StubHubClient : IAsyncDisposable
{
    private readonly StubHubFetchOptions _opt;
    private readonly WebView2Host _host;
    private readonly DataDomeBrowser _browser;

    public StubHubClient(string proxy = "", StubHubFetchOptions? options = null)
    {
        _opt = options ?? new StubHubFetchOptions();
        _host = new WebView2Host();
        _browser = new DataDomeBrowser(_host, proxy ?? "", _opt);
    }

    // -- per-event inventory --------------------------------------------
    /// <summary>Return the assembled raw-inventory JSON for one event (see
    /// StubHubAdapter for its shape). <paramref name="eventUrl"/> must be a
    /// <c>.../event/&lt;id&gt;/</c> page URL.</summary>
    public async Task<JsonElement> FetchEventInventoryAsync(
        string eventUrl, StubHubFetchOverrides? overrides = null, CancellationToken ct = default)
    {
        var opt = Apply(overrides);
        var basePath = new Uri(eventUrl).AbsolutePath;

        // open + bootstrap as one retriable unit: the DataDome page sometimes
        // tears the context down right as the first evaluate runs.
        JsonElement boot = default;
        for (var attempt = 1; attempt <= _opt.Retries; attempt++)
        {
            await _browser.OpenFreshAsync(eventUrl);
            try
            {
                boot = await _browser.EvaluateJsonAsync(StubHubScripts.Bootstrap, basePath);
                break;
            }
            catch (Exception e)
            {
                if (attempt == _opt.Retries)
                    throw new InvalidOperationException(
                        $"stubhub: bootstrap evaluate failed for {eventUrl} after {attempt} tries ({e.Message})");
                Console.Error.WriteLine($"  [stubhub] bootstrap retry {attempt} ({e.Message})");
                await Task.Delay(TimeSpan.FromSeconds(2.0 * attempt), ct);
            }
        }

        if (boot.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"stubhub: bootstrap failed for {eventUrl} (empty)");
        var bootErr = Str(boot, "error");
        if (bootErr.Length > 0)
            throw new InvalidOperationException($"stubhub: bootstrap failed for {eventUrl} ({bootErr})");

        var popupKeys = StrArray(boot, "sectionPopupKeys");
        if (popupKeys.Count == 0)
            throw new InvalidOperationException($"stubhub: no sections found for {eventUrl}");

        var totalCount = IntOrNull(boot, "totalCount");
        var sectionIds = UniqueSectionIds(popupKeys);
        var itemsById = new Dictionary<string, JsonElement>();
        string? method = null;
        var failed = new List<string>();

        // --- primary: one (or few) POST(s) to the grid endpoint -------------
        var sessionId = Str(boot, "filterSortSessionId");
        if (opt.UseGridPost && sessionId.Length > 0)
        {
            var got = await FetchViaGridPostAsync(
                basePath, Str(boot, "eventId"), sessionId, IntOrNull(boot, "categoryId"),
                totalCount, opt, ct);
            if (got != null)
            {
                foreach (var it in got)
                {
                    var k = ItemKey(it);
                    if (k != null) itemsById[k] = it;
                }
                method = "grid-post";
            }
        }

        // --- fallback / gap-fill: section sweep ----------------------------
        var needSweep = method == null ||
                        (totalCount is { } tc && itemsById.Count < tc * 0.97);
        if (needSweep)
        {
            if (method != null)
                Console.Error.WriteLine(
                    $"  [stubhub] grid-post got {itemsById.Count}/{totalCount}" +
                    " - filling the gap with a section sweep");

            failed = await SweepSectionsAsync(
                basePath, sectionIds, itemsById, opt.Concurrency, opt.BatchDelaySeconds, ct);

            // Retry passes for 429'd sections. Each reopens fresh (new datadome
            // cookie, and - if the proxy rotates - a new exit IP, which is what
            // actually lifts a per-IP limit).
            for (var rp = 1; rp <= _opt.RetryPasses; rp++)
            {
                if (failed.Count == 0) break;
                Console.Error.WriteLine(
                    $"  [stubhub] retry pass {rp}: {failed.Count} section(s), fresh context");
                await Task.Delay(TimeSpan.FromSeconds(_opt.RetryPassDelaySeconds), ct);
                try
                {
                    await _browser.OpenFreshAsync(eventUrl);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"  [stubhub] retry pass {rp} could not reopen ({e.Message})");
                    break;
                }
                failed = await SweepSectionsAsync(basePath, failed, itemsById, 1, 2.0, ct);
            }
            method = method == null ? "section-sweep" : "grid-post+sweep";
        }

        var items = itemsById.Values.ToList();
        var covPct = totalCount is { } t && t > 0
            ? Math.Round(items.Count / (double)t * 100, 1)
            : (double?)null;
        Console.Error.WriteLine(
            $"  [stubhub] done via {method}: {items.Count} listings / totalCount {totalCount} " +
            $"({covPct}%), {failed.Count} section(s) unrecovered");

        return AssembleRaw(boot, eventUrl, totalCount, items, method!, sectionIds.Count, failed.Count, covPct);
    }

    private async Task<List<JsonElement>?> FetchViaGridPostAsync(
        string basePath, string eid, string sessionId, int? categoryId, int? totalCount,
        StubHubFetchOptions opt, CancellationToken ct)
    {
        var collected = new Dictionary<string, JsonElement>();
        var pageSize = _opt.GridPageSize;
        int? firstCount = null;
        var maxPages = 80;

        for (var cp = 1; cp <= maxPages; cp++)
        {
            JsonElement res;
            try
            {
                res = await _browser.EvaluateJsonAsync(StubHubScripts.GridPost, new
                {
                    basePath,
                    eid,
                    sessionId,
                    categoryId = categoryId ?? 0,
                    pageSize,
                    currentPage = cp,
                });
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"  [stubhub] grid-post evaluate failed ({e.Message})");
                break;
            }

            if (res.ValueKind != JsonValueKind.Object || IntOrNull(res, "status") != 200)
            {
                if (cp == 1) return null;      // endpoint unusable -> caller sweeps
                break;
            }

            var items = ArrayItems(res, "items");
            if (items.Count == 0) break;
            foreach (var it in items)
            {
                var k = ItemKey(it);
                if (k != null) collected[k] = it;
            }

            var srvTotal = IntOrNull(res, "totalCount") ?? totalCount;
            if (firstCount == null)
            {
                firstCount = items.Count;
                // server capped the page small and there's clearly more to get
                if (firstCount <= 12 && srvTotal is { } st && st > 40)
                {
                    Console.Error.WriteLine(
                        $"  [stubhub] grid-post PageSize capped at ~{firstCount}" +
                        $" - not worth paging {srvTotal} that way, sweeping");
                    return null;
                }
                if (srvTotal is { } st2)
                    maxPages = st2 / Math.Max(firstCount.Value, 1) + 3;
            }

            Console.Error.WriteLine(
                $"  [stubhub] grid-post page {cp}: +{items.Count} -> {collected.Count}/{srvTotal}");
            if (srvTotal is { } s3 && collected.Count >= s3) break;
            if (items.Count < pageSize) break;      // short page = last page
            await Task.Delay(TimeSpan.FromSeconds(opt.BatchDelaySeconds), ct);
        }

        return collected.Values.ToList();
    }

    private async Task<List<string>> SweepSectionsAsync(
        string basePath, List<string> sectionIds, Dictionary<string, JsonElement> itemsById,
        int concurrency, double delay, CancellationToken ct)
    {
        var failedAll = new List<string>();
        var n = sectionIds.Count;
        var curDelay = delay;

        for (var start = 0; start < n; start += concurrency)
        {
            var batch = sectionIds.Skip(start).Take(concurrency).ToList();
            JsonElement res;
            try
            {
                res = await _browser.EvaluateJsonAsync(
                    StubHubScripts.SectionBatch, new { ids = batch, basePath });
            }
            catch (Exception e)
            {
                // page/context died mid-sweep - record this batch and keep going
                failedAll.AddRange(batch);
                Console.Error.WriteLine(
                    $"  [stubhub] batch evaluate failed ({e.Message}); skipping {batch.Count} section(s)");
                await Task.Delay(TimeSpan.FromSeconds(curDelay), ct);
                continue;
            }

            foreach (var it in ArrayItems(res, "items"))
            {
                var k = ItemKey(it);
                if (k != null) itemsById[k] = it;
            }

            var batchFailed = StrArray(res, "failed");
            failedAll.AddRange(batchFailed);

            // adaptive backoff: 429s in a batch -> slow the rest of the sweep
            // (up to 6s); a clean batch relaxes it back toward the base delay
            curDelay = batchFailed.Count > 0
                ? Math.Min(curDelay * 1.6 + 0.5, 6.0)
                : Math.Max(delay, curDelay * 0.8);

            var done = Math.Min(start + concurrency, n);
            Console.Error.WriteLine(
                $"  [stubhub] sections {done}/{n} -> {itemsById.Count} listings" +
                (batchFailed.Count > 0 ? $" (backoff {curDelay:F1}s)" : ""));

            if (start + concurrency < n)
                await Task.Delay(TimeSpan.FromSeconds(curDelay), ct);
        }

        return failedAll;
    }

    // -- discovery ----------------------------------------------------------
    /// <summary>Turn a <c>/category/</c>, <c>/grouping/</c>, <c>/venue/</c> or
    /// <c>/performer/</c> URL into its event list. <paramref name="scope"/>="all"
    /// walks restGrid (every location); "home" walks primaryGrid.</summary>
    public async Task<JsonElement> DiscoverAsync(
        string catalogUrl, string scope = "all", int maxPages = 50, CancellationToken ct = default)
    {
        var grid = scope != "home" ? "restGrid" : "primaryGrid";
        var param = grid == "restGrid" ? "restPage" : "primaryPage";
        var path = new Uri(catalogUrl).AbsolutePath;

        for (var attempt = 1; attempt <= _opt.Retries; attempt++)
        {
            await _browser.OpenFreshAsync(catalogUrl);
            try
            {
                await _browser.EvaluateJsonAsync("async () => 1", new { });   // confirm live
                break;
            }
            catch (Exception e)
            {
                if (attempt == _opt.Retries)
                    throw new InvalidOperationException(
                        $"stubhub: discover page unstable for {catalogUrl} ({e.Message})");
                await Task.Delay(TimeSpan.FromSeconds(2.0 * attempt), ct);
            }
        }

        var events = new Dictionary<string, JsonNode?>();
        int? totalCount = null;
        for (var nn = 1; nn <= maxPages; nn++)
        {
            JsonElement res;
            try
            {
                res = await _browser.EvaluateJsonAsync(
                    StubHubScripts.DiscoverPage, new { path, param, n = nn, grid });
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"  [stubhub] discover page {nn} evaluate failed ({e.Message})");
                break;
            }

            if (res.ValueKind != JsonValueKind.Object || IntOrNull(res, "status") != 200) break;
            // server clamps an out-of-range page back to page 1 -> stop
            var pageIndex = IntOrNull(res, "pageIndex");
            if (pageIndex is { } pi && pi != nn - 1) break;

            var pageItems = ArrayItems(res, "items");
            if (pageItems.Count == 0) break;
            totalCount = IntOrNull(res, "totalCount") ?? totalCount;

            foreach (var ev in pageItems)
            {
                var eid = ev.ValueKind == JsonValueKind.Object &&
                          ev.TryGetProperty("eventId", out var e2) && e2.ValueKind != JsonValueKind.Null
                    ? e2.ToString()
                    : null;
                if (eid != null) events[eid] = SlimEvent(ev);
            }
            if (totalCount is { } t && events.Count >= t) break;
        }

        var outObj = new JsonObject
        {
            ["sourceUrl"] = catalogUrl,
            ["scope"] = scope,
            ["totalCount"] = totalCount,
            ["collected"] = events.Count,
            ["events"] = new JsonArray(events.Values.Select(v => v?.DeepClone()).ToArray()),
        };
        return JsonSerializer.SerializeToElement(outObj);
    }

    // -- helpers ------------------------------------------------------------
    private StubHubFetchOptions Apply(StubHubFetchOverrides? ov)
    {
        if (ov == null) return _opt;
        return new StubHubFetchOptions
        {
            Concurrency = ov.Concurrency is { } c ? Math.Max(1, c) : _opt.Concurrency,
            BatchDelaySeconds = ov.BatchDelaySeconds ?? _opt.BatchDelaySeconds,
            UseGridPost = ov.UseGridPost ?? _opt.UseGridPost,
            Retries = _opt.Retries,
            NavRetries = _opt.NavRetries,
            TimeoutSeconds = _opt.TimeoutSeconds,
            ChallengeWaitSeconds = _opt.ChallengeWaitSeconds,
            RetryPasses = _opt.RetryPasses,
            RetryPassDelaySeconds = _opt.RetryPassDelaySeconds,
            GridPageSize = _opt.GridPageSize,
        };
    }

    private static readonly string[] SlimKeys =
    {
        "eventId", "name", "url", "formattedDate", "formattedTime", "venueId", "venueName",
        "formattedVenueLocation", "venueCity", "venueStateProvince", "countryCode",
        "hasActiveListings", "eventAvailabilityState", "allowPublicPurchase", "isParkingEvent",
    };

    private static JsonNode? SlimEvent(JsonElement ev)
    {
        var o = new JsonObject();
        foreach (var k in SlimKeys)
            if (ev.TryGetProperty(k, out var v))
                o[k] = JsonNode.Parse(v.GetRawText());
        return o;
    }

    private static List<string> UniqueSectionIds(IEnumerable<string> popupKeys)
    {
        var seen = new HashSet<string>();
        var outl = new List<string>();
        foreach (var k in popupKeys)
        {
            var parts = k.Split('_');
            var sid = parts[^1];
            if (sid.Length > 0 && seen.Add(sid)) outl.Add(sid);
        }
        return outl;
    }

    private JsonElement AssembleRaw(
        JsonElement boot, string eventUrl, int? totalCount, List<JsonElement> items,
        string method, int sections, int sectionsFailed, double? covPct)
    {
        JsonNode? Copy(string prop) =>
            boot.TryGetProperty(prop, out var v) ? JsonNode.Parse(v.GetRawText()) : null;

        var eventId = Str(boot, "eventId");
        if (eventId.Length == 0)
            eventId = JsonTokenExtractor.EventIdFromUrl(eventUrl) ?? "";

        var o = new JsonObject
        {
            ["eventId"] = eventId,
            ["eventName"] = Str(boot, "eventName"),
            ["eventUrl"] = eventUrl,
            ["venueName"] = Str(boot, "venueName"),
            ["venueId"] = Copy("venueId"),
            ["venueConfigId"] = Copy("venueConfigId"),
            ["formattedEventDateTime"] = Str(boot, "formattedEventDateTime"),
            ["sportsEvent"] = Copy("sportsEvent"),
            ["totalCount"] = totalCount,
            ["ticketClasses"] = Copy("ticketClasses") ?? new JsonArray(),
            ["ticketClassPopupData"] = Copy("ticketClassPopupData") ?? new JsonObject(),
            ["items"] = new JsonArray(items.Select(it => (JsonNode?)JsonNode.Parse(it.GetRawText())).ToArray()),
            ["coverage"] = new JsonObject
            {
                ["collected"] = items.Count,
                ["totalCount"] = totalCount,
                ["coverage_pct"] = covPct,
                ["method"] = method,
                ["sections"] = sections,
                ["sections_failed"] = sectionsFailed,
                ["note"] = covPct is null || covPct >= 97
                    ? null
                    : "incomplete - DataDome rate-limited the IP mid-sweep; use a rotating "
                      + "residential proxy for full coverage",
            },
        };
        return JsonSerializer.SerializeToElement(o);
    }

    private static string? ItemKey(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object) return null;
        if (it.TryGetProperty("id", out var a) && a.ValueKind != JsonValueKind.Null) return a.ToString();
        if (it.TryGetProperty("listingId", out var b) && b.ValueKind != JsonValueKind.Null) return b.ToString();
        return null;
    }

    private static string Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int? IntOrNull(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    private static List<JsonElement> ArrayItems(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => e.Clone()).ToList()
            : new List<JsonElement>();

    private static List<string> StrArray(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v) ||
            v.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return v.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString())
            .Where(s => s.Length > 0)
            .ToList();
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _host.Dispose();
    }
}
