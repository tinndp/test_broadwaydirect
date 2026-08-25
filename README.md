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

See each subfolder's README for setup, usage, and scope/limitations (in
particular: this project stops at fetching + grouping + storing data, it
does not push listings to any resale marketplace).
