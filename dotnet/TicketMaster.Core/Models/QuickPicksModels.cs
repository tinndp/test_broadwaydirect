using System.Text.Json.Serialization;

namespace TicketMaster.Core.Models;

/// <summary>
/// JSON contract for Ticketmaster's <c>quickpicks</c> endpoint
/// (<c>offeradapter.ticketmaster.com/api/ismds/event/{eventId}/quickpicks</c>). Field set verified
/// live 2026-09 against event 05006389BE118DE0. One page per call; <see cref="Offset"/> /
/// <see cref="Total"/> drive pagination (<c>offset += limit</c> until <c>offset + limit &gt;= total</c>).
/// Only what <see cref="ListingBuilder"/> consumes is modelled.
/// </summary>
public sealed class QuickPicksResponse
{
    [JsonPropertyName("eventId")] public string EventId { get; set; } = "";
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }

    [JsonPropertyName("picks")] public List<Pick> Picks { get; set; } = new();
    [JsonPropertyName("places")] public List<Place> Places { get; set; } = new();
    [JsonPropertyName("_embedded")] public Embedded? Embedded { get; set; }
}

public sealed class Pick
{
    [JsonPropertyName("section")] public string Section { get; set; } = "";
    [JsonPropertyName("row")] public string Row { get; set; } = "";
    [JsonPropertyName("descriptionId")] public string? DescriptionId { get; set; }
    [JsonPropertyName("maxQuantity")] public int MaxQuantity { get; set; }
    [JsonPropertyName("offerGroups")] public List<OfferGroup> OfferGroups { get; set; } = new();
}

public sealed class OfferGroup
{
    [JsonPropertyName("offers")] public List<string> Offers { get; set; } = new();
    [JsonPropertyName("places")] public List<string> Places { get; set; } = new();
    [JsonPropertyName("seats")] public List<string> Seats { get; set; } = new();
}

public sealed class Offer
{
    [JsonPropertyName("offerId")] public string OfferId { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("inventoryType")] public string? InventoryType { get; set; }
    [JsonPropertyName("offerType")] public string? OfferType { get; set; }
    [JsonPropertyName("listPrice")] public decimal ListPrice { get; set; }
    [JsonPropertyName("faceValue")] public decimal FaceValue { get; set; }
    [JsonPropertyName("totalPrice")] public decimal TotalPrice { get; set; }
    [JsonPropertyName("noChargesPrice")] public decimal NoChargesPrice { get; set; }
    [JsonPropertyName("charges")] public List<Charge>? Charges { get; set; }
    [JsonPropertyName("sellableQuantities")] public List<int>? SellableQuantities { get; set; }
}

public sealed class Charge
{
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("amount")] public double Amount { get; set; }
}

public sealed class Embedded
{
    [JsonPropertyName("offer")] public List<Offer> Offer { get; set; } = new();
    [JsonPropertyName("description")] public List<TmDescription> Description { get; set; } = new();
}

public sealed class TmDescription
{
    [JsonPropertyName("descriptionId")] public string DescriptionId { get; set; } = "";
    [JsonPropertyName("descriptions")] public List<string> Descriptions { get; set; } = new();
}

public sealed class Place
{
    [JsonPropertyName("section")] public string? Section { get; set; }
    [JsonPropertyName("attributes")] public List<string>? Attributes { get; set; }
    [JsonPropertyName("places")] public List<string>? Places { get; set; }
}
