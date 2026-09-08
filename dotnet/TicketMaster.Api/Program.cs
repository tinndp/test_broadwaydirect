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
TicketMasterInventoryStore? store = null;
var storeInitFailed = false;
var storeLock = new object();

TicketMasterInventoryStore? GetStore()
{
    lock (storeLock)
    {
        if (store != null || storeInitFailed) return store;
        try
        {
            var uri = Environment.GetEnvironmentVariable("MONGO_URI") ?? "mongodb://localhost:27017";
            var dbName = Environment.GetEnvironmentVariable("MONGO_DB") ?? "broadwaydirect";
            store = new TicketMasterInventoryStore(uri, dbName);
        }
        catch (Exception e)
        {
            storeInitFailed = true;
            Console.Error.WriteLine($"  !! MongoDB unreachable: {e.Message}");
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

    try
    {
        var s = GetStore()
            ?? throw new InvalidOperationException("MongoDB is unreachable - listing data cannot be persisted");
        s.SaveEventInventory(req.EventId, listings);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: $"fetch succeeded but persistence failed: {ex.Message}",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Json(new
    {
        eventId = crawl.EventId,
        total = crawl.Total,
        pageCount = crawl.PageCount,
        listingCount = listings.Count,
        persistedTo = TicketMasterInventoryStore.CollectionPrefix + req.EventId,
        listings = listings.Select(l => new
        {
            l.Id, l.Section, l.Row, l.MaxQuantity,
            l.ListPrice, l.FaceValue, l.TotalPrice, l.NoChargesPrice,
            l.OfferName, l.OfferType, l.InventoryType,
            l.Attributes, l.OfferGroupSeats, l.OfferGroupSeatMin, l.OfferGroupSeatMax,
            l.DescriptionId, l.Description, l.SellableQuantities, l.ChargeJson,
            l.DisplaySeat,
        }),
        includeRaw = req.IncludeRaw,
        rawPages = req.IncludeRaw ? crawl.RawPages : null,
    });
});

app.Lifetime.ApplicationStopping.Register(() => store?.Close());

app.Run();

/// <summary>proxy: standard "scheme://[user:pass@]host:port" or raw "host:port:user:pass".
/// includeRaw: also return every page's raw quickpicks JSON (large).</summary>
internal record EventInventoryRequest(string EventId, string Url, string? Proxy, bool IncludeRaw = false);
