"""StubHub-side data holders.

`PriceLevel` is reused verbatim from broadwaydirect so cleaned_events'
price_levels documents are byte-for-byte the same shape across sources.

`Listing` needs its own class (it can't reuse broadwaydirect.models.Listing)
for two reasons:
  - broadwaydirect's Listing derives `quantity` from `len(seat_keys)`, which
    only works with one key per seat. StubHub only exposes per-seat keys when
    `hasSeatDetails=true`, so quantity is an explicit field here (from
    `availableTickets`) and seat_keys is `[]` otherwise.
  - broadwaydirect's Listing derives `seat_range` from `seat_nums`; StubHub
    provides `seatFrom`/`seatTo` strings directly, so seat_range is explicit.
    `seat_detail_level` records whether that range is StubHub-confirmed
    ("exact"), seller-declared ("declared"), or absent ("none").

`seat_range_label` is kept as a property alias so MongoStore.save_cleaned_event
(written against broadwaydirect.models.Listing) accepts these objects
unchanged - cleaned_events therefore carries the same 7 listing keys as
broadwaydirect. The extra per-listing price (`raw_price`/`currency`) lives
only in raw_events and in the HTTP response, never in cleaned_events.
"""

from dataclasses import dataclass, field

from broadwaydirect.models import PriceLevel  # re-exported; identical shape

__all__ = ["PriceLevel", "Listing"]


@dataclass
class Listing:
    section_label: str            # StubHub sectionMapName (fallback: section)
    row: str                      # StubHub row (fallback: rowContent minus "Row ")
    price_level_id: int           # StubHub ticketClass id -> joins to PriceLevel
    quantity: int                 # StubHub availableTickets (NOT len(seat_keys))
    seat_range: str               # "9001-9007" / "9001", or "" - see seat_detail_level
    seat_keys: list = field(default_factory=list)  # per-seat keys, [] unless seat_detail_level == "exact"
    seat_detail_level: str = "none"     # "exact" (StubHub-confirmed) | "declared" (seller) | "none"
    seating_type: str = "Consecutive"   # "Consecutive" (isSeatedTogether) | "Piggyback"
    raw_price: float = 0.0        # per-listing price in listing currency (raw_events / HTTP only)
    currency: str = "USD"         # StubHub listingCurrencyCode

    @property
    def seat_range_label(self) -> str:
        """Alias so broadwaydirect's MongoStore.save_cleaned_event (which
        reads `.seat_range_label`) works on these objects unchanged."""
        return self.seat_range
