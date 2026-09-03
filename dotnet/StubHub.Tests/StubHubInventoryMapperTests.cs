using BroadwayDirect.Core.Models;
using StubHub.Core;
using StubHub.Core.Models;

namespace StubHub.Tests;

/// <summary>Covers the pure mapping in <see cref="StubHubInventoryMapper"/>
/// (id / seat-range / price-fallback / price-level join / dedupe). The Mongo
/// write itself (<see cref="StubHub.Core.Storage.StubHubInventoryStore"/>) needs
/// a live server and is not unit-tested here.</summary>
public class StubHubInventoryMapperTests
{
    private static readonly List<PriceLevel> PriceLevels = new()
    {
        new PriceLevel
        {
            PriceLevelId = 3875, DisplayName = "Infield Box Value", Zone = "Infield Box Value",
            Price = 118.87, DisplayPrice = 118.87, PriceClass = "teal",
        },
    };

    [Fact]
    public void Id_IsTheNativeListingId_WhenPresent()
    {
        var l = new StubHubListing { ListingId = "12733224978", SectionLabel = "34FD", PriceLevelId = 3875 };
        var t = StubHubInventoryMapper.Build("159257698", PriceLevels, new[] { l }).Single();

        Assert.Equal("12733224978", t.Id);
        Assert.Equal("12733224978", t.ListingId);
        Assert.Equal("159257698", t.SourceEventId);
    }

    [Fact]
    public void Id_FallsBackToDeterministicHash_WhenNoListingId()
    {
        StubHubListing Make() => new() { ListingId = "", SectionLabel = "ORCH", Row = "C", SeatRange = "5-7" };

        var a = StubHubInventoryMapper.Build("evt", PriceLevels, new[] { Make() }).Single().Id;
        var b = StubHubInventoryMapper.Build("evt", PriceLevels, new[] { Make() }).Single().Id;
        var other = StubHubInventoryMapper.Build("evt", PriceLevels,
            new[] { new StubHubListing { SectionLabel = "ORCH", Row = "D", SeatRange = "5-7" } }).Single().Id;

        Assert.False(string.IsNullOrEmpty(a));
        Assert.Equal(a, b);                       // stable across calls (not string.GetHashCode)
        Assert.NotEqual(a, other);                // varies with Section_Row_Low_High
        Assert.True(int.TryParse(a, out _));      // hash rendered as an int
    }

    [Theory]
    [InlineData("1-3", 1, 3)]
    [InlineData("5", 5, 5)]
    [InlineData("", 0, 0)]
    public void SeatRange_ParsesToLowHigh(string seatRange, int low, int high)
    {
        var l = new StubHubListing { ListingId = "x", SeatRange = seatRange };
        var t = StubHubInventoryMapper.Build("e", PriceLevels, new[] { l }).Single();

        Assert.Equal(low, t.LowSeat);
        Assert.Equal(high, t.HighSeat);
    }

    [Fact]
    public void Price_UsesRawPrice_ElseFallsBackToPriceLevel()
    {
        var withRaw = new StubHubListing { ListingId = "a", PriceLevelId = 3875, RawPrice = 49.97 };
        var noRaw = new StubHubListing { ListingId = "b", PriceLevelId = 3875, RawPrice = 0 };

        var docs = StubHubInventoryMapper.Build("e", PriceLevels, new[] { withRaw, noRaw })
            .ToDictionary(d => d.Id);

        Assert.Equal(49.97, docs["a"].Price);
        Assert.Equal(118.87, docs["b"].Price);   // price level min
    }

    [Fact]
    public void PriceLevelFields_AreDenormalizedOntoEachDocument()
    {
        var l = new StubHubListing { ListingId = "a", PriceLevelId = 3875 };
        var t = StubHubInventoryMapper.Build("e", PriceLevels, new[] { l }).Single();

        Assert.Equal("Infield Box Value", t.DisplayName);
        Assert.Equal("Infield Box Value", t.Zone);
        Assert.Equal(118.87, t.DisplayPrice);
        Assert.Equal("teal", t.PriceClass);
    }

    [Fact]
    public void UnknownPriceLevel_LeavesDenormalizedFieldsNull()
    {
        var l = new StubHubListing { ListingId = "a", PriceLevelId = 9999 };
        var t = StubHubInventoryMapper.Build("e", PriceLevels, new[] { l }).Single();

        Assert.Null(t.DisplayName);
        Assert.Null(t.Zone);
        Assert.Equal(0, t.DisplayPrice);
        Assert.Null(t.PriceClass);
        Assert.Equal(0, t.Price);
    }

    [Fact]
    public void DuplicateIds_AreCollapsed_LastWins()
    {
        var first = new StubHubListing { ListingId = "dup", Quantity = 2 };
        var second = new StubHubListing { ListingId = "dup", Quantity = 9 };

        var docs = StubHubInventoryMapper.Build("e", PriceLevels, new[] { first, second });

        Assert.Single(docs);
        Assert.Equal(9, docs[0].Quantity);
    }

    [Fact]
    public void SeatKeys_And_Superset_CarryThrough()
    {
        var l = new StubHubListing
        {
            ListingId = "a", SeatKeys = new List<string> { "18RS-GG-1", "18RS-GG-2" },
            RawPrice = 200.0, Currency = "USD", SeatingType = "Consecutive",
        };
        var t = StubHubInventoryMapper.Build("e", PriceLevels, new[] { l }).Single();

        Assert.Equal(new[] { "18RS-GG-1", "18RS-GG-2" }, t.SeatKeys);
        Assert.Equal(200.0, t.RawPrice);
        Assert.Equal("USD", t.Currency);
        Assert.Equal("Consecutive", t.Seating);
    }
}
