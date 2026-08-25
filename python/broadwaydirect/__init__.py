"""broadwaydirect: a tool that pulls + processes ticket data from sites
running on the Tixtrack / Broadway Direct platform (e.g.
tickets.broadwaydirect.com).

This package's scope is limited to: fetching data (client.py), grouping
individual seats into 'listings' per the naming rules (grouping.py), and
saving data to MongoDB (mongo_storage.py). It does NOT include pushing listings to
any secondary marketplace (no "Event Mapper -> Add to TA -> Broadcast via
Autopilot" or "undercutter" pricing bot) - that's out of scope for this
project, see README.md's "Scope & limitations" section for why.
"""
