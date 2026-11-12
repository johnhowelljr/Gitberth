"""The rig's test app. Endpoints:
  /         who it is: its marker (marker.txt in the repo), its commit, when it started
  /health   200
  /env      its environment, as JSON
  /build    what the build saw (build.json, from build_info.py)
  /file     GET ?path= a file's contents (404 when missing); POST ?path= writes the body there (for volume checks)
  /cron     the cron job vercel.json schedules: each hit is a line in /data/cron.log
  /exit     POST ?code= ends the process with that code, as a crash does (for notifications)
  /sick     POST ?on=1 makes /tmp/sick, which a HEALTHCHECK can fail on; ?on=0 takes it away
"""
import json
import os
import signal
import socket
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

PORT = int(os.environ.get("PORT") or "8080")
STARTED = time.time()


def read(path):
    try:
        with open(path) as f:
            return f.read()
    except OSError:
        return None


class Handler(BaseHTTPRequestHandler):
    server_version = "gbe2e-testapp"

    def _send(self, status, body, content_type="application/json"):
        data = (json.dumps(body, indent=1) if content_type == "application/json" else str(body)).encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        url = urlsplit(self.path)
        q = parse_qs(url.query)
        if url.path == "/":
            self._send(200, {"app": "gbe2e-testapp", "marker": (read("/app/marker.txt") or "").strip() or None,
                             "commit": os.environ.get("VERCEL_GIT_COMMIT_SHA"), "hostname": socket.gethostname(), "started": STARTED})
        elif url.path == "/health":
            self._send(200, {"ok": True})
        elif url.path == "/env":
            self._send(200, dict(os.environ))
        elif url.path == "/build":
            self._send(200, json.loads(read("/app/build.json") or "{}"))
        elif url.path == "/file":
            text = read(q.get("path", [""])[0])
            self._send(200, text, "text/plain") if text is not None else self._send(404, {"error": "no such file"})
        elif url.path == "/cron":
            os.makedirs("/data", exist_ok=True)
            with open("/data/cron.log", "a") as f:
                f.write(f"{int(time.time())} {self.headers.get('User-Agent', '')}\n")
            self._send(200, {"ok": True})
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        url = urlsplit(self.path)
        q = parse_qs(url.query)
        if url.path == "/exit":
            code = int(q.get("code", ["1"])[0])
            self._send(200, {"exiting": code})
            self.wfile.flush()
            print(f"exiting with {code}, as asked", flush=True)
            os._exit(code)
        if url.path == "/sick":
            if q.get("on", ["1"])[0] == "1":
                open("/tmp/sick", "w").close()
            elif os.path.exists("/tmp/sick"):
                os.remove("/tmp/sick")
            self._send(200, {"sick": os.path.exists("/tmp/sick")})
            return
        if url.path != "/file":
            self._send(404, {"error": "not found"})
            return
        path = parse_qs(url.query).get("path", [""])[0]
        data = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        os.makedirs(os.path.dirname(path) or "/", exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)
        self._send(200, {"written": path, "bytes": len(data)})

    def log_message(self, fmt, *args):
        print(f"{self.address_string()} {fmt % args}", flush=True)


def main():
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    commit = (os.environ.get("VERCEL_GIT_COMMIT_SHA") or "unknown")[:7]
    print(f"gbe2e test app listening on :{PORT} (commit {commit}, {os.environ.get('VERCEL_ENV') or 'no VERCEL_ENV'})", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
