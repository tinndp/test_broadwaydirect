using MongoDB.Bson;
using MongoDB.Driver;
using BroadwayDirect.Core.Models;

namespace BroadwayDirect.Core.Storage;

/// <summary>
/// An ADDITIONAL (mirror) store on MongoDB - does NOT replace SQLite/CSV.
/// 1:1 port of broadwaydirect/mongo_storage.py. See README.md's "MongoDB vs SQL" section.
///
/// 2 collections, shared across ticket sources (not one collection pair per
/// source) - every document carries a "source" field (e.g.
/// "tickets.broadwaydirect.com", taken from the fetched event's domain) so
/// that adding another ticketing site later means writing more documents
/// with a different "source" value, not creating more collections. Unique
/// key is (source, event_id) on both:
///   - raw_events: the eventinventory JSON returned by the API verbatim,
///     with NO fields transformed/filtered - used for cross-checking/
///     debugging or re-grouping under new rules later without calling the
///     API again.
///   - cleaned_events: a document-shaped copy of the grouped data
///     (PriceLevel + Listing), same content as the price_levels/listings
///     tables in SQLite, just structured differently (nested instead of
///     relational).
/// </summary>
public class MongoStore
{
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    public MongoStore(string uri = "mongodb://localhost:27017", string dbName = "broadwaydirect")
    {
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        _client = new MongoClient(settings);
        _client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1));
        _db = _client.GetDatabase(dbName);

        _db.GetCollection<BsonDocument>("raw_events")
            .Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("source").Ascending("event_id"),
                new CreateIndexOptions { Unique = true }));
        _db.GetCollection<BsonDocument>("cleaned_events")
            .Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("source").Ascending("event_id"),
                new CreateIndexOptions { Unique = true }));
    }

    /// <summary>source: identifies which ticket site/platform this came from
    /// (e.g. "tickets.broadwaydirect.com") - lets raw_events hold data from
    /// multiple sources without colliding. raw: the original eventinventory
    /// JSON returned by the API, saved verbatim with no modifications.</summary>
    public void SaveRawEvent(string source, string eventId, BsonDocument raw)
    {
        var key = new BsonDocument { ["source"] = source, ["event_id"] = eventId };
        var doc = new BsonDocument(key)
        {
            ["raw"] = raw,
            ["fetched_at"] = DateTime.UtcNow,
        };
        _db.GetCollection<BsonDocument>("raw_events")
            .ReplaceOne(key, doc, new ReplaceOptions { IsUpsert = true });
    }

    public void SaveCleanedEvent(string source, string eventId, IEnumerable<PriceLevel> priceLevels,
        IEnumerable<Listing> listings, string? seriesId = null)
    {
        var key = new BsonDocument { ["source"] = source, ["event_id"] = eventId };
        var doc = new BsonDocument(key)
        {
            ["series_id"] = string.IsNullOrEmpty(seriesId) ? BsonNull.Value : seriesId,
            ["price_levels"] = new BsonArray(priceLevels.Select(pl => new BsonDocument
            {
                ["price_level_id"] = pl.PriceLevelId,
                ["display_name"] = pl.DisplayName,
                ["zone"] = pl.Zone,
                ["price"] = pl.Price,
                ["display_price"] = pl.DisplayPrice,
                ["price_class"] = pl.PriceClass,
            })),
            ["listings"] = new BsonArray(listings.Select(l => new BsonDocument
            {
                ["section_label"] = l.SectionLabel,
                ["row"] = l.Row,
                ["price_level_id"] = l.PriceLevelId,
                ["seating_type"] = l.SeatingType,
                ["quantity"] = l.Quantity,
                ["seat_range"] = l.SeatRangeLabel,
                ["seat_keys"] = new BsonArray(l.SeatKeys),
            })),
            ["updated_at"] = DateTime.UtcNow,
        };

        _db.GetCollection<BsonDocument>("cleaned_events")
            .ReplaceOne(key, doc, new ReplaceOptions { IsUpsert = true });
    }

    public void Close() { /* MongoClient in the .NET driver doesn't need manual disposal */ }
}
