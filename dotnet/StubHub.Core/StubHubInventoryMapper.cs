using BroadwayDirect.Core.Models;
using StubHub.Core.Models;

namespace StubHub.Core;

/// <summary>
/// Turns the adapter's price-level + listing output into the per-listing
/// <see cref="StubHubInventoryTicket"/> documents. 1:1 port of the mapping in
/// ETECH.Application.MarkAutomation StubHubCrawler's
/// <c>StubHubSessionForm.SaveResultsAsync</c> (<c>BuildTicketId</c> /
/// <c>GetDeterministicHashCode</c> / <c>ParseSeatRange</c> included), so the two
/// bots produce byte-identical documents.
/// </summary>
public static class StubHubInventoryMapper
{
    /// <summary>Build the ticket documents for one event, de-duplicated by
    /// <see cref="StubHubInventoryTicket.Id"/> (last one wins - mirrors the
    /// fetcher's dedupe-by-listing-id).</summary>
    public static List<StubHubInventoryTicket> Build(
        string eventId, IEnumerable<PriceLevel> priceLevels, IEnumerable<StubHubListing> listings)
    {
        var plById = new Dictionary<long, PriceLevel>();
        foreach (var pl in priceLevels) plById[pl.PriceLevelId] = pl;

        var byId = new Dictionary<string, StubHubInventoryTicket>();
        foreach (var l in listings)
        {
            plById.TryGetValue(l.PriceLevelId, out var pl);
            ParseSeatRange(l.SeatRange, out var low, out var high);

            var t = new StubHubInventoryTicket
            {
                SourceEventId = eventId,
                Section = l.SectionLabel,
                Row = l.Row,
                LowSeat = low,
                HighSeat = high,
                Quantity = l.Quantity,
                Seating = l.SeatingType,
                // StubHub carries a real per-listing price; the price level's min is the fallback.
                Price = l.RawPrice != 0 ? l.RawPrice : (pl?.Price ?? 0),
                PriceLevelId = l.PriceLevelId,
                DisplayName = pl?.DisplayName,
                Zone = pl?.Zone,
                DisplayPrice = pl?.DisplayPrice ?? 0,
                PriceClass = pl?.PriceClass,
                SeatKeys = l.SeatKeys.ToArray(),
                ListingId = l.ListingId,
                RawPrice = l.RawPrice,
                Currency = l.Currency,
            };
            t.Id = BuildTicketId(t);
            byId[t.Id] = t;
        }
        return byId.Values.ToList();
    }

    /// <summary>Native listing id when present, else a deterministic hash of
    /// Section_Row_LowSeat_HighSeat (matches StubHubCrawler.BuildTicketId).</summary>
    private static string BuildTicketId(StubHubInventoryTicket t)
    {
        if (!string.IsNullOrEmpty(t.ListingId)) return t.ListingId!;
        if (!string.IsNullOrEmpty(t.TicketId)) return t.TicketId!;
        var key = $"{t.Section}_{t.Row}_{t.LowSeat}_{t.HighSeat}";
        return GetDeterministicHashCode(key).ToString();
    }

    /// <summary>"1-3" -&gt; (1, 3); "5" -&gt; (5, 5); "" -&gt; (0, 0).</summary>
    private static void ParseSeatRange(string seatRange, out int low, out int high)
    {
        low = 0;
        high = 0;
        if (string.IsNullOrEmpty(seatRange)) return;
        var parts = seatRange.Split('-');
        if (int.TryParse(parts[0].Trim(), out var a)) { low = a; high = a; }
        if (parts.Length > 1 && int.TryParse(parts[^1].Trim(), out var b)) high = b;
    }

    /// <summary>Framework-independent string hash (does NOT use the randomized
    /// <see cref="string.GetHashCode()"/>). Byte-identical to
    /// StubHubCrawler.GetDeterministicHashCode.</summary>
    private static int GetDeterministicHashCode(string str)
    {
        unchecked
        {
            var hash1 = (5381 << 16) + 5381;
            var hash2 = hash1;
            for (var i = 0; i < str.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ str[i];
                if (i == str.Length - 1) break;
                hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
            }
            return hash1 + (hash2 * 1566083941);
        }
    }
}
