using System.Text.Json;
using BroadwayDirect.Core.Models;

namespace BroadwayDirect.Core.Grouping;

/// <summary>
/// Parses mapSeats -> groups individual seats into 'listings' following the
/// client's exact rules. 1:1 port of broadwaydirect/grouping.py - see that
/// file's docstring for details on the SIDES/CENTER/Box rules, price code as
/// delimiter, and ADA/reserved/kill seat exclusion.
/// </summary>
public static class SeatGrouper
{
    /// <summary>'ORCH L-C-5' -> ('ORCH L', 'C', 5). Only splits on the last 2
    /// '-' characters (equivalent to Python's str.rsplit("-", 2)) since the
    /// section name may contain spaces.</summary>
    public static (string Section, string Row, int SeatNum) ParseSeatKey(string key)
    {
        var parts = RSplit(key, '-', 2);
        if (parts.Count != 3)
            throw new FormatException($"Could not parse seat key: '{key}' (expected Section-Row-Seat# format)");

        var (section, row, seatStr) = (parts[0], parts[1], parts[2]);
        if (!int.TryParse(seatStr, out var seatNum))
            throw new FormatException($"Seat# part is not an integer in key: '{key}'");

        return (section, row, seatNum);
    }

    private static List<string> RSplit(string s, char sep, int maxSplit)
    {
        var parts = new List<string>();
        var remaining = s;
        var count = 0;
        while (count < maxSplit)
        {
            var idx = remaining.LastIndexOf(sep);
            if (idx == -1) break;
            parts.Insert(0, remaining[(idx + 1)..]);
            remaining = remaining[..idx];
            count++;
        }
        parts.Insert(0, remaining);
        return parts;
    }

    /// <summary>Returns (suffix or null, seating_type) per the client's rule table.</summary>
    public static (string? Suffix, string SeatingType) ClassifySection(string section, int seatNum, SectionRules rules)
    {
        var threshold = rules.CenterSeatThreshold;
        var sectionUpper = section.ToUpperInvariant();

        if (rules.BoxPrefixes.Any(p => sectionUpper.StartsWith(p.ToUpperInvariant())))
            return (null, "Consecutive");

        if (rules.SidesCenterPrefixes.Any(p => sectionUpper.StartsWith(p.ToUpperInvariant())))
            return seatNum > threshold ? (rules.CenterSuffix, "Consecutive") : (rules.SidesSuffix, "Odd/Even");

        // section doesn't match any configured rule -> fall back to the same
        // default as ORCH/FMEZZ/RMEZZ so no data is lost.
        return seatNum > threshold ? (rules.CenterSuffix, "Consecutive") : (rules.SidesSuffix, "Odd/Even");
    }

    /// <summary>Converts mapSeats (raw JSON from the eventinventory API) into a
    /// list of Seat with section/row/seat_num parsed, with isReserved/isKill/
    /// killSeatKeys seats ALREADY EXCLUDED.</summary>
    public static List<Seat> SeatsFromInventory(JsonElement inventory)
    {
        var killKeys = new HashSet<string>();
        if (inventory.TryGetProperty("killSeatKeys", out var ksk) && ksk.ValueKind == JsonValueKind.Array)
            foreach (var k in ksk.EnumerateArray())
                if (k.GetString() is { } s) killKeys.Add(s);

        var seats = new List<Seat>();
        if (!inventory.TryGetProperty("mapSeats", out var mapSeats) || mapSeats.ValueKind != JsonValueKind.Array)
            return seats;

        foreach (var raw in mapSeats.EnumerateArray())
        {
            if (GetBool(raw, "isReserved") || GetBool(raw, "isKill")) continue;

            var key = GetString(raw, "key", "");
            if (killKeys.Contains(key)) continue;

            var priceKey = GetString(raw, "priceKey", "");
            var plidPart = priceKey.Split('-')[0];
            if (!long.TryParse(plidPart, out var plid)) continue;

            string section, row;
            int seatNum;
            try
            {
                (section, row, seatNum) = ParseSeatKey(key);
            }
            catch (FormatException)
            {
                // key isn't in the Section-Row-Seat# format (e.g. GA seats) -> skip
                continue;
            }

            seats.Add(new Seat
            {
                Key = key,
                PriceLevelId = plid,
                Section = section,
                Row = row,
                SeatNum = seatNum,
                AdaType = GetString(raw, "adaType", "None") is { Length: > 0 } a ? a : "None",
                IsReserved = GetBool(raw, "isReserved"),
                IsKill = GetBool(raw, "isKill"),
            });
        }

        return seats;
    }

    private static string GetString(JsonElement obj, string prop, string fallback)
    {
        if (!obj.TryGetProperty(prop, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        return v.GetString() ?? fallback;
    }

    private static bool GetBool(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false,
        };
    }

    /// <summary>Groups a list of Seats (already filtered to sellable ones) into a
    /// list of Listing. Grouping key = (section_label with suffix applied, row,
    /// price_level_id, seating_type, parity if Odd/Even). Grouping step = 2 for
    /// Odd/Even (SIDES), 1 for Consecutive (CENTER/Box/other). Price code is
    /// always a delimiter since it's part of the grouping key.</summary>
    public static List<Listing> GroupIntoListings(IEnumerable<Seat> seats, SectionRules? rules = null, bool includeAda = false)
    {
        rules ??= SectionRules.Default();
        var buckets = new Dictionary<(string Label, string Row, long Plid, string SeatingType, int? Parity), List<Seat>>();

        foreach (var s in seats)
        {
            if (s.AdaType != "None" && !includeAda) continue;

            var (suffix, seatingType) = ClassifySection(s.Section, s.SeatNum, rules);
            var label = suffix != null ? $"{s.Section} {suffix}".Trim() : s.Section;
            int? parity = seatingType == "Odd/Even" ? s.SeatNum % 2 : null;
            var bucketKey = (label, s.Row, s.PriceLevelId, seatingType, parity);

            if (!buckets.TryGetValue(bucketKey, out var list))
                buckets[bucketKey] = list = new List<Seat>();
            list.Add(s);
        }

        var listings = new List<Listing>();
        foreach (var ((label, row, plid, seatingType, _), group) in buckets)
        {
            var step = seatingType == "Odd/Even" ? 2 : 1;
            var sorted = group.OrderBy(s => s.SeatNum).ToList();
            var run = new List<Seat> { sorted[0] };
            for (var i = 1; i < sorted.Count; i++)
            {
                var prev = sorted[i - 1];
                var cur = sorted[i];
                if (cur.SeatNum - prev.SeatNum == step)
                {
                    run.Add(cur);
                }
                else
                {
                    listings.Add(MakeListing(label, row, plid, seatingType, run));
                    run = new List<Seat> { cur };
                }
            }
            listings.Add(MakeListing(label, row, plid, seatingType, run));
        }

        return listings;
    }

    private static Listing MakeListing(string label, string row, long plid, string seatingType, List<Seat> run) => new()
    {
        SectionLabel = label,
        Row = row,
        PriceLevelId = plid,
        SeatKeys = run.Select(s => s.Key).ToList(),
        SeatNums = run.Select(s => s.SeatNum).ToList(),
        SeatingType = seatingType,
    };
}
