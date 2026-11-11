#!/usr/bin/env python3
"""A fake cloudflared for the rig. Gitberth's TunnelLocator finds it as it finds a real one: an image named cloudflared,
started with `tunnel run --token <token>`, on the host's network (or Gitberth's, or neither). It never contacts
Cloudflare. It plays the connector of a remotely managed tunnel:

- Configurations: it polls the Cloudflare stand-in for the tunnel's configuration, and takes a new version as
  cloudflared's orchestrator does (UpdateConfig). A version no newer than the one it runs is ignored. One it can't
  decode, or whose ingress it refuses (ingress.py: cloudflared's own checks), is logged as an error and the old one
  stays. Otherwise it logs the line Gitberth's wait for the tunnel reads (like all its log, on stderr, as cloudflared
  writes it):
      2026-11-10T12:00:00Z INF Updated to new configuration config="{\"ingress\":[...]}" version=7
- Traffic: the stand-in's "edge" sends each request Cloudflare would send through the tunnel to this connector's edge
  port (EDGE_PORT, 7844). It's routed by the ingress the connector runs, from its own network as cloudflared would
  (localhost is the host it runs on, names are its networks'): an http_status rule answers itself, a rule with
  originRequest.access refuses a request without a valid Access token (403), and an http(s) service gets the request
  with its Host. An origin that can't be reached gives 502. Until it runs a configuration it answers 503, as a new
  connector does. Its answers carry X-E2E-* headers saying which rule and service it used, which the stand-in records
  and never passes on.
- Each poll tells the stand-in the version it runs and where its edge port is.
"""
import base64
import json
import os
import signal
import ssl
import sys
import threading
import time
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone
from http.client import HTTPConnection, HTTPSConnection
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path[:0] = ["/usr/local/lib/gbe2e", os.path.dirname(os.path.dirname(os.path.abspath(__file__)))]
import ingress  # noqa: E402

VERSION = "2026.11.0"
EDGE_PORT = int(os.environ.get("EDGE_PORT", "7844"))
NETWORK = os.environ.get("E2E_NETWORK", "")


def log(level, message):
    """One line of cloudflared's console log, which it writes to stderr (zerolog's ConsoleWriter on os.Stderr)."""
    print(f"{datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')} {level} {message}", file=sys.stderr, flush=True)


def quote(text):
    """A field value as zerolog's console writer prints it: quoted when it has spaces, quotes or '='."""
    return json.dumps(text, ensure_ascii=False) if any(c in text for c in ' "\\=') or not text.isprintable() else text


def option(args, name):
    for i, a in enumerate(args):
        if a == name and i + 1 < len(args):
            return args[i + 1]
        if a.startswith(name + "="):
            return a[len(name) + 1:]
    return None


class State:
    def __init__(self, tunnel_id):
        self.lock = threading.Lock()
        self.tunnel_id = tunnel_id
        # The orchestrator starts below any version, so the first configuration (version 0 or more) is taken.
        self.version = -1
        self.rules = None
        self.refused = set()


def b64url_json(part):
    return json.loads(base64.urlsafe_b64decode(part + "=" * (-len(part) % 4)))


def access_refusal(access, headers):
    """Why cloudflared's Access check (middleware.JWTValidator) refuses the request, or None. It wants the
    Cf-Access-Jwt-Assertion header: a token from the team's issuer for one of the rule's audience tags."""
    token = headers.get("Cf-Access-Jwt-Assertion")
    if not token:
        return "no access token in request"
    try:
        _, payload, _ = token.split(".")
        claims = b64url_json(payload)
    except (ValueError, TypeError):
        return "Invalid token in jwt: malformed"
    issuer = f"https://{access.get('teamName') or ''}.cloudflareaccess.com"
    if claims.get("iss") != issuer:
        return f"Invalid token in jwt: id token issued by a different provider, expected {issuer!r} got {claims.get('iss')!r}"
    if (claims.get("exp") or 0) < time.time():
        return "Invalid token in jwt: token is expired"
    auds = claims.get("aud") if isinstance(claims.get("aud"), list) else [claims.get("aud")]
    if not set(auds) & set(access.get("audTag") or []):
        return "Invalid token in jwt: no matching audience tag"
    return None


