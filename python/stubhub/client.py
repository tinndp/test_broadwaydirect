"""Fetches StubHub event-listing data through a real browser.

Why a browser (same reason broadwaydirect needs one, different bot wall):
`stubhub.com` sits behind **DataDome**. Plain `requests`/`curl` get a 403
"Please enable JS" challenge page (`x-datadome: protected`,
`geo.captcha-delivery.com`). `patchright` (a Playwright build with patched
CDP fingerprints, `headless=False`) loads the page, DataDome's JS challenge
runs and sets the `datadome` cookie, and from then on same-origin
`fetch()` calls inside that page context succeed.

Strategy (from the extraction report, re-verified live 2026-09):
  1. Open a FRESH browser context per event and navigate to the event page.
     Fresh context = empty cookie jar = no stale `FilterSortSessionId`.
     StubHub keeps a PER-EVENT server-side filter session; a poisoned one
     (e.g. from deep-linking a single listing) makes every later request
     for that event return just that one listing. Isolation avoids it.
  2. Bootstrap off the SSR HTML: ticketClasses, ticketClassPopupData, the
     sectionPopupData keys ("<ticketClassId>_<sectionId>" - every stadium
     section), totalCount, and the page's own `filterSortSessionId`.
  3. PRIMARY: one `POST /event/<id>/grid` with a large `PageSize` and that
     `filterSortSessionId`. If the server honours the page size the whole
     event comes back in ~1 request - which is what keeps a single IP under
     DataDome's rate limit. If PageSize is capped, page `CurrentPage`.
  4. FALLBACK / gap-fill: if the POST is unusable or comes back short, sweep
     one SSR request per section (`&sections=<id>&quantity=0`); every
     section holds <=10 listings so no pagination is needed. Batches with
     adaptive backoff + fresh-context retry passes for 429'd sections.
     A `sectionPopupData` key is `"<prefix>_<sectionId>"`; for most sections
     the prefix is the venue-config id, but premium ticket classes use the
     `ticketClassId` as the prefix and a bare `&sections=<id>` then comes
     back empty - those are swept with `&ticketClasses=<id>&sections=<id>`
     (extraction-report Step 6).
  5. Dedupe listings by `id` (== `listingId`), assemble the raw dict that
     adapter.py consumes and mongo_storage stores in raw_events.

`discover()` turns a category / grouping / venue URL into the list of its
events, paginating the server-rendered `performerGridSurface` grid.
"""

import asyncio
import sys
from typing import Optional
from urllib.parse import urlparse

from patchright.async_api import async_playwright

from broadwaydirect.proxy_pool import normalize_proxy
from .extract import event_id_from_url

COMMON_QS = "?estimatedFees=false&quantity=0&sortDirection=1&sortBy=PRICE"

# --- in-page JS ---------------------------------------------------------
#
# Each constant below is ONE function expression - that's what
# page.evaluate(str, arg) requires (a plain script with top-level
# `function` declarations is rejected with "Unexpected token 'function'").
# `_JS_HELPERS` is a snippet of function declarations spliced INSIDE each
# function body.

# Balanced [..]/{..} extractor, ignoring braces inside strings. Same logic
# as stubhub.extract.extract_json_token, run in the page for speed.
_JS_HELPERS = r"""
  function __ex(text, marker){
    const m = text.indexOf(marker); if(m<0) return null;
    let i = m + marker.length;
    while(i<text.length && ' \t\r\n:'.indexOf(text[i])>=0) i++;
    if(i>=text.length || '[{'.indexOf(text[i])<0) return null;
    const start=i; let dS=0,dC=0,inStr=false,esc=false;
    for(; i<text.length; i++){ const c=text[i];
      if(esc){esc=false;continue;}
      if(c==='\\' && inStr){esc=true;continue;}
      if(c==='"'){inStr=!inStr;continue;}
      if(inStr) continue;
      if(c==='[')dS++; else if(c==='{')dC++; else if(c===']')dS--; else if(c==='}')dC--;
      if(dS===0 && dC===0){ i++; break; }
    }
    try { return JSON.parse(text.slice(start,i)); } catch(e){ return null; }
  }
  function __gridItems(html){
    let v = __ex(html, '"grid":{"items":');
    if(v===null){ const g=html.indexOf('"grid":'); if(g>=0) v=__ex(html.slice(g), '"items":'); }
    return v || [];
  }
"""

