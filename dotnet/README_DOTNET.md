# BroadwayDirect .NET (port from Python)

.NET 8 port of `../python/broadwaydirect/` (Python). See the full plan at
`~/.claude/plans/linked-imagining-gray.md`.

**IMPORTANT - code was written on macOS, NOT yet run/tested on real
Windows.** The entire `BroadwayDirect.Fetch` (WebView2) part only
build-checks (`EnableWindowsTargeting=true` allows compiling on
macOS/Linux) but CANNOT run on macOS - WebView2 only works on Windows. You
MUST build + run it yourself on a Windows machine and report back the
results.

## Structure

```
BroadwayDirect.Core/    - Models, Grouping, Storage (SQLite+Mongo+CSV), Discover
                          (cross-platform, builds+tests green on macOS)
BroadwayDirect.Fetch/   - WebView2Host, ProxyEnvironmentPool, BroadwayDirectFetchClient
                          (Windows-only, NOT yet run)
BroadwayDirect.Api/     - ASP.NET Core Minimal API: POST /api/eventinventory
                          (the only entry point currently - see "Running the API" below)
BroadwayDirect.Tests/   - xUnit, 1:1 port of the original test_grouping.py + test_discover.py
                          (the Python side has since dropped its CLI/discover.py in
                          favor of an API-only workflow - see ../python/README.md;
                          this .NET port still keeps ShowDiscovery/Grouping/Storage
                          in Core even though nothing currently calls them from Api)
```

**`BroadwayDirect.Cli` has been removed** (the discover/crawl/crawl-all
console app) - no longer used; the HTTP API is now the sole entry point for
calling fetch.

## Step 1 (REQUIRED first): spike to confirm WebView2 gets past Cloudflare

Before trusting the whole `BroadwayDirect.Fetch` layer, run a small test on
a **real Windows machine** using the API itself (see "Running the API"
below to start the server), then call it:

```bash
curl -X POST http://localhost:5263/api/eventinventory \
  -H "Content-Type: application/json" \
  -d '{"eventId":"<real eventId>","url":"https://tickets.broadwaydirect.com/tickets/series/860860"}'
```

(series `860860` is the example series_id you provided, as the URL
`https://tickets.broadwaydirect.com/shop/tickets/series/860860`; you need a
real `eventId` - get one by opening the URL above in a regular browser,
DevTools -> Network -> the `getbymonth` request, and grabbing any
`eventId` from the response).

Expected: 1 Edge window (WebView2) appears off-screen (~10s) on the first
call, then the API returns real eventinventory JSON, **with no 403/502
error**. If you keep getting 403s despite the session-reopen mechanism,
Cloudflare may be detecting WebView2 the same way it detects plain
Playwright/Puppeteer - report this back so we can evaluate other options
(see "Technical risks" in the plan file).

## Running the API

```bash
cd BroadwayDirect.Api
dotnet run
```

Listens on `http://localhost:5263` by default (see
`Properties/launchSettings.json`).

Swagger UI: open `http://localhost:5263/swagger` in a browser to call the
endpoint directly (no need for curl/Postman).

Try it with curl:

```bash
curl -X POST http://localhost:5263/api/eventinventory \
  -H "Content-Type: application/json" \
  -d '{"eventId":"<real eventId>","url":"https://tickets.broadwaydirect.com/tickets/series/860860"}'
```

`proxy` is an **optional** JSON field, accepting either
`scheme://[user:pass@]host:port` or the raw `host:port:user:pass` format
proxy providers commonly hand out (auto-converted - see
`BroadwayDirect.Core/Proxy/ProxyUri.cs`'s `Normalize`) - if omitted,
WebView2 calls directly with no proxy:

```bash
curl -X POST http://localhost:5263/api/eventinventory \
  -H "Content-Type: application/json" \
  -d '{"eventId":"...","url":"...","proxy":"http://user:pass@45.32.1.2:8080"}'
```

Default retry/sleep/concurrency configuration lives in
`BroadwayDirect.Api/appsettings.json` under the `"Fetch"` section.

Response shape: `{"eventId", "raw": <unmodified Tixtrack JSON>, "price_levels": [...], "listings": [...]}` -
"raw" preserves the old bare-JSON behavior; "price_levels"/"listings" are
the same grouped data (via `SeatGrouper`/`SqliteStorage.ParsePriceLevelsFromInventory`)
mirrored to MongoDB's `cleaned_events` collection, so callers don't have to
duplicate the grouping logic themselves. Same contract as
`../python/broadwaydirect/api.py`.

Mongo mirroring (environment variables, all optional):

```
MONGO_URI            default "mongodb://localhost:27017"
MONGO_DB             default "broadwaydirect"
SECTION_RULES_PATH   path to section_rules.json (default: built-in SectionRules.Default())
```

Mongo writes are best-effort: if MongoDB is unreachable, a warning is
logged but the HTTP response still succeeds with the fetched JSON -
persistence failure never blocks the fetch result. See
`BroadwayDirect.Core/Storage/MongoStore.cs` for the schema (2 collections,
`raw_events`/`cleaned_events`, shared across sources, unique key
`(source, event_id)`).

## Notable differences from the Python version

- **No more `--headless`**: WebView2 always uses a real window (positioned
  off-screen) - there's no headless option, since Cloudflare still blocks
  headless as the Python version already found.
- **Calls the API via `fetch()` run directly in the page** (through
  `ExecuteScriptAsync`) instead of a dedicated `context.request`-style API
  like Playwright's - because WebView2 has no equivalent API. This is the
  HIGHEST-RISK point that needs the spike confirmation (step 1 above).
- **Proxy is passed per request** (optional `proxy` field), format
  `scheme://[user:pass@]host:port`. Since WebView2 sets the proxy at the
  environment level (not per-call), each distinct proxy value creates its
  own environment/session (may cost an extra ~10s "warming up" Cloudflare
  the first time a new proxy is used).

## Testing

```bash
dotnet test BroadwayDirect.Tests/BroadwayDirect.Tests.csproj
```

Runs green 17/17 on macOS (Core only, doesn't touch WebView2 - `ProxyUri`
lives in Core specifically so it stays covered here).
