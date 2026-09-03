"""stubhub: pulls + normalizes ticket-listing data from stubhub.com into the
same shape the broadwaydirect package produces, so both feed one MongoDB
schema (raw_events + cleaned_events, keyed by (source, event_id)).

Scope mirrors broadwaydirect exactly: fetch public listing data
(client.py), normalize it into price_levels + listings (adapter.py), and
save to MongoDB (reuses broadwaydirect.mongo_storage). It does NOT push
listings to any secondary marketplace.

Why this looks different from broadwaydirect internally:
  - StubHub renders listings server-side inside a React tree
    (memoizedProps.indexData); there is no public JSON API. client.py uses
    a real browser (patchright) to get past DataDome, then reads the
    embedded state + sweeps one SSR request per stadium section.
  - StubHub listings are ALREADY grouped (each grid.items[] entry is a
    bundle of N seats at one price), so there is no seat-grouping step like
    broadwaydirect.grouping - adapter.py is a straight field mapping.
"""
