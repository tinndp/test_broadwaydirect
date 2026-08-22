using BroadwayDirect.Core.Discover;
using Xunit;

namespace BroadwayDirect.Tests;

public class DiscoverTests
{
    private const string SampleListingHtml = """
        <div class="show-card">
          <a href="https://broadwaydirect.com/show/hamilton/">Hamilton</a>
          <a href="https://broadwaydirect.com/show/aladdin/">Aladdin</a>
          <a href="https://broadwaydirect.com/show/hamilton/">Hamilton (duplicate link elsewhere on page)</a>
        </div>
        """;

    private const string SampleShowPageOnPlatform = """
        <a class="nav" href="https://broadwaydirect.com/show/hamilton/#tickets">Tickets</a>
        <a class="buy" href="https://tickets.broadwaydirect.com/tickets/series/426458">Buy Tickets</a>
        """;

    private const string SampleShowPageShopVariant = """
        <a class="buy" href="https://tickets.broadwaydirect.com/shop/tickets/series/860860">Buy Tickets</a>
        """;

    private const string SampleShowPageOffPlatform = """
        <a class="login" href="https://tickets.broadwaydirect.com/v2/account/login">Login</a>
        <a class="buy" href="https://www.walterkerrbroadway.com/events/hadestown/calendar/">Buy Tickets</a>
        """;

    [Fact]
    public void ShowLinkRegex_DedupesViaSet()
    {
        var slugs = ShowDiscovery.ShowLinkRegex.Matches(SampleListingHtml)
            .Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s).ToList();
        Assert.Equal(new[] { "aladdin", "hamilton" }, slugs);
    }

    [Fact]
    public void SeriesLinkRegex_MatchesTicketsPath()
    {
        var m = ShowDiscovery.SeriesLinkRegex.Match(SampleShowPageOnPlatform);
        Assert.True(m.Success);
        Assert.Equal("426458", m.Groups[1].Value);
    }

    [Fact]
    public void SeriesLinkRegex_MatchesShopTicketsPath()
    {
        var m = ShowDiscovery.SeriesLinkRegex.Match(SampleShowPageShopVariant);
        Assert.True(m.Success);
        Assert.Equal("860860", m.Groups[1].Value);
    }

    [Fact]
    public void SeriesLinkRegex_IgnoresLoginLinkAndExternalVendor()
    {
        var m = ShowDiscovery.SeriesLinkRegex.Match(SampleShowPageOffPlatform);
        Assert.False(m.Success);
    }
}
