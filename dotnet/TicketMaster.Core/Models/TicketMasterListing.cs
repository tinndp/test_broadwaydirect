namespace TicketMaster.Core.Models;

/// <summary>
/// One grouped listing - the POC counterpart of the ETECH bot's <c>RowingListingInfo</c>
/// (persisted to Mongo <c>TMEvent_{eventId}</c>). Field set is intentionally identical so the
/// grouping logic can be validated here and reused there.
/// </summary>
public sealed class TicketMasterListing
{
    public string TMEventId { get; set; } = "";
    public string Id { get; set; } = "";

    public string Section { get; set; } = "";
    public string Row { get; set; } = "";

    public double ListPrice { get; set; }
    public double FaceValue { get; set; }
    public double TotalPrice { get; set; }
    public double NoChargesPrice { get; set; }

    public int MaxQuantity { get; set; }

    public string? InventoryType { get; set; }
    public string? OfferName { get; set; }
    public string? OfferType { get; set; }
    public string? OfferDescription { get; set; }

    public string? DescriptionId { get; set; }
    /// <summary>JSON array of description strings, or null.</summary>
    public string? Description { get; set; }

    /// <summary>JSON array of sellable quantities, or null.</summary>
    public string? SellableQuantities { get; set; }
    public string? ChargeJson { get; set; }

    public string? Attributes { get; set; }
    public string? OfferGroupSeats { get; set; }
    public int OfferGroupSeatMin { get; set; }
    public int OfferGroupSeatMax { get; set; }

    public int DisplaySeat { get; set; }
}
