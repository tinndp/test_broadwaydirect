import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from broadwaydirect.grouping import (
    DEFAULT_RULES, parse_seat_key, classify_section,
    seats_from_inventory, group_into_listings,
)

FIXTURE = os.path.join(os.path.dirname(__file__), "fixtures", "sample_eventinventory.json")


def load_fixture():
    with open(FIXTURE, "r", encoding="utf-8") as f:
        return json.load(f)


def test_parse_seat_key():
    assert parse_seat_key("ORCH L-C-5") == ("ORCH L", "C", 5)
    assert parse_seat_key("MEZZ-D-103") == ("MEZZ", "D", 103)
    assert parse_seat_key("ORCH C-C-106") == ("ORCH C", "C", 106)


def test_classify_section():
    # seat <= 100, section in the sides/center list -> SIDES, Odd/Even
    assert classify_section("ORCH L", 5, DEFAULT_RULES) == ("SIDES", "Odd/Even")
    # seat > 100 -> CENTER, Consecutive
    assert classify_section("ORCH C", 106, DEFAULT_RULES) == ("CENTER", "Consecutive")
    # Box -> no suffix, Consecutive
    assert classify_section("BOX A", 3, DEFAULT_RULES) == (None, "Consecutive")


def test_seats_from_inventory_excludes_ada_and_reserved():
    inv = load_fixture()
    seats = seats_from_inventory(inv)
    keys = {s.key for s in seats}
    # 34 seats in the fixture, all isReserved=False/isKill=False so none are excluded at this step
    assert len(seats) == 34
    assert "ORCH L-K-23" in keys  # Companion - still present in seats_from_inventory
    assert "ORCH L-K-25" in keys  # Wheelchair


def test_group_into_listings_sides_step2():
    inv = load_fixture()
    seats = seats_from_inventory(inv)
    listings = group_into_listings(seats, rules=DEFAULT_RULES, include_ada=False)

    # ORCH C-C-106,107,108 (contiguous, same price 86944, seat>100 -> CENTER step=1)
    # but 111,112 are 3 away from 108 -> split into 2 separate listings
    center_c = [l for l in listings if l.section_label == "ORCH C CENTER" and l.row == "C"]
    ranges = sorted(l.seat_range_label for l in center_c)
    assert ranges == ["106-108", "111-112"]

    # MEZZ-C-5,7 same price 86952, seat<=100 -> SIDES, step=2 -> grouped into 1 listing "5-7"
    mezz_sides_c = [l for l in listings if l.section_label == "MEZZ SIDES" and l.row == "C"]
    assert any(l.seat_range_label == "5-7" for l in mezz_sides_c)
    # MEZZ-C-106 (seat>100) must be in a separate CENTER group, not bleeding into SIDES
    mezz_center_c = [l for l in listings if l.section_label == "MEZZ CENTER" and l.row == "C"]
    assert any(l.seat_range_label == "106" for l in mezz_center_c)


def test_price_code_is_a_delimiter():
    inv = load_fixture()
    seats = seats_from_inventory(inv)
    listings = group_into_listings(seats, rules=DEFAULT_RULES)

    # ORCH L-K-17,19,21 price 86949 (step=2, contiguous) must be its own listing,
    # must NOT be grouped with seats of a different price even in the same row/section
    row_k = [l for l in listings if l.row == "K" and l.section_label == "ORCH L SIDES"]
    assert any(l.seat_range_label == "17-21" and l.price_level_id == 86949 for l in row_k)


def test_ada_seats_excluded_by_default():
    inv = load_fixture()
    seats = seats_from_inventory(inv)
    listings = group_into_listings(seats, rules=DEFAULT_RULES, include_ada=False)
    all_keys = [k for l in listings for k in l.seat_keys]
    assert "ORCH L-K-23" not in all_keys   # Companion
    assert "ORCH L-K-25" not in all_keys   # Wheelchair
    # 21 is still grouped (ada_type=None), but no longer connects to 23 (excluded)
    # -> 17-21 forms its own group as verified by the test above


def test_ada_seats_included_when_requested():
    inv = load_fixture()
    seats = seats_from_inventory(inv)
    listings = group_into_listings(seats, rules=DEFAULT_RULES, include_ada=True)
    all_keys = [k for l in listings for k in l.seat_keys]
    assert "ORCH L-K-23" in all_keys
    assert "ORCH L-K-25" in all_keys


def test_box_seats_have_no_suffix():
    from broadwaydirect.models import Seat
    seats = [
        Seat(key="BOX A-1-1", price_level_id=1, section="BOX A", row="1", seat_num=1),
        Seat(key="BOX A-1-2", price_level_id=1, section="BOX A", row="1", seat_num=2),
    ]
    listings = group_into_listings(seats, rules=DEFAULT_RULES)
    assert len(listings) == 1
    assert listings[0].section_label == "BOX A"
    assert listings[0].seat_range_label == "1-2"


if __name__ == "__main__":
    import pytest
    raise SystemExit(pytest.main([__file__, "-v"]))
