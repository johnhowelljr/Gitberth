"""A small HTTP client (the standard library's), for the control API and for checking that apps answer."""
import json
import urllib.error
import urllib.request


class HttpError(AssertionError):
    def __init__(self, status, url, body):
        super().__init__(f"HTTP {status} from {url}: {body[:800]}")
        self.status = status
        self.body = body


def call(method, url, body=None, headers=None, timeout=30, raw=False):
    """Sends a request; returns (status, headers, text). Never raises for an HTTP status, only for no answer at all."""
    data = None
    hdrs = dict(headers or {})
    if body is not None:
        data = body if isinstance(body, (bytes, bytearray)) else json.dumps(body).encode()
        hdrs.setdefault("Content-Type", "application/json")
    req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, dict(r.headers), r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers or {}), (e.read() or b"").decode("utf-8", "replace")


def json_call(method, url, body=None, timeout=60):
    """A control API call: its JSON answer, or HttpError with what the stand-in said."""
    status, _, text = call(method, url, body, timeout=timeout)
    if status >= 400:
        raise HttpError(status, f"{method} {url}", text)
    return json.loads(text) if text else None


def get(url, timeout=10):
    """(status, text) of a GET; (0, the error) when nothing answers, so it can be polled."""
    try:
        status, _, text = call("GET", url, timeout=timeout)
        return status, text
    except OSError as e:
        return 0, f"{type(e).__name__}: {e}"


def get_json(url, timeout=10):
    """The JSON a GET answers with 200, or None (so until() keeps polling)."""
    status, text = get(url, timeout)
    if status != 200:
        return None
    try:
        return json.loads(text)
    except ValueError:
        return None
