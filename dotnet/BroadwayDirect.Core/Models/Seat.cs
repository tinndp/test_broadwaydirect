namespace BroadwayDirect.Core.Models;

public class Seat
{
    public string Key { get; init; } = "";
    public long PriceLevelId { get; init; }
    public string Section { get; init; } = "";
    public string Row { get; init; } = "";
    public int SeatNum { get; init; }
    public string AdaType { get; init; } = "None";
    public bool IsReserved { get; init; }
    public bool IsKill { get; init; }
}
