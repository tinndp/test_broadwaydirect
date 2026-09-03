import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", ".."))

from stubhub.client import _section_specs, _ticket_class_ids


def test_ticket_class_ids_from_classes_and_popup():
    boot = {
        "ticketClasses": [{"ticketClassId": 1957}, {"ticketClassId": 3917}],
        "ticketClassPopupData": {"3892": {"count": 8}, "3631": {"count": 1}},
    }
    assert _ticket_class_ids(boot) == {"1957", "3917", "3892", "3631"}


def test_ticket_class_ids_empty():
    assert _ticket_class_ids({}) == set()


def test_section_specs_dominant_prefix_is_plain():
    # "1957" is the venue prefix (dominant); its sections get no ticketClasses
    keys = ["1957_1473972", "1957_1473980", "1957_1473990"]
    assert _section_specs(keys, {"3917", "3892"}) == [
        {"sec": "1473972", "tc": ""},
        {"sec": "1473980", "tc": ""},
        {"sec": "1473990", "tc": ""},
    ]


def test_section_specs_minority_ticketclass_prefix_sets_tc():
    # report Step 6: "3917_1338877" (Loge Box MVP) needs &ticketClasses=3917
    keys = [
        "1957_1", "1957_2", "1957_3",   # venue prefix -> dominant
        "3917_1338877",                 # ticket-class prefix -> tc
        "3892_1476207",
    ]
    assert _section_specs(keys, {"3917", "3892"}) == [
        {"sec": "1", "tc": ""},
        {"sec": "2", "tc": ""},
        {"sec": "3", "tc": ""},
        {"sec": "1338877", "tc": "3917"},
        {"sec": "1476207", "tc": "3892"},
    ]


def test_section_specs_same_section_under_both_prefixes_yields_both():
    keys = ["1957_500", "1957_501", "3917_500"]
    assert _section_specs(keys, {"3917"}) == [
        {"sec": "500", "tc": ""},
        {"sec": "501", "tc": ""},
        {"sec": "500", "tc": "3917"},
    ]


def test_section_specs_unknown_minority_prefix_not_a_ticketclass_stays_plain():
    # a minority prefix that is NOT a known ticketClassId is left plain rather
    # than sending a bogus &ticketClasses= value
    keys = ["1957_1", "1957_2", "9999_3"]
    assert _section_specs(keys, {"3917", "3892"}) == [
        {"sec": "1", "tc": ""},
        {"sec": "2", "tc": ""},
        {"sec": "3", "tc": ""},
    ]


def test_section_specs_no_ticket_class_set_trusts_dominant_only():
    keys = ["1957_1", "1957_2", "3917_3"]
    assert _section_specs(keys) == [
        {"sec": "1", "tc": ""},
        {"sec": "2", "tc": ""},
        {"sec": "3", "tc": "3917"},
    ]


def test_section_specs_skips_empty_section_ids_keeps_bare_id():
    # "" and "1957_" carry no section id -> skipped; a bare "12345" (no
    # separator) is kept as a plain section
    assert _section_specs(["", "1957_", "12345"], {"1957"}) == [
        {"sec": "12345", "tc": ""},
    ]
