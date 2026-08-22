using System.Text.Json;
using BroadwayDirect.Core.Grouping;
using BroadwayDirect.Core.Models;
using Xunit;

namespace BroadwayDirect.Tests;

public class GroupingTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample_eventinventory.json");

    private static JsonElement LoadFixture()
    {
        var text = File.ReadAllText(FixturePath);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ParseSeatKey_Works()
    {
        Assert.Equal(("ORCH L", "C", 5), SeatGrouper.ParseSeatKey("ORCH L-C-5"));
        Assert.Equal(("MEZZ", "D", 103), SeatGrouper.ParseSeatKey("MEZZ-D-103"));
        Assert.Equal(("ORCH C", "C", 106), SeatGrouper.ParseSeatKey("ORCH C-C-106"));
    }

    [Fact]
    public void ClassifySection_Works()
    {
        var rules = SectionRules.Default();
        Assert.Equal(("SIDES", "Odd/Even"), SeatGrouper.ClassifySection("ORCH L", 5, rules));
        Assert.Equal(("CENTER", "Consecutive"), SeatGrouper.ClassifySection("ORCH C", 106, rules));
        Assert.Equal(((string?)null, "Consecutive"), SeatGrouper.ClassifySection("BOX A", 3, rules));
    }

    [Fact]
    public void SeatsFromInventory_ExcludesAdaAndReserved()
    {
        var inv = LoadFixture();
        var seats = SeatGrouper.SeatsFromInventory(inv);
        var keys = seats.Select(s => s.Key).ToHashSet();

        // 34 seats in the fixture, all isReserved=False/isKill=False so none are excluded at this step
        Assert.Equal(34, seats.Count);
        Assert.Contains("ORCH L-K-23", keys); // Companion - still present in seats_from_inventory
        Assert.Contains("ORCH L-K-25", keys); // Wheelchair
    }

    [Fact]
    public void GroupIntoListings_SidesStep2()
    {
        var inv = LoadFixture();
        var seats = SeatGrouper.SeatsFromInventory(inv);
        var listings = SeatGrouper.GroupIntoListings(seats, SectionRules.Default(), includeAda: false);

        // ORCH C-C-106,107,108 (contiguous, same price 86944, seat>100 -> CENTER step=1)
        // but 111,112 are 3 away from 108 -> split into 2 separate listings
        var centerC = listings.Where(l => l.SectionLabel == "ORCH C CENTER" && l.Row == "C")
            .Select(l => l.SeatRangeLabel).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "106-108", "111-112" }, centerC);

        // MEZZ-C-5,7 same price 86952, seat<=100 -> SIDES, step=2 -> grouped into 1 listing "5-7"
        var mezzSidesC = listings.Where(l => l.SectionLabel == "MEZZ SIDES" && l.Row == "C");
        Assert.Contains(mezzSidesC, l => l.SeatRangeLabel == "5-7");

        // MEZZ-C-106 (seat>100) must be in a separate CENTER group, not bleeding into SIDES
        var mezzCenterC = listings.Where(l => l.SectionLabel == "MEZZ CENTER" && l.Row == "C");
        Assert.Contains(mezzCenterC, l => l.SeatRangeLabel == "106");
    }

    [Fact]
    public void PriceCode_IsADelimiter()
    {
        var inv = LoadFixture();
        var seats = SeatGrouper.SeatsFromInventory(inv);
        var listings = SeatGrouper.GroupIntoListings(seats, SectionRules.Default());

        // ORCH L-K-17,19,21 price 86949 (step=2, contiguous) must be its own listing,
        // must NOT be grouped with seats of a different price even in the same row/section
        var rowK = listings.Where(l => l.Row == "K" && l.SectionLabel == "ORCH L SIDES");
        Assert.Contains(rowK, l => l.SeatRangeLabel == "17-21" && l.PriceLevelId == 86949);
    }

    [Fact]
    public void AdaSeats_ExcludedByDefault()
    {
        var inv = LoadFixture();
        var seats = SeatGrouper.SeatsFromInventory(inv);
        var listings = SeatGrouper.GroupIntoListings(seats, SectionRules.Default(), includeAda: false);
        var allKeys = listings.SelectMany(l => l.SeatKeys).ToHashSet();

        Assert.DoesNotContain("ORCH L-K-23", allKeys); // Companion
        Assert.DoesNotContain("ORCH L-K-25", allKeys); // Wheelchair
    }

    [Fact]
    public void AdaSeats_IncludedWhenRequested()
    {
        var inv = LoadFixture();
        var seats = SeatGrouper.SeatsFromInventory(inv);
        var listings = SeatGrouper.GroupIntoListings(seats, SectionRules.Default(), includeAda: true);
        var allKeys = listings.SelectMany(l => l.SeatKeys).ToHashSet();

        Assert.Contains("ORCH L-K-23", allKeys);
        Assert.Contains("ORCH L-K-25", allKeys);
    }

    [Fact]
    public void BoxSeats_HaveNoSuffix()
    {
        var seats = new List<Seat>
        {
            new() { Key = "BOX A-1-1", PriceLevelId = 1, Section = "BOX A", Row = "1", SeatNum = 1 },
            new() { Key = "BOX A-1-2", PriceLevelId = 1, Section = "BOX A", Row = "1", SeatNum = 2 },
        };
        var listings = SeatGrouper.GroupIntoListings(seats, SectionRules.Default());

        Assert.Single(listings);
        Assert.Equal("BOX A", listings[0].SectionLabel);
        Assert.Equal("1-2", listings[0].SeatRangeLabel);
    }
}
