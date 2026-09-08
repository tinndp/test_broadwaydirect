using TicketMaster.Core.Models;

namespace TicketMaster.Core;

/// <summary>
/// Turns <see cref="ListingBuilder"/>'s grouped <see cref="TicketMasterListing"/> rows into the
/// per-listing <see cref="TicketMasterInventoryTicket"/> staging documents (Integration Template
/// shape). Mirrors <see cref="StubHub.Core.StubHubInventoryMapper"/> and the ETECH
/// <c>TicketMasterListingBuilder</c>: base <c>IIntegrationTemplateSourceTicket</c> fields are filled
/// from the TM offer/offerGroup, and every legacy <c>RowingListingInfo</c> field is carried on as a
/// concrete field so nothing is lost.
/// </summary>
public static class TicketMasterInventoryMapper
{
    public static List<TicketMasterInventoryTicket> Build(string eventId, IEnumerable<TicketMasterListing> listings)
    {
        var byId = new Dictionary<string, TicketMasterInventoryTicket>();

        foreach (var l in listings)
        {
            var seats = ParseSeats(l.OfferGroupSeats);

            var t = new TicketMasterInventoryTicket
            {
                // base
                Id = l.Id,                       // deterministic hash, already computed by ListingBuilder
                SourceEventId = eventId,
                Section = l.Section,
                Row = l.Row,
                LowSeat = l.OfferGroupSeatMin,
                HighSeat = l.OfferGroupSeatMax,
                Quantity = seats.Count > 0 ? seats.Count : l.MaxQuantity,
                Seating = seats.Count > 0 ? "Consecutive" : "GA",
                Price = l.TotalPrice,            // all-in price
                PublicNotes = DescriptionsToNote(l.Description),
                PrivateNotes = null,
                Splits = SellableToCsv(l.SellableQuantities),
                BrownerOwned = null,

                // concrete
                TMEventId = l.TMEventId,
                OfferName = l.OfferName,
                OfferType = l.OfferType,
                InventoryType = l.InventoryType,
                OfferDescription = l.OfferDescription,
                ListPrice = l.ListPrice,
                FaceValue = l.FaceValue,
                TotalPrice = l.TotalPrice,
                NoChargesPrice = l.NoChargesPrice,
                ChargeJson = l.ChargeJson,
                MaxQuantity = l.MaxQuantity,
                Attributes = l.Attributes,
                OfferGroupSeats = l.OfferGroupSeats,
                OfferGroupSeatMin = l.OfferGroupSeatMin,
                OfferGroupSeatMax = l.OfferGroupSeatMax,
                DescriptionId = l.DescriptionId,
                Description = l.Description,
                SellableQuantities = l.SellableQuantities,
                DisplaySeat = l.DisplaySeat,
            };

            byId[t.Id] = t;
        }

        return byId.Values.ToList();
    }

    private static List<int> ParseSeats(string? csv)
    {
        var result = new List<int>();
        if (string.IsNullOrEmpty(csv)) return result;
        foreach (var p in csv.Split(','))
            if (int.TryParse(p.Trim(), out var n)) result.Add(n);
        return result;
    }

    /// <summary>'["Balcony","Great view"]' -&gt; "Balcony; Great view".</summary>
    private static string? DescriptionsToNote(string? descriptionJson)
    {
        if (string.IsNullOrWhiteSpace(descriptionJson)) return null;
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(descriptionJson);
            return arr is { Count: > 0 } ? string.Join("; ", arr) : null;
        }
        catch { return null; }
    }

    /// <summary>'[1,2,3,4]' -&gt; "1,2,3,4".</summary>
    private static string? SellableToCsv(string? sellableJson)
    {
        if (string.IsNullOrWhiteSpace(sellableJson)) return null;
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<List<int>>(sellableJson);
            return arr is { Count: > 0 } ? string.Join(",", arr) : null;
        }
        catch { return null; }
    }
}
