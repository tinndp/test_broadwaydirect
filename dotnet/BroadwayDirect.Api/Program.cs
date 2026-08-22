using System.Text.Json;
using BroadwayDirect.Core.Grouping;
using BroadwayDirect.Core.Models;
using BroadwayDirect.Core.Storage;
using BroadwayDirect.Fetch;
using MongoDB.Bson;

var builder = WebApplication.CreateBuilder(args);

var fetchOptions = builder.Configuration.GetSection("Fetch").Get<FetchOptions>() ?? new FetchOptions();
builder.Services.AddSingleton(fetchOptions);

// Singleton is required: BroadwayDirectFetchClient holds 1 WebView2Host (UI
// thread + pool of sessions already "warmed" past Cloudflare) that lives for
// the app's whole lifetime - creating a new one per request would pay the
// 10s Cloudflare challenge cost every time, losing the entire benefit of
// reusing sessions.
builder.Services.AddSingleton(sp => new BroadwayDirectFetchClient(sp.GetRequiredService<FetchOptions>()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

var rules = SectionRules.Load(Environment.GetEnvironmentVariable("SECTION_RULES_PATH"));

// Lazily connects to MongoDB on first use. Returns null (and only warns
// once) if unreachable - callers must treat Mongo persistence as
// best-effort, never fatal to the request. 1:1 port of api.py's _get_mongo().
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
            var uri = Environment.GetEnvironmentVariable("MONGO_URI") ?? "mongodb://localhost:27017";
            var dbName = Environment.GetEnvironmentVariable("MONGO_DB") ?? "broadwaydirect";
            mongo = new MongoStore(uri, dbName);
        }
        catch (Exception e)
        {
            mongoInitFailed = true;
            Console.Error.WriteLine($"  !! could not connect to MongoDB, persistence disabled for this run: {e.Message}");
        }
        return mongo;
    }
}

// Best-effort: saves raw + cleaned to Mongo. Any failure is logged, never
// raised. source: the ticket site's domain (e.g. tickets.broadwaydirect.com)
// - both collections are shared across sources, keyed by (source, event_id).
void MirrorToMongo(string source, string eventId, JsonElement raw, IEnumerable<PriceLevel> priceLevels, IEnumerable<Listing> listings)
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

// POST /api/eventinventory { eventId, url, proxy? } -> {eventId, raw,
// price_levels, listings}. "raw" preserves the old bare-JSON behavior for
// callers who only want that; "price_levels"/"listings" are the same
// grouped data mirrored to MongoDB's cleaned_events (see MongoStore.cs), so
// callers don't have to duplicate the grouping logic or query Mongo
// separately just to get it. Same response contract as python/broadwaydirect/api.py.
//
// url = the series ticket page (e.g.
// https://tickets.broadwaydirect.com/tickets/series/{seriesId}), used to
// navigate there first to get past Cloudflare - the API domain is inferred
// from this url.
app.MapPost("/api/eventinventory", async (EventInventoryRequest req, BroadwayDirectFetchClient client) =>
{
    if (string.IsNullOrWhiteSpace(req.EventId) || string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest(new { error = "eventId and url are required" });

    if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var parsedUrl))
        return Results.BadRequest(new { error = "invalid url" });

    JsonElement json;
    try
    {
        json = await client.GetEventInventoryAsync(req.EventId, req.Url, req.Proxy ?? "");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var seats = SeatGrouper.SeatsFromInventory(json);
    var listings = SeatGrouper.GroupIntoListings(seats, rules);
    var priceLevels = SqliteStorage.ParsePriceLevelsFromInventory(json);

    MirrorToMongo(parsedUrl.Host, req.EventId, json, priceLevels, listings);

    return Results.Json(new
    {
        eventId = req.EventId,
        raw = json,
        price_levels = priceLevels.Select(pl => new
        {
            price_level_id = pl.PriceLevelId,
            display_name = pl.DisplayName,
            zone = pl.Zone,
            price = pl.Price,
            display_price = pl.DisplayPrice,
            price_class = pl.PriceClass,
        }),
        listings = listings.Select(l => new
        {
            section_label = l.SectionLabel,
            row = l.Row,
            price_level_id = l.PriceLevelId,
            seating_type = l.SeatingType,
            quantity = l.Quantity,
            seat_range = l.SeatRangeLabel,
            seat_keys = l.SeatKeys,
        }),
    });
});

app.Lifetime.ApplicationStopping.Register(() => mongo?.Close());

app.Run();

/// <summary>proxy: optional, in the standard "scheme://[user:pass@]host:port"
/// format (e.g. "http://45.32.1.2:8080" or "http://user:pass@45.32.1.2:8080").</summary>
internal record EventInventoryRequest(string EventId, string Url, string? Proxy);
