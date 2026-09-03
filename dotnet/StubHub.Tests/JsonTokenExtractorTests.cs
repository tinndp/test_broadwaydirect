using System.Text.Json;
using StubHub.Core.Json;

namespace StubHub.Tests;

/// <summary>1:1 port of python/stubhub/tests/test_extract.py.</summary>
public class JsonTokenExtractorTests
{
    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ExtractJsonToken_ArrayAndObject()
    {
        var arr = JsonTokenExtractor.ExtractJsonToken("x = \"items\": [1, 2, 3] end", "\"items\":");
        Assert.NotNull(arr);
        Assert.Equal(new[] { 1, 2, 3 }, arr!.Value.EnumerateArray().Select(e => e.GetInt32()));

        var obj = JsonTokenExtractor.ExtractJsonToken("\"grid\": {\"a\": 1, \"b\": [2]} tail", "\"grid\":");
        Assert.NotNull(obj);
        Assert.Equal(1, obj!.Value.GetProperty("a").GetInt32());
        Assert.Equal(new[] { 2 }, obj.Value.GetProperty("b").EnumerateArray().Select(e => e.GetInt32()));

        Assert.Null(JsonTokenExtractor.ExtractJsonToken("no marker here", "\"items\":"));
    }

    [Fact]
    public void ExtractJsonToken_IgnoresBracketsInsideStrings()
    {
        // a ']' inside a string value must not end the array early
        const string text = "\"items\":[{\"note\":\"aisle ] seat\",\"x\":1},{\"y\":2}]";
        var v = JsonTokenExtractor.ExtractJsonToken(text, "\"items\":");
        Assert.NotNull(v);
        var items = v!.Value.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("aisle ] seat", items[0].GetProperty("note").GetString());
        Assert.Equal(1, items[0].GetProperty("x").GetInt32());
        Assert.Equal(2, items[1].GetProperty("y").GetInt32());
    }

    [Fact]
    public void ExtractGridItems_HandlesDeepNesting()
    {
        // the report's bug: a naive counter exits early on nested {} inside items
        var items = JsonTokenExtractor.ExtractGridItems(ReadFixture("sample_section_ssr.html"));
        Assert.Equal(2, items.Count);
        Assert.Equal(13909934699L, items[0].GetProperty("id").GetInt64());
        var nestedB = items[0].GetProperty("inventoryListingScore").GetProperty("nested").GetProperty("b");
        Assert.Equal("[1,2,{\"c\":3}]", nestedB.GetRawText());
        Assert.Equal("1", items[1].GetProperty("seatFrom").GetString());
        Assert.True(items[1].GetProperty("hasSeatDetails").GetBoolean());
    }

    [Fact]
    public void ExtractGridItems_EmptyOnChallengePage()
    {
        Assert.Empty(JsonTokenExtractor.ExtractGridItems("<html>Please enable JS</html>"));
    }

    [Fact]
    public void EventIdFromUrl()
    {
        Assert.Equal("159257698", JsonTokenExtractor.EventIdFromUrl(
            "https://www.stubhub.com/los-angeles-dodgers-los-angeles-tickets-9-4-2026/event/159257698/?x=1"));
        Assert.Null(JsonTokenExtractor.EventIdFromUrl("https://www.stubhub.com/category/mlb/"));
    }
}
