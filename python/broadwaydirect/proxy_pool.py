"""Loads a proxy list file (one `host:port:user:pass` per line - the format
proxy providers commonly hand out, e.g. `proxylist.txt` at the repo root)
and round-robins through it. Tolerant of CRLF line endings and blank lines
(CRLF trips up naive `line.split(":")` parsing - the trailing `\\r` ends up
stuck on the password and breaks proxy auth).

Optional - only used by api.py when PROXY_LIST_PATH is set and a request
doesn't supply its own `proxy` field (see api.py's `_pick_proxy`)."""
import itertools
import threading
from pathlib import Path


def normalize_proxy(raw: str) -> str:
    """Accepts either an already-proper `scheme://[user:pass@]host:port` URI
    (returned unchanged) or the raw `host:port:user:pass` format proxy
    providers hand out (converted to `http://user:pass@host:port` - the
    format ProxyUri.Parse/urlparse expect elsewhere in this project)."""
    line = raw.strip()
    if "://" in line:
        return line
    parts = line.split(":", 3)
    if len(parts) != 4:
        raise ValueError(f"expected host:port:user:pass or scheme://[user:pass@]host:port, got {raw!r}")
    host, port, user, pwd = parts
    return f"http://{user}:{pwd}@{host}:{port}"


def load_proxies(path: str) -> list[str]:
    """Parses each non-blank line of `path` via `normalize_proxy`. Raises
    ValueError on a malformed line so a typo'd list fails at load time, not
    silently mid-run."""
    proxies = []
    for lineno, raw_line in enumerate(Path(path).read_text().splitlines(), start=1):
        line = raw_line.strip()
        if not line:
            continue
        try:
            proxies.append(normalize_proxy(line))
        except ValueError as e:
            raise ValueError(f"{path}:{lineno}: {e}") from e
    return proxies


class ProxyPool:
    """Thread-safe round-robin cycle over the proxies loaded from `path`."""

    def __init__(self, path: str):
        self.path = path
        self._proxies = load_proxies(path)
        if not self._proxies:
            raise ValueError(f"no proxies found in {path}")
        self._cycle = itertools.cycle(self._proxies)
        self._lock = threading.Lock()

    def next(self) -> str:
        with self._lock:
            return next(self._cycle)

    def __len__(self) -> int:
        return len(self._proxies)
