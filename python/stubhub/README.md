# stubhub (Python)

Pulls ticket-listing data from `stubhub.com` and normalizes it into the
**same `price_levels` + `listings` shape** the `broadwaydirect` package
produces, so both sources feed one MongoDB schema
(`raw_events` + `cleaned_events`, keyed by `(source, event_id)`; this
source is tagged `stubhub.com`).

Run everything from the `python/` directory (`cd python` first).

## How it differs from broadwaydirect

| | broadwaydirect | stubhub |
|---|---|---|
| Data source | 2 public JSON APIs (Tixtrack) | none - listings are embedded in the server-rendered event page |
| Bot wall | Cloudflare Managed Challenge | **DataDome** |
| Fetch | `patchright` browser, `context.request.get`, pooled+reused session | `patchright` browser, in-page `fetch()`, **fresh context per event** |
| Grouping | groups individual seats into listings (`grouping.py`) | none - each StubHub `grid.items[]` entry is already a bundle; `adapter.py` is a straight field map |
| Fetch shape | 2 API calls | **primary:** one `POST /event/<id>/grid` with a big `PageSize` (whole event in ~1 request). **fallback:** one SSR request per stadium section (`&sections=<id>&quantity=0`), <=10 listings each so no paging |

Why a fresh browser context per event: StubHub keeps a **per-event
server-side filter session**. A poisoned one (e.g. from deep-linking a
single listing) makes every later request for that event return just that
one listing. An empty cookie jar per event avoids inheriting it.

## Files

```
extract.py   - bracket-count JSON extractor for the embedded state
               (grid.items / ticketClasses / ticketClassPopupData /
               sectionPopupData) + JSON-LD SportsEvent parser. Pure Python;
               used by tests and reprocess.py. client.py runs the same
               logic in-page for speed.
adapter.py   - raw StubHub dict -> price_levels (one per ticket class) +
               listings (one per grid item) + Event metadata.
client.py    - StubHubClient (patchright). fetch_event_inventory(url) sweeps
               every section; discover(url) turns a category/grouping/venue
               URL into its event list.
api.py       - FastAPI. POST /api/eventinventory (same contract as
               broadwaydirect) + POST /api/discover.
reprocess.py - rebuild cleaned_events (+ optional CSV) from raw_events
               where source matches "stubhub", no re-crawl.
tests/       - test_extract.py, test_adapter.py (fixtures from real data).
```

## HTTP API

```bash
cd python
pip install -r requirements.txt
python3 -m patchright install chromium      # first run only
python3 -m uvicorn stubhub.api:app --port 8100
```

### `POST /api/eventinventory`

```bash
curl -X POST http://localhost:8100/api/eventinventory \
  -H "Content-Type: application/json" \
  -d '{"eventId":"159257698",
       "url":"https://www.stubhub.com/los-angeles-dodgers-los-angeles-tickets-9-4-2026/event/159257698/"}'
```

Response - identical to broadwaydirect's, plus `raw_price`/`currency` on
each listing (StubHub listings carry their own price; broadwaydirect
listings inherit it from their price_level):

```json
{
  "eventId": "159257698",
  "raw": { "...assembled StubHub inventory (stored verbatim in raw_events)..." },
  "price_levels": [
    {"price_level_id": 3631, "display_name": "Infield Reserve", "zone": "Infield Reserve",
     "price": 59.31, "display_price": 59.31, "price_class": "purple2"}
  ],
  "listings": [
    {"section_label": "18RS", "row": "GG", "price_level_id": 3631,
     "seating_type": "Consecutive", "quantity": 3, "seat_range": "1-3",
     "seat_keys": ["18RS-GG-1","18RS-GG-2","18RS-GG-3"],
     "raw_price": 85.1, "currency": "USD"}
  ]
}
```

`eventId` is **required** and cross-checked against the id in `url` (they
must match). `quantity` is forced to `0` internally (all listings,
regardless of how many each sells) - the "How many tickets?" popup on the
site is a UI filter the crawler never touches. `cleaned_events` stores only
the 7 shared listing keys; `raw_price`/`currency` live in `raw_events` and
the HTTP response only.

### `POST /api/discover`

