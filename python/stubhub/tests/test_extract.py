import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", ".."))

from stubhub.extract import (
    extract_json_token, extract_grid_items, extract_named_grid,
    extract_sports_event, event_id_from_url,
)

FIXTURES = os.path.join(os.path.dirname(__file__), "fixtures")


def _read(name):
    with open(os.path.join(FIXTURES, name), "r", encoding="utf-8") as f:
        return f.read()


def test_extract_json_token_array_and_object():
    assert extract_json_token('x = "items": [1, 2, 3] end', '"items":') == [1, 2, 3]
    assert extract_json_token('"grid": {"a": 1, "b": [2]} tail', '"grid":') == {"a": 1, "b": [2]}
    assert extract_json_token("no marker here", '"items":') is None


def test_extract_json_token_ignores_brackets_inside_strings():
    # a ']' inside a string value must not end the array early
    text = '"items":[{"note":"aisle ] seat","x":1},{"y":2}]'
    assert extract_json_token(text, '"items":') == [{"note": "aisle ] seat", "x": 1}, {"y": 2}]


def test_extract_grid_items_handles_deep_nesting():
    # the report's bug: a naive counter exits early on nested {} inside items
    items = extract_grid_items(_read("sample_section_ssr.html"))
    assert len(items) == 2
    assert items[0]["id"] == 13909934699
    assert items[0]["inventoryListingScore"]["nested"]["b"] == [1, 2, {"c": 3}]
    assert items[1]["seatFrom"] == "1" and items[1]["hasSeatDetails"] is True


def test_extract_grid_items_empty_on_challenge_page():
    assert extract_grid_items("<html>Please enable JS</html>") == []


def test_extract_named_grid():
    html = _read("sample_category_ssr.html")
    rest = extract_named_grid(html, "restGrid")
    assert rest["totalCount"] == 38
    assert rest["pageIndex"] == 0
    assert rest["pageSize"] == 6
    assert [e["eventId"] for e in rest["items"]] == [159257696, 160436262]

    primary = extract_named_grid(html, "primaryGrid")
    assert primary["totalCount"] == 28
    assert len(primary["items"]) == 1

    assert extract_named_grid(html, "noSuchGrid") is None


def test_extract_sports_event():
    se = extract_sports_event(_read("sample_section_ssr.html"))
    assert se["@type"] == "SportsEvent"
    assert se["startDate"] == "2026-09-04T19:10:00"
    assert se["location"]["name"] == "UNIQLO Field at Dodger Stadium"


def test_event_id_from_url():
    assert event_id_from_url(
        "https://www.stubhub.com/los-angeles-dodgers-los-angeles-tickets-9-4-2026/event/159257698/"
    ) == "159257698"
    assert event_id_from_url("https://www.stubhub.com/event/159257698/?x=1") == "159257698"
    assert event_id_from_url("https://www.stubhub.com/los-angeles-dodgers-tickets/category/138300832") is None
    assert event_id_from_url("") is None
