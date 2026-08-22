using System.Text.Json;

namespace BroadwayDirect.Core.Grouping;

/// <summary>Section naming / ticket grouping rules, ported from python/config/section_rules.json + DEFAULT_RULES.</summary>
public class SectionRules
{
    public List<string> SidesCenterPrefixes { get; set; } = new() { "ORCH", "FMEZZ", "RMEZZ", "MEZZ" };
    public List<string> BoxPrefixes { get; set; } = new() { "BOX" };
    public int CenterSeatThreshold { get; set; } = 100;
    public string SidesSuffix { get; set; } = "SIDES";
    public string CenterSuffix { get; set; } = "CENTER";

    public static SectionRules Default() => new();

    /// <summary>Reads section_rules.json, only overwriting fields present in the file (matches load_rules()'s behavior).</summary>
    public static SectionRules Load(string? path = null)
    {
        var rules = Default();
        if (string.IsNullOrEmpty(path)) return rules;

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (root.TryGetProperty("sides_center_prefixes", out var scp))
            rules.SidesCenterPrefixes = scp.EnumerateArray().Select(e => e.GetString()!).ToList();
        if (root.TryGetProperty("box_prefixes", out var bp))
            rules.BoxPrefixes = bp.EnumerateArray().Select(e => e.GetString()!).ToList();
        if (root.TryGetProperty("center_seat_threshold", out var cst))
            rules.CenterSeatThreshold = cst.GetInt32();
        if (root.TryGetProperty("sides_suffix", out var ss))
            rules.SidesSuffix = ss.GetString()!;
        if (root.TryGetProperty("center_suffix", out var cs))
            rules.CenterSuffix = cs.GetString()!;

        return rules;
    }
}
