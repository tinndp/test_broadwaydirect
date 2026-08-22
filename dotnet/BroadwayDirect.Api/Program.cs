using BroadwayDirect.Fetch;

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

// POST /api/eventinventory { eventId, url, proxy? } -> raw JSON from eventinventory.
// url = the series ticket page (e.g.
// https://tickets.broadwaydirect.com/tickets/series/{seriesId}), used to
// navigate there first to get past Cloudflare - the API domain is inferred
// from this url.
app.MapPost("/api/eventinventory", async (EventInventoryRequest req, BroadwayDirectFetchClient client) =>
{
    if (string.IsNullOrWhiteSpace(req.EventId) || string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest(new { error = "eventId and url are required" });

    if (!Uri.TryCreate(req.Url, UriKind.Absolute, out _))
        return Results.BadRequest(new { error = "invalid url" });

    try
    {
        var json = await client.GetEventInventoryAsync(req.EventId, req.Url, req.Proxy ?? "");
        return Results.Text(json.GetRawText(), "application/json");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

/// <summary>proxy: optional, in the standard "scheme://[user:pass@]host:port"
/// format (e.g. "http://45.32.1.2:8080" or "http://user:pass@45.32.1.2:8080").</summary>
internal record EventInventoryRequest(string EventId, string Url, string? Proxy);
