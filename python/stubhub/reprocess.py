#!/usr/bin/env python3
"""Rebuild cleaned_events (and optionally a CSV) from the StubHub raw
documents already in MongoDB's raw_events - no re-crawl, no browser, no
DataDome.

Use after changing adapter.py (the field mapping) to re-apply it to
everything already fetched. Mirrors broadwaydirect/reprocess.py; the only
differences are the raw shape (an assembled dict, not a vendor API
response) and that it filters raw_events to StubHub sources.

    python -m stubhub.reprocess --mongo-uri mongodb://localhost:27017 --csv stubhub_rebuilt.csv
"""

import argparse
import csv
import json

from .adapter import build_event, normalize_listings, parse_price_levels

CSV_HEADER = ["event_id", "event_name", "local_date", "section_label", "row",
              "price_level_id", "price", "raw_price", "currency", "seating_type",
              "quantity", "seat_range", "seat_keys", "event_url"]


def _export_csv(rows, out_path):
    with open(out_path, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f)
        w.writerow(CSV_HEADER)
        w.writerows(rows)
    print(f"Exported: {out_path}")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--mongo-uri", default="mongodb://localhost:27017")
    ap.add_argument("--mongo-db", default="broadwaydirect")
    ap.add_argument("--csv", default=None)
    args = ap.parse_args(argv)

    from broadwaydirect.mongo_storage import MongoStore
    mongo = MongoStore(args.mongo_uri, args.mongo_db)

    n_events = n_listings = 0
    csv_rows = []
    for doc in mongo.db.raw_events.find({"source": {"$regex": "stubhub"}}):
        source = doc["source"]
        raw = doc["raw"]
        if "items" not in raw or "ticketClasses" not in raw:
            print(f"  !! skipping {doc.get('event_id')}: not a StubHub raw shape")
            continue

        price_levels = parse_price_levels(raw)
        listings = normalize_listings(raw)
        event = build_event(raw)

        mongo.save_cleaned_event(source, event, price_levels, listings)
        n_events += 1
        n_listings += len(listings)

        if args.csv:
            price_by_id = {pl.price_level_id: pl for pl in price_levels}
            for l in listings:
                pl = price_by_id.get(l.price_level_id)
                csv_rows.append([
                    event.id, event.name, event.local_date, l.section_label, l.row,
                    l.price_level_id, pl.price if pl else "", l.raw_price, l.currency,
                    l.seating_type, l.quantity, l.seat_range, json.dumps(l.seat_keys),
                    raw.get("eventUrl", ""),
                ])

    print(f"Rebuilt {n_events} events, {n_listings} listings into cleaned_events")
    if args.csv:
        _export_csv(csv_rows, args.csv)
    mongo.close()


if __name__ == "__main__":
    main()
