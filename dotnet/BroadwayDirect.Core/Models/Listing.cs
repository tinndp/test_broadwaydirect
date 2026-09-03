namespace BroadwayDirect.Core.Models;

/// <summary>
/// A group of contiguous seats, already labeled per the SIDES/CENTER rules,
/// ready to feed into the inventory-management step (not automatically pushed anywhere).
/// </summary>
public class Listing : ICleanedListing
{
    public string SectionLabel { get; init; } = "";
    public string Row { get; init; } = "";
    public long PriceLevelId { get; init; }
    public List<string> SeatKeys { get; init; } = new();
    public List<int> SeatNums { get; init; } = new();
    public string SeatingType { get; init; } = "Consecutive"; // "Consecutive" or "Odd/Even"

    public int Quantity => SeatKeys.Count;

    public string SeatRangeLabel
    {
        get
        {
            if (SeatNums.Count == 0) return "";
            if (SeatNums.Count == 1) return SeatNums[0].ToString();
            return $"{SeatNums[0]}-{SeatNums[^1]}";
        }
    }
}