# Bootstrap works off the SSR HTML (fetched in-page), NOT the React fiber:
# in a fresh context the fiber often isn't hydrated yet when this runs, but
# ticketClasses / venueMapData / the JSON-LD block are all in the raw HTML
# immediately. Returns {error: ...} on a challenge / non-200.
_JS_BOOTSTRAP = ("async (basePath) => {" + _JS_HELPERS + r"""
  const r = await fetch(basePath + %r, { credentials: 'include' });
  if(r.status !== 200) return { error: 'status ' + r.status };
  const h = await r.text();
  if(/Please enable JS|captcha-delivery/i.test(h)) return { error: 'datadome challenge' };

  let sportsEvent = null;
  const ldRe = /<script[^>]+application\/ld\+json[^>]*>([\s\S]*?)<\/script>/g;
  let mm;
  while((mm = ldRe.exec(h))){
    try { const d = JSON.parse(mm[1].trim());
      if(d && (d['@type']==='SportsEvent' || d['@type']==='Event')){ sportsEvent = d; break; }
    } catch(e){}
  }
  const tcM = h.match(/"totalCount":(\d+)/);
  const nameM = h.match(/"eventName":"((?:[^"\\]|\\.)*)"/);
  const venM = h.match(/"venueName":"((?:[^"\\]|\\.)*)"/);
  const vidM = h.match(/"venueId":(\d+)/);
  const vcfgM = h.match(/"venueConfigId":(\d+)/);
  const fdtM = h.match(/"formattedEventDateTime":"([^"]+)"/);
  const fssM = h.match(/"filterSortSessionId":"([^"]+)"/);
  const catM = h.match(/"categoryId":(\d+)/);
  const eidM = basePath.match(/\/event\/(\d+)/);
  return {
    eventId: eidM ? eidM[1] : ((sportsEvent && String(sportsEvent.url||'').match(/\/event\/(\d+)/)||[])[1] || ''),
    eventName: (sportsEvent && sportsEvent.name) || (nameM && JSON.parse('"'+nameM[1]+'"')) || '',
    venueName: (venM && JSON.parse('"'+venM[1]+'"')) ||
               (sportsEvent && sportsEvent.location && sportsEvent.location.name) || '',
    venueId: vidM ? parseInt(vidM[1],10) : null,
    venueConfigId: vcfgM ? parseInt(vcfgM[1],10) : null,
    formattedEventDateTime: fdtM ? fdtM[1] : '',
    totalCount: tcM ? parseInt(tcM[1],10) : null,
    filterSortSessionId: fssM ? fssM[1] : null,
    categoryId: catM ? parseInt(catM[1],10) : null,
    ticketClasses: __ex(h, '"ticketClasses":') || [],
    ticketClassPopupData: __ex(h, '"ticketClassPopupData":') || {},
    sectionPopupKeys: Object.keys(__ex(h, '"sectionPopupData":') || {}),
    sportsEvent: sportsEvent,
  };
}""") % COMMON_QS

# Primary strategy: one POST to the grid endpoint with a large PageSize,
# using the page's own filterSortSessionId. If the server honours the page
# size, a whole event comes back in ~1 request instead of ~240 section GETs
# - which is what keeps a single IP under DataDome's rate limit. If it caps
# PageSize (historically 10), the caller pages CurrentPage and/or falls back
# to the section sweep.
_JS_GRID_POST = r"""async (arg) => {
  const body = JSON.stringify({
    ShowAllTickets: true, PageSize: arg.pageSize, CurrentPage: arg.currentPage,
    SortBy: "PRICE", SortDirection: 1, Sections: "", TicketClasses: "",
    PriceRange: "", FilterSortSessionId: arg.sessionId, Method: "IndexSh",
    CategoryId: arg.categoryId || 0
  });
  const paths = [arg.basePath + 'grid', '/event/' + arg.eid + '/grid'];
  for (const p of paths) {
    try {
      const r = await fetch(p, { method: 'POST', credentials: 'include',
        headers: { 'Content-Type': 'application/json' }, body });
      if (r.status !== 200) continue;
      let j;
      try { j = await r.json(); } catch (e) { continue; }
      const g = (j && j.grid) ? j.grid : (j || {});
      const items = g.items || j.items || [];
      const tc = (g.totalCount != null ? g.totalCount
                 : (j.totalCount != null ? j.totalCount : null));
      return { status: 200, path: p, items: items, totalCount: tc };
    } catch (e) {}
  }
  return { status: 0 };
}"""

