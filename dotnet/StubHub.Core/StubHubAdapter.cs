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
                SectionLabel = FirstNonEmpty(StringOrEmpty(it, "sectionMapName"), StringOrEmpty(it, "section")),
                Row = Row(it),
                PriceLevelId = LongOrNull(it, "ticketClass") ?? 0,
                Quantity = (int)(LongOrNull(it, "availableTickets") is { } q && q != 0 ? q : Math.Max(seatKeys.Count, 1)),
                SeatRange = SeatRange(it),
                SeatKeys = seatKeys,
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

    private static string SeatRange(JsonElement it)
    {
        if (!BoolOrFalse(it, "hasSeatDetails")) return "";
        var a = StringOrEmpty(it, "seatFrom");
        var b = StringOrEmpty(it, "seatTo");
        if (a.Length == 0 || b.Length == 0) return "";
        return a == b ? a : $"{a}-{b}";
    }

    /// <summary>Per-seat keys "SECTION-ROW-N" when StubHub exposes a seat range that
    /// lines up with the ticket count; otherwise an empty list - when
    /// <c>hasSeatDetails=false</c> StubHub sends no seat numbers at all, so there is
    /// nothing to key on (quantity still carries the real count).</summary>
    private static List<string> SeatKeys(JsonElement it)
    {
        if (BoolOrFalse(it, "hasSeatDetails"))
        {
            var sec = FirstNonEmpty(StringOrEmpty(it, "sectionMapName"), StringOrEmpty(it, "section"));
            var row = Row(it);
            if (int.TryParse(StringOrEmpty(it, "seatFrom"), out var aI) &&
                int.TryParse(StringOrEmpty(it, "seatTo"), out var bI))
            {
                var lo = Math.Min(aI, bI);
                var hi = Math.Max(aI, bI);
                var count = hi - lo + 1;
                var qty = (int)(LongOrNull(it, "availableTickets") ?? 0);
                if (count > 0 && count <= Math.Max(qty, 1) + 4)
                    return Enumerable.Range(lo, count).Select(n => $"{sec}-{row}-{n}").ToList();
            }
        }
        return new List<string>();
    }

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
