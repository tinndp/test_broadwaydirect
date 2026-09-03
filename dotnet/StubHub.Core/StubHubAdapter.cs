using System.Globalization;
using System.Text.Json;
using BroadwayDirect.Core.Models;
using StubHub.Core.Models;

namespace StubHub.Core;

/// <summary>
/// Maps a StubHub raw-inventory JSON object (assembled by StubHubClient, stored
/// in <c>raw_events</c>) into the common price_levels + listings shape. 1:1 port
/// of python/stubhub/adapter.py.
///
/// StubHub listings are already grouped (one grid item == one bundle of N seats
/// at one price), so this is a straight field mapping - there is no seat-grouping
/// step like BroadwayDirect's SeatGrouper.
///
/// Input <paramref name="raw"/> shape (see StubHubClient.FetchEventInventoryAsync):
/// <code>
/// {
///   "eventId": "159257698", "eventName": "...", "eventUrl": "...",
///   "venueName": "...", "venueId": 1817, "formattedEventDateTime": "Fri Sep 04 2026 7:10 PM",
///   "sportsEvent": { ...schema.org JSON-LD... } | null,
///   "totalCount": 733,
///   "ticketClasses":        [ {ticketClassId, name, cssPostFix, ...}, ... ],
///   "ticketClassPopupData": { "3631": {rawMinPrice, count, ...}, ... },
///   "items":                [ { ...grid item... }, ... ],
///   "coverage": { collected, totalCount, sections }
/// }
/// </code>
/// </summary>
public static class StubHubAdapter
{
    // ---------------------------------------------------------------------
    // price_levels: one per ticket class (the ~19 seating zones)
    // ---------------------------------------------------------------------
    public static List<PriceLevel> ParsePriceLevels(JsonElement raw)
    {
        var classes = ArrayOrEmpty(raw, "ticketClasses");
        raw.TryGetProperty("ticketClassPopupData", out var popup);

        // fallback min price straight off the collected listings, per class
        var itemMin = new Dictionary<long, double>();
        foreach (var it in ArrayOrEmpty(raw, "items"))
        {
            var cid = LongOrNull(it, "ticketClass");
            var rp = DoubleOrNull(it, "rawPrice");
            if (cid is { } c && rp is { } p)
                itemMin[c] = itemMin.TryGetValue(c, out var cur) ? Math.Min(cur, p) : p;
        }

        var outList = new List<PriceLevel>();
        foreach (var c in classes)
        {
            var cid = LongOrNull(c, "ticketClassId") ?? 0;
            JsonElement popupEntry = default;
            var hasPopup = popup.ValueKind == JsonValueKind.Object &&
                           popup.TryGetProperty(cid.ToString(CultureInfo.InvariantCulture), out popupEntry);

            var price =
                Truthy(hasPopup ? DoubleOrNull(popupEntry, "rawMinPrice") : null) ??
                Truthy(DoubleOrNull(c, "minListingsRawPrice")) ??
                (itemMin.TryGetValue(cid, out var im) && im != 0 ? im : (double?)null) ??
                0.0;

            var name = StringOrEmpty(c, "name");
            outList.Add(new PriceLevel
            {
                PriceLevelId = cid,
                DisplayName = name,
                Zone = name,
                Price = price,
                DisplayPrice = price,   // StubHub fees are per-listing; no class-level display price
                PriceClass = StringOrEmpty(c, "cssPostFix"),
            });
        }
        return outList;
    }

    // ---------------------------------------------------------------------
    // listings: one per collected grid item
    // ---------------------------------------------------------------------
    public static List<StubHubListing> NormalizeListings(JsonElement raw)
    {
        var outList = new List<StubHubListing>();
        foreach (var it in ArrayOrEmpty(raw, "items"))
        {
            var seatKeys = SeatKeys(it);
            outList.Add(new StubHubListing
            {
                ListingId = FirstNonEmpty(StringOrEmpty(it, "id"), StringOrEmpty(it, "listingId")),
                SectionLabel = FirstNonEmpty(StringOrEmpty(it, "sectionMapName"), StringOrEmpty(it, "section")),
                Row = Row(it),
                PriceLevelId = LongOrNull(it, "ticketClass") ?? 0,
                Quantity = (int)(LongOrNull(it, "availableTickets") is { } q && q != 0 ? q : Math.Max(seatKeys.Count, 1)),
                SeatRange = SeatRange(it),
                SeatKeys = seatKeys,
                SeatDetailLevel = SeatDetailLevel(it),
                SeatingType = BoolOrFalse(it, "isSeatedTogether") ? "Consecutive" : "Piggyback",
                RawPrice = DoubleOrNull(it, "rawPrice") ?? 0.0,
                Currency = FirstNonEmpty(StringOrEmpty(it, "listingCurrencyCode"), "USD"),
            });
        }
        return outList;
    }

