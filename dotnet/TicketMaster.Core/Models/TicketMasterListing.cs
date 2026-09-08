namespace TicketMaster.Core.Models;

/// <summary>
/// One grouped listing produced by <see cref="ListingBuilder"/> - the intermediate shape
/// (field-for-field the legacy TMCrawler's <c>RowingListingInfo</c>). It is NOT persisted directly
/// any more: <see cref="TicketMasterInventoryMapper"/> converts it to
/// <see cref="TicketMasterInventoryTicket"/> (the Integration Template staging document).
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
