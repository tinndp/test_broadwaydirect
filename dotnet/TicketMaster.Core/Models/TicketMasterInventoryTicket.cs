using MongoDB.Bson.Serialization.Attributes;

namespace TicketMaster.Core.Models;

/// <summary>
/// One persisted TicketMaster listing in the per-event staging collection
/// (<c>TicketMaster_Inventories_NEW_{eventId}</c>) - the Integration Template shape, mirroring
/// <see cref="StubHub.Core.Models.StubHubInventoryTicket"/> and the ETECH
/// <c>TicketMasterIntegrationTemplateSourceTicket</c>:
///
///  * <b>base</b> <c>IIntegrationTemplateSourceTicket</c> fields (Id / SourceEventId / Section /
///    Row / LowSeat / HighSeat / Quantity / Seating / Price / PublicNotes / PrivateNotes / Splits /
///    BrownerOwned) - the only fields the synchronizer / DataTable mapper depend on;
///  * <b>concrete</b> TicketMaster fields - every field the legacy <c>RowingListingInfo</c> carried,
///    kept so nothing is lost when moving off the old <c>TMEvent_{eventId}</c> collection.
///
/// Property names are the BSON element names (PascalCase, no camel-casing). <c>Id</c> maps to
/// <c>_id</c> and is the deterministic hash (same key as legacy TMCrawler:
/// <c>Section_Row_MaxQuantity_OfferGroupSeatMin_OfferName</c>).
/// </summary>
[BsonIgnoreExtraElements]
public sealed class TicketMasterInventoryTicket
{
    // --- IIntegrationTemplateSourceTicket (base) ---

    [BsonId]
    public string Id { get; set; } = "";

    public string SourceEventId { get; set; } = "";
    public string Section { get; set; } = "";
    public string Row { get; set; } = "";
    public int LowSeat { get; set; }
    public int HighSeat { get; set; }
    public int Quantity { get; set; }
    public string Seating { get; set; } = "";
    /// <summary>All-in price the buyer pays (TicketMaster <c>totalPrice</c>).</summary>
    public double Price { get; set; }
    public string? PublicNotes { get; set; }
    public string? PrivateNotes { get; set; }
    /// <summary>Sellable quantities as a CSV (e.g. "1,2,3,4,5,6").</summary>
    public string? Splits { get; set; }
    public bool? BrownerOwned { get; set; }

    // --- TicketMaster concrete (carried over from RowingListingInfo) ---

    public string TMEventId { get; set; } = "";

    public string? OfferName { get; set; }
    public string? OfferType { get; set; }
    public string? InventoryType { get; set; }
    public string? OfferDescription { get; set; }

    public double ListPrice { get; set; }
    public double FaceValue { get; set; }
    public double TotalPrice { get; set; }
    public double NoChargesPrice { get; set; }
    public string? ChargeJson { get; set; }

    public int MaxQuantity { get; set; }

    public string? Attributes { get; set; }
    public string? OfferGroupSeats { get; set; }
    public int OfferGroupSeatMin { get; set; }
    public int OfferGroupSeatMax { get; set; }

    public string? DescriptionId { get; set; }
    /// <summary>JSON array of description strings (e.g. <c>["Balcony"]</c>).</summary>
    public string? Description { get; set; }
    /// <summary>JSON array of sellable quantities.</summary>
    public string? SellableQuantities { get; set; }

    public int DisplaySeat { get; set; }
}
