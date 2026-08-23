import pytest

from broadwaydirect.proxy_pool import ProxyPool, load_proxies, normalize_proxy


def test_normalize_proxy_converts_raw_format():
    assert normalize_proxy("82.29.144.169:61234:events_9a0bb1f1385b:QD4eTl82") == \
        "http://events_9a0bb1f1385b:QD4eTl82@82.29.144.169:61234"


def test_normalize_proxy_passes_through_existing_uri():
    uri = "http://alice:secret@1.2.3.4:8080"
    assert normalize_proxy(uri) == uri


def test_normalize_proxy_rejects_malformed_input():
    with pytest.raises(ValueError):
        normalize_proxy("not-a-valid-proxy")


def test_load_proxies_parses_and_converts(tmp_path):
    f = tmp_path / "proxies.txt"
    f.write_text("1.2.3.4:61234:alice:secret\n5.6.7.8:61234:bob:hunter2\n")
    assert load_proxies(str(f)) == [
        "http://alice:secret@1.2.3.4:61234",
        "http://bob:hunter2@5.6.7.8:61234",
    ]


def test_load_proxies_strips_crlf_and_blank_lines(tmp_path):
    f = tmp_path / "proxies.txt"
    f.write_bytes(b"1.2.3.4:61234:alice:secret\r\n\r\n5.6.7.8:61234:bob:hunter2\r\n")
    assert load_proxies(str(f)) == [
        "http://alice:secret@1.2.3.4:61234",
        "http://bob:hunter2@5.6.7.8:61234",
    ]


def test_load_proxies_rejects_malformed_line(tmp_path):
    f = tmp_path / "proxies.txt"
    f.write_text("not-a-valid-line\n")
    with pytest.raises(ValueError):
        load_proxies(str(f))


def test_pool_round_robins(tmp_path):
    f = tmp_path / "proxies.txt"
    f.write_text("1.2.3.4:61234:alice:secret\n5.6.7.8:61234:bob:hunter2\n")
    pool = ProxyPool(str(f))
    assert len(pool) == 2
    seen = [pool.next() for _ in range(4)]
    assert seen == [
        "http://alice:secret@1.2.3.4:61234",
        "http://bob:hunter2@5.6.7.8:61234",
        "http://alice:secret@1.2.3.4:61234",
        "http://bob:hunter2@5.6.7.8:61234",
    ]


def test_pool_rejects_empty_file(tmp_path):
    f = tmp_path / "proxies.txt"
    f.write_text("")
    with pytest.raises(ValueError):
        ProxyPool(str(f))
