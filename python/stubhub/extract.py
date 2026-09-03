"""Pulls the embedded JSON out of StubHub's server-rendered event HTML.

StubHub does not expose a listing API. Every event page ships its full
React state inline; the pieces this project needs are:

  - `"grid":{"items":[ ... ]}`          - the ticket listings (10 per SSR
                                           page, or all of a section's when
                                           fetched with &sections=<id>)
  - `"ticketClasses":[ ... ]`           - the ~19 seating zones (id + name)
  - `"ticketClassPopupData":{ ... }`    - per-zone min price / listing count
  - `"sectionPopupData":{ ... }`        - keys are "<ticketClassId>_<sectionId>",
                                           used to enumerate every section id
  - `<script type="application/ld+json">` SportsEvent - event name/date/venue

`extract_json_token` is a bracket counter that walks a balanced `[...]` or
`{...}` starting at a marker, ignoring braces inside strings. It replaces
naive regex/`json.loads` on a slice, which breaks on nested objects inside
`items` (the exact bug called out in the extraction report). client.py runs
the equivalent logic in-page for speed; this module is the pure-Python copy
used by tests and reprocess.py.
"""

import json
import re
from typing import Optional


def extract_json_token(text: str, marker: str, start_from: int = 0):
    """Find `marker` in `text`, then parse the balanced JSON value that
    begins at the first `[` or `{` at/after the end of the marker. Braces
    and brackets inside double-quoted strings (with `\\` escaping) are
    ignored. Returns the parsed value, or None if the marker isn't found or
    the slice doesn't parse.
    """
    m = text.find(marker, start_from)
    if m < 0:
        return None
    i = m + len(marker)
    # skip whitespace / a leading ':' between the marker and the value
    while i < len(text) and text[i] in " \t\r\n:":
        i += 1
    if i >= len(text) or text[i] not in "[{":
        return None
    start = i
    depth_sq = depth_cu = 0
    in_str = escape = False
    while i < len(text):
        ch = text[i]
        if escape:
            escape = False
        elif ch == "\\" and in_str:
            escape = True
        elif ch == '"':
            in_str = not in_str
        elif not in_str:
            if ch == "[":
                depth_sq += 1
            elif ch == "{":
                depth_cu += 1
            elif ch == "]":
                depth_sq -= 1
            elif ch == "}":
                depth_cu -= 1
            if depth_sq == 0 and depth_cu == 0:
                i += 1
                break
        i += 1
    try:
        return json.loads(text[start:i])
    except json.JSONDecodeError:
        return None


def extract_grid_items(html: str) -> list:
    """Return the `grid.items` array embedded in an event-page SSR response.
    Empty list if not present (e.g. a 403 challenge page or an empty
    section)."""
    items = extract_json_token(html, '"grid":{"items":')
    if items is None:
        # some responses put other keys before "items" inside "grid"
        g = html.find('"grid":')
        if g >= 0:
            items = extract_json_token(html, '"items":', start_from=g)
    return items or []


def extract_sports_event(html: str) -> Optional[dict]:
    """Parse the schema.org SportsEvent JSON-LD block (event name, startDate,
    location). Returns None if absent or unparseable."""
    for m in re.finditer(
        r'<script[^>]+type="application/ld\+json"[^>]*>(.*?)</script>', html, re.S
    ):
        try:
            data = json.loads(m.group(1).strip())
        except json.JSONDecodeError:
            continue
        if isinstance(data, dict) and data.get("@type") in ("SportsEvent", "Event"):
            return data
        if isinstance(data, list):
            for d in data:
                if isinstance(d, dict) and d.get("@type") in ("SportsEvent", "Event"):
                    return d
    return None


def event_id_from_url(url: str) -> Optional[str]:
    """`https://www.stubhub.com/<slug>/event/159257698/?...` -> "159257698"."""
    m = re.search(r"/event/(\d+)", url or "")
    return m.group(1) if m else None
