"""StubHub-side data holders.

`PriceLevel` is reused verbatim from broadwaydirect so cleaned_events'
price_levels documents are byte-for-byte the same shape across sources.

`Listing` needs its own class (it can't reuse broadwaydirect.models.Listing)
for two reasons:
  - broadwaydirect's Listing derives `quantity` from `len(seat_keys)`, which
    only works when you have one key per seat. StubHub usually gives no
    per-seat detail (`hasSeatDetails=false`) - it sends no seat numbers at
    all - so quantity is an explicit field here and seat_keys is `[]`.
  - broadwaydirect's Listing derives `seat_range` from `seat_nums`; StubHub
    provides `seatFrom`/`seatTo` strings directly, so seat_range is explicit.

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
    seat_range: str               # "9001-9007", or "" when no seat detail
    seat_keys: list = field(default_factory=list)  # per-seat keys, or [] when no seat detail
    seating_type: str = "Consecutive"   # "Consecutive" (isSeatedTogether) | "Piggyback"
    raw_price: float = 0.0        # per-listing price in listing currency (raw_events / HTTP only)
    currency: str = "USD"         # StubHub listingCurrencyCode

    @property
    def seat_range_label(self) -> str:
        """Alias so broadwaydirect's MongoStore.save_cleaned_event (which
        reads `.seat_range_label`) works on these objects unchanged."""
        return self.seat_range
