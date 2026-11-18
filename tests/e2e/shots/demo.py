"""What the docs' demo is made of: the example repos (on the GitHub and GitLab stand-ins, as github.com/you/... and
gitlab.com/you/...), the pasted docker run and Compose service, and the local image. Every app builds or runs from
images on Docker Hub alone: no package registry, no GitHub, nothing else on the internet."""

AUTHOR = {"name": "You", "email": "you@example.com"}

SITE_CSS = """:root { color-scheme: light dark; font-family: system-ui, sans-serif; }
body { max-width: 42rem; margin: 3rem auto; padding: 0 1rem; line-height: 1.6; }
nav a { margin-right: 1rem; }
"""


def page(title, body):
    return f"""<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title}</title><link rel="stylesheet" href="styles.css"></head>
<body>
<nav><a href="index.html">Home</a><a href="about.html">About</a></nav>
{body}
</body>
</html>
"""


WEBSITE = {
    "README.md": "# website\n\nThe home page of example.com: plain HTML in `site/`.\n",
    "site/index.html": page("Example", "<h1>Hello from the home lab</h1>\n<p>This page is served by Gitberth.</p>"),
    "site/about.html": page("About", "<h1>About</h1>\n<p>A small static site.</p>"),
    "site/styles.css": SITE_CSS,
}

# A pull request's change, and a branch whose preview takes a while to build (its own Dockerfile, with a pause).
PRICING = {"site/pricing.html": page("Pricing", "<h1>Pricing</h1>\n<p>Free for your home lab.</p>")}
LOGIN = {
    "site/login.html": page("Sign in", "<h1>Sign in</h1>\n<form><input name=\"email\"> <button>Send a link</button></form>"),
    "site/Dockerfile": "FROM nginx:alpine\n# Optimizing the images first.\nRUN sleep 1500\nCOPY . /usr/share/nginx/html\n",
}

ORDERS_API = {
    "package.json": """{
  "name": "orders-api",
  "version": "1.4.0",
  "private": true,
  "scripts": { "start": "node server.js" },
  "engines": { "node": ">=22" }
}
""",
    "package-lock.json": """{
  "name": "orders-api",
  "version": "1.4.0",
  "lockfileVersion": 3,
  "requires": true,
  "packages": {
    "": { "name": "orders-api", "version": "1.4.0", "engines": { "node": ">=22" } }
  }
}
""",
    "server.js": """const http = require("node:http");

const orders = [
  { id: 1041, item: "USB-C dock", status: "shipped" },
  { id: 1042, item: "NVMe drive", status: "packing" },
];

const routes = {
  "/": () => ({ service: "orders-api", version: "1.4.0" }),
  "/api/orders": () => orders,
  "/api/cron/cleanup": () => ({ removed: 0 }),
  "/api/cron/report": () => ({ orders: orders.length }),
};

http.createServer((req, res) => {
  const route = routes[new URL(req.url, "http://localhost").pathname];
  res.writeHead(route ? 200 : 404, { "Content-Type": "application/json" });
  res.end(JSON.stringify(route ? route() : { error: "not found" }));
}).listen(process.env.PORT || 3000);
""",
    "vercel.json": """{
  "crons": [
    { "path": "/api/cron/cleanup", "schedule": "0 3 * * *" },
    { "path": "/api/cron/report", "schedule": "*/30 * * * *" }
  ]
}
""",
}

INVENTORY = {
    "main.py": '''"""The inventory service: what's on the shelves, as JSON."""
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ITEMS = [{"sku": "HDD-8T", "count": 4}, {"sku": "RAM-32G", "count": 9}]


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        body = json.dumps({"service": "inventory", "items": ITEMS} if self.path in ("/", "/items") else {"error": "not found"})
        self.send_response(200 if self.path in ("/", "/items") else 404)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body.encode())


if __name__ == "__main__":
    port = int(os.environ.get("PORT", "8000"))
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()
''',
    "requirements.txt": "",
}

# A Compose app: a web service and a worker built from the repo, and their Redis.
STOREFRONT = {
    "compose.yaml": """services:
  web:
    build: .
    ports:
      - "8090:8000"
    environment:
      SHOP_NAME: ${SHOP_NAME:-Storefront}
      SECRET_KEY: ${SECRET_KEY}
      REDIS_URL: redis://redis:6379
    depends_on:
      - redis
  worker:
    build: .
    command: ["python3", "-u", "worker.py"]
    environment:
      REDIS_URL: redis://redis:6379
    depends_on:
      - redis
  redis:
    image: redis:7-alpine
    volumes:
      - data:/data
volumes:
  data:
""",
    "Dockerfile": """FROM python:3.12-alpine
WORKDIR /app
COPY . .
EXPOSE 8000
CMD ["python3", "-u", "web.py"]
""",
    "web.py": '''import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Type", "text/html")
        self.end_headers()
        self.wfile.write(f"<h1>{os.environ.get('SHOP_NAME', 'Storefront')}</h1>".encode())


ThreadingHTTPServer(("0.0.0.0", 8000), Handler).serve_forever()
''',
    "worker.py": '''import time

print("worker ready: sending the order emails")
while True:
    time.sleep(60)
    print("no new orders")
''',
}

MAILPIT = """docker run -d --name mailpit \\
  --restart unless-stopped \\
  -p 8025:8025 -p 1025:1025 \\
  -v mailpit-data:/data \\
  -e MP_MAX_MESSAGES=5000 \\
  axllent/mailpit"""

ADMINER = """services:
  adminer:
    image: adminer:latest
    restart: unless-stopped
    ports:
      - "8081:8080"
    environment:
      ADMINER_DEFAULT_SERVER: postgres
      ADMINER_DESIGN: dracula"""

# The local image (backup-agent:1.0), built on the host as someone would.
BACKUP_AGENT = """FROM alpine:3.20
CMD ["sh", "-c", "echo backup agent ready; while true; do sleep 3600; done"]
"""
