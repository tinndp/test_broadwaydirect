namespace BroadwayDirect.Core.Models;

/// <summary>
/// The 7 listing fields <see cref="Storage.MongoStore.SaveCleanedEvent"/> writes
/// to <c>cleaned_events</c>. <see cref="Listing"/> (BroadwayDirect, seat-grouped)
/// implements it, and so does StubHub.Core's own listing type - so both sources
/// mirror the exact same document shape without StubHub taking a dependency on
/// the seat-grouping model. Mirrors the Python side, where
/// <c>mongo_storage.save_cleaned_event</c> just duck-types <c>.quantity</c> /
/// <c>.seat_range_label</c> off whichever Listing it's handed.
/// </summary>
public interface ICleanedListing
{
    string SectionLabel { get; }
    string Row { get; }
    long PriceLevelId { get; }
    string SeatingType { get; }
    int Quantity { get; }
    string SeatRangeLabel { get; }
    List<string> SeatKeys { get; }
}
