using System.Text.Json;
using TicketMaster.Core.Models;

namespace TicketMaster.Core;

/// <summary>
/// Pure transform: <see cref="QuickPicksResponse"/> pages -&gt; <see cref="TicketMasterListing"/>
/// rows. Faithful port of the legacy TMCrawler's <c>ConvertToRowingListingInfo</c> +
/// <c>SaveData</c>'s DisplaySeat block + <c>GetDeterministicHashCode</c>
/// (ETECH.Application/TMCrawler/Program.cs), so this validates the exact logic the ETECH bot's
/// <c>TicketMasterListingBuilder</c> uses. No I/O.
///
/// Feed pages in order via <see cref="AddPage"/>; description text is emitted once (on the page
/// that first carries it). <see cref="Build"/> returns the de-duplicated, resale-filtered,
/// DisplaySeat-assigned listing set.
/// </summary>
public sealed class ListingBuilder
{
    public int DisplaySeatStart { get; init; } = 500;
    public int DisplaySeatStep { get; init; } = 20;

    private readonly Dictionary<string, TicketMasterListing> _byId = new();
    private readonly List<TmDescription> _descriptions = new();
    private readonly HashSet<string> _descIds = new();

    public IReadOnlyList<TmDescription> Descriptions => _descriptions;

    public void AddPage(QuickPicksResponse page)
    {
        if (page.Embedded?.Offer is not { Count: > 0 } offers) return;

        foreach (var d in page.Embedded.Description)
            if (d.Descriptions.Count > 0 && _descIds.Add(d.DescriptionId))
                _descriptions.Add(d);

        foreach (var pick in page.Picks)
        {
            if (pick.OfferGroups.Count == 0) continue;

            foreach (var og in pick.OfferGroups)
            {
                if (og.Offers.Count == 0) continue;

                var attributes = new List<string>();
                if (og.Places.Count > 0 && page.Places.Count > 0)
                {
                    foreach (var p in og.Places)
                        foreach (var rp in page.Places)
                            if (rp.Places != null
                                && rp.Places.Any(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase))
                                && rp.Attributes != null)
                                attributes.AddRange(rp.Attributes);
                }
                if (attributes.Count > 0) attributes = attributes.Distinct().ToList();

                var seats = og.Seats.Select(s => int.TryParse(s, out var n) ? n : (int?)null)
                                    .Where(n => n.HasValue).Select(n => n!.Value).ToList();

                foreach (var offerId in og.Offers)
                {
                    var offer = offers.FirstOrDefault(o => o.OfferId == offerId);
                    if (offer is null || offer.ListPrice == 0 || string.IsNullOrWhiteSpace(offer.Name))
                        continue;

                    var t = new TicketMasterListing
                    {
                        TMEventId = page.EventId,
                        Section = pick.Section,
                        Row = pick.Row,
                        MaxQuantity = 0,
                        InventoryType = offer.InventoryType,
                        OfferName = offer.Name,
                        OfferDescription = offer.Description,
                        ListPrice = (double)offer.ListPrice,
                        FaceValue = (double)offer.FaceValue,
                        TotalPrice = (double)offer.TotalPrice,
                        NoChargesPrice = (double)offer.NoChargesPrice,
                        OfferType = offer.OfferType,
                        Attributes = string.Join(",", attributes),
                    };

                    if (seats.Count > 0)
                    {
                        t.OfferGroupSeats = string.Join(",", og.Seats);
                        t.OfferGroupSeatMin = seats.Min();
                        t.OfferGroupSeatMax = seats.Max();
                    }

                    t.DescriptionId = pick.DescriptionId;
                    if (!string.IsNullOrWhiteSpace(pick.DescriptionId))
                    {
                        var desc = page.Embedded.Description.FirstOrDefault(
                            x => string.Equals(x.DescriptionId, pick.DescriptionId, StringComparison.OrdinalIgnoreCase));
                        if (desc is { Descriptions.Count: > 0 })
                            t.Description = JsonSerializer.Serialize(desc.Descriptions);
                    }

                    if (offer.Charges is { Count: > 0 })
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var c in offer.Charges)
                            sb.Append($"[ Reason: {c.Reason} | Type : {c.Type} | Amt: {c.Amount:c2} ]");
                        t.ChargeJson = sb.ToString();
                    }

                    t.MaxQuantity = pick.MaxQuantity;
                    if (offer.SellableQuantities is { Count: > 0 })
                    {
                        t.SellableQuantities = JsonSerializer.Serialize(offer.SellableQuantities);
                        if (t.MaxQuantity == 0) t.MaxQuantity = offer.SellableQuantities.Max();
                    }

                    t.Id = GetDeterministicHashCode(
                        $"{t.Section}_{t.Row}_{t.MaxQuantity}_{t.OfferGroupSeatMin}_{t.OfferName}").ToString();

                    if (string.Equals(t.TMEventId, page.EventId, StringComparison.OrdinalIgnoreCase)
                        && !_byId.ContainsKey(t.Id))
                        _byId[t.Id] = t;
                }
            }
        }
    }

    /// <summary>Returns listings with <c>InventoryType == "resale"</c> or <c>TotalPrice &lt;= 0</c>
    /// removed and <see cref="TicketMasterListing.DisplaySeat"/> assigned per Section+Row group.
    /// <paramref name="previousById"/> lets an unchanged listing keep its prior DisplaySeat.</summary>
    public List<TicketMasterListing> Build(IReadOnlyDictionary<string, TicketMasterListing>? previousById = null)
    {
        var valid = _byId.Values
            .Where(x => !string.Equals(x.InventoryType, "resale", StringComparison.OrdinalIgnoreCase) && x.TotalPrice > 0)
            .ToList();

        AssignDisplaySeats(valid, previousById, DisplaySeatStart, DisplaySeatStep);
        return valid;
    }

    public static void AssignDisplaySeats(
        List<TicketMasterListing> listings,
        IReadOnlyDictionary<string, TicketMasterListing>? oldById,
        int start,
        int step)
    {
        foreach (var group in listings.GroupBy(x => new { x.Section, x.Row }))
        {
            var current = start;
            var groupList = group.ToList();
            var fresh = new List<TicketMasterListing>(groupList.Count);

            foreach (var t in groupList)
            {
                if (oldById != null
                    && oldById.TryGetValue(t.Id, out var matched)
                    && matched.MaxQuantity == t.MaxQuantity
                    && matched.DisplaySeat > 0
                    && matched.DisplaySeat >= start)
                    t.DisplaySeat = matched.DisplaySeat;
                else
                    fresh.Add(t);
            }

            foreach (var t in fresh)
            {
                while (groupList.Any(x => x.DisplaySeat > 0 && x.DisplaySeat == current))
                    current += step;
                t.DisplaySeat = current;
                current += step;
            }
        }
    }

    /// <summary>Byte-for-byte the same hash the legacy TMCrawler / Broadway copies use.</summary>
    public static int GetDeterministicHashCode(string str)
    {
        unchecked
        {
            int hash1 = (5381 << 16) + 5381;
            int hash2 = hash1;
            for (int i = 0; i < str.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ str[i];
                if (i == str.Length - 1) break;
                hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
            }
            return hash1 + (hash2 * 1566083941);
        }
    }
}
