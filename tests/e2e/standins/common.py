"""Plumbing shared by the stand-ins: requests and responses, the record of every call, and injected failures."""
import json
import re
import threading
import time
import traceback
import urllib.parse
import uuid
from datetime import datetime, timezone
from http import HTTPStatus


def utc_now():
    return datetime.now(timezone.utc)


def iso(dt=None):
    """GitHub's and Cloudflare's timestamps: 2026-11-10T12:00:00Z."""
    return (dt or utc_now()).strftime("%Y-%m-%dT%H:%M:%SZ")


class ApiError(Exception):
    """An error answer: the status, and a body each stand-in shapes its own way."""

    def __init__(self, status, message, code=None, extra=None):
        super().__init__(message)
        self.status = status
        self.message = message
        self.code = code
        self.extra = extra or {}


class Response:
    def __init__(self, status=200, body=b"", headers=None, content_type=None):
        self.status = status
        self.body = body if isinstance(body, (bytes, bytearray)) else str(body).encode()
        self.headers = dict(headers or {})
        if content_type:
            self.headers["Content-Type"] = content_type

    @staticmethod
    def json(status, obj, headers=None):
        body = b"" if obj is None else json.dumps(obj, indent=None, separators=(",", ":")).encode()
        h = {"Content-Type": "application/json; charset=utf-8"}
        h.update(headers or {})
        return Response(status, body, h)

    @staticmethod
    def text(status, text, headers=None):
        h = {"Content-Type": "text/plain; charset=utf-8"}
        h.update(headers or {})
        return Response(status, text.encode(), h)


class Request:
    """One HTTP request, read whole (Content-Length or chunked, as .NET's JsonContent and git send them)."""

    def __init__(self, handler):
        self.method = handler.command
        url = urllib.parse.urlsplit(handler.path)
        self.path = url.path
        self.query_string = url.query
        self.query = {k: v[-1] for k, v in urllib.parse.parse_qs(url.query, keep_blank_values=True).items()}
        self.headers = handler.headers
        self.host = (handler.headers.get("Host") or "").split(":")[0].strip().lower()
        self.client = handler.client_address[0] if handler.client_address else ""
        self.body = read_body(handler)
        self._json = None

    def json(self, default=None):
        if not self.body:
            return default
        if self._json is None:
            try:
                self._json = json.loads(self.body.decode("utf-8"))
            except (ValueError, UnicodeDecodeError) as e:
                raise ApiError(400, f"Problems parsing JSON: {e}")
        return self._json


def read_body(handler):
    te = (handler.headers.get("Transfer-Encoding") or "").lower()
    if "chunked" in te:
        out = bytearray()
        while True:
            line = handler.rfile.readline()
            if not line:
                break
            size = int(line.strip().split(b";")[0] or b"0", 16)
            if size == 0:
                while True:
                    trailer = handler.rfile.readline()
                    if trailer in (b"\r\n", b"\n", b""):
                        break
                break
            out += handler.rfile.read(size)
            handler.rfile.readline()
        return bytes(out)
    length = int(handler.headers.get("Content-Length") or 0)
    return handler.rfile.read(length) if length > 0 else b""


def send(handler, resp):
    handler.send_response(resp.status)
    for k, v in resp.headers.items():
        handler.send_header(k, v)
    handler.send_header("Content-Length", str(len(resp.body)))
    handler.end_headers()
    if handler.command != "HEAD" and resp.body:
        handler.wfile.write(resp.body)


# Headers that say who called and how; Authorization is kept as the token's tag, never the token.
_KEPT_HEADERS = ("User-Agent", "Content-Type", "Accept", "X-GitHub-Api-Version", "Git-Protocol", "Content-Encoding",
                 "Transfer-Encoding", "If-None-Match")


def token_of(headers):
    """The token in an Authorization header (Bearer, token, or git's Basic x-access-token:<token>), or None."""
    auth = headers.get("Authorization") or headers.get("authorization")
    if not auth:
        return None
    scheme, _, cred = auth.strip().partition(" ")
    scheme = scheme.lower()
    if scheme in ("bearer", "token"):
        return cred.strip() or None
    if scheme == "basic":
        import base64
        try:
            user, _, password = base64.b64decode(cred.strip()).decode("utf-8", "replace").partition(":")
        except ValueError:
            return None
        return password or user or None
    return None


