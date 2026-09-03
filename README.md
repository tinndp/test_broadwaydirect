# broadwaydirect-project

A tool that pulls ticket-inventory data from sites running on the Tixtrack /
Broadway Direct platform (e.g. `tickets.broadwaydirect.com`), gets past
Cloudflare's bot challenge with a real browser, and groups individual seats
into "listings" following the client's naming rules.

Two implementations live in this repo:

- **[`python/`](python/README.md)** - the original implementation
  (`patchright`/Playwright for the Cloudflare bypass). Cross-platform (runs
  on macOS/Linux/Windows), fully working and verified end-to-end. The HTTP
  API (`api.py`) is the only fetch entry point - it fetches, groups into
  listings, returns both the raw and grouped data in the response, and
  mirrors both to MongoDB (the only persisted store - no SQLite). `reprocess.py`
  can rebuild Mongo's cleaned_events (and optionally a CSV) from what's
  already stored, without calling the live API again.
- **[`dotnet/`](dotnet/README_DOTNET.md)** - a .NET 8 port
  (WebView2 for the Cloudflare bypass). **Windows-only** for the fetch
  layer (WebView2 requirement) and not yet verified working end-to-end on
  Windows - see that folder's README for current status. Exposes only an
  HTTP API (`BroadwayDirect.Api`), which currently returns raw JSON only
  (no grouping/Mongo mirroring yet).

## Other ticket sources

- **[`python/stubhub/`](python/stubhub/README.md)** - a second source that
  normalizes `stubhub.com` listing data into the **same `price_levels` +
  `listings` shape** and writes to the same MongoDB collections, tagged
  `source = "stubhub.com"`. Different bot wall (DataDome, not Cloudflare)
  and no seat-grouping step (StubHub listings arrive pre-bundled), so it's
  its own package rather than a platform switch inside `broadwaydirect`. It
  reuses `broadwaydirect`'s `mongo_storage` and `models`. Same API contract
  (`POST /api/eventinventory`) plus a `POST /api/discover` for turning a
  team/artist/venue URL into its event list.
- **[`dotnet/` `StubHub.*`](dotnet/README_STUBHUB_DOTNET.md)** - the .NET 8
  port of `python/stubhub/`, matching how `dotnet/` ports `python/`
  (`StubHub.Core` / `StubHub.Fetch` / `StubHub.Api` / `StubHub.Tests`, added
  to the same `BroadwayDirect.sln`, reusing `BroadwayDirect.Core` for
  `Event` / `PriceLevel` / `MongoStore`). `StubHub.Core` + `StubHub.Tests`
  build and pass on any OS; `StubHub.Fetch` (WebView2 for the DataDome
  bypass) is **Windows-only** and not yet run end-to-end - same status as
  `BroadwayDirect.Fetch`.

See each subfolder's README for setup, usage, and scope/limitations (in
particular: this project stops at fetching + grouping + storing data, it
does not push listings to any resale marketplace).
