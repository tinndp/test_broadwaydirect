using System.Text.Json;
using TicketMaster.Core;
using TicketMaster.Core.Models;

namespace TicketMaster.Tests;

public class TicketMasterInventoryMapperTests
{
    private static List<TicketMasterInventoryTicket> BuildTickets()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample_quickpicks.json");
        var page = JsonSerializer.Deserialize<QuickPicksResponse>(File.ReadAllText(path))!;
        var b = new ListingBuilder();
        b.AddPage(page);
        return TicketMasterInventoryMapper.Build("05006389BE118DE0", b.Build());
    }

    [Fact]
    public void Base_integration_template_fields_are_populated()
    {
        var t = BuildTickets().First(x => x.Section == "213" && x.MaxQuantity == 4);

        Assert.Equal("05006389BE118DE0", t.SourceEventId);
        Assert.Equal("213", t.Section);
        Assert.Equal("10", t.Row);
        Assert.Equal(1, t.LowSeat);
        Assert.Equal(2, t.HighSeat);
        Assert.Equal(2, t.Quantity);                 // 2 physical seats in the offer group
        Assert.Equal("Consecutive", t.Seating);
        Assert.Equal(35.40, t.Price, 3);             // all-in totalPrice
        Assert.Equal("Aisle seat; Great view", t.PublicNotes);
        Assert.Equal("1,2,3,4", t.Splits);           // sellable quantities as CSV
        Assert.Null(t.BrownerOwned);
    }

    [Fact]
    public void Concrete_ticketmaster_fields_are_carried_over_unchanged()
    {
        var t = BuildTickets().First(x => x.Section == "213" && x.MaxQuantity == 4);

        Assert.Equal("Standard Ticket", t.OfferName);
        Assert.Equal("standard", t.OfferType);
        Assert.Equal("primary", t.InventoryType);
        Assert.Equal(35.40, t.ListPrice, 3);
        Assert.Equal(30.00, t.FaceValue, 3);
        Assert.Equal(35.40, t.TotalPrice, 3);
        Assert.Contains("Service Fee", t.ChargeJson);
        Assert.Equal("[1,2,3,4]", t.SellableQuantities);
        Assert.Equal("1,2", t.OfferGroupSeats);
        Assert.Equal("AISLE", t.Attributes);
        Assert.Equal("D1", t.DescriptionId);
        Assert.Contains("Aisle seat", t.Description);
        Assert.Equal(500, t.DisplaySeat);
    }

    [Fact]
    public void Resale_filtered_and_ids_are_the_legacy_hash()
    {
        var tickets = BuildTickets();
        Assert.Equal(3, tickets.Count);                                   // RES1 dropped upstream
        Assert.All(tickets, t => Assert.False(string.IsNullOrEmpty(t.Id)));
        Assert.Equal(tickets.Count, tickets.Select(t => t.Id).Distinct().Count());

        // Id is the deterministic hash of Section_Row_MaxQuantity_OfferGroupSeatMin_OfferName
        var expected = ListingBuilder.GetDeterministicHashCode("213_10_4_1_Standard Ticket").ToString();
        Assert.Contains(tickets, t => t.Id == expected);
    }

    [Fact]
    public void Id_is_stable_across_reruns()
    {
        var a = BuildTickets().Select(t => t.Id).OrderBy(x => x).ToArray();
        var b = BuildTickets().Select(t => t.Id).OrderBy(x => x).ToArray();
        Assert.Equal(a, b);
    }
}
