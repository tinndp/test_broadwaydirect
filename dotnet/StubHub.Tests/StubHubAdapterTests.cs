using System.Text.Json;
using StubHub.Core;

namespace StubHub.Tests;

/// <summary>1:1 port of python/stubhub/tests/test_adapter.py.</summary>
public class StubHubAdapterTests
{
    private static JsonElement Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample_event_raw.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void PriceLevels_OnePerTicketClass()
    {
        var pls = StubHubAdapter.ParsePriceLevels(Load());
        Assert.Equal(4, pls.Count);
        var byId = pls.ToDictionary(pl => pl.PriceLevelId);

        // price comes from ticketClassPopupData.rawMinPrice
        Assert.Equal(118.87, byId[3875].Price);
        Assert.Equal("Infield Box Value", byId[3875].DisplayName);
        Assert.Equal("Infield Box Value", byId[3875].Zone);
        Assert.Equal("teal", byId[3875].PriceClass);
        Assert.Equal(57.37, byId[1957].Price);
        // class with no popup entry and no min: falls back to the min raw price
        // seen on its own collected listings, else 0.0
        Assert.Equal(0.0, byId[3907].Price);
    }

    [Fact]
    public void Listings_CountAndSharedShape()
    {
        var ls = StubHubAdapter.NormalizeListings(Load());
        Assert.Equal(3, ls.Count);

        var first = ls[0];
        Assert.Equal("34FD", first.SectionLabel);
        Assert.Equal("G", first.Row);
        Assert.Equal(3875, first.PriceLevelId);
        Assert.Equal("Consecutive", first.SeatingType);   // isSeatedTogether = true
        Assert.Equal(4, first.Quantity);                  // availableTickets, not SeatKeys.Count
        Assert.Equal("", first.SeatRange);                // seatFrom/seatTo empty
        Assert.Empty(first.SeatKeys);
        Assert.Equal("none", first.SeatDetailLevel);
        // raw_price / currency are the documented StubHub-only superset
        Assert.Equal(49.97, first.RawPrice);
        Assert.Equal("USD", first.Currency);
    }

    [Fact]
    public void Listings_SeatDetailExpandsKeys()
    {
        var second = StubHubAdapter.NormalizeListings(Load())[1];
        Assert.Equal("1-3", second.SeatRange);
        Assert.Equal(new[] { "18RS-GG-1", "18RS-GG-2", "18RS-GG-3" }, second.SeatKeys);
        Assert.Equal("exact", second.SeatDetailLevel);    // hasSeatDetails = true

        var third = StubHubAdapter.NormalizeListings(Load())[2];
        Assert.Equal("Piggyback", third.SeatingType);     // isSeatedTogether = false
    }

    // live shape: hasSeatDetails=false, seatFrom/seatTo consistent with the count
    private static StubHub.Core.Models.StubHubListing One(
        bool hasSeatDetails = false, string seatFrom = "7", string seatTo = "11",
        int availableTickets = 5, string extra = "")
    {
        var json = $$"""
        { "items": [ {
            "sectionMapName": "163LG", "row": "P", "ticketClass": 3927,
            "hasSeatDetails": {{(hasSeatDetails ? "true" : "false")}},
            "seatFrom": "{{seatFrom}}", "seatTo": "{{seatTo}}",
            "availableTickets": {{availableTickets}}, "isSeatedTogether": true,
            "rawPrice": 72.01, "listingCurrencyCode": "USD"{{extra}}
        } ] }
        """;
        using var doc = JsonDocument.Parse(json);
        return StubHubAdapter.NormalizeListings(doc.RootElement.Clone())[0];
    }

    [Fact]
    public void DeclaredRange_KeptButNoSeatKeys()
    {
        var l = One();
        Assert.Equal("7-11", l.SeatRange);
        Assert.Equal("declared", l.SeatDetailLevel);
        Assert.Empty(l.SeatKeys);
        Assert.Equal(5, l.Quantity);
    }

    [Fact]
    public void SeatSpan_RejectsMismatchZoneHiddenAndJunk()
    {
        Assert.Equal("none", One(seatTo: "50").SeatDetailLevel);                 // width != qty
        Assert.Equal("none", One(extra: ", \"isZoneTicketClass\": true").SeatDetailLevel);
        Assert.Equal("none", One(extra: ", \"hideSeatAndRowInfo\": true").SeatDetailLevel);
        Assert.Equal("", One(seatFrom: "AA", seatTo: "EE").SeatRange);
        Assert.Equal("", One(seatFrom: "", seatTo: "").SeatRange);
    }

    [Fact]
    public void ExactSingleSeat()
    {
        var l = One(hasSeatDetails: true, seatTo: "7", availableTickets: 1);
        Assert.Equal("7", l.SeatRange);
        Assert.Equal(new[] { "163LG-P-7" }, l.SeatKeys);
        Assert.Equal("exact", l.SeatDetailLevel);
    }

    [Fact]
    public void BuildEvent_UsesJsonLdStartDate()
    {
        var ev = StubHubAdapter.BuildEvent(Load());
        Assert.Equal(159257698L, ev.Id);
        Assert.Equal("2026-09-04T19:10:00", ev.LocalDate);
        Assert.Equal("Washington Nationals at Los Angeles Dodgers", ev.Name);
        Assert.Equal("", ev.SeriesId);
    }

    [Fact]
    public void IsoFromFormatted_ParsesStubHubDateString()
    {
        Assert.Equal("2026-09-04T19:10:00", StubHubAdapter.IsoFromFormatted("Fri Sep 04 2026 7:10 PM"));
        Assert.Equal("", StubHubAdapter.IsoFromFormatted("not a date"));
        Assert.Equal("", StubHubAdapter.IsoFromFormatted(null));
    }
}
