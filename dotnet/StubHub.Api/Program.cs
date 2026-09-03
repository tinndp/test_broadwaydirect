using System.Collections.Concurrent;
using System.Text.Json;
using BroadwayDirect.Core.Proxy;
using StubHub.Core;
using StubHub.Core.Json;
using StubHub.Core.Storage;
using StubHub.Fetch;

var builder = WebApplication.CreateBuilder(args);

var fetchOptions = builder.Configuration.GetSection("Fetch").Get<StubHubFetchOptions>() ?? new StubHubFetchOptions();
builder.Services.AddSingleton(fetchOptions);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// One StubHubClient per proxy value (each holds its own WebView2 window warmed
// past DataDome). 1:1 port of python/stubhub/api.py's _get_client cache.
var clients = new ConcurrentDictionary<string, StubHubClient>();
StubHubClient GetClient(string proxy) =>
    clients.GetOrAdd(proxy, p => new StubHubClient(p, fetchOptions));

// Lazily connects to MongoDB on first use. Unlike BroadwayDirect this is NOT a
// best-effort mirror: the per-event StubHub_Inventories_NEW_{eventId} collection
// is the ONLY place listing data lands, so a null store or a failed write is
// fatal to the request (matches ETECH.Application.MarkAutomation's StubHubCrawler).
StubHubInventoryStore? store = null;
var storeInitFailed = false;
var storeLock = new object();

StubHubInventoryStore? GetStore()
{
    lock (storeLock)
    {
        if (store != null || storeInitFailed) return store;
        try
        {
            // TEST: hardcoded to the shared broadwaydirect Mongo, same as
            // BroadwayDirect.Api. Restore the env-var lines before merging.
            //var uri = Environment.GetEnvironmentVariable("MONGO_URI") ?? "mongodb://localhost:27017";
            //var dbName = Environment.GetEnvironmentVariable("MONGO_DB") ?? "broadwaydirect";
            var uri = "mongodb://broadwaydirect_user:broadwaydirect123@192.168.100.2:27017/broadwaydirect";
            var dbName = "broadwaydirect";
            store = new StubHubInventoryStore(uri, dbName);
        }
        catch (Exception e)
        {
            storeInitFailed = true;
            Console.Error.WriteLine($"  !! MongoDB unreachable: {e.Message}");
        }
        return store;
    }
}

// Drop + rewrite StubHub_Inventories_NEW_{eventId} with one document per listing.
// Throws if Mongo is unreachable or the write fails - the caller turns that into
// a 5xx (the fetch succeeded but the data is not persisted anywhere).
void PersistInventory(string eventId,
    IEnumerable<BroadwayDirect.Core.Models.PriceLevel> priceLevels,
    IEnumerable<StubHub.Core.Models.StubHubListing> listings)
{
    var s = GetStore()
        ?? throw new InvalidOperationException("MongoDB is unreachable - listing data cannot be persisted");
    var docs = StubHubInventoryMapper.Build(eventId, priceLevels, listings);
    s.SaveEventInventory(eventId, docs);
}

// POST /api/eventinventory { eventId, url, proxy?, useGridPost?, concurrency?, batchDelay? }
//   -> { eventId, raw, price_levels[], listings[] }
// url = a .../event/<id>/ page. Fetches every section, normalizes, and writes one
// document per listing into StubHub_Inventories_NEW_{eventId} (dropped + rewritten
// each crawl, same as ETECH.Application.MarkAutomation's StubHubCrawler). A
// persistence failure returns 5xx - it is NOT swallowed.
//
// useGridPost/concurrency/batchDelay are the sweep-path tuning knobs (defaults
// mirror StubHubFetchOptions); omit them for today's behaviour, set
// useGridPost=false to force the section sweep and compare coverage/timing.
app.MapPost("/api/eventinventory", async (EventInventoryRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.EventId) || string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest(new { error = "eventId and url are required" });

    if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var parsedUrl) ||
        string.IsNullOrEmpty(parsedUrl.Scheme) || string.IsNullOrEmpty(parsedUrl.Host))
        return Results.BadRequest(new { error = "invalid url" });

    var idFromUrl = JsonTokenExtractor.EventIdFromUrl(req.Url);
    if (idFromUrl != null && idFromUrl != req.EventId)
        return Results.BadRequest(new
        {
            error = $"eventId mismatch: url has {idFromUrl}, body has {req.EventId}",
        });

    var proxy = "";
    if (!string.IsNullOrWhiteSpace(req.Proxy))
    {
        try { proxy = ProxyUri.Normalize(req.Proxy); }
        catch (FormatException ex) { return Results.BadRequest(new { error = $"invalid proxy: {ex.Message}" }); }
    }

    var overrides = new StubHubFetchOverrides(
        UseGridPost: req.UseGridPost,
        Concurrency: req.Concurrency,
        BatchDelaySeconds: req.BatchDelay);

    JsonElement raw;
    try
    {
        raw = await GetClient(proxy).FetchEventInventoryAsync(req.Url, overrides);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var priceLevels = StubHubAdapter.ParsePriceLevels(raw);
    var listings = StubHubAdapter.NormalizeListings(raw);
    var ev = StubHubAdapter.BuildEvent(raw);

    try
    {
        PersistInventory(ev.Id.ToString(), priceLevels, listings);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: $"fetch succeeded but persistence failed: {ex.Message}",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Json(new
    {
        eventId = req.EventId,
        raw,
        price_levels = priceLevels.Select(pl => new
        {
            price_level_id = pl.PriceLevelId,
            display_name = pl.DisplayName,
            zone = pl.Zone,
            price = pl.Price,
            display_price = pl.DisplayPrice,
            price_class = pl.PriceClass,
        }),
        // the fetch/adapter shape; listing_id / raw_price / currency are the
        // StubHub-only superset. The persisted document shape is different -
        // see StubHubInventoryTicket (one doc per listing, PascalCase fields).
        listings = listings.Select(l => new
        {
            listing_id = l.ListingId,
            section_label = l.SectionLabel,
            row = l.Row,
            price_level_id = l.PriceLevelId,
            seating_type = l.SeatingType,
            quantity = l.Quantity,
            seat_range = l.SeatRange,
            seat_keys = l.SeatKeys,
            raw_price = l.RawPrice,
            currency = l.Currency,
        }),
    });
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    foreach (var c in clients.Values)
        c.DisposeAsync().AsTask().GetAwaiter().GetResult();
    store?.Close();
});

app.Run();

/// <summary>proxy: optional - "scheme://[user:pass@]host:port" or the raw
/// "host:port:user:pass" providers hand out. useGridPost/concurrency/batchDelay:
/// optional sweep-path tuning (null = client default).</summary>
internal record EventInventoryRequest(
    string EventId, string Url, string? Proxy,
    bool? UseGridPost, int? Concurrency, double? BatchDelay);
