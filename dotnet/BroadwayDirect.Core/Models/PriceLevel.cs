namespace BroadwayDirect.Core.Models;

public class PriceLevel
{
    public long PriceLevelId { get; init; }
    public string DisplayName { get; init; } = "";
    public string Zone { get; init; } = "";
    public double Price { get; init; }
    public double DisplayPrice { get; init; }
    public string PriceClass { get; init; } = "";
}