class Recorder:
    """Every call a stand-in answered, in order, with a sequence number scenarios use as a marker."""

    def __init__(self, keep=50000):
        self._lock = threading.Lock()
        self._calls = []
        self._seq = 0
        self._keep = keep

    def next_seq(self):
        with self._lock:
            self._seq += 1
            return self._seq

    @property
    def seq(self):
        with self._lock:
            return self._seq

    def start(self, req, kind):
        body_json = None
        body_text = None
        if req.body:
            try:
                body_json = json.loads(req.body.decode("utf-8"))
            except (ValueError, UnicodeDecodeError):
                body_text = req.body[:4000].decode("utf-8", "replace") if kind != "git" else None
        headers = {k: req.headers.get(k) for k in _KEPT_HEADERS if req.headers.get(k) is not None}
        token = token_of(req.headers)
        call = {
            "seq": self.next_seq(), "at": time.time(), "kind": kind, "host": req.host, "method": req.method,
            "path": req.path, "query": req.query, "headers": headers, "token": token, "body": body_json,
            "body_text": body_text, "status": None, "injected": None, "client": req.client,
        }
        with self._lock:
            self._calls.append(call)
            if len(self._calls) > self._keep:
                del self._calls[: len(self._calls) - self._keep]
        return call

    def calls(self, since=0, host=None, method=None, path=None, kind=None):
        rx = re.compile(path) if path else None
        with self._lock:
            out = list(self._calls)
        return [c for c in out if c["seq"] > since
                and (host is None or c["host"] == host)
                and (method is None or c["method"] == method.upper())
                and (kind is None or c["kind"] == kind)
                and (rx is None or rx.search(c["path"]))]

    def clear(self):
        with self._lock:
            self._calls.clear()


class Failures:
    """Injected failures: per host, method and path pattern, an answer (status, body, headers), a delay, or a dropped
    connection; for a number of calls or until cleared. An answer without a body comes in the host's own shape (see
    injected_response), and rate_limit ("primary" or "secondary") makes it the service's rate-limit answer."""

    RATE_LIMITS = (None, "primary", "secondary")

    def __init__(self):
        self._lock = threading.Lock()
        self._rules = []
        self._next = 1

    def add(self, spec):
        if spec.get("rate_limit") not in self.RATE_LIMITS:
            raise ValueError(f"rate_limit is primary or secondary, not {spec.get('rate_limit')!r}")
        rule = {
            "id": self._next_id(),
            "host": (spec.get("host") or "*").lower(),
            "method": (spec.get("method") or "*").upper(),
            "path": spec.get("path") or ".*",
            "query": spec.get("query"),
            "status": spec.get("status"),
            "body": spec.get("body"),
            "headers": spec.get("headers") or {},
            "rate_limit": spec.get("rate_limit"),
            "delay_ms": int(spec.get("delay_ms") or 0),
            "times": spec.get("times"),
            "action": spec.get("action") or "respond",
            "hits": 0,
        }
        re.compile(rule["path"])
        with self._lock:
            self._rules.append(rule)
        return rule

    def _next_id(self):
        with self._lock:
            n = self._next
            self._next += 1
            return n

    def remove(self, rule_id=None):
        with self._lock:
            if rule_id is None:
                self._rules.clear()
            else:
                self._rules = [r for r in self._rules if r["id"] != rule_id]

    def list(self):
        with self._lock:
            return [dict(r) for r in self._rules]

    def match(self, req):
        with self._lock:
            for rule in self._rules:
                if rule["host"] not in ("*", req.host):
                    continue
                if rule["method"] not in ("*", req.method):
                    continue
                if not re.search(rule["path"], req.path):
                    continue
                if rule["query"] and not re.search(rule["query"], req.query_string or ""):
                    continue
                if rule["times"] is not None and rule["hits"] >= int(rule["times"]):
                    continue
                rule["hits"] += 1
                return dict(rule)
        return None


GITHUB_DOCS = "https://docs.github.com/rest"
GITHUB_LIMITS = "https://docs.github.com/rest/using-the-rest-api/rate-limits-for-the-rest-api"


def github_rate_headers(rate):
    """GitHub's x-ratelimit-* headers for a caller's primary rate limit (rate: see gh.RateLimits.take)."""
    return {"x-ratelimit-limit": str(rate["limit"]), "x-ratelimit-remaining": str(max(0, rate["limit"] - rate["used"])),
            "x-ratelimit-used": str(rate["used"]), "x-ratelimit-reset": str(rate["reset"]), "x-ratelimit-resource": "core"}


