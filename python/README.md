# broadwaydirect-project (Python)

All commands below assume you're running from this `python/` directory
(`cd python` first if you're at the repo root). See `../dotnet/README_DOTNET.md`
for the .NET port.

Pulls ticket-inventory data from sites on the Tixtrack / Broadway Direct
platform (e.g. `tickets.broadwaydirect.com`), gets past Cloudflare's bot
challenge with a real browser, and groups individual seats into "listings"
per the naming rules in the requirements doc. The HTTP API (`api.py`) is
the only entry point - the caller supplies `eventId` + the series ticket
page `url` directly, no discovery/crawl step needed first.

## Scope & limitations (read before using)

This project stops at: **fetching public data + grouping seats into listings
+ saving to MongoDB for you to manage yourself**. It does **not** include:

- An "Event Mapper", "Add to TA", "Broadcast via Autopilot", or any automated
  "undercutter" pricing bot meant to push listings onto secondary resale
  marketplaces.
- Any logic that creates/advertises a listing BEFORE you actually own or
  have a contractually guaranteed ticket to deliver at delivery time.

Reason: if a listing is created and broadcast to the secondary market BEFORE
the underlying ticket is actually owned (commonly called "speculative"/
"phantom" ticket listing), this is prohibited under New York law (General
Business Law §25.24) - where Hamilton is playing (Richard Rodgers Theatre).
If you already own/are authorized to resell the tickets in this DB, pushing
them to sales channels (Event Mapper, TA, Autopilot...) is a separate
business step you'll need to build/integrate yourself - out of scope here.

## Project structure

```
broadwaydirect/
  client.py        - calls the getbymonth + eventinventory APIs (with retry/backoff)
  models.py         - Event, PriceLevel, Seat, Listing (dataclasses)
  grouping.py       - parses mapSeatsKey, SIDES/CENTER/Box rules, groups contiguous seats,
                      also parses price levels (parse_price_levels_from_inventory)
  mongo_storage.py  - MongoDB store (raw_events + cleaned_events), used by api.py - the only
                      persisted store (SQLite/storage.py was removed, see "MongoDB vs SQL" below)
  reprocess.py      - rebuilds Mongo cleaned_events (and optionally a CSV) from raw JSON
                      already in Mongo, without calling the API again (see its own docstring)
  api.py            - HTTP API, the only fetch entry point (see below)
config/
  section_rules.json - section naming rules, EDIT PER VENUE
tests/
  fixtures/sample_eventinventory.json - sample JSON (see note below)
  test_grouping.py - 8 tests for the seat-grouping logic
  Run all: python3 -m pytest tests/ -v  (8 tests)
```

## IMPORTANT NOTE about the sample data

The JSON fixture (eventId `1825450`) is **actually data from a Schmigadoon
performance at the Nederlander Theatre**, not real Hamilton data - kept
as-is since it's in the real API format, and `grouping.py` is fully
verified against it (8/8 tests, see "Test results" below).

**Before using this for real against Hamilton NY**, you need to:

1. Hamilton's series_id is **426458** (bootstrap url
   `https://tickets.broadwaydirect.com/tickets/series/426458`), confirmed
   from the "Buy Tickets" link on `broadwaydirect.com/show/hamilton/`. This
   could change if Broadway Direct alters its URL structure later.
2. Cross-check Hamilton's real `mapSeats` (Richard Rodgers Theatre) against
   `config/section_rules.json`. The requirements doc lists sections as
   `ORCH/FMEZZ/RMEZZ`, but the sample data (Schmigadoon) only has `MEZZ`
   and `ORCH C/L/R` - Hamilton's real section names are likely different,
   so `sides_center_prefixes`/`box_prefixes` will need adjusting.

## HTTP API (broadwaydirect/api.py)

The only fetch entry point. Runs on macOS/Linux since `patchright` isn't
Windows-only like WebView2 - verified end-to-end live, got past Cloudflare
and returned real JSON.

