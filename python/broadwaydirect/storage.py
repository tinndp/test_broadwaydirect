"""Saves processed data (events, price levels, listings) to SQLite + exports CSV/Excel.

For why SQLite (relational) instead of MongoDB, see README.md's "MongoDB vs
SQL" section. Summary: the data here has a fixed, well-defined structure
(Event/PriceLevel/Listing) and needs querying/joining/reporting (e.g. "total
tickets left by section, by date") - a better fit for a relational model
than a document store. SQLite is enough for the current scale (a few
hundred performances x a few thousand seats); if multiple processes/
machines need to write concurrently later, switching to Postgres needs
almost no changes to this same schema.
"""

import csv
import json
import sqlite3
from typing import Iterable

from .models import Event, PriceLevel, Listing

SCHEMA = """
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
    seat_keys TEXT   -- JSON array, ascending order
);

CREATE INDEX IF NOT EXISTS idx_listings_event ON listings(event_id);
CREATE INDEX IF NOT EXISTS idx_listings_section ON listings(event_id, section_label);
"""


def init_db(path: str) -> sqlite3.Connection:
    conn = sqlite3.connect(path)
    conn.executescript(SCHEMA)
    _migrate_add_missing_columns(conn)
    return conn


def _migrate_add_missing_columns(conn) -> None:
    """A DB created before the name/series_id columns existed won't have
    them automatically (CREATE TABLE IF NOT EXISTS doesn't alter an
    existing table) - add them if missing."""
    cols = {row[1] for row in conn.execute("PRAGMA table_info(events)")}
    if "name" not in cols:
        conn.execute("ALTER TABLE events ADD COLUMN name TEXT")
    if "series_id" not in cols:
        conn.execute("ALTER TABLE events ADD COLUMN series_id TEXT")


def save_event(conn, event: Event):
    conn.execute(
        "INSERT OR REPLACE INTO events (event_id, local_date, availability_color, name, series_id) "
        "VALUES (?, ?, ?, ?, ?)",
        (event.id, event.local_date, event.availability_color, event.name, event.series_id),
    )


def save_price_levels(conn, event_id: int, price_levels: Iterable[PriceLevel]):
    for pl in price_levels:
        conn.execute(
            "INSERT OR REPLACE INTO price_levels "
            "(event_id, price_level_id, display_name, zone, price, display_price, price_class) "
            "VALUES (?, ?, ?, ?, ?, ?, ?)",
            (event_id, pl.price_level_id, pl.display_name, pl.zone, pl.price,
             pl.display_price, pl.price_class),
        )


def save_listings(conn, event_id: int, listings: Iterable[Listing]):
    conn.execute("DELETE FROM listings WHERE event_id = ?", (event_id,))
    for lst in listings:
        conn.execute(
            "INSERT INTO listings "
            "(event_id, section_label, row, price_level_id, seating_type, quantity, seat_range, seat_keys) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            (event_id, lst.section_label, lst.row, lst.price_level_id, lst.seating_type,
             lst.quantity, lst.seat_range_label, json.dumps(lst.seat_keys)),
        )


def parse_price_levels_from_inventory(inventory: dict) -> list:
    out = []
    for pm in inventory.get("priceMaps", []) or []:
        out.append(PriceLevel(
            price_level_id=pm.get("priceLevelId"),
            display_name=pm.get("displayName", ""),
            zone=pm.get("zone", ""),
            price=pm.get("price", 0.0),
            display_price=pm.get("displayPrice", 0.0),
            price_class=pm.get("class", ""),
        ))
    return out


def export_listings_csv(conn, out_path: str):
    cur = conn.execute(
        "SELECT l.event_id, e.name, e.series_id, e.local_date, l.section_label, l.row, "
        "l.price_level_id, p.display_price, l.seating_type, l.quantity, l.seat_range, l.seat_keys "
        "FROM listings l "
        "JOIN events e ON e.event_id = l.event_id "
        "LEFT JOIN price_levels p ON p.event_id = l.event_id AND p.price_level_id = l.price_level_id "
        "ORDER BY e.name, l.event_id, l.section_label, l.row, l.price_level_id"
    )
    with open(out_path, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f)
        w.writerow(["event_id", "show_name", "series_id", "local_date", "section_label", "row",
                    "price_level_id", "display_price", "seating_type", "quantity", "seat_range",
                    "seat_keys", "show_url"])
        for row in cur:
            event_id, name, series_id, local_date = row[0], row[1], row[2], row[3]
            show_url = (f"https://tickets.broadwaydirect.com/tickets/series/{series_id}"
                        if series_id else "")
            w.writerow(list(row) + [show_url])
    print(f"Exported: {out_path}")
