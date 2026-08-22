"""An ADDITIONAL (mirror) store on MongoDB - does NOT replace SQLite/CSV.
See README.md's "MongoDB vs SQL" section for why SQLite remains the source
of truth.

2 collections, shared across ticket sources (not one collection pair per
source) - every document carries a "source" field (e.g. "tickets.broadwaydirect.com",
taken from the fetched event's domain) so that adding another ticketing
site later means writing more documents with a different "source" value,
not creating more collections. Unique key is (source, event_id) on both:
  - raw_events: the eventinventory JSON returned by the API verbatim, with
    NO fields transformed/filtered - used for cross-checking/debugging or
    re-grouping under new rules later without calling the API again.
  - cleaned_events: a document-shaped copy of the grouped data (Event +
    PriceLevel + Listing), same content as the events/price_levels/listings
    tables in SQLite, just structured differently (nested instead of
    relational).
"""

import datetime
from typing import Iterable, Optional

from .models import Event, Listing, PriceLevel


class MongoStore:
    def __init__(self, uri: str = "mongodb://localhost:27017",
                 db_name: str = "broadwaydirect"):
        import pymongo  # lazy import: only needed when Mongo is actually used
        self.client = pymongo.MongoClient(uri, serverSelectionTimeoutMS=5000)
        self.client.admin.command("ping")  # fail fast if it can't connect
        self.db = self.client[db_name]
        self.db.raw_events.create_index([("source", 1), ("event_id", 1)], unique=True)
        self.db.cleaned_events.create_index([("source", 1), ("event_id", 1)], unique=True)

    def save_raw_event(self, source: str, event_id, raw: dict) -> None:
        """source: identifies which ticket site/platform this came from (e.g.
        "tickets.broadwaydirect.com") - lets raw_events hold data from
        multiple sources without colliding. raw: the original eventinventory
        JSON returned by the API, saved verbatim with no modifications."""
        key = {"source": source, "event_id": event_id}
        doc = dict(key)
        doc["raw"] = raw
        doc["fetched_at"] = datetime.datetime.now(datetime.timezone.utc)
        self.db.raw_events.replace_one(key, doc, upsert=True)

    def save_cleaned_event(self, source: str, event: Event, price_levels: Iterable[PriceLevel],
                            listings: Iterable[Listing], series_id=None) -> None:
        key = {"source": source, "event_id": event.id}
        doc = {
            **key,
            "series_id": str(series_id) if series_id is not None else (event.series_id or None),
            "name": event.name,
            "local_date": event.local_date,
            "availability_color": event.availability_color,
            "price_levels": [
                {
                    "price_level_id": pl.price_level_id,
                    "display_name": pl.display_name,
                    "zone": pl.zone,
                    "price": pl.price,
                    "display_price": pl.display_price,
                    "price_class": pl.price_class,
                }
                for pl in price_levels
            ],
            "listings": [
                {
                    "section_label": l.section_label,
                    "row": l.row,
                    "price_level_id": l.price_level_id,
                    "seating_type": l.seating_type,
                    "quantity": l.quantity,
                    "seat_range": l.seat_range_label,
                    "seat_keys": l.seat_keys,
                }
                for l in listings
            ],
            "updated_at": datetime.datetime.now(datetime.timezone.utc),
        }
        self.db.cleaned_events.replace_one(key, doc, upsert=True)

    def close(self) -> None:
        self.client.close()
