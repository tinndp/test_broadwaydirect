using System.Collections.Concurrent;
using System.Text.Json;
using BroadwayDirect.Core.Models;
using BroadwayDirect.Core.Proxy;
using BroadwayDirect.Core.Storage;
using MongoDB.Bson;
using StubHub.Core;
using StubHub.Core.Json;
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

// Lazily connects to MongoDB on first use; returns null (warns once) if
// unreachable - persistence is best-effort, never fatal to the request.
// 1:1 port of api.py's _get_mongo().
MongoStore? mongo = null;
var mongoInitFailed = false;
var mongoLock = new object();

MongoStore? GetMongo()
{
    lock (mongoLock)
    {
        if (mongo != null || mongoInitFailed) return mongo;
        try
        {
            // TEST: hardcoded to the shared broadwaydirect Mongo, same as
            // BroadwayDirect.Api. Restore the env-var lines before merging.
            //var uri = Environment.GetEnvironmentVariable("MONGO_URI") ?? "mongodb://localhost:27017";
            //var dbName = Environment.GetEnvironmentVariable("MONGO_DB") ?? "broadwaydirect";
            var uri = "mongodb://broadwaydirect_user:broadwaydirect123@192.168.100.2:27017/broadwaydirect";
            var dbName = "broadwaydirect";
            mongo = new MongoStore(uri, dbName);
        }
        catch (Exception e)
        {
            mongoInitFailed = true;
            Console.Error.WriteLine($"  !! MongoDB unreachable, persistence disabled this run: {e.Message}");
        }
        return mongo;
    }
}

// "https://www.stubhub.com/..." -> "stubhub.com" (drop a leading www.). This is
// the `source` tag on both Mongo collections, shared across sources.
static string SourceOf(Uri url)
{
    var host = url.Host.ToLowerInvariant();
    return host.StartsWith("www.") ? host[4..] : host;
}

void MirrorToMongo(string source, string eventId, JsonElement raw,
    IEnumerable<PriceLevel> priceLevels, IEnumerable<ICleanedListing> listings)
{
    var m = GetMongo();
    if (m == null) return;
    try
    {
        m.SaveRawEvent(source, eventId, BsonDocument.Parse(raw.GetRawText()));
        m.SaveCleanedEvent(source, eventId, priceLevels, listings);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"  !! failed to mirror event {eventId} to MongoDB: {e.Message}");
    }
}

// POST /api/eventinventory { eventId, url, proxy?, useGridPost?, concurrency?, batchDelay? }
//   -> { eventId, raw, price_levels[], listings[] }
// url = a .../event/<id>/ page. Fetches every section, normalizes, and mirrors
// raw + cleaned to MongoDB (source = "stubhub.com"). Same response contract as
// BroadwayDirect.Api + python/stubhub/api.py.
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
    MirrorToMongo(SourceOf(parsedUrl), ev.Id.ToString(), raw, priceLevels, listings);

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
        // first 7 keys == BroadwayDirect's listing shape exactly; raw_price /
        // currency are the StubHub-only superset (cleaned_events stores only the 7).
        listings = listings.Select(l => new
        {
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

// POST /api/discover { url, scope?, maxPages?, proxy? }
//   -> { sourceUrl, scope, totalCount, collected, events[] }
// url = a /category/ , /grouping/ , /venue/ or /performer/ page. Returns the
// event list only (no inventory) - the caller loops each event.eventId back into
// /api/eventinventory.
app.MapPost("/api/discover", async (DiscoverRequest req) =>
{
    if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var parsedUrl) ||
        string.IsNullOrEmpty(parsedUrl.Scheme) || string.IsNullOrEmpty(parsedUrl.Host))
        return Results.BadRequest(new { error = "invalid url" });

    string[] segs = { "/category/", "/grouping/", "/venue/", "/performer/" };
    if (!segs.Any(parsedUrl.AbsolutePath.Contains))
        return Results.BadRequest(new
        {
            error = "url must be a /category/ , /grouping/ , /venue/ or /performer/ page",
        });

    var proxy = "";
    if (!string.IsNullOrWhiteSpace(req.Proxy))
    {
        try { proxy = ProxyUri.Normalize(req.Proxy); }
        catch (FormatException ex) { return Results.BadRequest(new { error = $"invalid proxy: {ex.Message}" }); }
    }

    try
    {
        var result = await GetClient(proxy).DiscoverAsync(req.Url, req.Scope, req.MaxPages);
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    foreach (var c in clients.Values)
        c.DisposeAsync().AsTask().GetAwaiter().GetResult();
    mongo?.Close();
});

app.Run();

/// <summary>proxy: optional - "scheme://[user:pass@]host:port" or the raw
/// "host:port:user:pass" providers hand out. useGridPost/concurrency/batchDelay:
/// optional sweep-path tuning (null = client default).</summary>
internal record EventInventoryRequest(
    string EventId, string Url, string? Proxy,
    bool? UseGridPost, int? Concurrency, double? BatchDelay);

internal record DiscoverRequest(string Url, string Scope = "all", int MaxPages = 50, string? Proxy = null);