    private static string Row(JsonElement it)
    {
        var r = StringOrEmpty(it, "row").Trim();
        if (r.Length > 0 && r != "_") return r;
        var rc = StringOrEmpty(it, "rowContent").Trim();
        return rc.Length >= 4 && rc[..4].Equals("row ", StringComparison.OrdinalIgnoreCase)
            ? rc[4..].Trim()
            : rc;
    }

    /// <summary><c>(lo, hi)</c> when <c>seatFrom</c>/<c>seatTo</c> describe one
    /// contiguous block that matches <c>availableTickets</c>; else null. Independent
    /// of <c>hasSeatDetails</c> (StubHub sends a consistent range on plenty of
    /// <c>hasSeatDetails=false</c> listings too). The width==quantity check is what
    /// separates a real reserved-seat block from a zone ticket / seller typo /
    /// placeholder range.</summary>
    private static (int lo, int hi)? SeatSpan(JsonElement it)
    {
        if (BoolOrFalse(it, "isZoneTicketClass") || BoolOrFalse(it, "hideSeatAndRowInfo"))
            return null;
        if (!int.TryParse(StringOrEmpty(it, "seatFrom"), out var a) ||
            !int.TryParse(StringOrEmpty(it, "seatTo"), out var b))
            return null;
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        var qty = (int)(LongOrNull(it, "availableTickets") ?? 0);
        if (lo < 1 || hi - lo + 1 != qty) return null;
        return (lo, hi);
    }

    private static string SeatRange(JsonElement it)
    {
        if (SeatSpan(it) is not { } s) return "";
        return s.lo == s.hi ? s.lo.ToString(CultureInfo.InvariantCulture) : $"{s.lo}-{s.hi}";
    }

    /// <summary>Per-seat keys "SECTION-ROW-N" ONLY when StubHub confirms the exact
    /// seats (<c>hasSeatDetails=true</c>). For a seller-declared range the numbers
    /// are not guaranteed, so keying on them would assert false per-seat identity -
    /// return [] and let identity fall to the listing id.</summary>
    private static List<string> SeatKeys(JsonElement it)
    {
        if (!BoolOrFalse(it, "hasSeatDetails") || SeatSpan(it) is not { } s)
            return new List<string>();
        var row = Row(it);
        if (row.Length == 0) return new List<string>();
        var sec = FirstNonEmpty(StringOrEmpty(it, "sectionMapName"), StringOrEmpty(it, "section"));
        return Enumerable.Range(s.lo, s.hi - s.lo + 1).Select(n => $"{sec}-{row}-{n}").ToList();
    }

    /// <summary>"exact" (hasSeatDetails + consistent range - SeatKeys populated),
    /// "declared" (consistent range but seller-supplied, not confirmed), or "none".</summary>
    private static string SeatDetailLevel(JsonElement it) =>
        SeatSpan(it) is null ? "none" : (BoolOrFalse(it, "hasSeatDetails") ? "exact" : "declared");

    // ---------------------------------------------------------------------
    // event metadata for cleaned_events (name / local_date)
    // ---------------------------------------------------------------------
    public static Event BuildEvent(JsonElement raw)
    {
        raw.TryGetProperty("sportsEvent", out var se);
        var seObj = se.ValueKind == JsonValueKind.Object;

        var localDate = FirstNonEmpty(
            seObj ? StringOrEmpty(se, "startDate") : "",
            IsoFromFormatted(StringOrEmpty(raw, "formattedEventDateTime")));

        return new Event
        {
            Id = long.Parse(StringOrEmpty(raw, "eventId"), CultureInfo.InvariantCulture),
            LocalDate = localDate,
            AvailabilityColor = "",
            Name = FirstNonEmpty(StringOrEmpty(raw, "eventName"), seObj ? StringOrEmpty(se, "name") : ""),
            SeriesId = "",   // StubHub has no series concept
        };
    }

    /// <summary>Best-effort: "Fri Sep 04 2026 7:10 PM" -&gt; ISO. "" on failure; the
    /// JSON-LD startDate is the primary source, this is only a fallback.</summary>
    public static string IsoFromFormatted(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        string[] formats = { "ddd MMM dd yyyy h:mm tt", "ddd MMM dd yyyy H:mm" };
        return DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out var dt)
            ? dt.ToString("s", CultureInfo.InvariantCulture)
            : "";
    }

    // --- JSON helpers ----------------------------------------------------
    private static IEnumerable<JsonElement> ArrayOrEmpty(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string StringOrEmpty(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v))
            return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    private static double? DoubleOrNull(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d : null;

    private static long? LongOrNull(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static bool BoolOrFalse(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.True;

    private static double? Truthy(double? v) => v is not null && v.Value != 0.0 ? v : null;

    private static string FirstNonEmpty(params string[] xs) =>
        xs.FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
}
