import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", ".."))

from stubhub.adapter import (
    build_event, normalize_listings, parse_price_levels,
)

FIXTURE = os.path.join(os.path.dirname(__file__), "fixtures", "sample_event_raw.json")


def load():
    with open(FIXTURE, "r", encoding="utf-8") as f:
        return json.load(f)


def test_price_levels_one_per_ticket_class():
    pls = parse_price_levels(load())
    assert len(pls) == 4
    by_id = {pl.price_level_id: pl for pl in pls}
    # price comes from ticketClassPopupData.rawMinPrice
    assert by_id[3875].price == 118.87
    assert by_id[3875].display_name == "Infield Box Value"
    assert by_id[3875].zone == "Infield Box Value"
    assert by_id[3875].price_class == "teal"
    assert by_id[1957].price == 57.37
    # class with no popup entry and no min: falls back to the min raw price
    # seen on its own collected listings, else 0.0
    assert by_id[3907].price == 0.0


def test_price_level_dict_shape_matches_broadwaydirect():
    from stubhub.api import _price_level_dict
    d = _price_level_dict(parse_price_levels(load())[0])
    assert set(d) == {"price_level_id", "display_name", "zone", "price",
                      "display_price", "price_class"}


def test_listings_count_and_shared_keys():
    ls = normalize_listings(load())
    assert len(ls) == 3
    from stubhub.api import _listing_dict
    d = _listing_dict(ls[0])
    # first 7 keys are exactly broadwaydirect's listing shape
    assert set(list(d)[:7]) == {"section_label", "row", "price_level_id",
                                "seating_type", "quantity", "seat_range", "seat_keys"}
    # seat_detail_level / raw_price / currency are the documented StubHub superset
    assert d["seat_detail_level"] == "none"
    assert d["raw_price"] == 49.97 and d["currency"] == "USD"


def test_listing_quantity_is_availabletickets_not_seatkey_count():
    ls = {l.section_label: l for l in normalize_listings(load())}
    # no seat detail -> no seat keys, but quantity still reflects the bundle
    fd = ls["34FD"]
    assert fd.seat_keys == []
    assert fd.quantity == 4
    assert fd.seat_range == ""
    assert fd.seat_detail_level == "none"
    assert fd.seating_type == "Consecutive"   # isSeatedTogether


def test_listing_with_seat_detail_expands_keys_and_range():
    rs = {l.section_label: l for l in normalize_listings(load())}["18RS"]
    assert rs.seat_range == "1-3"
    assert rs.seat_keys == ["18RS-GG-1", "18RS-GG-2", "18RS-GG-3"]
    assert rs.seat_detail_level == "exact"
    assert rs.quantity == 3


def _item(**over):
    it = {
        "sectionMapName": "163LG", "row": "P", "ticketClass": 3927,
        "hasSeatDetails": False, "seatFrom": "7", "seatTo": "11",
        "availableTickets": 5, "isSeatedTogether": True,
        "rawPrice": 72.01, "listingCurrencyCode": "USD",
    }
    it.update(over)
    return normalize_listings({"items": [it]})[0]


def test_declared_range_kept_but_no_seat_keys():
    # live shape: hasSeatDetails=false, seatFrom/seatTo consistent with the count
    l = _item()
    assert l.seat_range == "7-11"
    assert l.seat_detail_level == "declared"
    assert l.seat_keys == []            # not StubHub-confirmed -> no per-seat identity
    assert l.quantity == 5


def test_seat_span_rejects_width_mismatch():
    l = _item(seatFrom="7", seatTo="50", availableTickets=5)
    assert l.seat_range == "" and l.seat_detail_level == "none"


def test_seat_span_rejects_zone_and_hidden():
    assert _item(isZoneTicketClass=True).seat_detail_level == "none"
    assert _item(hideSeatAndRowInfo=True).seat_detail_level == "none"


def test_seat_span_rejects_nonnumeric_and_empty():
    assert _item(seatFrom="AA", seatTo="EE").seat_range == ""
    assert _item(seatFrom="", seatTo="").seat_range == ""


def test_exact_single_seat():
    l = _item(hasSeatDetails=True, seatFrom="9", seatTo="9", availableTickets=1)
    assert l.seat_range == "9"
    assert l.seat_keys == ["163LG-P-9"]
    assert l.seat_detail_level == "exact"


def test_listing_piggyback_seating_type():
    td = {l.section_label: l for l in normalize_listings(load())}["4TD"]
    assert td.seating_type == "Piggyback"     # isSeatedTogether == false
    assert td.row == "J"


def test_build_event_uses_jsonld_startdate():
    ev = build_event(load())
    assert ev.id == 159257698
    assert ev.name == "Washington Nationals at Los Angeles Dodgers"
    assert ev.local_date == "2026-09-04T19:10:00"
    assert ev.series_id == ""


def test_build_event_falls_back_to_formatted_datetime():
    raw = load()
    raw["sportsEvent"] = None
    ev = build_event(raw)
    assert ev.local_date.startswith("2026-09-04T19:10")
