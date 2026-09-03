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


def _seat_range(it: dict) -> str:
    if not it.get("hasSeatDetails"):
        return ""
    a, b = it.get("seatFrom"), it.get("seatTo")
    if a in (None, "") or b in (None, ""):
        return ""
    return str(a) if str(a) == str(b) else f"{a}-{b}"


def _seat_keys(it: dict) -> list:
    """Per-seat keys "SECTION-ROW-N" when StubHub exposes a seat range that
    lines up with the ticket count; otherwise a single opaque key = the
    listing id (quantity still carries the real count)."""
    if it.get("hasSeatDetails"):
        sec = (it.get("sectionMapName") or it.get("section") or "").strip()
        row = _row(it)
        try:
            a_i, b_i = int(it.get("seatFrom")), int(it.get("seatTo"))
        except (TypeError, ValueError):
            a_i = b_i = None
        if a_i is not None and b_i is not None:
            nums = list(range(min(a_i, b_i), max(a_i, b_i) + 1))
            qty = it.get("availableTickets") or 0
            if 0 < len(nums) <= max(qty, 1) + 4:
                return [f"{sec}-{row}-{n}" for n in nums]
    return [str(it.get("id"))]


def normalize_listings(raw: dict) -> list:
    out = []
    for it in raw.get("items") or []:
        out.append(Listing(
            section_label=(it.get("sectionMapName") or it.get("section") or "").strip(),
            row=_row(it),
            price_level_id=it.get("ticketClass"),
            quantity=int(it.get("availableTickets") or len(_seat_keys(it))),
            seat_range=_seat_range(it),
            seat_keys=_seat_keys(it),
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
