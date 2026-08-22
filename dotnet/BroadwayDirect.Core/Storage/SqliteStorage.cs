using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using BroadwayDirect.Core.Models;

namespace BroadwayDirect.Core.Storage;

/// <summary>
/// Saves processed data (events, price levels, listings) to SQLite + exports CSV.
/// 1:1 port of python/broadwaydirect/storage.py - see python/README.md's "MongoDB vs SQL"
/// section for why SQLite (relational) is used as the source of truth instead of MongoDB.
/// </summary>
public static class SqliteStorage
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS events (
            event_id INTEGER PRIMARY KEY,
            local_date TEXT,
            availability_color TEXT,
            name TEXT,
            series_id TEXT
        );

        CREATE TABLE IF NOT EXISTS price_levels (
            event_id INTEGER,
            price_level_id INTEGER,
            display_name TEXT,
            zone TEXT,
            price REAL,
            display_price REAL,
            price_class TEXT,
            PRIMARY KEY (event_id, price_level_id)
        );

        CREATE TABLE IF NOT EXISTS listings (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id INTEGER,
            section_label TEXT,
            row TEXT,
            price_level_id INTEGER,
            seating_type TEXT,
            quantity INTEGER,
            seat_range TEXT,
            seat_keys TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_listings_event ON listings(event_id);
        CREATE INDEX IF NOT EXISTS idx_listings_section ON listings(event_id, section_label);
        """;

    public static SqliteConnection InitDb(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }
        MigrateAddMissingColumns(conn);
        return conn;
    }

    /// <summary>A DB created before the name/series_id columns existed won't
    /// have them automatically (CREATE TABLE IF NOT EXISTS doesn't alter an
    /// existing table) - add them if missing.</summary>
    private static void MigrateAddMissingColumns(SqliteConnection conn)
    {
        var cols = new HashSet<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(events)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) cols.Add(reader.GetString(1));
        }
        if (!cols.Contains("name"))
            Exec(conn, "ALTER TABLE events ADD COLUMN name TEXT");
        if (!cols.Contains("series_id"))
            Exec(conn, "ALTER TABLE events ADD COLUMN series_id TEXT");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static void SaveEvent(SqliteConnection conn, Event ev)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO events (event_id, local_date, availability_color, name, series_id)
            VALUES ($eventId, $localDate, $availabilityColor, $name, $seriesId)
            """;
        cmd.Parameters.AddWithValue("$eventId", ev.Id);
        cmd.Parameters.AddWithValue("$localDate", ev.LocalDate);
        cmd.Parameters.AddWithValue("$availabilityColor", ev.AvailabilityColor);
        cmd.Parameters.AddWithValue("$name", ev.Name);
        cmd.Parameters.AddWithValue("$seriesId", ev.SeriesId);
        cmd.ExecuteNonQuery();
    }

    public static void SavePriceLevels(SqliteConnection conn, long eventId, IEnumerable<PriceLevel> priceLevels)
    {
        foreach (var pl in priceLevels)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO price_levels
                (event_id, price_level_id, display_name, zone, price, display_price, price_class)
                VALUES ($eventId, $plid, $displayName, $zone, $price, $displayPrice, $priceClass)
                """;
            cmd.Parameters.AddWithValue("$eventId", eventId);
            cmd.Parameters.AddWithValue("$plid", pl.PriceLevelId);
            cmd.Parameters.AddWithValue("$displayName", pl.DisplayName);
            cmd.Parameters.AddWithValue("$zone", pl.Zone);
            cmd.Parameters.AddWithValue("$price", pl.Price);
            cmd.Parameters.AddWithValue("$displayPrice", pl.DisplayPrice);
            cmd.Parameters.AddWithValue("$priceClass", pl.PriceClass);
            cmd.ExecuteNonQuery();
        }
    }

    public static void SaveListings(SqliteConnection conn, long eventId, IEnumerable<Listing> listings)
    {
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM listings WHERE event_id = $eventId";
            del.Parameters.AddWithValue("$eventId", eventId);
            del.ExecuteNonQuery();
        }

        foreach (var lst in listings)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO listings
                (event_id, section_label, row, price_level_id, seating_type, quantity, seat_range, seat_keys)
                VALUES ($eventId, $sectionLabel, $row, $plid, $seatingType, $quantity, $seatRange, $seatKeys)
                """;
            cmd.Parameters.AddWithValue("$eventId", eventId);
            cmd.Parameters.AddWithValue("$sectionLabel", lst.SectionLabel);
            cmd.Parameters.AddWithValue("$row", lst.Row);
            cmd.Parameters.AddWithValue("$plid", lst.PriceLevelId);
            cmd.Parameters.AddWithValue("$seatingType", lst.SeatingType);
            cmd.Parameters.AddWithValue("$quantity", lst.Quantity);
            cmd.Parameters.AddWithValue("$seatRange", lst.SeatRangeLabel);
            cmd.Parameters.AddWithValue("$seatKeys", JsonSerializer.Serialize(lst.SeatKeys));
            cmd.ExecuteNonQuery();
        }
    }

    public static List<PriceLevel> ParsePriceLevelsFromInventory(JsonElement inventory)
    {
        var result = new List<PriceLevel>();
        if (!inventory.TryGetProperty("priceMaps", out var priceMaps) || priceMaps.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var pm in priceMaps.EnumerateArray())
        {
            result.Add(new PriceLevel
            {
                PriceLevelId = pm.TryGetProperty("priceLevelId", out var id) ? id.GetInt64() : 0,
                DisplayName = pm.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                Zone = pm.TryGetProperty("zone", out var z) ? z.GetString() ?? "" : "",
                Price = pm.TryGetProperty("price", out var p) ? p.GetDouble() : 0.0,
                DisplayPrice = pm.TryGetProperty("displayPrice", out var dp) ? dp.GetDouble() : 0.0,
                PriceClass = pm.TryGetProperty("class", out var pc) ? pc.GetString() ?? "" : "",
            });
        }
        return result;
    }

    public static void ExportListingsCsv(SqliteConnection conn, string outPath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT l.event_id, e.name, e.series_id, e.local_date, l.section_label, l.row,
                   l.price_level_id, p.display_price, l.seating_type, l.quantity, l.seat_range, l.seat_keys
            FROM listings l
            JOIN events e ON e.event_id = l.event_id
            LEFT JOIN price_levels p ON p.event_id = l.event_id AND p.price_level_id = l.price_level_id
            ORDER BY e.name, l.event_id, l.section_label, l.row, l.price_level_id
            """;

        using var writer = new StreamWriter(outPath, false, new UTF8Encoding(true));
        writer.WriteLine(CsvRow(new[]
        {
            "event_id", "show_name", "series_id", "local_date", "section_label", "row",
            "price_level_id", "display_price", "seating_type", "quantity", "seat_range",
            "seat_keys", "show_url",
        }));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var seriesId = reader.IsDBNull(2) ? "" : reader.GetValue(2).ToString() ?? "";
            var showUrl = seriesId.Length > 0
                ? $"https://tickets.broadwaydirect.com/tickets/series/{seriesId}"
                : "";

            var values = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
                values.Add(reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
            values.Add(showUrl);

            writer.WriteLine(CsvRow(values));
        }

        Console.WriteLine($"Exported: {outPath}");
    }

    private static string CsvRow(IEnumerable<string> values) =>
        string.Join(",", values.Select(EscapeCsv));

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
