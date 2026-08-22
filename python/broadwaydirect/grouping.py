"""
Parses mapSeats -> groups individual seats into 'listings' following the
client's exact rules:

  - key looks like "Section-Row-Seat#" (e.g. "ORCH L-C-5", "MEZZ-D-103").
  - Section in the ORCH/FMEZZ/RMEZZ group (configured in section_rules.json):
      seat# <= threshold (default 100) -> append 'SIDES', type Odd/Even
      seat# >  threshold                -> append 'CENTER', type Consecutive
  - Section is Box: no suffix appended, type Consecutive.
  - Price code (priceLevelId / class) IS A DELIMITER: two adjacent seats
    with a different price code are NOT grouped into the same listing, even
    if the seat numbers are consecutive.
  - Seats with adaType != 'None' (Wheelchair/Companion) are split out
    separately, not grouped with regular seats (since they have different
    sale conditions).
  - Seats with isReserved=True or isKill=True (or listed in killSeatKeys)
    are excluded before grouping, since they're no longer available to sell.
"""

import json
from typing import Iterable

from .models import Seat, Listing


DEFAULT_RULES = {
    "sides_center_prefixes": ["ORCH", "FMEZZ", "RMEZZ", "MEZZ"],
    "box_prefixes": ["BOX"],
    "center_seat_threshold": 100,
    "sides_suffix": "SIDES",
    "center_suffix": "CENTER",
}


def load_rules(path: str = None) -> dict:
    if not path:
        return dict(DEFAULT_RULES)
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    rules = dict(DEFAULT_RULES)
    for k in ("sides_center_prefixes", "box_prefixes", "center_seat_threshold",
              "sides_suffix", "center_suffix"):
        if k in data:
            rules[k] = data[k]
    return rules


def parse_seat_key(key: str):
    """'ORCH L-C-5' -> ('ORCH L', 'C', 5). Uses rsplit since the section
    name may contain spaces but never contains a '-'."""
    parts = key.rsplit("-", 2)
    if len(parts) != 3:
        raise ValueError(f"Could not parse seat key: {key!r} (expected Section-Row-Seat# format)")
    section, row, seat_str = parts
    try:
        seat_num = int(seat_str)
    except ValueError:
        raise ValueError(f"Seat# part is not an integer in key: {key!r}")
    return section, row, seat_num


def classify_section(section: str, seat_num: int, rules: dict):
    """Returns (suffix_or_None, seating_type) per the client's rule table."""
    threshold = rules["center_seat_threshold"]

    if any(section.upper().startswith(p.upper()) for p in rules["box_prefixes"]):
        return None, "Consecutive"

    if any(section.upper().startswith(p.upper()) for p in rules["sides_center_prefixes"]):
        if seat_num > threshold:
            return rules["center_suffix"], "Consecutive"
        return rules["sides_suffix"], "Odd/Even"

    # section doesn't match any configured rule -> fall back to the same
    # default as ORCH/FMEZZ/RMEZZ so no data is lost, but this should be
    # added to section_rules.json once a new section is discovered.
    if seat_num > threshold:
        return rules["center_suffix"], "Consecutive"
    return rules["sides_suffix"], "Odd/Even"


def seats_from_inventory(inventory: dict) -> list:
    """Converts mapSeats (raw JSON from the eventinventory API) into a
    list[Seat] with section/row/seat_num parsed, with isReserved/isKill/
    killSeatKeys seats ALREADY EXCLUDED."""
    kill_keys = set(inventory.get("killSeatKeys", []) or [])
    seats = []
    for raw in inventory.get("mapSeats", []) or []:
        if raw.get("isReserved") or raw.get("isKill"):
            continue
        key = raw.get("key", "")
        if key in kill_keys:
            continue
        price_key = raw.get("priceKey") or ""
        try:
            plid = int(str(price_key).split("-")[0])
        except (ValueError, IndexError):
            continue
        try:
            section, row, seat_num = parse_seat_key(key)
        except ValueError:
            # key isn't in the Section-Row-Seat# format (e.g. GA seats) -> skip
            continue
        seats.append(Seat(
            key=key,
            price_level_id=plid,
            section=section,
            row=row,
            seat_num=seat_num,
            ada_type=raw.get("adaType") or "None",
            is_reserved=bool(raw.get("isReserved")),
            is_kill=bool(raw.get("isKill")),
        ))
    return seats


def group_into_listings(seats: Iterable[Seat], rules: dict = None,
                         include_ada: bool = False) -> list:
    """Groups a list of Seats (already filtered to sellable ones) into a
    list[Listing].

    Grouping key = (section_label_with_suffix_applied, row, price_level_id,
    seating_type, parity_if_Odd/Even). Within each group, seat_num is
    sorted ascending and split into contiguous 'runs' by the matching step:
    step=2 for Odd/Even (SIDES), step=1 for Consecutive (CENTER/Box/other).
    Price code (price_level_id) is already part of the grouping key, so
    it's always a delimiter.
    """
    rules = rules or DEFAULT_RULES
    buckets = {}
    for s in seats:
        if s.ada_type != "None" and not include_ada:
            continue
        suffix, seating_type = classify_section(s.section, s.seat_num, rules)
        label = f"{s.section} {suffix}".strip() if suffix else s.section
        parity = (s.seat_num % 2) if seating_type == "Odd/Even" else None
        bucket_key = (label, s.row, s.price_level_id, seating_type, parity)
        buckets.setdefault(bucket_key, []).append(s)

    listings = []
    for (label, row, plid, seating_type, _parity), group in buckets.items():
        step = 2 if seating_type == "Odd/Even" else 1
        group_sorted = sorted(group, key=lambda s: s.seat_num)
        run = [group_sorted[0]]
        for prev, cur in zip(group_sorted, group_sorted[1:]):
            if cur.seat_num - prev.seat_num == step:
                run.append(cur)
            else:
                listings.append(_make_listing(label, row, plid, seating_type, run))
                run = [cur]
        listings.append(_make_listing(label, row, plid, seating_type, run))

    return listings


def _make_listing(label, row, plid, seating_type, run) -> Listing:
    return Listing(
        section_label=label,
        row=row,
        price_level_id=plid,
        seat_keys=[s.key for s in run],
        seat_nums=[s.seat_num for s in run],
        seating_type=seating_type,
    )
