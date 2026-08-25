#!/usr/bin/env python3
"""Rebuilds the 'cleaned' data (MongoDB's cleaned_events collection, and
optionally a CSV export) ENTIRELY FROM RAW JSON already stored in MongoDB's
raw_events - without calling the tickets.broadwaydirect.com API again, no
need to get past Cloudflare again.

Use this when:
  - You changed the seat-grouping rules (config/section_rules.json) and want
    to re-apply them to already-crawled data without re-crawling (no time
    cost, no dependency on Cloudflare/network).
  - You fixed a bug in grouping.py and need to rebuild all previous results.
  - You want a fresh CSV copy from a single stored raw source, instead of
    calling the API again (each re-call would return the on-sale status AT
    THE TIME of that call, which differs from when the raw data was
    originally saved).

Mongo is now the ONLY store this writes to (SQLite/CSV-via-SQLite support
was removed - see storage.py's removal and README.md's "MongoDB vs SQL"
section for why). CSV, if requested via --csv, is now built directly from
the same in-memory (event, price_levels, listings) data collected during
the loop below, instead of round-tripping through a SQLite file first.

Example:
    python -m broadwaydirect.reprocess --mongo-uri mongodb://localhost:27017 \\
        --csv rebuilt.csv
"""

import argparse
import csv
import json

from .grouping import (load_rules, seats_from_inventory, group_into_listings,
                        parse_price_levels_from_inventory)
from .models import Event

CSV_HEADER = ["event_id", "show_name", "series_id", "local_date", "section_label", "row",
              "price_level_id", "display_price", "seating_type", "quantity", "seat_range",
              "seat_keys", "show_url"]


def _export_csv(rows: list, out_path: str):
    with open(out_path, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f)
        w.writerow(CSV_HEADER)
        w.writerows(rows)
    print(f"Exported: {out_path}")


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Rebuilds MongoDB's cleaned_events (and optionally a CSV) from raw JSON "
                     "already stored in raw_events, without calling the API again")
    parser.add_argument("--mongo-uri", default="mongodb://localhost:27017")
    parser.add_argument("--mongo-db", default="broadwaydirect")
    parser.add_argument("--rules", default=None, help="Path to section_rules.json (defaults to the standard config)")
    parser.add_argument("--csv", default=None, help="Optional: also export a CSV copy of the rebuilt listings")
    args = parser.parse_args(argv)

    from .mongo_storage import MongoStore
    mongo = MongoStore(args.mongo_uri, args.mongo_db)
    rules = load_rules(args.rules)

    n_events = 0
    n_listings = 0
    csv_rows = []

    # raw_events is shared across ticket sources (each doc tagged with
    # "source", e.g. tickets.broadwaydirect.com) - iterates all of them.
    for doc in mongo.db.raw_events.find():
        source = doc["source"]
        event_id = doc["event_id"]
        inv = doc["raw"]
        # api.py doesn't call getbymonth, so no show name/date metadata is
        # ever stored - local_date/name/series_id are always empty here.
        event = Event(id=event_id, local_date="", availability_color="", name="", series_id="")

        price_levels = parse_price_levels_from_inventory(inv)
        seats = seats_from_inventory(inv)
        listings = group_into_listings(seats, rules=rules)

        mongo.save_cleaned_event(source, event, price_levels, listings)
        n_events += 1
        n_listings += len(listings)

        if args.csv:
            price_by_id = {pl.price_level_id: pl for pl in price_levels}
            show_url = (f"https://tickets.broadwaydirect.com/tickets/series/{event.series_id}"
                        if event.series_id else "")
            for lst in listings:
                pl = price_by_id.get(lst.price_level_id)
                csv_rows.append([
                    event_id, event.name, event.series_id, event.local_date,
                    lst.section_label, lst.row, lst.price_level_id,
                    pl.display_price if pl else "", lst.seating_type, lst.quantity,
                    lst.seat_range_label, json.dumps(lst.seat_keys), show_url,
                ])

    print(f"Rebuilt {n_events} performances, {n_listings} listings into MongoDB's cleaned_events")

    if args.csv:
        _export_csv(csv_rows, args.csv)
    mongo.close()


if __name__ == "__main__":
    main()
