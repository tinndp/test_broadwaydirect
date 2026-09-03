# StubHub .NET (port from Python)

.NET 8 port of [`../python/stubhub/`](../python/stubhub/README.md). Mirrors the
way `dotnet/` (README_DOTNET.md) ports `python/broadwaydirect/`: same project
split, added to the same `BroadwayDirect.sln`, reusing `BroadwayDirect.Core` for
the shared `Event` / `PriceLevel`. Persistence, however, is **not** shared -
StubHub has its own `StubHubInventoryStore` (see "Mongo persistence" below).

**IMPORTANT - `StubHub.Fetch` (WebView2) was written on macOS and has NOT been
run on real Windows.** It only build-checks off-Windows
(`EnableWindowsTargeting=true`). You MUST build + run the API on Windows and
report back - same open risk as `BroadwayDirect.Fetch` (see "Step 1" below).

## Structure

```
StubHub.Core/     - JsonTokenExtractor (embedded-JSON bracket parser),
                    StubHubAdapter (raw -> price_levels + listings + Event),
                    Models/StubHubListing, plus the persistence layer:
                    StubHubInventoryMapper (listings -> per-listing docs) +
                    Storage/StubHubInventoryStore (drop + rewrite one collection
                    per event) + Models/StubHubInventoryTicket. Pure/build-safe
                    except the store needs a live Mongo. Ports
                    python/stubhub/extract.py + adapter.py + models.py.
StubHub.Fetch/    - StubHubClient (grid-POST primary + section-sweep fallback +
                    retry passes), DataDomeBrowser (one real WebView2 window per
                    proxy, fresh cookies per event, in-page evaluate via
                    postMessage), StubHubScripts (the in-page JS, verbatim from
                    client.py). Windows-only, NOT yet run.
                    Reuses BroadwayDirect.Fetch.WebView2Host (the STA pump) only.
StubHub.Api/      - ASP.NET Core Minimal API: POST /api/eventinventory. Same
                    response contract as python/stubhub/api.py.
StubHub.Tests/    - xUnit: port of test_extract.py + test_adapter.py (same
                    fixtures) + StubHubInventoryMapperTests. 20/20 green on macOS.
```

### Shared-model note in `BroadwayDirect.Core`

`MongoStore.SaveCleanedEvent` takes `IEnumerable<ICleanedListing>`; `Listing`
implements it (all 7 members already existed) so BroadwayDirect callers are
unaffected. `StubHubListing` still implements it too, but that path is now
**vestigial** - the StubHub API writes through `StubHubInventoryStore`, not
`MongoStore`, so it no longer produces `cleaned_events` documents.

## Step 1 (REQUIRED first): confirm WebView2 gets past DataDome

On a **real Windows machine**, start the API (below) and call it with a real
`.../event/<id>/` URL:

```bash
curl -X POST http://localhost:5299/api/eventinventory ^
  -H "Content-Type: application/json" ^
  -d "{\"eventId\":\"159257698\",\"url\":\"https://www.stubhub.com/.../event/159257698/\"}"
```

Expected: one Edge (WebView2) window appears off-screen (~10-15s on the first
call while DataDome's JS challenge clears), then the API returns the assembled
inventory JSON with `raw`, `price_levels`, `listings`, **no 502**. If you keep
getting `502` with `"DataDome-flagged"` in the detail, the exit IP is flagged -
retry later or pass a rotating residential `proxy` (see below).

## Running the API

```bash
cd StubHub.Api
dotnet run
```

Listens on `http://localhost:5299` (see `Properties/launchSettings.json`).
Swagger UI: `http://localhost:5299/swagger`.

### `POST /api/eventinventory`

Body: `{ eventId, url, proxy?, useGridPost?, concurrency?, batchDelay? }`

| field | default | meaning |
|---|---|---|
| `url` | - | a `.../event/<id>/` page URL |
| `proxy` | none | `scheme://[user:pass@]host:port` or raw `host:port:user:pass` |
| `useGridPost` | `true` | `false` = skip the grid POST, force the section sweep |
| `concurrency` | `3` | section GETs per sweep batch |
| `batchDelay` | `1.0` | seconds between sweep batches (pre-backoff) |

Response: `{ eventId, raw, price_levels[], listings[] }` - `raw` is the assembled
inventory dict (with a nested `coverage` block: `collected` / `totalCount` /
`coverage_pct` / `method` / `sections` / `sections_failed` / `note`). `listings[]`
carries the 7 shared keys plus StubHub's `seat_detail_level` (`exact` /
`declared` / `none` - how far to trust `seat_range`; `seat_keys` is populated
only for `exact`), `raw_price` and `currency`. Same contract as
`python/stubhub/api.py`.

