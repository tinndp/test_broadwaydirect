using MongoDB.Bson;
using MongoDB.Driver;
using BroadwayDirect.Core.Models;

namespace BroadwayDirect.Core.Storage;

/// <summary>
/// An ADDITIONAL (mirror) store on MongoDB - does NOT replace SQLite/CSV.
/// 1:1 port of broadwaydirect/mongo_storage.py. See README.md's "MongoDB vs SQL" section.
///
/// 2 kinds of data are written:
///   - raw_&lt;kind&gt; (e.g. raw_eventinventory, raw_events_by_month): the JSON
///     returned by the API verbatim, with no fields transformed/filtered.
///   - cleaned_events: a document-shaped copy of the grouped data (Event + PriceLevel + Listing).
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

        _db.GetCollection<BsonDocument>("raw_eventinventory")
            .Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("event_id"),
                new CreateIndexOptions { Unique = true }));
        _db.GetCollection<BsonDocument>("raw_events_by_month")
            .Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("series_id").Ascending("year").Ascending("month"),
                new CreateIndexOptions { Unique = true }));
        _db.GetCollection<BsonDocument>("cleaned_events")
            .Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("event_id"),
                new CreateIndexOptions { Unique = true }));
    }

    /// <summary>kind: "eventinventory" | "events_by_month". key: identifies the
    /// record (e.g. {"event_id": 123}). raw: the original JSON returned by the
    /// API, saved verbatim.</summary>
    public void SaveRaw(string kind, BsonDocument key, BsonDocument raw)
    {
        var coll = _db.GetCollection<BsonDocument>($"raw_{kind}");
        var doc = new BsonDocument(key);
        doc["raw"] = raw;
        doc["fetched_at"] = DateTime.UtcNow;
        coll.ReplaceOne(new BsonDocument(key), doc, new ReplaceOptions { IsUpsert = true });
    }

    public void SaveCleanedEvent(Event ev, IEnumerable<PriceLevel> priceLevels,
        IEnumerable<Listing> listings, string? seriesId = null)
    {
        var doc = new BsonDocument
        {
            ["event_id"] = ev.Id,
            ["series_id"] = !string.IsNullOrEmpty(seriesId) ? seriesId
                : (string.IsNullOrEmpty(ev.SeriesId) ? BsonNull.Value : ev.SeriesId),
            ["name"] = ev.Name,
            ["local_date"] = ev.LocalDate,
            ["availability_color"] = ev.AvailabilityColor,
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
            .ReplaceOne(new BsonDocument("event_id", ev.Id), doc, new ReplaceOptions { IsUpsert = true });
    }

    public void Close() { /* MongoClient in the .NET driver doesn't need manual disposal */ }
}