_JS_SECTION_BATCH = ("async (arg) => {" + _JS_HELPERS + r"""
  const specs = arg.specs, basePath = arg.basePath;
  const failed = [];
  const results = await Promise.all(specs.map(sp => {
    const tcq = sp.tc ? ('&ticketClasses=' + sp.tc) : '';
    return fetch(basePath + %r + tcq + '&sections=' + sp.sec, { credentials: 'include' })
      .then(async r => {
        if(r.status !== 200){ failed.push(sp); return []; }
        return __gridItems(await r.text());
      })
      .catch(() => { failed.push(sp); return []; });
  }));
  return { items: results.flat(), failed };
}""") % COMMON_QS

_JS_DISCOVER_PAGE = "async (arg) => {" + _JS_HELPERS + r"""
  const path = arg.path, param = arg.param, n = arg.n, grid = arg.grid;
  try {
    const r = await fetch(path + '?' + param + '=' + n, { credentials: 'include' });
    if(r.status !== 200) return { status: r.status };
    const h = await r.text();
    const a = h.indexOf('"' + grid + '":{');
    if(a < 0) return { status: 200, items: [], pageIndex: null, totalCount: null };
    const items = __ex(h.slice(a), '"items":') || [];
    const tail = h.slice(a, a + 60000);
    const gi = (tail.match(/"pageIndex":(-?\d+)/) || [])[1];
    const tc = (tail.match(/"totalCount":(-?\d+)/) || [])[1];
    const ps = (tail.match(/"pageSize":(-?\d+)/) || [])[1];
    return {
      status: 200, items,
      pageIndex: gi != null ? parseInt(gi,10) : null,
      totalCount: tc != null ? parseInt(tc,10) : null,
      pageSize: ps != null ? parseInt(ps,10) : null,
    };
  } catch(e){ return { status: -1, error: String(e) }; }
}"""

_CHALLENGE_MARKERS = ("Please enable JS and disable", "geo.captcha-delivery.com")


def parse_proxy(proxy: Optional[str]) -> Optional[dict]:
    """"scheme://[user:pass@]host:port" -> dict for chromium.launch(proxy=...)."""
    if not proxy:
        return None
    from urllib.parse import unquote
    u = urlparse(proxy)
    out = {"server": f"{u.scheme}://{u.hostname}:{u.port}"}
    if u.username:
        out["username"] = unquote(u.username)
        out["password"] = unquote(u.password) if u.password else ""
    return out


