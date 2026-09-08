using TicketMaster.Core;
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

// POST /api/eventinventory { eventId, url, proxy? }
//   -> { eventId, total, pageCount, listingCount, listings:[...], rawPages:[...] }
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

    try
    {
        var (crawl, listings) = await client.GetListingsAsync(req.EventId, req.Url, req.Proxy ?? "");
        return Results.Json(new
        {
            eventId = crawl.EventId,
            total = crawl.Total,
            pageCount = crawl.PageCount,
            listingCount = listings.Count,
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
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

/// <summary>proxy: standard "scheme://[user:pass@]host:port" or raw "host:port:user:pass".
/// includeRaw: also return every page's raw quickpicks JSON (large).</summary>
internal record EventInventoryRequest(string EventId, string Url, string? Proxy, bool IncludeRaw = false);
