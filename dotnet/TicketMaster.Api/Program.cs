using TicketMaster.Core;
using TicketMaster.Core.Storage;
using TicketMaster.Fetch;

var builder = WebApplication.CreateBuilder(args);

// Singleton: TicketMasterFetchClient owns one WebView2Host (an STA UI thread). One per app,
// not per request - a new WebView2 environment per request would re-pay the Kasada warm-up every
// time. Same reasoning as BroadwayDirect.Api.
builder.Services.AddSingleton(_ => new TicketMasterFetchClient());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();

// Lazily connects to MongoDB on first use. NOT a best-effort mirror: the per-event
// TMEvent_{eventId} collection is the ONLY place listing data lands, so a null store or a failed
// write is fatal to the request (matches ETECH.Application.MarkAutomation's TicketMasterCrawlerBot /
// TicketMasterSavePipeline, and StubHub.Api). Connection from MONGO_URI / MONGO_DB env vars.
// Default points at the shared dev/test Mongo (same box StubHub.Api uses). MONGO_URI / MONGO_DB
// env vars override it.
var mongoUri = Environment.GetEnvironmentVariable("MONGO_URI")
    ?? "mongodb://broadwaydirect_user:broadwaydirect123@192.168.100.2:27017/broadwaydirect";
var mongoDb = Environment.GetEnvironmentVariable("MONGO_DB") ?? "broadwaydirect";

TicketMasterInventoryStore? store = null;
string? storeInitError = null;
var storeLock = new object();

TicketMasterInventoryStore? GetStore()
{
    lock (storeLock)
    {
        if (store != null || storeInitError != null) return store;
        try
        {
            store = new TicketMasterInventoryStore(mongoUri, mongoDb);
        }
        catch (Exception e)
        {
            storeInitError = e.Message;
            Console.Error.WriteLine($"  !! MongoDB unreachable ({mongoUri}): {e.Message}");
        }
        return store;
    }
}

// POST /api/eventinventory { eventId, url, proxy?, includeRaw? }
//   -> { eventId, total, pageCount, listingCount, listings[], rawPages? }
//
// Fetches every quickpicks page, groups with ListingBuilder, then drop+rewrites
// TMEvent_{eventId} in MongoDB with one document per listing (same as the ETECH bot). A fetch
// failure returns 502; a persistence failure returns 500 - neither is swallowed.
//
// url   = the public event page, e.g.
//         https://www.ticketmaster.com/<slug>/event/05006389BE118DE0
// proxy = REQUIRED in practice - ticketmaster.com hides tickets without one. Standard
//         "scheme://[user:pass@]host:port" or raw "host:port:user:pass".
app.MapPost("/api/eventinventory", async (EventInventoryRequest req, TicketMasterFetchClient client) =>
{
    if (string.IsNullOrWhiteSpace(req.EventId) || string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest(new { error = "eventId and url are required" });
    if (!Uri.TryCreate(req.Url, UriKind.Absolute, out _))
        return Results.BadRequest(new { error = "invalid url" });

    TicketMasterCrawlResult crawl;
    List<TicketMaster.Core.Models.TicketMasterListing> listings;
    try
    {
        (crawl, listings) = await client.GetListingsAsync(req.EventId, req.Url, req.Proxy ?? "");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var tickets = TicketMasterInventoryMapper.Build(req.EventId, listings);

    var persisted = false;
    if (req.Persist)
    {
        try
        {
            var s = GetStore()
                ?? throw new InvalidOperationException(
                    $"MongoDB is unreachable at '{mongoUri}' ({storeInitError}). " +
                    "Set MONGO_URI / MONGO_DB, or pass \"persist\": false to skip persistence.");
            s.SaveEventInventory(req.EventId, tickets);
            persisted = true;
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"fetch succeeded but persistence failed: {ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    return Results.Json(new
    {
        eventId = crawl.EventId,
        total = crawl.Total,
        pageCount = crawl.PageCount,
        listingCount = tickets.Count,
        persisted,
        persistedTo = persisted ? TicketMasterInventoryStore.CollectionPrefix + req.EventId : null,
        // Integration Template staging documents - exactly what lands in
        // TicketMaster_Inventories_NEW_{eventId}
        listings = tickets,
        includeRaw = req.IncludeRaw,
        rawPages = req.IncludeRaw ? crawl.RawPages : null,
    });
});

app.Lifetime.ApplicationStopping.Register(() => store?.Close());

app.Run();

/// <summary>proxy: standard "scheme://[user:pass@]host:port" or raw "host:port:user:pass".
/// includeRaw: also return every page's raw quickpicks JSON (large).
/// persist: write the listings to Mongo TMEvent_{eventId} (default true); set false to test the
/// crawl without a Mongo.</summary>
internal record EventInventoryRequest(
    string EventId, string Url, string? Proxy, bool IncludeRaw = false, bool Persist = true);
