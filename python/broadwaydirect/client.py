"""Calls the 2 public JSON APIs of Tixtrack / Broadway Direct.

`tickets.broadwaydirect.com` sits behind a Cloudflare Managed Challenge
(Turnstile) - calling it directly with plain `requests` gets a 403
"Just a moment...". This client uses `patchright` (a Playwright build with
patched CDP fingerprinting) to open a REAL browser window, get past the
challenge once at startup, then reuses that same `BrowserContext`
(`context.request.get`) for all subsequent API calls - because the
`cf_clearance` cookie is tied to the browser's TLS/HTTP fingerprint, so it
can't be "borrowed" into a separate `requests.Session()`. See the README's
"Cloudflare risk" section for details.

Important note: you must navigate to the exact series ticket page
(`/tickets/series/{series_id}`), NOT the homepage (`/`) - we verified that
the homepage still grants the `cf_clearance` cookie but the API still
returns 403 (likely an extra protection layer, e.g. Queue-it, that only
unlocks once you visit the actual ticket page). So this client always
navigates to the series page before calling that series's API.

Note on speed: `eventinventory` calls (the heaviest, 1 call per event) run
CONCURRENTLY (bounded by `concurrency`, default 6) via asyncio, instead of
sequentially one at a time as originally - this significantly cuts crawl
time when there are many performances (thousands). headless=True is still
detected and blocked by Cloudflare - headless=False (a real browser
window) is required.
"""

import asyncio
import sys
import time
from typing import Optional
from urllib.parse import urlparse, unquote

from patchright.async_api import async_playwright

DEFAULT_HEADERS = {
    "Accept": "application/json, text/plain, */*",
}


def parse_proxy(proxy: Optional[str]) -> Optional[dict]:
    """Parses "scheme://[user:pass@]host:port" -> dict for Playwright's
    chromium.launch(proxy=...). Returns None if proxy is empty/None."""
    if not proxy:
        return None
    u = urlparse(proxy)
    out = {"server": f"{u.scheme}://{u.hostname}:{u.port}"}
    if u.username:
        out["username"] = unquote(u.username)
        out["password"] = unquote(u.password) if u.password else ""
    return out


