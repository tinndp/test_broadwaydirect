using MongoDB.Bson;
using MongoDB.Driver;
using TicketMaster.Core.Models;

namespace TicketMaster.Core.Storage;

/// <summary>
/// Persists TicketMaster listings the Integration Template way (same as
/// <see cref="StubHub.Core.Storage.StubHubInventoryStore"/> and the ETECH
/// <c>TicketMasterCrawlerBot</c>): one staging collection per event
/// <c>TicketMaster_Inventories_NEW_{eventId}</c>, <b>dropped and rewritten</b> on every crawl, one
/// <see cref="TicketMasterInventoryTicket"/> document per grouped listing (Integration Template
/// shape), index on <c>SourceEventId</c>.
///
/// It is the ONLY place listing data lands, so a failure here is <b>fatal</b> to the request -
/// <c>TicketMaster.Api</c> returns 5xx, it does not report success with the data unsaved.
///
/// Collection naming matches <c>SettingConfiguration.GetIntegrationNewInventoryCollectionName(
/// DataSourceType.TicketMaster, sourceEventId)</c> in ETECH.Application.MarkAutomation
/// (<c>{prefix}_Inventories_NEW_{sourceEventId}</c>).
/// </summary>
public sealed class TicketMasterInventoryStore
{
    public const string CollectionPrefix = "TicketMaster_Inventories_NEW_";

    private readonly IMongoDatabase _db;

    public TicketMasterInventoryStore(string uri = "mongodb://localhost:27017", string dbName = "broadwaydirect")
    {
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        var client = new MongoClient(settings);
        client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1));
        _db = client.GetDatabase(dbName);
    }

    /// <summary>Drop <c>TicketMaster_Inventories_NEW_{eventId}</c> and rewrite it from
    /// <paramref name="tickets"/> (one document each, <c>_id</c> = <see cref="TicketMasterInventoryTicket.Id"/>).
    /// Throws on any write failure.</summary>
    public void SaveEventInventory(string eventId, IReadOnlyList<TicketMasterInventoryTicket> tickets)
    {
        var name = CollectionPrefix + eventId;
        _db.DropCollection(name);
        if (tickets.Count == 0) return;

        var col = _db.GetCollection<TicketMasterInventoryTicket>(name);
        col.Indexes.CreateOne(new CreateIndexModel<TicketMasterInventoryTicket>(
            Builders<TicketMasterInventoryTicket>.IndexKeys.Ascending(t => t.SourceEventId)));
        col.InsertMany(tickets);
    }

    public void Close() { /* MongoClient in the .NET driver needs no manual disposal */ }
}