Beyond fetching, this endpoint also groups the inventory into listings
(`config/section_rules.json`, or the built-in defaults if
`SECTION_RULES_PATH` isn't set) and mirrors both raw and grouped data to
MongoDB (best-effort - see "MongoDB" below).

> **Note:** `dotnet/BroadwayDirect.Api` doesn't implement this shape yet
> (still returns bare raw JSON, no grouping/Mongo). The two are meant to
> share the same contract eventually; that .NET-side change just hasn't
> been done.

```bash
cd python   # must run from here, not from inside broadwaydirect/
pip install -r requirements.txt
python3 -m uvicorn broadwaydirect.api:app --port 8000
```

`python3 -m uvicorn ...` works regardless of `PATH`. If `pip install --user`
put the `uvicorn` script somewhere not on your `PATH` (common on macOS -
check the "WARNING: The script uvicorn is installed in ..." line from pip),
plain `uvicorn ...` will fail with `command not found`; either keep using
`python3 -m uvicorn`, or add that directory to `PATH` (e.g.
`export PATH="$HOME/Library/Python/3.9/bin:$PATH"` in `~/.zshrc`).

```bash
curl -X POST http://localhost:8000/api/eventinventory \
  -H "Content-Type: application/json" \
  -d '{"eventId":"<real eventId>","url":"https://tickets.broadwaydirect.com/tickets/series/860860"}'
```

Response shape (`price_levels`/`listings` use the same field names as the
`cleaned_events` Mongo document below - one naming convention, not two):

```json
{
  "eventId": "2149529",
  "raw": { "...exactly what Tixtrack's eventinventory API returns, unmodified..." },
  "price_levels": [
    {"price_level_id": 86944, "display_name": "Premium", "zone": "Premium",
     "price": 229.0, "display_price": 250.0, "price_class": "p0"}
  ],
  "listings": [
    {"section_label": "ORCH C CENTER", "row": "C", "price_level_id": 86944,
     "seating_type": "Consecutive", "quantity": 3, "seat_range": "106-108",
     "seat_keys": ["ORCH C-C-106", "ORCH C-C-107", "ORCH C-C-108"]}
  ]
}
```

`proxy` is an optional field (`{"proxy": "scheme://[user:pass@]host:port"}`),
applied at the browser level (Playwright has the same per-request
limitation as WebView2 here). Sessions/browsers are pooled and reused by
(`url`, `proxy`) pair across requests - see `broadwaydirect/api.py`.

Configuration (environment variables, all optional):

| Variable | Default | Meaning |
|---|---|---|
| `MONGO_URI` | `mongodb://localhost:27017` | MongoDB connection string |
| `MONGO_DB` | `broadwaydirect` | MongoDB database name |
| `SECTION_RULES_PATH` | (unset, uses built-in defaults) | path to a custom `section_rules.json` |

## MongoDB

Every successful call writes to 2 collections **shared across ticket
sources** (not one pair per source) - every document carries a `source`
field (the ticket site's domain, e.g. `tickets.broadwaydirect.com`, taken
from the request `url`), with a unique index on `(source, event_id)`. A
future ticketing platform just writes more documents with a different
`source` value into these same 2 collections:

- `raw_events` - the API response verbatim, unmodified - used for
  cross-checking/debugging or re-grouping later without calling the API again.
- `cleaned_events` - the grouped result (`Event` metadata + `PriceLevel`s +
  `Listing`s), same shape `reprocess.py` produces.

Writes are best-effort: if MongoDB is unreachable, a warning is logged but
the HTTP response still succeeds with the fetched JSON.

Note: since `api.py` only calls `eventinventory` (not `getbymonth`), saved
`Event` metadata has no `local_date`/`name`/`series_id` - that information
isn't part of the API contract today. To add it, extend the request and
thread it into the `Event(...)` built in `_mirror_to_mongo()`.

`reprocess.py` rebuilds Mongo `cleaned_events` (and optionally a CSV export,
built directly from the same in-memory data, no SQLite involved) from
whatever's in `raw_events`, without calling the live API again - useful
after changing `section_rules.json`:

```bash
python -m broadwaydirect.reprocess --mongo-uri mongodb://localhost:27017 \
    --csv rebuilt.csv
```

**MongoDB vs SQL (updated decision):** this project originally kept a SQLite
mirror (`storage.py`) alongside Mongo, on the reasoning that relational
storage is a better fit than Mongo's document queries for reporting/
analytics (joins across shows, "total tickets left by section" style
queries) - see git history for that version if you need it back. `storage.py`
was removed and MongoDB is now the **only** persisted store, to match how
this data is actually consumed downstream: the .NET Rowing-integration bots
this project feeds into (TMCrawler and BroadwayCrawler, in the main
ETECH.Application.MarkAutomation repo) only ever read/write listing data via
Mongo, never SQL, for the exact same kind of grouped-listing data - keeping
a SQLite copy here that nothing downstream reads added persistence surface
without a corresponding consumer. If you need relational
reporting/analytics later, `reprocess.py` still has all the (event,
price_levels, listings) data in memory each pass - point it at whatever SQL
target you need instead of (or alongside) `--csv`.

## Cloudflare risk on tickets.broadwaydirect.com (RESOLVED)

Plain `requests` (no browser) gets blocked with a 403 by Cloudflare
**Managed Challenge** (Turnstile) on the first call to `/api/consumer/...`
- a real interactive JS challenge (`cf-mitigated: challenge`, "Just a
moment..." page), not a simple IP/rate block.

Tried and concluded:
- `requests` + a spoofed User-Agent: **does not get through**.
- Plain Playwright/Puppeteer, even with a real window: **does not get
  through** - Cloudflare detects the CDP protocol traces left by ordinary
  automation.
- `patchright` (patched to hide CDP fingerprints) with `headless=False`
  (headless is still blocked): **gets through**, but only when navigating
  to the exact **series ticket page**
  (`https://tickets.broadwaydirect.com/tickets/series/{series_id}`), not
  the homepage `/` - the homepage grants `cf_clearance` but the API still
  403s (suspected extra layer, e.g. Queue-it, that only unlocks on the real
  ticket page).
- The `cf_clearance` cookie **cannot** be borrowed into a separate
  `requests.Session()` - still 403s, since Cloudflare also ties it to the
  browser's TLS/HTTP fingerprint. The API must be called through the
  browser's own `context.request` (Playwright's APIRequestContext).

`client.py` already implements this: the first call for a given (url,
proxy) pair opens a real browser (~10s) to get past the challenge, then
reuses that session for subsequent requests. A 403 mid-run (expired
cookie) triggers one automatic reopen before retrying (up to `retries`,
default 3).

Notes when running:
- Needs a machine with a real display (`headless=False` is required, won't
  work on a headless server/CI).
- A Chromium window appears briefly the first time a given (url, proxy)
  pair is used - expected, not a bug.
- Persistent 403s despite the auto-reopen usually mean Cloudflare is
  applying stricter IP/rate scrutiny (e.g. too many calls in a short test
  window) - try a different network or wait a few minutes.

## Test results

```
8/8 tests pass (tests/test_grouping.py), using the Schmigadoon fixture above:
- parses "ORCH L-C-5" -> section="ORCH L", row="C", seat=5
- seat<=100 in ORCH/MEZZ sections -> SIDES suffix, group step = 2 (odd/even)
- seat>100 -> CENTER suffix, group step = 1
- Box -> no suffix, group step = 1
- price code (priceLevelId) acts as a delimiter: contiguous seats with a
  different price are NOT grouped together
- ADA seats (Wheelchair/Companion) are excluded from regular listings by
  default, can be included via include_ada=True
```

Full end-to-end on the fixture (34 seats) produces 14 listings, manually
verified line by line (e.g. `ORCH C CENTER, row C, 106-108` and `111-112`
split into 2 listings since 108 and 111 aren't contiguous).

Also verified live against the real API (Cloudflare bypass, real JSON
returned) and a real MongoDB instance (both the unreachable-Mongo fallback
and the successful-write path - `raw_events`/`cleaned_events` populated
correctly, counts matching the HTTP response).
