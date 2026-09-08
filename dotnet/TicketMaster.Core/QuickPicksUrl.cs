namespace TicketMaster.Core;

/// <summary>
/// Order-preserving query string, split/joined raw (no re-encoding) - so a URL rebuilt from a
/// captured <c>quickpicks</c> request is byte-identical to what Ticketmaster's own JS sends apart
/// from the values we deliberately override (<c>offset</c>, <c>includeResale</c>). Same helper the
/// ETECH bot's fetch client uses.
/// </summary>
public sealed class QuickPicksUrl
{
    private readonly List<KeyValuePair<string, string>> _pairs = new();

    public string PathBeforeQuery { get; private set; } = "";

    public static QuickPicksUrl Parse(string fullUrl)
    {
        var qs = new QuickPicksUrl();
        var i = fullUrl.IndexOf('?');
        if (i < 0)
        {
            qs.PathBeforeQuery = fullUrl;
            return qs;
        }
        qs.PathBeforeQuery = fullUrl[..i];
        foreach (var part in fullUrl[(i + 1)..].Split('&'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            if (eq < 0) qs._pairs.Add(new(part, ""));
            else qs._pairs.Add(new(part[..eq], part[(eq + 1)..]));
        }
        return qs;
    }

    public bool Has(string key) => _pairs.Any(p => p.Key == key);
    public string? Get(string key) => _pairs.FirstOrDefault(p => p.Key == key).Value;

    public int GetInt(string key, int fallback)
    {
        var v = Get(key);
        if (string.IsNullOrEmpty(v)) return fallback;
        var digits = new string(v.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : fallback;
    }

    public void Set(string key, string value)
    {
        for (var i = 0; i < _pairs.Count; i++)
        {
            if (_pairs[i].Key == key) { _pairs[i] = new(key, value); return; }
        }
        _pairs.Add(new(key, value));
    }

    public QuickPicksUrl Clone()
    {
        var q = new QuickPicksUrl { PathBeforeQuery = PathBeforeQuery };
        q._pairs.AddRange(_pairs);
        return q;
    }

    public string ToQueryString() => string.Join("&", _pairs.Select(p => $"{p.Key}={p.Value}"));

    public string BuildUrl() => $"{PathBeforeQuery}?{ToQueryString()}";
}
