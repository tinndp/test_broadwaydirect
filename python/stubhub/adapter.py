"""Maps a StubHub raw-inventory dict (assembled by client.py, stored in
raw_events) into the common price_levels + listings shape.

Input dict (`raw`) shape - see client.StubHubClient.fetch_event_inventory:
    {
      "eventId": "159257698",
      "eventName": "Washington Nationals at Los Angeles Dodgers",
      "eventUrl": "...", "venueName": "...", "venueId": 1817,
      "formattedEventDateTime": "Fri Sep 04 2026 7:10 PM",
      "sportsEvent": { ...schema.org JSON-LD... } | None,
      "totalCount": 660,                     # grid.totalCount at quantity=0
      "ticketClasses":        [ {ticketClassId, name, cssPostFix, ...}, ... ],
      "ticketClassPopupData": { "3631": {rawMinPrice, count, ...}, ... },
      "items":                [ { ...75-field grid item... }, ... ],   # deduped by id
      "coverage": {collected, totalCount, sections}
    }

StubHub listings are already grouped (one grid item == one bundle of N
seats at one price), so this is a straight field mapping - there is no
seat-grouping step like broadwaydirect.grouping.
"""

from typing import Optional

from broadwaydirect.models import Event
from .models import Listing, PriceLevel


# --------------------------------------------------------------------------
# price_levels: one per ticket class (the ~19 seating zones)
# --------------------------------------------------------------------------
def parse_price_levels(raw: dict) -> list:
    classes = raw.get("ticketClasses") or []
    popup = raw.get("ticketClassPopupData") or {}

    # fallback min price straight off the collected listings, per class
    item_min: dict = {}
    for it in raw.get("items") or []:
        cid, rp = it.get("ticketClass"), it.get("rawPrice")
        if cid is not None and isinstance(rp, (int, float)):
            item_min[cid] = rp if cid not in item_min else min(item_min[cid], rp)

    out = []
    for c in classes:
        cid = c.get("ticketClassId")
        p = popup.get(str(cid)) or {}
        price = p.get("rawMinPrice") or c.get("minListingsRawPrice") or item_min.get(cid) or 0.0
        out.append(PriceLevel(
            price_level_id=cid,
            display_name=c.get("name", "") or "",
            zone=c.get("name", "") or "",
            price=float(price or 0.0),
            display_price=float(price or 0.0),   # StubHub fees are per-listing; no class-level display price
            price_class=c.get("cssPostFix", "") or "",
        ))
    return out


# --------------------------------------------------------------------------
# listings: one per collected grid item
# --------------------------------------------------------------------------
def _row(it: dict) -> str:
    r = (it.get("row") or "").strip()
    if r and r != "_":
        return r
    rc = (it.get("rowContent") or "").strip()
    return rc[4:].strip() if rc[:4].lower() == "row " else rc


def _seat_span(it: dict):
    """`(lo, hi)` when `seatFrom`/`seatTo` describe one contiguous block that
    matches `availableTickets`; otherwise `None`. Independent of
    `hasSeatDetails` (which is unreliable - StubHub sends a consistent range
    on plenty of `hasSeatDetails=false` listings too). The width==quantity
    check is what separates a real reserved-seat block from a zone ticket, a
    seller typo, or a partial/placeholder range."""
    if it.get("isZoneTicketClass") or it.get("hideSeatAndRowInfo"):
        return None
    try:
        a, b = int(it.get("seatFrom")), int(it.get("seatTo"))
    except (TypeError, ValueError):
        return None
    lo, hi = min(a, b), max(a, b)
    if lo < 1 or hi - lo + 1 != (it.get("availableTickets") or 0):
        return None
    return lo, hi


def _seat_range(it: dict) -> str:
    """"7-11" (or "7" for a single seat) from `_seat_span`, else "". Populated
    for both `hasSeatDetails=true` (exact) and `false` (seller-declared) - see
    `_seat_detail_level`."""
    span = _seat_span(it)
    if not span:
        return ""
    lo, hi = span
    return str(lo) if lo == hi else f"{lo}-{hi}"


def _seat_keys(it: dict) -> list:
    """Per-seat keys "SECTION-ROW-N" ONLY when StubHub confirms the exact
    seats (`hasSeatDetails=true`). For a seller-declared range the numbers
    are not guaranteed (you get N seats together in the row, but not
    necessarily those exact ones), so keying on them would assert false
    per-seat identity - return [] and let identity fall to the listing id."""
    if not it.get("hasSeatDetails"):
        return []
    span = _seat_span(it)
    row = _row(it)
    if not span or not row:
        return []
    sec = (it.get("sectionMapName") or it.get("section") or "").strip()
    lo, hi = span
    return [f"{sec}-{row}-{n}" for n in range(lo, hi + 1)]


def _seat_detail_level(it: dict) -> str:
    """"exact"    - hasSeatDetails=true and a consistent range (seat_keys populated)
    "declared" - range present & consistent but seller-supplied, not confirmed
    "none"     - no usable seat range (zone/GA/parking, hidden, empty, mismatch)"""
    if _seat_span(it):
        return "exact" if it.get("hasSeatDetails") else "declared"
    return "none"


def normalize_listings(raw: dict) -> list:
    out = []
    for it in raw.get("items") or []:
        keys = _seat_keys(it)
        out.append(Listing(
            section_label=(it.get("sectionMapName") or it.get("section") or "").strip(),
            row=_row(it),
            price_level_id=it.get("ticketClass"),
            quantity=int(it.get("availableTickets") or len(keys) or 1),
            seat_range=_seat_range(it),
            seat_keys=keys,
            seat_detail_level=_seat_detail_level(it),
            seating_type="Consecutive" if it.get("isSeatedTogether") else "Piggyback",
            raw_price=float(it.get("rawPrice") or 0.0),
            currency=it.get("listingCurrencyCode") or "USD",
        ))
    return out


# --------------------------------------------------------------------------
# event metadata for cleaned_events (name / local_date)
# --------------------------------------------------------------------------
def build_event(raw: dict) -> Event:
    se = raw.get("sportsEvent") or {}
    local_date = se.get("startDate") or _iso_from_formatted(raw.get("formattedEventDateTime"))
    return Event(
        id=int(raw["eventId"]),
        local_date=local_date or "",
        availability_color="",
        name=raw.get("eventName") or se.get("name") or "",
        series_id="",   # StubHub has no series concept
    )


def _iso_from_formatted(s: Optional[str]) -> str:
    """Best-effort: "Fri Sep 04 2026 7:10 PM" -> ISO. Returns "" on failure;
    the JSON-LD startDate is the primary source, this is only a fallback."""
    if not s:
        return ""
    from datetime import datetime
    for fmt in ("%a %b %d %Y %I:%M %p", "%a %b %d %Y %H:%M"):
        try:
            return datetime.strptime(s.strip(), fmt).isoformat()
        except ValueError:
            continue
    return ""
