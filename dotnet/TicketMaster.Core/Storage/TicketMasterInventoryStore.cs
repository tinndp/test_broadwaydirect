using MongoDB.Bson;
using MongoDB.Driver;
using TicketMaster.Core.Models;

namespace TicketMaster.Core.Storage;

/// <summary>
/// Persists TicketMaster listings the way ETECH.Application.MarkAutomation's crawler does (legacy
/// TMCrawler and the new <c>Rowing/TicketMaster/TicketMasterCrawlerBot</c>): one collection per
/// event <c>TMEvent_{eventId}</c>, <b>dropped and rewritten</b> on every crawl, one
/// <see cref="TicketMasterListing"/> document per grouped listing, index on <c>TMEventId</c>.
///
/// This is the ONLY place listing data lands, so a failure here is <b>fatal</b> to the request -
/// <c>TicketMaster.Api</c> returns 5xx, it does not report success with the data unsaved. Mirrors
/// <see cref="StubHub.Core.Storage.StubHubInventoryStore"/> (BroadwayDirect keeps its own shared
/// <c>MongoStore</c>; TicketMaster follows the StubHub / one-collection-per-event convention because
/// that is what the ETECH TicketMaster bot writes and what the TU sync reads).
///
/// Unlike StubHub there is no <c>_NEW_</c> suffix: the legacy TMCrawler writes
/// <c>TMEvent_{eventId}</c> directly and <c>SK4RowingSyncQueue</c> (Type <c>TicketMasterToTU</c>)
/// triggers the sync off that same collection.
/// </summary>
public sealed class TicketMasterInventoryStore
{
    public const string CollectionPrefix = "TMEvent_";

    private readonly IMongoDatabase _db;

    public TicketMasterInventoryStore(string uri = "mongodb://localhost:27017", string dbName = "broadwaydirect")
    {
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        var client = new MongoClient(settings);
        client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1));
        _db = client.GetDatabase(dbName);
    }

    /// <summary>Drop <c>TMEvent_{eventId}</c> and rewrite it from <paramref name="listings"/> (one
    /// document each). Throws on any write failure.</summary>
    public void SaveEventInventory(string eventId, IReadOnlyList<TicketMasterListing> listings)
    {
        var name = CollectionPrefix + eventId;
        _db.DropCollection(name);
        if (listings.Count == 0) return;

        var col = _db.GetCollection<TicketMasterListing>(name);
        col.Indexes.CreateOne(new CreateIndexModel<TicketMasterListing>(
            Builders<TicketMasterListing>.IndexKeys.Ascending(t => t.TMEventId)));
        col.InsertMany(listings);
    }

    public void Close() { /* MongoClient in the .NET driver needs no manual disposal */ }
}