```bash
curl -X POST http://localhost:8100/api/discover \
  -H "Content-Type: application/json" \
  -d '{"url":"https://www.stubhub.com/los-angeles-dodgers-tickets/category/138300832"}'
```

```json
{ "sourceUrl": "...", "scope": "all", "totalCount": 38, "collected": 38,
  "events": [ {"eventId": 159257696, "url": "...", "name": "...", "formattedDate": "Sep 02",
               "venueName": "...", "hasActiveListings": true}, ... ] }
```

Returns the event list only - **no fan-out**. The caller loops each
`events[].eventId` + `events[].url` back into `/api/eventinventory`,
keeping control of rate limiting / retry / resume. `scope`: `"all"`
(default, every location) or `"home"` (the venue-filtered subset).
Pagination walks `?restPage=N` / `?primaryPage=N` until `totalCount` is
reached or the server clamps an out-of-range page back to page 1.

## Fetch path: grid POST vs section sweep

`fetch_event_inventory` tries **`POST /event/<id>/grid`** first, with
`PageSize=grid_page_size` (default 2000) and the page's own
`filterSortSessionId`. If StubHub honours a large page size, one request
returns the whole event - no rate-limit problem, no proxy needed even for a
big venue.

If that endpoint is unusable (no session id, non-200) it falls straight to
the section sweep. If it returns but the page size is **capped small**
(historically 10) it pages `CurrentPage` a few times, then if still short
the section sweep gap-fills whatever's missing. `raw["coverage"]["method"]`
says which path produced the result (`grid-post`, `section-sweep`, or
`grid-post+sweep`). Set `use_grid_post=False` to force the sweep.

## DataDome - operational notes (read before a full run)

Verified end-to-end 2026-09. DataDome **rate-limits per exit IP**. This
only bites the **section sweep** (a full ~240-section sweep of Dodger
Stadium exhausts one IP's budget partway through - 429s start and the flag
holds for the rest of the run). The grid POST, when it works, sidesteps it.

- **No proxy, clean home IP:** ~490 listings, then 429s; if the IP is
  already flagged, even the first page load gets a hard slider challenge
  that never auto-clears -> the crawl fails outright.
- **One sticky residential proxy IP:** got through the challenge, swept
  ~200/243 sections cleanly, then 429s -> **655/733 listings, ~89%**. The
  retry passes (fresh context each) recovered nothing because the same
  sticky IP stayed flagged.
- **Rotating residential proxy (new IP per request / short sticky window):**
  the intended setup for full coverage - each retry pass, and ideally each
  batch, comes from a different IP so no single one hits the limit.

Mitigations, in order of effect:

1. **Rotating residential proxy** - pass `proxy` per request
   (`host:port:user:pass` or a full URI, same as broadwaydirect). Datacenter
   proxies are pre-flagged by DataDome; must be residential/mobile. This is
   the only thing that gets a big venue to ~100%.
2. **Slow down** - defaults: `concurrency=3`, `batch_delay=1.0`, adaptive
   backoff (batch delay ramps to 6s once 429s appear), then `retry_passes`
   more passes each on a fresh context. Raise `batch_delay` for a big venue
   on one IP.
3. **Cool down** - a flagged IP recovers on its own after a while; space out
   runs.

`raw["coverage"]` reports `collected`, `totalCount`, `coverage_pct`,
`sections_failed` and a `note` when the sweep came back incomplete, so it's
visible to the caller rather than silent. Small venues (concerts, Broadway
houses - tens of sections) usually complete on one IP.

## Config

Env vars (all optional): `MONGO_URI`, `MONGO_DB` (default `broadwaydirect` -
the same database broadwaydirect writes to; the collections are shared and
tagged by `source`). Needs a real display - patchright and DataDome both
reject headless Chromium.

## Tests

```bash
python3 -m pytest stubhub/tests/ -q      # 15 tests (extract + adapter)
```

Covers the bracket extractor (incl. the deep-nesting case the report's
naive counter failed on), the discovery-grid parser, the JSON-LD parser,
and every branch of the field mapping (seated-together vs piggyback, seat
detail vs none, quantity == availableTickets not seat-key count,
price-level price sourced from `ticketClassPopupData`). The live browser
path (`client.py`) is not unit-tested - run the API against a real event.