def make_edge(state):
    class Edge(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        timeout = 60

        def log_message(self, fmt, *args):
            pass

        def _answer(self, status, body=b"", headers=None, debug=None):
            try:
                self.send_response_only(status)
                for k, v in (headers or []):
                    self.send_header(k, v)
                for k, v in (debug or {}).items():
                    self.send_header(k, str(v))
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                if self.command != "HEAD" and body:
                    self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                # Cloudflare's edge hung up first (its client stopped waiting, as GitHub does after 10 seconds).
                self.close_connection = True
                log("ERR", f"error={quote('Incoming request ended abruptly: context canceled')} connIndex=0 event=1 "
                           f"ingressRule={(debug or {}).get('X-E2E-Rule', 0)} originService={(debug or {}).get('X-E2E-Service', '')}")

        def _body(self):
            if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
                out = bytearray()
                while True:
                    size = int(self.rfile.readline().strip().split(b";")[0] or b"0", 16)
                    if size == 0:
                        while self.rfile.readline() not in (b"\r\n", b"\n", b""):
                            pass
                        return bytes(out)
                    out += self.rfile.read(size)
                    self.rfile.readline()
            n = int(self.headers.get("Content-Length") or 0)
            return self.rfile.read(n) if n > 0 else b""

        def _serve(self):
            body = self._body()
            with state.lock:
                rules, version = state.rules, state.version
            debug = {"X-E2E-Version": version}
            host = (self.headers.get("Host") or "").strip().lower()
            path = urllib.parse.urlsplit(self.path).path
            if not rules:
                debug["X-E2E-Note"] = "the connector runs no configuration yet: cloudflared answers 503 to everything"
                return self._answer(503, b"", debug=debug)
            index, rule = ingress.match(rules, host, path)
            service, origin = rule["parsed"], rule["originRequest"]
            debug.update({"X-E2E-Rule": index + 1, "X-E2E-Rule-Host": rule["hostname"] or "*", "X-E2E-Rule-Path": rule["path"],
                          "X-E2E-Service": rule["service"]})
            access = origin.get("access")
            if access and access.get("required"):
                why = access_refusal(access, self.headers)
                if why:
                    debug["X-E2E-Note"] = f"cloudflared's Access check refused it: {why}"
                    return self._answer(403, b"", debug=debug)
            if service["kind"] == "status":
                return self._answer(service["code"], b"", debug=debug)
            if service["kind"] == "hello":
                return self._answer(200, b"Hello from cloudflared's hello_world service (the rig's fake)\n",
                                    [("Content-Type", "text/plain; charset=utf-8")], debug)
            if service["kind"] != "http":
                debug["X-E2E-Note"] = f"the fake cloudflared doesn't serve {service['kind']} services"
                return self._answer(502, b"", debug=debug)
            self._proxy(service, origin, body, debug, index)

        def _proxy(self, service, origin, body, debug, index):
            secure = service["scheme"] in ("https", "wss")
            port = service["port"] or (443 if secure else 80)
            headers = [(k, v) for k, v in self.headers.items() if k.lower() not in ingress.HOP_BY_HOP and k.lower() not in ("host", "content-length")]
            host = origin.get("httpHostHeader") or self.headers.get("Host") or ""
            headers += [("Host", host), ("Cf-Warp-Tag-Id", state.tunnel_id)]
            if body or self.command in ("POST", "PUT", "PATCH"):
                headers.append(("Content-Length", str(len(body))))
            target = f"{service['host']}:{port}"
            debug["X-E2E-Origin"] = f"{service['scheme']}://{target}{self.path}"
            try:
                if secure:
                    context = ssl.create_default_context()
                    if origin.get("noTLSVerify"):
                        context.check_hostname = False
                        context.verify_mode = ssl.CERT_NONE
                    conn = HTTPSConnection(service["host"], port, timeout=30, context=context)
                else:
                    conn = HTTPConnection(service["host"], port, timeout=30)
                try:
                    conn.putrequest(self.command, self.path, skip_host=True, skip_accept_encoding=True)
                    for k, v in headers:
                        conn.putheader(k, v)
                    try:
                        conn.endheaders(body if body else None)
                    except (BrokenPipeError, ConnectionResetError):
                        # The origin answered (a 413, say) before it read the whole body: its answer still counts.
                        pass
                    resp = conn.getresponse()
                    data = resp.read()
                finally:
                    conn.close()
            except OSError as e:
                why = (f"Unable to reach the origin service. The service may be down or it may not be responding to traffic from "
                       f"cloudflared: dial tcp {target}: {e}")
                log("ERR", f"error={quote(why)} connIndex=0 event=1 ingressRule={index} originService={service['url']}")
                debug["X-E2E-Note"] = why
                return self._answer(502, b"", debug=debug)
            kept = [(k, v) for k, v in resp.getheaders() if k.lower() not in ingress.HOP_BY_HOP and k.lower() != "content-length"]
            self._answer(resp.status, data, kept, debug)

        do_GET = do_POST = do_PUT = do_PATCH = do_DELETE = do_HEAD = do_OPTIONS = _serve

    return Edge


class EdgeServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def handle_error(self, request, client_address):
        # The edge closing its connection isn't the connector's error (socketserver would print a traceback).
        if isinstance(sys.exc_info()[1], ConnectionError):
            return
        super().handle_error(request, client_address)


def take(state, version, config):
    """cloudflared's Orchestrator.UpdateConfig: an older or equal version is ignored; one it can't decode or validate is
    logged (once: the edge pushes each version once) and the current one kept; otherwise it runs from now on."""
    with state.lock:
        if version <= state.version or version in state.refused:
            return False
    raw = json.dumps(config, separators=(",", ":"), ensure_ascii=False)
    try:
        rules = ingress.decode(config)
    except ingress.ConfigError as e:
        log("ERR", f"Failed to deserialize new configuration error={quote(str(e))} config={quote(raw)} version={version}")
        with state.lock:
            state.refused.add(version)
        return False
    with state.lock:
        state.version, state.rules = version, rules
    log("INF", f"Updated to new configuration config={quote(raw)} version={version}")
    return True


def main(argv):
    signal.signal(signal.SIGTERM, lambda *_: os._exit(0))
    args = argv[1:]
    if "tunnel" not in args:
        # `cloudflared version` prints to stdout (urfave/cli's version printer); only the log goes to stderr.
        print(f"cloudflared version {VERSION} (fake, for Gitberth's e2e rig)")
        return 0
    token = option(args, "--token") or os.environ.get("TUNNEL_TOKEN")
    token_file = option(args, "--token-file") or os.environ.get("TUNNEL_TOKEN_FILE")
    if not token and token_file:
        with open(token_file) as f:
            token = f.read().strip()
    if not token:
        log("ERR", "\"cloudflared tunnel run\" requires the ID or name of the tunnel to run as the last command line argument or in the configuration file.")
        return 1
    try:
        padded = token.strip() + "=" * (-len(token.strip()) % 4)
        tunnel_id = json.loads(base64.b64decode(padded.replace("-", "+").replace("_", "/")))["t"]
    except (ValueError, KeyError, TypeError):
        log("ERR", "Provided Tunnel token is not valid.")
        return 1
    standin = os.environ.get("STANDIN", "http://10.89.0.2:8099").rstrip("/")
    poll = float(os.environ.get("POLL_SECONDS", "1"))
    state = State(tunnel_id)
    server = EdgeServer(("0.0.0.0", EDGE_PORT), make_edge(state))
    threading.Thread(target=server.serve_forever, name="edge", daemon=True).start()
    log("INF", f"Starting tunnel tunnelID={tunnel_id}")
    log("INF", f"Version {VERSION} (fake: Gitberth's e2e rig; it never contacts Cloudflare)")
    log("INF", "Generated Connector ID: " + str(uuid.uuid4()))
    log("INF", "No ingress rules were defined in provided config (if any) nor from the cli, cloudflared will return 503 for all incoming HTTP requests")
    log("INF", f"Registered tunnel connection connIndex=0 connection={uuid.uuid4()} event=0 ip=198.41.200.13 location=e2e protocol=quic")
    failing = None
    while True:
        try:
            with state.lock:
                running = state.version
            query = urllib.parse.urlencode({"running": running, "edge": EDGE_PORT, "network": NETWORK})
            with urllib.request.urlopen(f"{standin}/__control/cloudflare/tunnels/{tunnel_id}/config?{query}", timeout=5) as r:
                data = json.load(r)
            failing = None
            take(state, int(data["version"]), data["config"])
        except Exception as e:  # noqa: BLE001 - any failure is logged once and retried
            if str(e) != failing:
                log("WRN", f"Couldn't read the tunnel's configuration: {e}")
                failing = str(e)
        time.sleep(poll)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