class BroadwayDirectClient:
    def __init__(self, domain: str = "tickets.broadwaydirect.com",
                 sleep: float = 0.3, retries: int = 3, timeout: int = 20,
                 headless: bool = False, concurrency: int = 6,
                 on_raw=None, bootstrap_url: Optional[str] = None,
                 proxy: Optional[str] = None):
        """on_raw: optional callable(kind: str, key: dict, raw), called with
        EVERY raw JSON received from the API (unmodified) - used to mirror
        to Mongo.

        bootstrap_url: if set, use this exact URL to get past Cloudflare
        (instead of building one from domain+series_id), and infer the
        domain from this url - used when calling through the HTTP API with
        an arbitrary series url (see api.py).
        proxy: optional, format "scheme://[user:pass@]host:port", applies
        to the whole browser (not per-request - patchright/Playwright has
        the same limitation as WebView2 here)."""
        self.domain = urlparse(bootstrap_url).netloc if bootstrap_url else domain
        self.sleep = sleep
        self.retries = retries
        self.timeout = timeout
        self.headless = headless
        self.concurrency = concurrency
        self.on_raw = on_raw
        self.bootstrap_url = bootstrap_url
        self.proxy = proxy
        self._playwright = None
        self._browser = None
        self._context = None
        self._series_id = None
        self._session_gen = 0
        self._reopen_lock = asyncio.Lock()

    async def _open_session(self) -> None:
        print("  opening a real browser to get past the Cloudflare Managed "
              "Challenge (once, ~10s)...", file=sys.stderr)
        if self._context is not None:
            await self._close_browser()
        self._playwright = await async_playwright().start()
        self._browser = await self._playwright.chromium.launch(
            headless=self.headless, proxy=parse_proxy(self.proxy))
        self._context = await self._browser.new_context(viewport={"width": 1280, "height": 800})
        page = await self._context.new_page()
        # Must navigate to the exact series ticket page, not the homepage -
        # see the note at the top of the file.
        if self.bootstrap_url is not None:
            seed_url = self.bootstrap_url
        elif self._series_id is not None:
            seed_url = f"https://{self.domain}/tickets/series/{self._series_id}"
        else:
            seed_url = f"https://{self.domain}/"
        await page.goto(seed_url, wait_until="domcontentloaded", timeout=self.timeout * 1000)
        await page.wait_for_timeout(8000)
        await page.close()
        cookies = await self._context.cookies(f"https://{self.domain}")
        if not any(c["name"] == "cf_clearance" for c in cookies):
            print("  !! cf_clearance cookie not found - Cloudflare may still "
                  "be blocking, subsequent requests will error", file=sys.stderr)
        self._session_gen += 1

    async def _ensure_session(self, series_id=None) -> None:
        async with self._reopen_lock:
            if series_id is not None and series_id != self._series_id:
                self._series_id = series_id
                await self._open_session()
                return
            if self._context is None:
                await self._open_session()

    async def _close_browser(self) -> None:
        for obj, closer in ((self._browser, "close"), (self._playwright, "stop")):
            if obj is not None:
                try:
                    await getattr(obj, closer)()
                except Exception:
                    pass
        self._browser = None
        self._context = None

    async def close(self) -> None:
        await self._close_browser()
        self._playwright = None

    async def __aenter__(self):
        return self

    async def __aexit__(self, exc_type, exc_val, exc_tb):
        await self.close()

    async def _get_json(self, url, params: Optional[dict] = None, series_id=None) -> dict:
        await self._ensure_session(series_id)
        last_err = None
        for attempt in range(1, self.retries + 1):
            gen_before = self._session_gen
            try:
                resp = await self._context.request.get(
                    url, params=params, headers=DEFAULT_HEADERS,
                    timeout=self.timeout * 1000)
                if resp.status == 403:
                    raise RuntimeError(f"Blocked by Cloudflare (HTTP {resp.status})")
                if resp.status >= 400:
                    raise RuntimeError(f"HTTP {resp.status}")
                return await resp.json()
            except Exception as e:
                last_err = e
                if attempt < self.retries:
                    async with self._reopen_lock:
                        # only reopen if no other task has already reopened after this error
                        if self._session_gen == gen_before:
                            await self._open_session()
                    await asyncio.sleep(self.sleep * attempt * 2)
        raise RuntimeError(f"Error calling {url}: {last_err}")

    async def get_events_by_month(self, series_id, year: int, month: int, promo_code: str = "") -> list:
        url = f"https://{self.domain}/api/consumer/events/getbymonth/{series_id}"
        params = {"requestedTime": f"{year}/{month}/01", "salesChannel": "Web", "promoCode": promo_code}
        data = await self._get_json(url, params=params, series_id=series_id)
        if self.on_raw:
            self.on_raw("events_by_month",
                        {"series_id": str(series_id), "year": year, "month": month}, data)
        return data.get("events", []) or []

    async def get_event_inventory(self, event_id) -> dict:
        url = f"https://{self.domain}/api/consumer/eventinventory/{event_id}"
        data = await self._get_json(url)
        if self.on_raw:
            self.on_raw("eventinventory", {"event_id": event_id}, data)
        return data

    async def iter_events(self, series_id, start_ym, end_ym, promo_code: str = ""):
        """start_ym/end_ym: (year, month) tuples. Yields deduplicated Events."""
        from .models import Event
        y, m = start_ym
        seen = {}
        while (y, m) <= end_ym:
            print(f"  [month {y}-{m:02d}] fetching event list...", file=sys.stderr)
            for e in await self.get_events_by_month(series_id, y, m, promo_code):
                seen[e["id"]] = Event(id=e["id"], local_date=e.get("localDate", ""),
                                       availability_color=e.get("availabilityColor", ""),
                                       name=e.get("name", ""), series_id=str(series_id))
            await asyncio.sleep(self.sleep)
            m += 1
            if m > 12:
                m = 1
                y += 1
        return sorted(seen.values(), key=lambda e: e.local_date)

    async def get_all_inventory(self, events: list) -> dict:
        """events: list[Event]. Returns {event_id: raw_inventory_json}.
        Runs concurrently (bounded by self.concurrency). Errors on a single
        performance are skipped (won't fail the whole crawl)."""
        out = {}
        n = len(events)
        done = 0
        sem = asyncio.Semaphore(max(1, self.concurrency))

        async def fetch_one(e):
            nonlocal done
            async with sem:
                try:
                    out[e.id] = await self.get_event_inventory(e.id)
                except RuntimeError as err:
                    print(f"    !! skipping event {e.id}: {err}", file=sys.stderr)
                done += 1
                print(f"  [{done}/{n}] eventinventory {e.id} ({e.local_date}) done", file=sys.stderr)
                await asyncio.sleep(self.sleep)

        await asyncio.gather(*(fetch_one(e) for e in events))
        return out
