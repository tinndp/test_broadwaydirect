using MongoDB.Bson.Serialization.Attributes;

namespace StubHub.Core.Models;

/// <summary>
/// One persisted StubHub listing in the per-event inventory collection
/// (<c>StubHub_Inventories_NEW_{eventId}</c>). Field-for-field port of
/// ETECH.Application.MarkAutomation StubHubCrawler's
/// <c>StubHubIntegrationTemplateSourceTicket</c> (the
/// <c>IIntegrationTemplateSourceTicket</c> base fields plus the StubHub concrete
/// fields) so both bots write the same document shape. Property names are the
/// BSON element names (PascalCase, no camel-casing) - identical to StubHubCrawler.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class StubHubInventoryTicket
{
    // --- IIntegrationTemplateSourceTicket (base) ---

    /// <summary>Native StubHub listing id when present, else a deterministic hash
    /// of Section_Row_LowSeat_HighSeat (see <see cref="StubHubInventoryMapper"/>).</summary>
    [BsonId]
    public string Id { get; set; } = "";

    public string SourceEventId { get; set; } = "";
    public string Section { get; set; } = "";
    public string Row { get; set; } = "";
    public int LowSeat { get; set; }
    public int HighSeat { get; set; }
    public int Quantity { get; set; }
    public string Seating { get; set; } = "";
    public double Price { get; set; }
    public string? PublicNotes { get; set; }
    public string? PrivateNotes { get; set; }
    public string? Splits { get; set; }
    public bool? BrownerOwned { get; set; }

    // --- StubHub concrete fields ---

    /// <summary>Native <c>grid.items[].id</c> / <c>listingId</c> - StubHub returns
    /// one per listing, so this is populated and used as <see cref="Id"/>.</summary>
    public string? ListingId { get; set; }
    public string? TicketId { get; set; }

    public long PriceLevelId { get; set; }
    public string? DisplayName { get; set; }
    public string? Zone { get; set; }
    public double DisplayPrice { get; set; }
    public string? PriceClass { get; set; }
    public string[] SeatKeys { get; set; } = System.Array.Empty<string>();

    /// <summary>"exact" (SeatKeys are StubHub-confirmed) | "declared" (LowSeat/HighSeat
    /// come from a seller-supplied range, not confirmed) | "none".</summary>
    public string SeatDetailLevel { get; set; } = "none";

    /// <summary>StubHub-only superset: per-listing price + currency (Broadway
    /// listings inherit price from their price level).</summary>
    public double RawPrice { get; set; }
    public string Currency { get; set; } = "USD";
}