### Mongo persistence (env vars, all optional)

```
MONGO_URI   default "mongodb://localhost:27017"
MONGO_DB    default "broadwaydirect"
```

**Not a mirror - the only place StubHub listing data lands.** Matches
`ETECH.Application.MarkAutomation`'s `StubHubCrawler`:

- one collection per event, `StubHub_Inventories_NEW_{eventId}`, **dropped and
  rewritten** on every crawl;
- one document per listing (`StubHubInventoryTicket` - the
  `IntegrationTemplateSourceTicket` shape, PascalCase fields), `_id` = the native
  StubHub listing id, or a deterministic hash of `Section_Row_LowSeat_HighSeat`
  when absent (`StubHubInventoryMapper`);
- price-level fields (`DisplayName` / `Zone` / `DisplayPrice` / `PriceClass`) are
  denormalised onto each document; `Price` = per-listing `RawPrice`, else the
  price level's min;
- `SeatDetailLevel` (`exact` / `declared` / `none`) records whether
  `LowSeat`/`HighSeat`/`SeatKeys` are StubHub-confirmed or seller-declared.

There is **no** `raw_events` / `cleaned_events` for this path any more. If Mongo
is unreachable or the write fails the request returns **500** (the fetch
succeeded but the data is not persisted) - it is not swallowed. Fetch tuning
defaults live in `StubHub.Api/appsettings.json` under `"Fetch"`.

`BroadwayDirect` is unchanged and still uses the shared `MongoStore` /
`raw_events` / `cleaned_events`. The Python `stubhub/api.py` also still uses
`MongoStore` - parity there is intentionally deferred.

## Notable differences from the Python version

- **Fresh context per event** (Python `browser.new_context()`) becomes
  `DataDomeBrowser.OpenFreshAsync` = `CookieManager.DeleteAllCookies()` +
  re-navigate + re-clear the challenge. Same effect (drops the per-event
  `FilterSortSessionId` and the stale `datadome` cookie); no second Edge process.
- **`page.evaluate(fn, arg)`** becomes `DataDomeBrowser.EvaluateJsonAsync`, which
  wraps the JS in `postMessage` + `WebMessageReceived` - WebView2's
  `ExecuteScriptAsync` doesn't reliably await a returned Promise (full analysis
  in `BroadwayDirect.Fetch/ProxyEnvironmentPool.cs`).
- **Proxy is per instance, not per call**: one `StubHubClient` (one WebView2
  window) per proxy value, pooled in `Program.cs` - same as
  `python/stubhub/api.py`'s `_get_client`. A new proxy costs one more ~10-15s
  DataDome warm-up.
- **No `headless` switch** - always a real (off-screen) window; DataDome blocks
  headless, as the Python side already found.

## Testing

```bash
dotnet test StubHub.Tests/StubHub.Tests.csproj
# or the whole solution (BroadwayDirect + StubHub): dotnet test BroadwayDirect.sln
```

`StubHub.Tests` runs green 11/11 on macOS (Core only - no WebView2). The solution
total is 28/28 (17 BroadwayDirect + 11 StubHub).