class StubHubClient:
    def __init__(self, headless: bool = False, proxy: Optional[str] = None,
                 concurrency: int = 3, batch_delay: float = 1.0,
                 retries: int = 3, nav_retries: int = 4, timeout: int = 25,
                 challenge_wait: float = 8.0,
                 retry_passes: int = 2, retry_pass_delay: float = 15.0,
                 grid_page_size: int = 2000, use_grid_post: bool = True):
        """headless=False is required - DataDome blocks headless Chromium.

        `use_grid_post` (default) tries `POST /event/<id>/grid` with
        `PageSize=grid_page_size` first - ideally the whole event in one
        request, which keeps a single IP well under DataDome's rate limit.
        The section sweep is the fallback / gap-fill: `concurrency` requests
        at a time with `batch_delay`s between, delay ramping up once a batch
        reports 429s (adaptive backoff). The report used 5 + 0.2s, but live
        testing over a full ~240-section stadium on ONE sticky IP rate-limits
        well before that - 3 + 1.0s yields ~90% that way. Sections that keep
        429ing get up to `retry_passes` more passes, each on a fresh context
        (new datadome cookie, new exit IP if the proxy rotates - that is what
        lifts a per-IP limit). **For full coverage of a big venue when the
        grid POST is capped, use a ROTATING residential `proxy`.**

        One browser is launched for the client's lifetime; a fresh CONTEXT is
        opened per fetch_event_inventory / discover call for cookie isolation."""
        self.headless = headless
        # accept either "scheme://[user:pass@]host:port" or the raw
        # "host:port:user:pass" a provider hands out (same as broadwaydirect)
        self.proxy = normalize_proxy(proxy) if proxy else None
        self.concurrency = max(1, concurrency)
        self.batch_delay = batch_delay
        self.retries = retries
        self.nav_retries = nav_retries
        self.timeout = timeout
        self.challenge_wait = challenge_wait
        self.retry_passes = retry_passes
        self.retry_pass_delay = retry_pass_delay
        self.grid_page_size = grid_page_size
        self.use_grid_post = use_grid_post
        self._pw = None
        self._browser = None
        self._lock = asyncio.Lock()

    # -- lifecycle -------------------------------------------------------
    async def _ensure_browser(self):
        async with self._lock:
            if self._browser is not None:
                try:
                    if self._browser.is_connected():
                        return
                except Exception:
                    pass
                # dead browser (crash / proxy drop) - drop it and relaunch
                self._browser = None
            if self._pw is None:
                self._pw = await async_playwright().start()
            self._browser = await self._pw.chromium.launch(
                headless=self.headless, proxy=parse_proxy(self.proxy))

    async def close(self):
        if self._browser is not None:
            try:
                await self._browser.close()
            except Exception:
                pass
        if self._pw is not None:
            try:
                await self._pw.stop()
            except Exception:
                pass
        self._browser = self._pw = None

    async def __aenter__(self):
        await self._ensure_browser()
        return self

    async def __aexit__(self, *exc):
        await self.close()

    async def _open_page(self, url: str):
        """New context (empty cookie jar) + page, navigated past DataDome.
        Returns (context, page). Raises RuntimeError if the challenge never
        clears.

        DataDome serves a plain JS challenge that clears itself in a few
        seconds when it trusts the client; when the IP is flagged (e.g.
        after a burst of section requests) it serves a harder challenge that
        may not auto-clear at all - that's a signal to slow down or route
        through a fresh/rotating proxy IP, not something more waiting fixes.
        """
        await self._ensure_browser()
        ctx = await self._browser.new_context(viewport={"width": 1280, "height": 900})
        page = await ctx.new_page()
        last = ""
        for attempt in range(1, self.nav_retries + 1):
            try:
                await page.goto(url, wait_until="domcontentloaded",
                                timeout=self.timeout * 1000)
            except Exception as e:
                last = str(e)
                await page.wait_for_timeout(2000 * attempt)
                continue
            # give the JS challenge time; poll instead of one fixed wait
            for _ in range(int(self.challenge_wait) + 4 * attempt):
                await page.wait_for_timeout(1000)
                try:
                    head = (await page.content())[:6000]
                except Exception:
                    head = ""          # mid-navigation (challenge reloading) - keep polling
                if head and not any(mk in head for mk in _CHALLENGE_MARKERS):
                    # DataDome typically reloads the page when it clears; let
                    # that settle, then confirm the page is actually live
                    # before handing it back (avoids TargetClosedError on the
                    # first evaluate).
                    await page.wait_for_timeout(2500)
                    try:
                        await page.wait_for_load_state("domcontentloaded", timeout=8000)
                        await page.evaluate("1")
                        return ctx, page
                    except Exception as e:
                        last = f"page not stable after challenge: {e}"
                        break
            else:
                last = "DataDome challenge page did not clear"
            await page.wait_for_timeout(3000 * attempt)
        await ctx.close()
        raise RuntimeError(f"stubhub: could not load {url} ({last}). "
                           f"The IP is likely DataDome-flagged - retry later "
                           f"or pass a rotating proxy.")

    # -- per-event inventory ------------------------------------------------
    async def fetch_event_inventory(self, event_url: str) -> dict:
        """Return the assembled raw-inventory dict for one event (see
        adapter.py for its shape). `event_url` must be a
        `.../event/<id>/` page URL."""
        base_path = urlparse(event_url).path

        # open + bootstrap as one retriable unit: the DataDome page sometimes
        # tears down the context right as the first evaluate runs
        # (TargetClosedError). Reopen a fresh context and try again.
        ctx = page = boot = None
        for attempt in range(1, self.retries + 1):
            ctx, page = await self._open_page(event_url)
            try:
                boot = await page.evaluate(_JS_BOOTSTRAP, base_path)
                break
            except Exception as e:
                await ctx.close()
                ctx = None
                if attempt == self.retries:
                    raise RuntimeError(
                        f"stubhub: bootstrap evaluate failed for {event_url} "
                        f"after {attempt} tries ({e})")
                print(f"  [stubhub] bootstrap retry {attempt} ({e})", file=sys.stderr)
                await asyncio.sleep(2.0 * attempt)

        try:
            if not boot or boot.get("error"):
                raise RuntimeError(
                    f"stubhub: bootstrap failed for {event_url} "
                    f"({(boot or {}).get('error', 'empty')})")
            if not boot.get("sectionPopupKeys"):
                raise RuntimeError(f"stubhub: no sections found for {event_url}")

            total_count = boot.get("totalCount")
            section_specs = _section_specs(
                boot.get("sectionPopupKeys") or [], _ticket_class_ids(boot))
            items_by_id: dict = {}
            method = None
            failed: list = []

            # --- primary: one (or few) POST(s) to the grid endpoint ---------
            if self.use_grid_post and boot.get("filterSortSessionId"):
                got = await self._fetch_via_grid_post(
                    page, base_path, boot["eventId"],
                    boot["filterSortSessionId"], boot.get("categoryId"),
                    total_count)
                if got:
                    for it in got:
                        k = it.get("id") or it.get("listingId")
                        if k is not None:
                            items_by_id[k] = it
                    method = "grid-post"

            # --- fallback / gap-fill: section sweep -------------------------
            need_sweep = (
                method is None
                or (total_count and len(items_by_id) < total_count * 0.97)
            )
            if need_sweep:
                if method:
                    print(f"  [stubhub] grid-post got {len(items_by_id)}/{total_count}"
                          f" - filling the gap with a section sweep", file=sys.stderr)
                failed = await self._sweep_sections(
                    page, base_path, section_specs, items_by_id,
                    self.concurrency, self.batch_delay)

                # Retry passes for 429'd sections. Each opens a FRESH context
                # first: a new datadome cookie, and - if the proxy rotates - a
                # new exit IP, which is what actually lifts a per-IP limit. On
                # a sticky proxy the IP stays flagged and these mostly no-op.
                for rp in range(1, self.retry_passes + 1):
                    if not failed:
                        break
                    print(f"  [stubhub] retry pass {rp}: {len(failed)} section(s), "
                          f"fresh context", file=sys.stderr)
                    await ctx.close()
                    ctx = None
                    await asyncio.sleep(self.retry_pass_delay)
                    try:
                        ctx, page = await self._open_page(event_url)
                    except RuntimeError as e:
                        print(f"  [stubhub] retry pass {rp} could not reopen ({e})",
                              file=sys.stderr)
                        ctx = None
                        break
                    failed = await self._sweep_sections(
                        page, base_path, failed, items_by_id, 1, 2.0)
                method = "section-sweep" if method is None else "grid-post+sweep"

            items = list(items_by_id.values())
            n = len(section_specs)
            cov_pct = round(len(items) / total_count * 100, 1) if total_count else None
            print(f"  [stubhub] done via {method}: {len(items)} listings / "
                  f"totalCount {total_count} ({cov_pct}%), "
                  f"{len(failed)} section(s) unrecovered", file=sys.stderr)
            return {
                "eventId": boot.get("eventId") or event_id_from_url(event_url) or "",
                "eventName": boot["eventName"],
                "eventUrl": event_url,
                "venueName": boot["venueName"],
                "venueId": boot["venueId"],
                "venueConfigId": boot["venueConfigId"],
                "formattedEventDateTime": boot["formattedEventDateTime"],
                "sportsEvent": boot["sportsEvent"],
                "totalCount": total_count,
                "ticketClasses": boot["ticketClasses"],
                "ticketClassPopupData": boot["ticketClassPopupData"],
                "items": items,
                "coverage": {
                    "collected": len(items),
                    "totalCount": total_count,
                    "coverage_pct": cov_pct,
                    "method": method,
                    "sections": n,
                    "sections_failed": len(failed),
                    "note": (None if (cov_pct is None or cov_pct >= 97)
                             else "incomplete - DataDome rate-limited the IP "
                                  "mid-sweep; use a rotating residential proxy "
                                  "for full coverage"),
                },
            }
        finally:
            if ctx is not None:
                await ctx.close()

    async def _fetch_via_grid_post(self, page, base_path, eid, session_id,
                                   category_id, total_count):
        """POST /event/<id>/grid with a large PageSize, paging CurrentPage
        until totalCount is reached. Returns the collected item list, or None
        if the endpoint isn't usable at all (caller then does a section
        sweep). If PageSize is capped (historically 10) this still pages
        through - fewer requests than a full section sweep - and the caller
        gap-fills whatever's missing."""
        collected: dict = {}
        page_size = self.grid_page_size
        first_count = None
        max_pages = 80
        for cp in range(1, max_pages + 1):
            try:
                res = await page.evaluate(_JS_GRID_POST, {
                    "basePath": base_path, "eid": str(eid),
                    "sessionId": session_id, "categoryId": category_id or 0,
                    "pageSize": page_size, "currentPage": cp,
                })
            except Exception as e:
                print(f"  [stubhub] grid-post evaluate failed ({e})", file=sys.stderr)
                break
            if not res or res.get("status") != 200:
                if cp == 1:
                    return None            # endpoint unusable -> caller sweeps
                break
            items = res.get("items") or []
            if not items:
                break
            for it in items:
                k = it.get("id") or it.get("listingId")
                if k is not None:
                    collected[k] = it
            srv_total = res.get("totalCount") or total_count
            if first_count is None:
                first_count = len(items)
                # server capped the page small and there's clearly more to get
                if first_count <= 12 and srv_total and srv_total > 40:
                    print(f"  [stubhub] grid-post PageSize capped at ~{first_count}"
                          f" - not worth paging {srv_total} that way, sweeping",
                          file=sys.stderr)
                    return None
                max_pages = (srv_total // max(first_count, 1) + 3) if srv_total else max_pages
            print(f"  [stubhub] grid-post page {cp}: +{len(items)} "
                  f"-> {len(collected)}/{srv_total}", file=sys.stderr)
            if srv_total and len(collected) >= srv_total:
                break
            if len(items) < page_size:
                break                      # short page = last page
            await asyncio.sleep(self.batch_delay)
        return list(collected.values())

    async def _sweep_sections(self, page, base_path, specs, items_by_id,
                              concurrency, delay) -> list:
        """Fetch each section spec (`{sec, tc}`) `concurrency` at a time with
        `delay`s between batches, folding listings into `items_by_id` (deduped
        by id). Returns the specs whose request came back non-200 (usually
        429), plus any whose in-page evaluate threw (page/context died)."""
        failed_all: list = []
        n = len(specs)
        cur_delay = delay
        for start in range(0, n, concurrency):
            batch = specs[start:start + concurrency]
            try:
                res = await page.evaluate(_JS_SECTION_BATCH,
                                          {"specs": batch, "basePath": base_path}) or {}
            except Exception as e:
                # page/context died mid-sweep - record this batch and keep going
                failed_all.extend(batch)
                print(f"  [stubhub] batch evaluate failed ({e}); skipping "
                      f"{len(batch)} section(s)", file=sys.stderr)
                await asyncio.sleep(cur_delay)
                continue
            for it in res.get("items") or []:
                key = it.get("id") or it.get("listingId")
                if key is not None:
                    items_by_id[key] = it
            batch_failed = res.get("failed") or []
            failed_all.extend(batch_failed)
            # adaptive backoff: 429s in a batch -> slow the rest of the sweep
            # (up to 6s); a clean batch relaxes it back toward the base delay
            if batch_failed:
                cur_delay = min(cur_delay * 1.6 + 0.5, 6.0)
            else:
                cur_delay = max(delay, cur_delay * 0.8)
            print(f"  [stubhub] sections {min(start + concurrency, n)}/{n} "
                  f"-> {len(items_by_id)} listings"
                  f"{' (backoff %.1fs)' % cur_delay if batch_failed else ''}",
                  file=sys.stderr)
            if start + concurrency < n:
                await asyncio.sleep(cur_delay)
        return failed_all

    # -- discovery -------------------------------------------------------
    async def discover(self, catalog_url: str, scope: str = "all",
                       max_pages: int = 50) -> dict:
        """Turn a /category/ , /grouping/ or /venue/ URL into its event list.
        scope="all" walks restGrid (every location); scope="home" walks
        primaryGrid (the location-filtered subset)."""
        grid = "restGrid" if scope != "home" else "primaryGrid"
        param = "restPage" if grid == "restGrid" else "primaryPage"
        path = urlparse(catalog_url).path

        ctx = page = None
        for attempt in range(1, self.retries + 1):
            ctx, page = await self._open_page(catalog_url)
            try:
                await page.evaluate("1")   # confirm live before the loop
                break
            except Exception as e:
                await ctx.close()
                ctx = None
                if attempt == self.retries:
                    raise RuntimeError(f"stubhub: discover page unstable for "
                                       f"{catalog_url} ({e})")
                await asyncio.sleep(2.0 * attempt)
        try:
            events: dict = {}
            total_count = None
            for n in range(1, max_pages + 1):
                try:
                    res = await page.evaluate(
                        _JS_DISCOVER_PAGE,
                        {"path": path, "param": param, "n": n, "grid": grid})
                except Exception as e:
                    print(f"  [stubhub] discover page {n} evaluate failed ({e})",
                          file=sys.stderr)
                    break
                if not res or res.get("status") != 200:
                    break
                # server clamps an out-of-range page back to page 1 -> stop
                if res.get("pageIndex") is not None and res["pageIndex"] != n - 1:
                    break
                page_items = res.get("items") or []
                if not page_items:
                    break
                total_count = res.get("totalCount") or total_count
                for ev in page_items:
                    eid = ev.get("eventId")
                    if eid is not None:
                        events[eid] = _slim_event(ev)
                if total_count is not None and len(events) >= total_count:
                    break
            return {
                "sourceUrl": catalog_url,
                "scope": scope,
                "totalCount": total_count,
                "collected": len(events),
                "events": list(events.values()),
            }
        finally:
            if ctx is not None:
                await ctx.close()


def _ticket_class_ids(boot: dict) -> set:
    """ticketClassId values for this event, as strings. Used only as a guard:
    a `sectionPopupData` prefix is treated as a ticket-class prefix when it is
    both NOT the venue's dominant prefix AND a known ticketClassId (or when the
    set is empty and we have to trust the dominant-prefix test alone)."""
    ids = set()
    for c in boot.get("ticketClasses") or []:
        cid = c.get("ticketClassId")
        if cid is not None:
            ids.add(str(cid))
    ids.update(str(k) for k in (boot.get("ticketClassPopupData") or {}))
    return ids


def _section_specs(popup_keys, ticket_class_ids=None) -> list:
    """`sectionPopupData` keys are "<prefix>_<sectionId>". Most sections share
    one prefix (the venue-config id); premium ticket classes use their
    `ticketClassId` as the prefix instead, and a bare `&sections=<id>` returns
    empty for those - they must be fetched with `&ticketClasses=<id>` too
    (extraction-report Step 6).

    Return an ordered, de-duplicated list of
    `{"sec": <sectionId>, "tc": <ticketClassId or "">}`. `tc` is set when the
    prefix is NOT the dominant (venue) prefix - confirmed against
    `ticket_class_ids` when that set is available. A section under both the
    venue prefix and a class prefix yields both specs (listings are de-duped
    by id downstream)."""
    tcids = ticket_class_ids or set()
    parsed = []
    counts: dict = {}
    for k in popup_keys:
        prefix, _, sid = str(k).rpartition("_")
        if not sid:
            continue
        parsed.append((prefix, sid))
        counts[prefix] = counts.get(prefix, 0) + 1
    dominant = max(counts, key=counts.get) if counts else ""

    seen, out = set(), []
    for prefix, sid in parsed:
        is_class = prefix != dominant and (not tcids or prefix in tcids)
        tc = prefix if is_class else ""
        dedupe_key = (tc, sid)
        if dedupe_key not in seen:
            seen.add(dedupe_key)
            out.append({"sec": sid, "tc": tc})
    return out


def _slim_event(ev: dict) -> dict:
    keep = ("eventId", "name", "url", "formattedDate", "formattedTime",
            "venueId", "venueName", "formattedVenueLocation", "venueCity",
            "venueStateProvince", "countryCode", "hasActiveListings",
            "eventAvailabilityState", "allowPublicPurchase", "isParkingEvent")
    return {k: ev.get(k) for k in keep if k in ev}
