# StubHub .NET (port from Python)

.NET 8 port of [`../python/stubhub/`](../python/stubhub/README.md). Mirrors the
way `dotnet/` (README_DOTNET.md) ports `python/broadwaydirect/`: same project
split, added to the same `BroadwayDirect.sln`, reusing `BroadwayDirect.Core` for
the shared `Event` / `PriceLevel` / `MongoStore`.

**IMPORTANT - `StubHub.Fetch` (WebView2) was written on macOS and has NOT been
run on real Windows.** It only build-checks off-Windows
(`EnableWindowsTargeting=true`). You MUST build + run the API on Windows and
report back - same open risk as `BroadwayDirect.Fetch` (see "Step 1" below).

## Structure

```
StubHub.Core/     - JsonTokenExtractor (embedded-JSON bracket parser),
                    StubHubAdapter (raw -> price_levels + listings + Event),
                    Models/StubHubListing. Pure; builds + tests on any OS.
                    Ports python/stubhub/extract.py + adapter.py + models.py.
StubHub.Fetch/    - StubHubClient (grid-POST primary + section-sweep fallback +
                    retry passes + discover), DataDomeBrowser (one real WebView2
                    window per proxy, fresh cookies per event, in-page evaluate
                    via postMessage), StubHubScripts (the in-page JS, verbatim
                    from client.py). Windows-only, NOT yet run.
                    Reuses BroadwayDirect.Fetch.WebView2Host (the STA pump) only.
StubHub.Api/      - ASP.NET Core Minimal API: POST /api/eventinventory +
                    POST /api/discover. Same response contract as
                    python/stubhub/api.py and BroadwayDirect.Api.
StubHub.Tests/    - xUnit, 1:1 port of test_extract.py + test_adapter.py
                    (same fixtures). 11/11 green on macOS.
```

### Shared-model change in `BroadwayDirect.Core`

`MongoStore.SaveCleanedEvent` now takes `IEnumerable<ICleanedListing>` instead of
`IEnumerable<Listing>`. `Listing` implements the new interface (all 7 members
already existed), so BroadwayDirect callers are unaffected (`IEnumerable<T>` is
covariant); `StubHub.Core`'s `StubHubListing` implements it too, so both sources
write byte-identical `cleaned_events` documents. This is the typed stand-in for
the Python side's duck-typing (`save_cleaned_event` just reads `.quantity` /
`.seat_range_label` off whatever Listing it's handed).

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
carries the 7 shared keys plus StubHub's `raw_price` / `currency`. Same contract
as `python/stubhub/api.py`.

### `POST /api/discover`

Body: `{ url, scope?, maxPages?, proxy? }` - `url` = a `/category/` , `/grouping/` ,
`/venue/` or `/performer/` page. Returns `{ sourceUrl, scope, totalCount,
collected, events[] }` (event list only; loop each `eventId` back into
`/api/eventinventory`).

### Mongo mirroring (env vars, all optional)

```
MONGO_URI   default "mongodb://localhost:27017"
MONGO_DB    default "broadwaydirect"
```

Best-effort: if MongoDB is unreachable a warning is logged and the HTTP response
still succeeds. `raw_events` / `cleaned_events`, unique key `(source, event_id)`,
`source = "stubhub.com"` - shared with BroadwayDirect. Fetch tuning defaults live
in `StubHub.Api/appsettings.json` under `"Fetch"`.

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
