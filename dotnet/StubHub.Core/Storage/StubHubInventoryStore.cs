using MongoDB.Bson;
using MongoDB.Driver;
using StubHub.Core.Models;

namespace StubHub.Core.Storage;

/// <summary>
/// Persists StubHub listings the way ETECH.Application.MarkAutomation's
/// StubHubCrawler does: one collection per event
/// (<c>StubHub_Inventories_NEW_{eventId}</c>), <b>dropped and rewritten</b> on
/// every crawl, one <see cref="StubHubInventoryTicket"/> document per listing.
///
/// This is the ONLY place StubHub listing data lands (there is no raw_events /
/// cleaned_events mirror for this path any more), so a failure here is
/// <b>fatal</b> to the request - StubHub.Api returns 5xx, it does not report
/// success with the data unsaved. BroadwayDirect still uses
/// <see cref="BroadwayDirect.Core.Storage.MongoStore"/>.
/// </summary>
public sealed class StubHubInventoryStore
{
    /// <summary>"_NEW_" holds this crawl's tickets only; the sibling
    /// <c>StubHub_Inventories_{eventId}</c> collection belongs to a separate sync
    /// process and is never touched here.</summary>
    public const string CollectionPrefix = "StubHub_Inventories_NEW_";

    private readonly IMongoDatabase _db;

    public StubHubInventoryStore(string uri = "mongodb://localhost:27017", string dbName = "broadwaydirect")
    {
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        var client = new MongoClient(settings);
        client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1));
        _db = client.GetDatabase(dbName);
    }

    /// <summary>Drop <c>StubHub_Inventories_NEW_{eventId}</c> and rewrite it from
    /// <paramref name="tickets"/> (one document each, keyed by
    /// <see cref="StubHubInventoryTicket.Id"/>). Throws on any write failure.</summary>
    public void SaveEventInventory(string eventId, IReadOnlyList<StubHubInventoryTicket> tickets)
    {
        var name = CollectionPrefix + eventId;
        _db.DropCollection(name);
        if (tickets.Count == 0) return;

        var col = _db.GetCollection<StubHubInventoryTicket>(name);
        col.Indexes.CreateOne(new CreateIndexModel<StubHubInventoryTicket>(
            Builders<StubHubInventoryTicket>.IndexKeys.Ascending(t => t.SourceEventId)));
        col.InsertMany(tickets);
    }

    public void Close() { /* MongoClient in the .NET driver needs no manual disposal */ }
}
