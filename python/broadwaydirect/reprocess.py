#!/usr/bin/env python3
"""Rebuilds the 'cleaned' data (SQLite/CSV + optionally the Mongo
cleaned_events collection) ENTIRELY FROM RAW JSON already stored in
MongoDB - without calling the tickets.broadwaydirect.com API again, no need
to get past Cloudflare again.

Use this when:
  - You changed the seat-grouping rules (config/section_rules.json) and want
    to re-apply them to already-crawled data without re-crawling (no time
    cost, no dependency on Cloudflare/network).
  - You fixed a bug in grouping.py and need to rebuild all previous results.
  - You want a fresh DB/CSV copy from a single stored raw source, instead of
    calling the API again (each re-call would return the on-sale status AT
    THE TIME of that call, which differs from when the raw data was
    originally saved).

Example:
    python -m broadwaydirect.reprocess --mongo-uri mongodb://localhost:27017 \\
        --db rebuilt.db --csv rebuilt.csv
"""

import argparse

from .grouping import load_rules, seats_from_inventory, group_into_listings
from .models import Event
from .storage import (init_db, save_event, save_price_levels, save_listings,
                       parse_price_levels_from_inventory, export_listings_csv)


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Rebuilds cleaned data from raw JSON stored in MongoDB (without calling the API again)")
    parser.add_argument("--mongo-uri", default="mongodb://localhost:27017")
    parser.add_argument("--mongo-db", default="broadwaydirect")
    parser.add_argument("--rules", default=None, help="Path to section_rules.json (defaults to the standard config)")
    parser.add_argument("--db", default="rebuilt.db")
    parser.add_argument("--csv", default=None)
    parser.add_argument("--write-mongo-cleaned", action="store_true",
                         help="Also overwrite the cleaned_events collection on Mongo")
    args = parser.parse_args(argv)

    from .mongo_storage import MongoStore
    mongo = MongoStore(args.mongo_uri, args.mongo_db)
    rules = load_rules(args.rules)

    conn = init_db(args.db)
    n_events = 0
    n_listings = 0

    # raw_events is shared across ticket sources (each doc tagged with
    # "source", e.g. tickets.broadwaydirect.com) - iterates all of them.
    # SQLite (init_db/save_event/...) is NOT source-aware (schema keys purely
    # by event_id), so this only makes sense today while there's a single
    # source; the Mongo side (save_cleaned_event below) does carry source
    # through correctly.
    for doc in mongo.db.raw_events.find():
        source = doc["source"]
        event_id = doc["event_id"]
        inv = doc["raw"]
        # api.py doesn't call getbymonth, so no show name/date metadata is
        # ever stored - local_date/name/series_id are always empty here.
        event = Event(id=event_id, local_date="", availability_color="", name="", series_id="")

        save_event(conn, event)
        price_levels = parse_price_levels_from_inventory(inv)
        save_price_levels(conn, event_id, price_levels)
        seats = seats_from_inventory(inv)
        listings = group_into_listings(seats, rules=rules)
        save_listings(conn, event_id, listings)
        n_events += 1
        n_listings += len(listings)

        if args.write_mongo_cleaned:
            mongo.save_cleaned_event(source, event, price_levels, listings)

    conn.commit()
    print(f"Rebuilt {n_events} performances, {n_listings} listings from raw JSON into {args.db}")

    if args.csv:
        export_listings_csv(conn, args.csv)
    conn.close()
    mongo.close()


if __name__ == "__main__":
    main()
