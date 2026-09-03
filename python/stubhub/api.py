"""HTTP API for the StubHub fetcher. Same response contract as
broadwaydirect/api.py so both sources feed one downstream schema.

    POST /api/eventinventory  { eventId, url, proxy? }
        -> { eventId, raw, price_levels[], listings[] }
        url = a .../event/<id>/ page. Fetches every section, normalizes,
        and mirrors raw + cleaned to MongoDB (source = "stubhub.com").

    POST /api/discover        { url, scope?, maxPages?, proxy? }
        -> { sourceUrl, scope, totalCount, collected, events[] }
        url = a /category/ , /grouping/ or /venue/ page. Returns the event
        list only (no inventory) - the caller loops each event.eventId back
        into /api/eventinventory. No fan-out here on purpose: per-event
        calls give the caller control over rate limiting / retry / resume.

Run:
    cd python
    pip install -r requirements.txt
    python3 -m patchright install chromium        # first time only
    python3 -m uvicorn stubhub.api:app --port 8100

Config (env, all optional): MONGO_URI, MONGO_DB (default "broadwaydirect").
Needs a real display - patchright/DataDome both reject headless Chromium.
"""

import asyncio
import os
import sys
from typing import Optional
from urllib.parse import urlparse

from fastapi import FastAPI
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from broadwaydirect.models import Event
from broadwaydirect.proxy_pool import normalize_proxy
from .adapter import build_event, normalize_listings, parse_price_levels
from .client import StubHubClient
from .extract import event_id_from_url

app = FastAPI(title="StubHub Fetch API")

_clients: dict = {}
_clients_lock = asyncio.Lock()
_mongo = None
_mongo_failed = False


class EventInventoryRequest(BaseModel):
    eventId: str
    url: str
    proxy: Optional[str] = None
    # sweep-path tuning knobs - defaults mirror StubHubClient.__init__, so a
    # caller that omits them gets today's behaviour. Set useGridPost=false to
    # force the section sweep (approach 2) and compare coverage/timing.
    useGridPost: bool = True
    concurrency: int = 3
    batchDelay: float = 1.0


class DiscoverRequest(BaseModel):
    url: str
    scope: str = "all"
    maxPages: int = 50
    proxy: Optional[str] = None


def _source(url: str) -> str:
    """"https://www.stubhub.com/..." -> "stubhub.com" (drop a leading www.).
    This is the `source` tag on both Mongo collections."""
    host = (urlparse(url).netloc or "").lower()
    return host[4:] if host.startswith("www.") else host


async def _get_client(proxy: Optional[str]) -> StubHubClient:
    key = proxy or ""
    async with _clients_lock:
        c = _clients.get(key)
        if c is None:
            c = StubHubClient(proxy=proxy)
            _clients[key] = c
        return c


def _get_mongo():
    global _mongo, _mongo_failed
    if _mongo is not None or _mongo_failed:
        return _mongo
    try:
        from broadwaydirect.mongo_storage import MongoStore
        _mongo = MongoStore(
            os.environ.get("MONGO_URI", "mongodb://localhost:27017"),
            os.environ.get("MONGO_DB", "broadwaydirect"),
        )
    except Exception as e:
        _mongo_failed = True
        print(f"  !! MongoDB unreachable, persistence disabled this run: {e}", file=sys.stderr)
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
    # first 7 keys == broadwaydirect's _listing_dict exactly; raw_price /
    # currency are a StubHub-only superset (per-listing price - broadwaydirect
    # listings inherit price from their price_level). cleaned_events still
    # stores only the 7 shared keys (MongoStore ignores the extras).
    return {
        "section_label": l.section_label,
        "row": l.row,
        "price_level_id": l.price_level_id,
        "seating_type": l.seating_type,
        "quantity": l.quantity,
        "seat_range": l.seat_range_label,
        "seat_keys": l.seat_keys,
        "raw_price": l.raw_price,
        "currency": l.currency,
    }


def _mirror_to_mongo(source: str, event: Event, raw: dict, price_levels, listings) -> None:
    mongo = _get_mongo()
    if mongo is None:
        return
    try:
        mongo.save_raw_event(source, str(event.id), raw)
        mongo.save_cleaned_event(source, event, price_levels, listings)
    except Exception as e:
        print(f"  !! failed to mirror event {event.id} to MongoDB: {e}", file=sys.stderr)


@app.post("/api/eventinventory")
async def event_inventory(req: EventInventoryRequest):
    if not req.eventId or not req.url:
        return JSONResponse(status_code=400, content={"error": "eventId and url are required"})
    parsed = urlparse(req.url)
    if not parsed.scheme or not parsed.netloc:
        return JSONResponse(status_code=400, content={"error": "invalid url"})

    id_from_url = event_id_from_url(req.url)
    if id_from_url and id_from_url != req.eventId:
        return JSONResponse(status_code=400, content={
            "error": f"eventId mismatch: url has {id_from_url}, body has {req.eventId}"})

    try:
        proxy = normalize_proxy(req.proxy) if req.proxy else None
    except ValueError as e:
        return JSONResponse(status_code=400, content={"error": f"invalid proxy: {e}"})

    client = await _get_client(proxy)
    # request-scoped overrides on the per-proxy client (read live inside
    # fetch_event_inventory). Fine for sequential test calls; concurrent
    # requests with different knobs would race, which the test harness doesn't do.
    client.use_grid_post = req.useGridPost
    client.concurrency = max(1, req.concurrency)
    client.batch_delay = req.batchDelay
    try:
        raw = await client.fetch_event_inventory(req.url)
    except Exception as e:
        return JSONResponse(status_code=502, content={"detail": str(e)})

    price_levels = parse_price_levels(raw)
    listings = normalize_listings(raw)
    event = build_event(raw)
    _mirror_to_mongo(_source(req.url), event, raw, price_levels, listings)

    return JSONResponse(content={
        "eventId": req.eventId,
        "raw": raw,
        "price_levels": [_price_level_dict(pl) for pl in price_levels],
        "listings": [_listing_dict(l) for l in listings],
    })


@app.post("/api/discover")
async def discover(req: DiscoverRequest):
    parsed = urlparse(req.url)
    if not parsed.scheme or not parsed.netloc:
        return JSONResponse(status_code=400, content={"error": "invalid url"})
    if not any(seg in parsed.path for seg in ("/category/", "/grouping/", "/venue/", "/performer/")):
        return JSONResponse(status_code=400, content={
            "error": "url must be a /category/ , /grouping/ , /venue/ or /performer/ page"})
    try:
        proxy = normalize_proxy(req.proxy) if req.proxy else None
    except ValueError as e:
        return JSONResponse(status_code=400, content={"error": f"invalid proxy: {e}"})

    client = await _get_client(proxy)
    try:
        result = await client.discover(req.url, scope=req.scope, max_pages=req.maxPages)
    except Exception as e:
        return JSONResponse(status_code=502, content={"detail": str(e)})
    return JSONResponse(content=result)


@app.on_event("shutdown")
async def _shutdown():
    await asyncio.gather(*(c.close() for c in _clients.values()), return_exceptions=True)
    if _mongo is not None:
        _mongo.close()
