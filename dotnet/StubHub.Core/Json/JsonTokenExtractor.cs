using System.Text.Json;
using System.Text.RegularExpressions;

namespace StubHub.Core.Json;

/// <summary>
/// Pulls the embedded JSON out of StubHub's server-rendered event HTML.
/// 1:1 port of python/stubhub/extract.py - see that file's docstring for the
/// full rationale.
///
/// StubHub exposes no listing API; every event page ships its full React
/// state inline. <see cref="ExtractJsonToken"/> is a bracket counter that
/// walks a balanced [ ... ] / { ... } from a marker, ignoring braces inside
/// strings - a naive regex / JsonDocument-on-a-slice breaks on nested
/// objects inside "items" (the exact bug called out in the extraction
/// report). StubHubClient runs the equivalent logic in-page for speed; this
/// is the pure copy used by tests and reprocessing.
/// </summary>
public static class JsonTokenExtractor
{
    /// <summary>Find <paramref name="marker"/> in <paramref name="text"/>, then parse the
    /// balanced JSON value that begins at the first '[' or '{' at/after the end of the
    /// marker. Brackets/braces inside double-quoted strings (with '\' escaping) are
    /// ignored. Returns null if the marker isn't found or the slice doesn't parse.</summary>
    public static JsonElement? ExtractJsonToken(string text, string marker, int startFrom = 0)
    {
        var m = text.IndexOf(marker, startFrom, StringComparison.Ordinal);
        if (m < 0) return null;

        var i = m + marker.Length;
        // skip whitespace / a leading ':' between the marker and the value
        while (i < text.Length && text[i] is ' ' or '\t' or '\r' or '\n' or ':') i++;
        if (i >= text.Length || (text[i] != '[' && text[i] != '{')) return null;

        var start = i;
        int depthSq = 0, depthCu = 0;
        bool inStr = false, escape = false;
        while (i < text.Length)
        {
            var ch = text[i];
            if (escape) escape = false;
            else if (ch == '\\' && inStr) escape = true;
            else if (ch == '"') inStr = !inStr;
            else if (!inStr)
            {
                if (ch == '[') depthSq++;
                else if (ch == '{') depthCu++;
                else if (ch == ']') depthSq--;
                else if (ch == '}') depthCu--;
                if (depthSq == 0 && depthCu == 0) { i++; break; }
            }
            i++;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..i]);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Return the <c>grid.items</c> array embedded in an event-page SSR
    /// response. Empty list if not present (e.g. a 403 challenge page or an empty
    /// section).</summary>
    public static List<JsonElement> ExtractGridItems(string html)
    {
        var items = ExtractJsonToken(html, "\"grid\":{\"items\":");
        if (items is null)
        {
            // some responses put other keys before "items" inside "grid"
            var g = html.IndexOf("\"grid\":", StringComparison.Ordinal);
            if (g >= 0) items = ExtractJsonToken(html, "\"items\":", g);
        }
        return AsArray(items);
    }

    /// <summary>Parse the schema.org SportsEvent / Event JSON-LD block (event name,
    /// startDate, location). Null if absent or unparseable.</summary>
    public static JsonElement? ExtractSportsEvent(string html)
    {
        foreach (Match m in Regex.Matches(
                     html, "<script[^>]+type=\"application/ld\\+json\"[^>]*>(.*?)</script>",
                     RegexOptions.Singleline))
        {
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value.Trim());
                root = doc.RootElement.Clone();
            }
            catch (JsonException) { continue; }

            if (root.ValueKind == JsonValueKind.Object && IsEvent(root)) return root;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in root.EnumerateArray())
                    if (d.ValueKind == JsonValueKind.Object && IsEvent(d))
                        return d.Clone();
            }
        }
        return null;

        static bool IsEvent(JsonElement d) =>
            d.TryGetProperty("@type", out var t) && t.ValueKind == JsonValueKind.String &&
            t.GetString() is "SportsEvent" or "Event";
    }

    /// <summary><c>https://www.stubhub.com/&lt;slug&gt;/event/159257698/?...</c> -&gt; "159257698".</summary>
    public static string? EventIdFromUrl(string? url)
    {
        var m = Regex.Match(url ?? "", "/event/(\\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<JsonElement> AsArray(JsonElement? v) =>
        v is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Select(e => e.Clone()).ToList()
            : new List<JsonElement>();
}
