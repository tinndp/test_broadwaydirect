using BroadwayDirect.Core.Models;

namespace StubHub.Core.Models;

/// <summary>
/// A StubHub listing (one <c>grid.items[]</c> entry = one bundle of N seats at
/// one price). Port of python/stubhub/models.py's Listing.
///
/// It can't reuse <see cref="BroadwayDirect.Core.Models.Listing"/> because that
/// one derives <c>Quantity</c> from <c>SeatKeys.Count</c> - StubHub only exposes
/// per-seat keys when <c>hasSeatDetails=true</c>, so <see cref="Quantity"/> is an
/// explicit field (from <c>availableTickets</c>) and <see cref="SeatKeys"/> is
/// empty otherwise. <see cref="SeatRange"/> comes from <c>seatFrom</c>/<c>seatTo</c>;
/// <see cref="SeatDetailLevel"/> records whether it is StubHub-confirmed ("exact"),
/// seller-declared ("declared"), or absent ("none").
///
/// It still implements <see cref="ICleanedListing"/> (kept for compatibility),
/// but the StubHub path no longer persists through
/// <see cref="BroadwayDirect.Core.Storage.MongoStore"/> - <see cref="StubHubInventoryMapper"/>
/// turns each listing into a <see cref="StubHubInventoryTicket"/> and
/// <see cref="StubHub.Core.Storage.StubHubInventoryStore"/> writes one document
/// per listing into <c>StubHub_Inventories_NEW_{eventId}</c> (matches
/// ETECH.Application.MarkAutomation's StubHubCrawler). <see cref="RawPrice"/> /
/// <see cref="Currency"/> are the per-listing price StubHub carries natively.
/// </summary>
public sealed class StubHubListing : ICleanedListing
{
    public string ListingId { get; init; } = "";      // native grid.items[].id / listingId (StubHub returns one per listing)
    public string SectionLabel { get; init; } = "";   // StubHub sectionMapName (fallback: section)
    public string Row { get; init; } = "";            // StubHub row (fallback: rowContent minus "Row ")
    public long PriceLevelId { get; init; }           // StubHub ticketClass id -> joins to PriceLevel
    public int Quantity { get; init; }                // StubHub availableTickets (NOT SeatKeys.Count)
    public string SeatRange { get; init; } = "";      // "9001-9007" / "9001", or "" - see SeatDetailLevel
    public List<string> SeatKeys { get; init; } = new();  // per-seat keys, [] unless SeatDetailLevel == "exact"
    public string SeatDetailLevel { get; init; } = "none"; // "exact" (StubHub-confirmed) | "declared" (seller) | "none"
    public string SeatingType { get; init; } = "Consecutive"; // "Consecutive" (isSeatedTogether) | "Piggyback"
    public double RawPrice { get; init; }             // per-listing price in listing currency (raw/HTTP only)
    public string Currency { get; init; } = "USD";    // StubHub listingCurrencyCode

    /// <summary>Alias so <see cref="ICleanedListing"/> / MongoStore see the same
    /// property name they read off BroadwayDirect's Listing.</summary>
    public string SeatRangeLabel => SeatRange;
}
