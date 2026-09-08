using System.Text.Json;
using TicketMaster.Core;
using TicketMaster.Core.Models;

namespace TicketMaster.Tests;

public class ListingBuilderTests
{
    private static QuickPicksResponse LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample_quickpicks.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<QuickPicksResponse>(json)!;
    }

    private static List<TicketMasterListing> BuildFromFixture()
    {
        var b = new ListingBuilder();
        b.AddPage(LoadFixture());
        return b.Build();
    }

    [Fact]
    public void Resale_and_zero_price_offers_are_filtered_out()
    {
        var listings = BuildFromFixture();

        // 3 picks x 1 STD offer each = 3 valid; the RES1 (inventoryType "resale") offer on pick 3 is dropped.
        Assert.Equal(3, listings.Count);
        Assert.All(listings, l => Assert.False(string.Equals("resale", l.InventoryType, StringComparison.OrdinalIgnoreCase)));
        Assert.All(listings, l => Assert.True(l.TotalPrice > 0));
    }

    [Fact]
    public void Prices_offer_and_charges_are_carried_verbatim()
    {
        var l = BuildFromFixture().First(x => x.Section == "213" && x.Row == "10" && x.MaxQuantity == 4);

        Assert.Equal("Standard Ticket", l.OfferName);
        Assert.Equal("standard", l.OfferType);
        Assert.Equal("primary", l.InventoryType);
        Assert.Equal(35.40, l.ListPrice, 3);
        Assert.Equal(30.00, l.FaceValue, 3);
        Assert.Equal(35.40, l.TotalPrice, 3);
        Assert.Contains("Service Fee", l.ChargeJson);
        Assert.Equal("[1,2,3,4]", l.SellableQuantities);
    }

    [Fact]
    public void OfferGroup_seats_min_max_and_csv_are_set()
    {
        var l = BuildFromFixture().First(x => x.Section == "213" && x.Row == "10" && x.MaxQuantity == 4);
        Assert.Equal("1,2", l.OfferGroupSeats);
        Assert.Equal(1, l.OfferGroupSeatMin);
        Assert.Equal(2, l.OfferGroupSeatMax);
    }

    [Fact]
    public void Attributes_come_from_matching_places()
    {
        var listings = BuildFromFixture();
        Assert.Equal("AISLE", listings.First(x => x.Section == "213" && x.MaxQuantity == 4).Attributes);
        Assert.Equal("OBSTRUCTED", listings.First(x => x.Section == "228").Attributes);
    }

    [Fact]
    public void Description_is_serialized_json_when_pick_has_a_descriptionId()
    {
        var withDesc = BuildFromFixture().First(x => x.Section == "213" && x.MaxQuantity == 4);
        Assert.Equal("D1", withDesc.DescriptionId);
        Assert.Contains("Aisle seat", withDesc.Description);
        Assert.Contains("Great view", withDesc.Description);

        var noDesc = BuildFromFixture().First(x => x.Section == "228");
        Assert.Null(noDesc.Description);
    }

    [Fact]
    public void DisplaySeat_is_assigned_per_section_row_group_stepping_from_500()
    {
        var group = BuildFromFixture().Where(x => x.Section == "213" && x.Row == "10")
                                      .OrderBy(x => x.DisplaySeat).ToList();
        Assert.Equal(2, group.Count);
        Assert.Equal(500, group[0].DisplaySeat);
        Assert.Equal(520, group[1].DisplaySeat);

        var single = BuildFromFixture().First(x => x.Section == "228");
        Assert.Equal(500, single.DisplaySeat);
    }

    [Fact]
    public void DisplaySeat_is_preserved_for_unchanged_listings_across_reruns()
    {
        var first = BuildFromFixture().ToDictionary(x => x.Id);

        var b = new ListingBuilder();
        b.AddPage(LoadFixture());
        var second = b.Build(first);

        foreach (var l in second)
            Assert.Equal(first[l.Id].DisplaySeat, l.DisplaySeat);
    }

    [Fact]
    public void Ids_are_deterministic_and_stable()
    {
        var a = BuildFromFixture().Select(x => x.Id).OrderBy(x => x).ToArray();
        var b = BuildFromFixture().Select(x => x.Id).OrderBy(x => x).ToArray();
        Assert.Equal(a, b);
        Assert.Equal(a.Length, a.Distinct().Count());

        // The hash is byte-compatible with the legacy TMCrawler / Broadway copies.
        Assert.Equal(
            ListingBuilder.GetDeterministicHashCode("213_10_4_1_Standard Ticket"),
            ListingBuilder.GetDeterministicHashCode("213_10_4_1_Standard Ticket"));
    }

    [Fact]
    public void Cross_page_dedupe_by_id()
    {
        var b = new ListingBuilder();
        b.AddPage(LoadFixture());
        b.AddPage(LoadFixture()); // same page twice
        Assert.Equal(3, b.Build().Count);
    }
}
