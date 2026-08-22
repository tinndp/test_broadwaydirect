"""Shared data structures used throughout the project."""

from dataclasses import dataclass, field
from typing import Optional


@dataclass
class Event:
    id: int
    local_date: str
    availability_color: str = ""
    name: str = ""            # e.g. "Hamilton (NY)" - taken from the "name" field of events_by_month
    series_id: str = ""       # used to rebuild the ticket page URL: /tickets/series/{series_id}


@dataclass
class PriceLevel:
    price_level_id: int
    display_name: str
    zone: str
    price: float
    display_price: float
    price_class: str = ""


@dataclass
class Seat:
    key: str                 # e.g. "ORCH L-C-5"
    price_level_id: int
    section: str = ""        # e.g. "ORCH L"  (parsed from key)
    row: str = ""             # e.g. "C"
    seat_num: int = 0         # e.g. 5
    ada_type: str = "None"
    is_reserved: bool = False
    is_kill: bool = False


@dataclass
class Listing:
    """A group of contiguous seats, already labeled per the SIDES/CENTER
    rules, ready to feed into the inventory-management step (not
    automatically pushed anywhere)."""
    section_label: str        # e.g. "ORCH L SIDES" or "ORCH C CENTER" or "BOX"
    row: str
    price_level_id: int
    seat_keys: list = field(default_factory=list)   # ascending order
    seat_nums: list = field(default_factory=list)
    seating_type: str = "Consecutive"  # "Consecutive" or "Odd/Even"

    @property
    def quantity(self) -> int:
        return len(self.seat_keys)

    @property
    def seat_range_label(self) -> str:
        if not self.seat_nums:
            return ""
        if len(self.seat_nums) == 1:
            return str(self.seat_nums[0])
        return f"{self.seat_nums[0]}-{self.seat_nums[-1]}"
