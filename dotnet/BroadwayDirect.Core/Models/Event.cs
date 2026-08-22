namespace BroadwayDirect.Core.Models;

public class Event
{
    public long Id { get; init; }
    public string LocalDate { get; init; } = "";
    public string AvailabilityColor { get; init; } = "";
    public string Name { get; init; } = "";
    public string SeriesId { get; init; } = "";
}
