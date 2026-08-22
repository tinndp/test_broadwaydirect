using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BroadwayDirect.Core.Discover;

// JSON property names kept as "slug"/"series_id"/"on_platform"/"off_platform"
// for backward compatibility with shows_master_list*.json files produced by
// the Python version.
public record DiscoveredShow(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("series_id")] string SeriesId);

public record DiscoverResult(
    [property: JsonPropertyName("on_platform")] List<DiscoveredShow> OnPlatform,
    [property: JsonPropertyName("off_platform")] List<string> OffPlatform);

/// <summary>
/// Finds every show on broadwaydirect.com that ACTUALLY sells tickets
/// through the Tixtrack system (tickets.broadwaydirect.com/tickets/series/{id}).
/// 1:1 port of python/broadwaydirect/discover.py - see that file's docstring for
/// details (many major shows point their "Buy Tickets" link to the venue's
/// own website; this module skips those and just lists them, rather than
/// guessing at how to crawl them). Only uses HttpClient + Regex, no browser
/// needed.
/// </summary>
public class ShowDiscovery
{
    private static readonly Regex ShowLinkRe = new(
        @"href=""https://broadwaydirect\.com/show/([a-z0-9-]+)/?""", RegexOptions.Compiled);

    private static readonly Regex SeriesLinkRe = new(
        @"tickets\.broadwaydirect\.com/(?:shop/)?tickets/series/(\d+)", RegexOptions.Compiled);

    public static Regex ShowLinkRegex => ShowLinkRe;
    public static Regex SeriesLinkRegex => SeriesLinkRe;

    private readonly HttpClient _http;

    public ShowDiscovery(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        }
    }

    public async Task<List<string>> GetShowSlugsAsync(CancellationToken ct = default)
    {
        var html = await _http.GetStringAsync("https://broadwaydirect.com/shows/", ct);
        return ShowLinkRe.Matches(html).Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s).ToList();
    }

    public async Task<string?> GetSeriesIdForSlugAsync(string slug, CancellationToken ct = default)
    {
        var html = await _http.GetStringAsync($"https://broadwaydirect.com/show/{slug}/", ct);
        var m = SeriesLinkRe.Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Returns the list of shows on Tixtrack (on_platform) and not (off_platform).</summary>
    public async Task<DiscoverResult> DiscoverAllAsync(double sleepSeconds = 0.3, CancellationToken ct = default)
    {
        var slugs = await GetShowSlugsAsync(ct);
        Console.Error.WriteLine($"Found {slugs.Count} shows on the /shows/ page.");

        var onPlatform = new List<DiscoveredShow>();
        var offPlatform = new List<string>();

        for (var i = 0; i < slugs.Count; i++)
        {
            var slug = slugs[i];
            Console.Error.WriteLine($"  [{i + 1}/{slugs.Count}] checking {slug} ...");
            string? seriesId;
            try
            {
                seriesId = await GetSeriesIdForSlugAsync(slug, ct);
            }
            catch (HttpRequestException e)
            {
                Console.Error.WriteLine($"    !! error loading show page {slug}: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(sleepSeconds), ct);
                continue;
            }

            if (seriesId != null)
                onPlatform.Add(new DiscoveredShow(slug, seriesId));
            else
                offPlatform.Add(slug);

            await Task.Delay(TimeSpan.FromSeconds(sleepSeconds), ct);
        }

        return new DiscoverResult(onPlatform, offPlatform);
    }
}
