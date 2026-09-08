# Ticketmaster .NET POC

Standalone, self-testable port of the Ticketmaster crawl logic - the same shape as
`BroadwayDirect.*` / `StubHub.*` in this solution, added to `BroadwayDirect.sln`, so you can
validate it without the whole ETECH.Application.MarkAutomation solution (SQL / Mongo / SignalR /
net48). It mirrors the ETECH bot at
`ETECH.Application/Rowing/TicketMaster/TicketMasterCrawlerBot`.

## Structure

```
TicketMaster.Core/    net8.0          Models/ (quickpicks JSON) + ListingBuilder (parse + group +
                                      DisplaySeat + deterministic Id) + QuickPicksUrl +
                                      Storage/TicketMasterInventoryStore (Mongo: drop+rewrite
                                      TMEvent_{eventId}, one doc per listing). Builds + tests on
                                      macOS/Linux (the store just needs a reachable Mongo at run time).
TicketMaster.Fetch/   net8.0-windows  TicketMasterBrowserSession (one off-screen WebView2 through
                                      the proxy: warm past Kasada + capture the page's own first
                                      quickpicks request) + TicketMasterFetchClient (ApiReplay:
                                      HttpClient replays offset += limit through the SAME proxy).
                                      WebView2 = Windows-only; build-checks on macOS via
                                      EnableWindowsTargeting, does NOT run there.
TicketMaster.Api/     net8.0-windows  Minimal API: POST /api/eventinventory. Fetch -> group ->
                                      drop+rewrite Mongo TMEvent_{eventId}. Persistence is fatal
                                      (500 on failure), same as StubHub.Api / the ETECH bot.
TicketMaster.Tests/   net8.0          xUnit for ListingBuilder against a captured quickpicks fixture.
```

## MongoDB

Like `StubHub.*` (and the ETECH `TicketMasterCrawlerBot`), the Api writes listing data to Mongo:
one collection per event **`TMEvent_{eventId}`**, dropped and rewritten each crawl, one document per
grouped listing (`TicketMasterListing`, same BSON shape as the ETECH bot's `RowingListingInfo`),
index on `TMEventId`. It is the **only** place the data lands, so a write failure returns **500**
(the fetch succeeded but nothing was persisted) - not swallowed.

Connection from env vars (default `mongodb://localhost:27017` / db `broadwaydirect`):

```bash
export MONGO_URI="mongodb://user:pass@host:27017/broadwaydirect"
export MONGO_DB="broadwaydirect"
dotnet run --project TicketMaster.Api
```

BroadwayDirect keeps its own shared `raw_events` / `cleaned_events` (`MongoStore`); TicketMaster
follows the StubHub one-collection-per-event convention because that is what the ETECH TicketMaster
bot writes and what the TU sync (`SK4RowingSyncQueue`, `Type = TicketMasterToTU`) reads.

## Why ApiReplay (not in-page fetch like BroadwayDirect)

Broadway Direct sits behind Cloudflare; a `fetch()` run *inside* the warmed page works. **Ticketmaster
sits behind Kasada, which wraps `window.fetch` / `XHR`** - verified live 2026-09 (event
`05006389BE118DE0`): `window.fetch` is not native, and replaying the exact `quickpicks` URL via a
programmatic `fetch()`/`XHR` **fails** even though the page's own call succeeds.

So the ETECH bot (and this POC) do what the legacy TMCrawler did: capture the page's own first
`quickpicks` request (URL + headers + cookies via `WebResourceResponseReceived`), then replay
`offset += limit` from a .NET `HttpClient` carrying those cookies/headers, routed through the same
proxy - which bypasses the page-side wrapper entirely.

### Is ApiReplay actually faster than scrolling?

The first page costs the same either way (WebView2 load + Kasada warm ≈ 12-18s). After that:

| per extra page | scroll the panel + capture | HttpClient replay |
|---|---|---|
| latency | ~1-2s (scroll debounce + XHR + render) | ~0.2-0.5s |
| event with `total=250` (7 pages) | ~+9s → ~25s total | ~+2s → ~18s total |
| event with ~2000 listings (50 pages) | ~+75s → ~90s total | ~+17s → ~32s total |

Replay wins, and the gap grows with event size. Trade-off: it depends on `quickpicks` accepting a
"dumb" replay with the captured cookies (no live Kasada instrumentation) - the legacy crawler proved
this works; if Ticketmaster tightens it, the ETECH bot auto-falls back to scroll-capture. This POC
does not implement the fallback (it just surfaces the 403/429).

## quickpicks contract (live-verified 2026-09)

`GET https://offeradapter.ticketmaster.com/api/ismds/event/{eventId}/quickpicks` - fires on page
load (`offset=0`), next page on scroll (`offset=40`, `limit=40`). Params the page sends:
`show=places maxQuantity sections`, `mode=primary:ppsectionrow resale:ga_areas platinum:all`, `qty`,
`q=not('accessible')`, `includeStandard`, `includeResale`, `includePlatinumInventoryType`,
`ticketTypes`, `embed=description`, `apikey`+`apisecret`, `resaleChannelId`, `limit`, `offset`,
`sort=noTaxTotalprice`. `qty` stays constant across pages. The code reuses whatever the captured
first request carries; it only overrides `offset` and forces `includeResale=false`.

## Run it

```bash
cd dotnet

# 1. Core logic - runs anywhere (macOS/Linux/Windows)
dotnet test TicketMaster.Tests/TicketMaster.Tests.csproj          # 9/9 green

# 2. Live crawl - Windows only (WebView2), needs a proxy
dotnet run --project TicketMaster.Api                              # http://localhost:5xxx/swagger
```

```bash
curl -s http://localhost:PORT/api/eventinventory -H 'content-type: application/json' -d '{
  "eventId": "05006389BE118DE0",
  "url": "https://www.ticketmaster.com/indiana-fever-vs-minnesota-lynx-indianapolis-indiana-09-22-2026/event/05006389BE118DE0",
  "proxy": "http://user:pass@host:port"
}' | jq '{total, pageCount, listingCount, sample: .listings[0]}'
```

`proxy` is required in practice - ticketmaster.com hides tickets without one.

## Open items to confirm on a real Windows run + live proxy

* Does `offeradapter.ticketmaster.com` accept the replayed `quickpicks` calls through the rotating
  proxy (right cookies/headers, same IP as the warm-up)? If it 403/429s, ApiReplay isn't viable and
  the ETECH bot's scroll fallback carries it.
* Does the WebView2 page get past Kasada on a fresh proxy session within the warm-up window?