def github_limit_message(rate):
    """GitHub's message when the primary rate limit is used up: it names the address for requests without a token, and
    the user for those with one."""
    if rate["anonymous"]:
        return (f"API rate limit exceeded for {rate['who']}. (But here's the good news: Authenticated requests get a higher "
                "rate limit. Check out the documentation for more details.)")
    return f"API rate limit exceeded for {rate['who']}."
# Cloudflare's error codes for an injected answer, by status: 10000 is its "Authentication error" (a missing permission),
# 971 its rate limit, 7003 a route it doesn't know.
CLOUDFLARE_CODES = {400: 1004, 401: 10000, 403: 10000, 404: 7003, 405: 10405, 429: 971}


def reason(status):
    try:
        return HTTPStatus(status).phrase
    except ValueError:
        return f"HTTP {status}"


def injected_status(rule, host):
    """The status an injected answer has: the rule's, or for a rate limit GitHub's 403 and Cloudflare's 429."""
    if rule.get("status"):
        return int(rule["status"])
    if rule.get("rate_limit"):
        return 429 if host == "api.cloudflare.com" else 403
    return None


def injected_response(rule, host, rate=None):
    """An injected answer, in the shape of the host it stands for when the rule gives no body:
    - api.github.com: GitHub's {message, documentation_url, status} with the caller's x-ratelimit-* headers (rate: its
      primary rate limit as the GitHub stand-in counts it, 60 an hour for an address without a token, 5,000 for a user
      with one); rate_limit "primary" is its 403 with x-ratelimit-remaining 0, the window's reset time and the message
      naming the address or the user, "secondary" its 403 with retry-after and the secondary-limit message;
    - api.cloudflare.com: Cloudflare's envelope {success: false, errors: [{code, message}], messages: [], result: null}
      (a rate limit: 429, error 971, Retry-After);
    - github.com (git): a line of text.
    The rule's own headers win over these, and a rule's body replaces the default one as it is."""
    status = injected_status(rule, host)
    body = rule.get("body")
    limit = rule.get("rate_limit")
    headers = {}
    if host == "api.github.com":
        rate = rate or {"limit": 5000, "used": 1, "reset": int(time.time()) + 3600, "anonymous": False, "who": "user ID 5123456"}
        headers = {"X-GitHub-Api-Version-Selected": "2022-11-28", "X-GitHub-Media-Type": "github.v3; format=json",
                   "X-GitHub-Request-Id": uuid.uuid4().hex[:20].upper(), **github_rate_headers(rate)}
        message, docs = reason(status), GITHUB_DOCS
        if limit == "primary":
            headers.update({"x-ratelimit-remaining": "0", "x-ratelimit-used": str(rate["limit"])})
            message, docs = github_limit_message(rate), GITHUB_LIMITS + "#about-the-primary-rate-limit"
        elif limit == "secondary":
            headers.update({"retry-after": "60"})
            message = ("You have exceeded a secondary rate limit. Please wait a few minutes before you try again. If you reach out to "
                       "GitHub Support for help, please include the request ID " + headers["X-GitHub-Request-Id"] + ".")
            docs = GITHUB_LIMITS + "#about-secondary-rate-limits"
        if body is None:
            body = {"message": message, "documentation_url": docs, "status": str(status)}
    elif host == "api.cloudflare.com":
        code = 971 if limit else CLOUDFLARE_CODES.get(status, 1000)
        message = "Please wait and consider throttling your request speed" if limit or status == 429 else \
            "Authentication error" if code == 10000 else reason(status)
        if limit or status == 429:
            headers["Retry-After"] = "300"
        if body is None:
            body = {"success": False, "errors": [{"code": code, "message": message}], "messages": [], "result": None}
    elif host in ("github.com", "www.github.com") and body is None:
        body = f"{reason(status)}\n"
    headers.update(rule.get("headers") or {})
    if body is None:
        return Response(status, b"", headers)
    if isinstance(body, (dict, list)):
        return Response.json(status, body, headers)
    headers.setdefault("Content-Type", "text/plain; charset=utf-8")
    return Response(status, str(body).encode(), headers)


_LOG_LOCK = threading.Lock()


def log(*parts):
    """One line of the stand-ins' log; lines from concurrent deliveries never run together."""
    line = " ".join([time.strftime("%H:%M:%S"), *map(str, parts)])
    with _LOG_LOCK:
        print(line, flush=True)


def log_exception(where):
    log(f"error in {where}:\n{traceback.format_exc()}")
