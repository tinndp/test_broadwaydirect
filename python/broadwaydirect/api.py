"""HTTP API wrapper for BroadwayDirectClient. Runs on macOS/Linux
(patchright/Playwright supports every OS, unlike WebView2 which only runs
on Windows). Use this when you want a stable HTTP endpoint to call from
elsewhere, instead of calling BroadwayDirectClient directly from Python code.

This endpoint groups the fetched inventory into listings (grouping.py, same
rules cli.py used to apply) and returns both the raw and the grouped
("cleaned") result in one response - see event_inventory()'s docstring for
the exact shape. It also mirrors both to MongoDB (mongo_storage.py) as a
side effect - this is now the only place in the Python project that
persists anything, since cli.py/crawl_all.py/discover.py were removed (the
caller supplies eventId+url directly, no need to auto-discover
series/shows). Mongo writes are best-effort: if MongoDB is unreachable, a
warning is logged but the HTTP response still succeeds with the fetched
JSON - persistence failure never blocks the fetch result.

dotnet/BroadwayDirect.Api/Program.cs implements the same response shape and
Mongo schema (see that file and BroadwayDirect.Core/Storage/MongoStore.cs).

Configuration (environment variables, all optional):
    MONGO_URI          default "mongodb://localhost:27017"
    MONGO_DB            default "broadwaydirect"
    SECTION_RULES_PATH  path to section_rules.json (default: built-in DEFAULT_RULES)

Run:
    pip install fastapi uvicorn
    uvicorn broadwaydirect.api:app --port 8000

Try it:
    curl -X POST http://localhost:8000/api/eventinventory \\
      -H "Content-Type: application/json" \\
      -d '{"eventId": "<real eventId>", "url": "https://tickets.broadwaydirect.com/tickets/series/860860"}'
"""

import asyncio
import os
import sys
from typing import Optional
from urllib.parse import urlparse

from fastapi import FastAPI
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from .client import BroadwayDirectClient
from .grouping import load_rules, seats_from_inventory, group_into_listings
from .models import Event
from .storage import parse_price_levels_from_inventory

app = FastAPI(title="BroadwayDirect Fetch API")

RULES = load_rules(os.environ.get("SECTION_RULES_PATH"))

# Client pool keyed by (url, proxy) - reuses a browser/context already
# "warmed up" past Cloudflare across requests with the same (url, proxy),
# similar to the .NET ProxyEnvironmentPool but simplified: the key includes
# the url too since bootstrap_url is fixed to the client (it doesn't
# "switch url" mid-flight like series_id does on the old CLI).
_clients: dict[tuple[str, str], BroadwayDirectClient] = {}
_pool_lock = asyncio.Lock()

_mongo = None
_mongo_init_failed = False


class EventInventoryRequest(BaseModel):
    eventId: str
    url: str
    proxy: Optional[str] = None


async def _get_client(url: str, proxy: Optional[str]) -> BroadwayDirectClient:
    key = (url, proxy or "")
    async with _pool_lock:
        client = _clients.get(key)
        if client is None:
            client = BroadwayDirectClient(bootstrap_url=url, proxy=proxy)
            _clients[key] = client
        return client


def _get_mongo():
    """Lazily connects to MongoDB on first use. Returns None (and only warns
    once) if unreachable - callers must treat Mongo persistence as
    best-effort, never fatal to the request."""
    global _mongo, _mongo_init_failed
    if _mongo is not None or _mongo_init_failed:
        return _mongo
    try:
        from .mongo_storage import MongoStore
        _mongo = MongoStore(
            os.environ.get("MONGO_URI", "mongodb://localhost:27017"),
            os.environ.get("MONGO_DB", "broadwaydirect"),
        )
    except Exception as e:
        _mongo_init_failed = True
        print(f"  !! could not connect to MongoDB, persistence disabled for this run: {e}",
              file=sys.stderr)
    return _mongo


def _price_level_dict(pl) -> dict:
    return {
        "price_level_id": pl.price_level_id,
        "display_name": pl.display_name,
        "zone": pl.zone,
        "price": pl.price,
        "display_price": pl.display_price,
        "price_class": pl.price_class,
    }


def _listing_dict(l) -> dict:
    return {
        "section_label": l.section_label,
        "row": l.row,
        "price_level_id": l.price_level_id,
        "seating_type": l.seating_type,
        "quantity": l.quantity,
        "seat_range": l.seat_range_label,
        "seat_keys": l.seat_keys,
    }


def _mirror_to_mongo(source: str, event_id: str, data: dict, listings: list, price_levels: list) -> None:
    """Best-effort: saves raw + cleaned to Mongo. Any failure is logged, never
    raised. source: the ticket site's domain (e.g. tickets.broadwaydirect.com)
    - both collections are shared across sources, keyed by (source, event_id),
    so a future different ticket site writes into the same 2 collections
    rather than needing its own."""
    mongo = _get_mongo()
    if mongo is None:
        return
    try:
        mongo.save_raw_event(source, event_id, data)
        mongo.save_cleaned_event(
            source,
            Event(id=int(event_id), local_date="", availability_color="", name=""),
            price_levels, listings,
        )
    except Exception as e:
        print(f"  !! failed to mirror event {event_id} to MongoDB: {e}", file=sys.stderr)


@app.post("/api/eventinventory")
async def event_inventory(req: EventInventoryRequest):
    """Returns {"eventId", "raw": <unmodified Tixtrack JSON>, "price_levels":
    [...], "listings": [...]}. "raw" preserves the old bare-JSON behavior for
    callers who only want that; "price_levels"/"listings" are the same
    grouped data mirrored to MongoDB's cleaned_events (see mongo_storage.py),
    so callers don't have to duplicate the grouping logic or query Mongo
    separately just to get it."""
    if not req.eventId or not req.url:
        return JSONResponse(status_code=400, content={"error": "eventId and url are required"})

    parsed = urlparse(req.url)
    if not parsed.scheme or not parsed.netloc:
        return JSONResponse(status_code=400, content={"error": "invalid url"})

    client = await _get_client(req.url, req.proxy)
    try:
        data = await client.get_event_inventory(req.eventId)
    except Exception as e:
        return JSONResponse(status_code=502, content={"detail": str(e)})

    seats = seats_from_inventory(data)
    listings = group_into_listings(seats, rules=RULES)
    price_levels = parse_price_levels_from_inventory(data)
    _mirror_to_mongo(parsed.netloc, req.eventId, data, listings, price_levels)

    return JSONResponse(content={
        "eventId": req.eventId,
        "raw": data,
        "price_levels": [_price_level_dict(pl) for pl in price_levels],
        "listings": [_listing_dict(l) for l in listings],
    })


@app.on_event("shutdown")
async def _shutdown():
    await asyncio.gather(*(c.close() for c in _clients.values()), return_exceptions=True)
    if _mongo is not None:
        _mongo.close()
