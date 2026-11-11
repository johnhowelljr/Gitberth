"""The Cloudflare stand-in: the REST calls in Gitberth's CloudflareApi.cs, in Cloudflare's envelope (success, errors,
messages, result, result_info), for one account with one remotely managed tunnel: token checks, zones, DNS records, the
tunnel's configuration (each PUT bumps its version, which the fake cloudflared takes), the Zero Trust organization and
Access applications. The control API seeds zones and the team, edits the tunnel as the dashboard would, and reads it all.

It's also Cloudflare's edge for the rig (edge()): a request to a host name, from GitHub's webhooks or from a scenario's
"visitor", gets there only as it would in real life. The host's DNS record in one of the stand-in's zones must be
proxied and lead to this tunnel; a self-hosted Access application covering the address sends a visitor who isn't signed
in to its login page; then the request goes to the tunnel's connector (the fake cloudflared), which routes it by the
ingress it runs. A tunnel with no connector answers 530 (error 1033)."""
import base64
import http.client
import json
import os
import re
import secrets
import threading
import time
import urllib.parse
import uuid

from common import ApiError, Response, iso, log, token_of

# Cloudflare's permission groups, as the stand-in names them:
#   tunnel       Account › Cloudflare Tunnel › Edit
#   dns          Zone › DNS › Edit
#   zone         Zone › Zone › Read
#   access_apps  Account › Access: Apps and Policies › Edit       (accounts/{id}/access/apps)
#   access_org   Account › Access: Organizations, Identity Providers, and Groups › Read   (accounts/{id}/access/organizations)
ALL_PERMISSIONS = ("tunnel", "dns", "zone", "access_apps", "access_org")
# The name the stand-in used before Access was split in two: both groups.
LEGACY = {"access": ("access_apps", "access_org")}

# Where a proxied host name answers: Cloudflare's HTTPS and HTTP ports.
EDGE_PORTS = {"https": {443, 2053, 2083, 2087, 2096, 8443}, "http": {80, 8080, 8880, 2052, 2082, 2086, 2095}}
TUNNEL_SUFFIX = ".cfargotunnel.com"
# A connector that hasn't polled for this long is gone (it polls every second).
CONNECTOR_STALE_SECONDS = 5


def hex32():
    return secrets.token_hex(16)


def b64url(data):
    return base64.urlsafe_b64encode(data).decode().rstrip("=")


class Cloudflare:
    def __init__(self, account_id, tunnel_id, state_dir=None):
        self.account_id = account_id
        self.tunnel_id = tunnel_id
        self.lock = threading.RLock()
        self._version_file = os.path.join(state_dir, "cf-tunnel-version") if state_dir else None
        # Cloudflare's version only ever goes up, and the connector ignores one it has seen: it's kept across restarts.
        self.version = self._load_version()
        # The fake cloudflared, as its last poll described it: where its edge port is, the version it runs.
        self.connector = None
        self.reset()

    def _load_version(self):
        try:
            with open(self._version_file) as f:
                return int(f.read().strip() or 0)
        except (TypeError, OSError, ValueError):
            return 0

    def _save_version(self):
        if not self._version_file:
            return
        try:
            with open(self._version_file + ".tmp", "w") as f:
                f.write(str(self.version))
            os.replace(self._version_file + ".tmp", self._version_file)
        except OSError:
            pass

    def reset(self):
        with self.lock:
            self.tokens = {}
            self.zones = {}
            self.dns = {}
            self.apps = {}
            self.team = None
            self.list_includes_policies = True
            self.version += 1
            self._save_version()
            self.config = {"ingress": [{"service": "http_status:404"}], "warp-routing": {"enabled": False}}
            self.config_at = iso()

    # ---- the envelope ----

    @staticmethod
    def ok(result, info=None, status=200, messages=None):
        body = {"success": True, "errors": [], "messages": messages or [], "result": result}
        if info is not None:
            body["result_info"] = info
        return Response.json(status, body)

    @staticmethod
    def error(status, code, message):
        return Response.json(status, {"success": False, "errors": [{"code": code, "message": message}], "messages": [], "result": None})

    # ---- tokens ----

    @staticmethod
    def permissions_of(perms):
        out = []
        for p in ALL_PERMISSIONS if perms is None else perms:
            for q in LEGACY.get(p, (p,)):
                if q not in out:
                    out.append(q)
        return out

    def add_token(self, spec):
        value = spec.get("token") or secrets.token_urlsafe(30)
        t = {"token": value, "id": hex32(), "kind": spec.get("kind") or "user", "status": "active",
             "permissions": self.permissions_of(spec.get("permissions"))}
        with self.lock:
            self.tokens[value] = t
        return t

    def _token(self, req, need=None):
        value = token_of(req.headers)
        if not value:
            raise ApiError(400, "Missing X-Auth-Key, X-Auth-Email or Authorization headers", code=9106)
        t = self.tokens.get(value)
        if t is None or t["status"] != "active":
            raise ApiError(403, "Invalid access token", code=9109)
        if need and need not in t["permissions"]:
            raise ApiError(403, "Authentication error", code=10000)
        return t

    def _account(self, account):
        if account != self.account_id:
            raise ApiError(403, "Authentication error", code=10000)

    # ---- REST ----

    ROUTES = [
        ("GET", r"^user/tokens/verify$", "verify_user_token"),
        ("GET", r"^accounts/(?P<a>[^/]+)/tokens/verify$", "verify_account_token"),
        ("GET", r"^accounts/(?P<a>[^/]+)/cfd_tunnel/(?P<t>[^/]+)$", "get_tunnel"),
        ("GET", r"^accounts/(?P<a>[^/]+)/cfd_tunnel/(?P<t>[^/]+)/configurations$", "get_config"),
        ("PUT", r"^accounts/(?P<a>[^/]+)/cfd_tunnel/(?P<t>[^/]+)/configurations$", "put_config"),
        ("GET", r"^zones$", "list_zones"),
        ("GET", r"^zones/(?P<z>[0-9a-f]{32})$", "get_zone"),
        ("GET", r"^zones/(?P<z>[0-9a-f]{32})/dns_records$", "list_dns"),
        ("POST", r"^zones/(?P<z>[0-9a-f]{32})/dns_records$", "create_dns"),
        ("GET", r"^zones/(?P<z>[0-9a-f]{32})/dns_records/(?P<id>[0-9a-f]{32})$", "get_dns"),
        ("PATCH", r"^zones/(?P<z>[0-9a-f]{32})/dns_records/(?P<id>[0-9a-f]{32})$", "patch_dns"),
        ("PUT", r"^zones/(?P<z>[0-9a-f]{32})/dns_records/(?P<id>[0-9a-f]{32})$", "put_dns"),
        ("DELETE", r"^zones/(?P<z>[0-9a-f]{32})/dns_records/(?P<id>[0-9a-f]{32})$", "delete_dns"),
        ("GET", r"^accounts/(?P<a>[^/]+)/access/organizations$", "get_org"),
        ("GET", r"^accounts/(?P<a>[^/]+)/access/apps$", "list_apps"),
        ("POST", r"^accounts/(?P<a>[^/]+)/access/apps$", "create_app"),
        ("GET", r"^accounts/(?P<a>[^/]+)/access/apps/(?P<id>[^/]+)$", "get_app"),
        ("PUT", r"^accounts/(?P<a>[^/]+)/access/apps/(?P<id>[^/]+)$", "put_app"),
        ("DELETE", r"^accounts/(?P<a>[^/]+)/access/apps/(?P<id>[^/]+)$", "delete_app"),
    ]
    _compiled = [(m, re.compile(p), fn) for m, p, fn in ROUTES]

    def api(self, req, call):
        path = req.path
        if not path.startswith("/client/v4/"):
            return self.unrouted(req, call, self.error(404, 7000, "No route for that URI"))
        path = path[len("/client/v4/"):]
        try:
            for method, rx, fn in self._compiled:
                m = rx.match(path)
                if m and method == req.method:
                    with self.lock:
                        return getattr(self, fn)(req, **m.groupdict())
            if any(rx.match(path) for _, rx, _ in self._compiled):
                return self.unrouted(req, call, self.error(405, 10405, "Method not allowed for this route"))
            return self.unrouted(req, call, self.error(404, 7003, "Could not route to /" + path + ", perhaps your object identifier is invalid?"))
        except ApiError as e:
            return self.error(e.status, e.code or 1000, e.message)

    @staticmethod
    def unrouted(req, call, resp):
        """A call no stand-in route answers: Cloudflare's own answer goes back, which Gitberth may read as a missing
        permission or as Zero Trust not set up. So it's logged and marked on its record (unrouted), and the scenarios
        fail on it."""
        call["unrouted"] = True
        log(f"no stand-in route: {req.method} {req.host}{req.path}")
        return resp

    def verify_user_token(self, req):
        value = token_of(req.headers)
        t = self.tokens.get(value or "")
        if t is None or t["status"] != "active" or t["kind"] != "user":
            return self.error(401, 1000, "Invalid API Token")
        return self.ok({"id": t["id"], "status": "active"}, messages=[{"code": 10000, "message": "This API Token is valid and active", "type": None}])

    def verify_account_token(self, req, a):
        value = token_of(req.headers)
        t = self.tokens.get(value or "")
        if t is None or t["status"] != "active" or a != self.account_id:
            return self.error(401, 1000, "Invalid API Token")
        return self.ok({"id": t["id"], "status": "active"}, messages=[{"code": 10000, "message": "This API Token is valid and active", "type": None}])

    def _tunnel(self, req, a, t):
        self._token(req, "tunnel")
        self._account(a)
        if t != self.tunnel_id:
            raise ApiError(404, "Tunnel not found", code=1003)

    def connector_live(self):
        c = self.connector
        return c is not None and time.time() - c["seen"] <= CONNECTOR_STALE_SECONDS

    def get_tunnel(self, req, a, t):
        self._tunnel(req, a, t)
        live = self.connector_live()
        return self.ok({"id": self.tunnel_id, "account_tag": self.account_id, "name": "gbe2e", "status": "healthy" if live else "down",
                        "config_src": "cloudflare", "remote_config": True, "created_at": "2026-01-01T00:00:00Z", "deleted_at": None,
                        "tun_type": "cfd_tunnel",
                        "connections": [{"colo_name": "e2e", "id": str(uuid.uuid4()), "is_pending_reconnect": False}] if live else []})

    def config_result(self):
        return {"account_id": self.account_id, "tunnel_id": self.tunnel_id, "version": self.version, "config": self.config,
                "source": "cloudflare", "created_at": self.config_at}

    def get_config(self, req, a, t):
        self._tunnel(req, a, t)
        return self.ok(self.config_result())

    def put_config(self, req, a, t):
        self._tunnel(req, a, t)
        body = req.json({}) or {}
        config = body.get("config")
        if not isinstance(config, dict):
            raise ApiError(400, "Bad Configuration: the request needs a config object", code=1001)
        problem = ingress_problem(config.get("ingress"))
        if problem:
            raise ApiError(400, f"Bad Configuration: {problem}", code=1055)
        self.set_config(config)
        return self.ok(self.config_result())

    def set_config(self, config):
        with self.lock:
            self.config = config
            self.version += 1
            self._save_version()
            self.config_at = iso()
            return self.version

    # zones
    def zone_json(self, z):
        return {"id": z["id"], "name": z["name"], "status": z["status"], "paused": False, "type": "full", "development_mode": 0,
                "name_servers": ["ada.ns.cloudflare.com", "bob.ns.cloudflare.com"],
                "account": {"id": self.account_id, "name": "Gitberth e2e"}, "created_on": z["created_on"], "modified_on": z["created_on"],
                "permissions": ["#zone:read", "#dns_records:edit"]}

    def list_zones(self, req):
        self._token(req, "zone")
        zones = list(self.zones.values())
        if req.query.get("name"):
            zones = [z for z in zones if z["name"] == req.query["name"].lower()]
        if req.query.get("account.id") and req.query["account.id"] != self.account_id:
            zones = []
        if req.query.get("status"):
            zones = [z for z in zones if z["status"] == req.query["status"]]
        page, per = int(req.query.get("page") or 1), int(req.query.get("per_page") or 20)
        chunk = zones[(page - 1) * per: page * per]
        return self.ok([self.zone_json(z) for z in chunk],
                       {"page": page, "per_page": per, "count": len(chunk), "total_count": len(zones), "total_pages": max(1, -(-len(zones) // per))})

    def _zone(self, z):
        zone = self.zones.get(z)
        if zone is None:
            raise ApiError(404, "Invalid zone identifier", code=1001)
        return zone

    def get_zone(self, req, z):
        self._token(req, "zone")
        return self.ok(self.zone_json(self._zone(z)))

    # DNS records
    def record_json(self, r):
        return dict(r)

    def list_dns(self, req, z):
        self._token(req, "dns")
        zone = self._zone(z)
        records = [r for r in self.dns.values() if r["zone_id"] == zone["id"]]
        if req.query.get("name"):
            records = [r for r in records if r["name"] == req.query["name"].lower().rstrip(".")]
        if req.query.get("type"):
            records = [r for r in records if r["type"] == req.query["type"]]
        page, per = int(req.query.get("page") or 1), int(req.query.get("per_page") or 100)
        chunk = records[(page - 1) * per: page * per]
        return self.ok([self.record_json(r) for r in chunk],
                       {"page": page, "per_page": per, "count": len(chunk), "total_count": len(records), "total_pages": max(1, -(-len(records) // per))})

    def add_record(self, zone, body):
        name = (body.get("name") or "").lower().rstrip(".")
        if name == "@":
            name = zone["name"]
        elif name and not (name == zone["name"] or name.endswith("." + zone["name"])):
            name = f"{name}.{zone['name']}"
        rtype = body.get("type")
        if rtype not in ("A", "AAAA", "CNAME", "TXT", "MX", "NS", "CAA", "SRV"):
            raise ApiError(400, "DNS Validation Error", code=1004)
        if not body.get("content"):
            raise ApiError(400, "Content for the record is invalid.", code=9009)
        if rtype in ("A", "AAAA", "CNAME"):
            clash = [r for r in self.dns.values() if r["zone_id"] == zone["id"] and r["name"] == name and r["type"] in ("A", "AAAA", "CNAME")]
            if clash and (rtype == "CNAME" or any(r["type"] == "CNAME" for r in clash)):
                raise ApiError(400, "An A, AAAA, or CNAME record with that host already exists.", code=81053)
        rid = hex32()
        now = iso()
        record = {"id": rid, "zone_id": zone["id"], "zone_name": zone["name"], "name": name, "type": rtype, "content": body["content"],
                  "proxiable": rtype in ("A", "AAAA", "CNAME"), "proxied": bool(body.get("proxied", False)), "ttl": int(body.get("ttl") or 1),
                  "locked": False, "meta": {}, "comment": body.get("comment"), "tags": list(body.get("tags") or []),
                  "created_on": now, "modified_on": now}
        self.dns[rid] = record
        return record

    def create_dns(self, req, z):
        self._token(req, "dns")
        zone = self._zone(z)
        return self.ok(self.record_json(self.add_record(zone, req.json({}) or {})))

    def _record(self, z, rid):
        self._zone(z)
        r = self.dns.get(rid)
        if r is None or r["zone_id"] != z:
            raise ApiError(404, "Record does not exist.", code=81044)
        return r

    def get_dns(self, req, z, id):
        self._token(req, "dns")
        return self.ok(self.record_json(self._record(z, id)))

    def patch_dns(self, req, z, id):
        self._token(req, "dns")
        r = self._record(z, id)
        body = req.json({}) or {}
        for k in ("content", "proxied", "ttl", "comment", "tags", "name", "type"):
            if k in body:
                r[k] = body[k]
        r["modified_on"] = iso()
        return self.ok(self.record_json(r))

    def put_dns(self, req, z, id):
        self._token(req, "dns")
        r = self._record(z, id)
        body = req.json({}) or {}
        for k in ("content", "proxied", "ttl", "comment", "tags", "name", "type"):
            r[k] = body.get(k, r[k] if k in ("name", "type", "content") else None if k == "comment" else r[k])
        r["modified_on"] = iso()
        return self.ok(self.record_json(r))

    def delete_dns(self, req, z, id):
        self._token(req, "dns")
        r = self._record(z, id)
        del self.dns[r["id"]]
        return self.ok({"id": r["id"]})

    # Zero Trust: the organization (team domain) and Access applications. Cloudflare keeps them in two permission
    # groups: the organization needs "Access: Organizations, Identity Providers, and Groups" (Read, Write or Revoke), and
    # the applications "Access: Apps and Policies".
    def get_org(self, req, a):
        self._token(req, "access_org")
        self._account(a)
        if not self.team:
            raise ApiError(404, "access.api.error.not_found", code=12130)
        return self.ok({"auth_domain": f"{self.team}.cloudflareaccess.com", "name": self.team, "is_ui_read_only": False,
                        "auto_redirect_to_identity": False, "login_design": {}, "created_at": "2026-01-01T00:00:00Z", "updated_at": iso()})

    def app_json(self, app, with_policies=True):
        out = {k: v for k, v in app.items() if k not in ("policies", "made_by")}
        if with_policies:
            out["policies"] = app.get("policies", [])
        return out

    def list_apps(self, req, a):
        self._token(req, "access_apps")
        self._account(a)
        apps = sorted(self.apps.values(), key=lambda x: x["created_at"])
        page, per = int(req.query.get("page") or 1), int(req.query.get("per_page") or 25)
        chunk = apps[(page - 1) * per: page * per]
        return self.ok([self.app_json(x, self.list_includes_policies) for x in chunk],
                       {"page": page, "per_page": per, "count": len(chunk), "total_count": len(apps), "total_pages": max(1, -(-len(apps) // per))})

    def _app(self, id):
        app = self.apps.get(id)
        if app is None:
            raise ApiError(404, "access.api.error.not_found", code=12130)
        return app

    def get_app(self, req, a, id):
        self._token(req, "access_apps")
        self._account(a)
        return self.ok(self.app_json(self._app(id)))

    def make_app(self, body, made_by):
        if not body.get("domain") or not body.get("name"):
            raise ApiError(400, "access.api.error.invalid_request: name and domain are required", code=12000)
        aid = str(uuid.uuid4())
        now = iso()
        app = {"id": aid, "uid": aid, "aud": secrets.token_hex(32), "created_at": now, "updated_at": now, "made_by": made_by}
        self._fill_app(app, body)
        self.apps[aid] = app
        return app

    def _fill_app(self, app, body):
        for k in ("name", "domain", "type", "session_duration", "app_launcher_visible", "allowed_idps", "auto_redirect_to_identity"):
            if k in body:
                app[k] = body[k]
        app.setdefault("type", "self_hosted")
        app.setdefault("session_duration", "24h")
        app["self_hosted_domains"] = [app["domain"]]
        app["destinations"] = [{"type": "public", "uri": app["domain"]}]
        policies = []
        for i, p in enumerate(body.get("policies") or []):
            if isinstance(p, dict):
                policies.append({"id": str(uuid.uuid4()), "name": p.get("name") or f"Policy {i + 1}", "decision": p.get("decision", "allow"),
                                 "include": p.get("include") or [], "exclude": p.get("exclude") or [], "require": p.get("require") or [],
                                 "precedence": p.get("precedence", i + 1), "reusable": False, "created_at": iso(), "updated_at": iso()})
            else:
                policies.append({"id": str(p), "reusable": True})
        app["policies"] = policies
        app["updated_at"] = iso()

    def create_app(self, req, a):
        self._token(req, "access_apps")
        self._account(a)
        return self.ok(self.app_json(self.make_app(req.json({}) or {}, "api")), status=201)

    def put_app(self, req, a, id):
        self._token(req, "access_apps")
        self._account(a)
        app = self._app(id)
        self._fill_app(app, req.json({}) or {})
        return self.ok(self.app_json(app))

    def delete_app(self, req, a, id):
        self._token(req, "access_apps")
        self._account(a)
        app = self._app(id)
        del self.apps[app["id"]]
        return self.ok({"id": app["id"]}, status=202)

    # ---- the connector (the fake cloudflared) ----

    def register_connector(self, address, port, version, network):
        with self.lock:
            self.connector = {"address": address, "port": port, "version": version, "network": network or None, "seen": time.time()}

    def connector_state(self):
        with self.lock:
            c = dict(self.connector) if self.connector else None
            current = self.version
        if c:
            c["seen_seconds_ago"] = round(time.time() - c.pop("seen"), 1)
            c["live"] = c["seen_seconds_ago"] <= CONNECTOR_STALE_SECONDS
        # Exactly the current version: a connector ahead of the stand-in (whose state was lost) ignores every version it has.
        return {"connector": c, "version": current, "runs_current": bool(c and c["live"] and c["version"] == current)}

    # ---- the edge: what a request to a host name meets on its way ----

    def resolve(self, host):
        """Where public DNS sends host, as far as the rig knows: its record in one of the stand-in's active zones, followed
        through CNAMEs to other names in them. It reaches the tunnel when the first record is proxied and the chain ends at
        <tunnel>.cfargotunnel.com. Returns {"ok", "tunnel", "chain", "error"}."""
        host = (host or "").lower().rstrip(".")
        chain = []

        def fail(why):
            return {"ok": False, "tunnel": None, "chain": chain, "error": why}

        with self.lock:
            zones = [z for z in self.zones.values() if z["status"] == "active"]
            records = list(self.dns.values())

        def zone_of(name):
            found = [z for z in zones if name == z["name"] or name.endswith("." + z["name"])]
            return max(found, key=lambda z: len(z["name"])) if found else None

        def records_of(zone, name):
            kinds = ("A", "AAAA", "CNAME")
            exact = [r for r in records if r["zone_id"] == zone["id"] and r["name"] == name and r["type"] in kinds]
            if exact or name == zone["name"]:
                return exact
            wildcard = "*." + name.split(".", 1)[1]
            return [r for r in records if r["zone_id"] == zone["id"] and r["name"] == wildcard and r["type"] in kinds]

        name, zone, first = host, zone_of(host), None
        if zone is None:
            return fail(f"Couldn't resolve host {host}: it isn't in any zone of the Cloudflare stand-in")
        home = {"zone": zone["name"], "total_tls": bool(zone.get("total_tls"))}
        for _ in range(8):
            found = records_of(zone, name)
            if not found:
                return fail(f"Couldn't resolve host {host}: {name} has no A, AAAA or CNAME record in {zone['name']}")
            r = next((x for x in found if x["type"] == "CNAME"), found[0])
            chain.append(f"{r['name']} {r['type']} {r['content']}{' (proxied)' if r['proxied'] else ' (DNS only)'}")
            first = first or r
            if r["type"] != "CNAME":
                return fail(f"{host} leads to {r['content']}, which isn't in the rig")
            target = r["content"].lower().rstrip(".")
            if target.endswith(TUNNEL_SUFFIX):
                if not first["proxied"]:
                    return fail(f"Couldn't connect to {host}: its DNS record isn't proxied, and a tunnel's address ({target}) takes "
                                "no visitors except through Cloudflare's proxy")
                return {"ok": True, "tunnel": target[:-len(TUNNEL_SUFFIX)], "chain": chain, "error": None} | home
            zone = zone_of(target)
            if zone is None:
                return fail(f"{host} leads to {target}, which isn't in the rig")
            name = target
        return fail(f"Couldn't resolve host {host}: too many CNAMEs")

    def access_app_for(self, host, path):
        """The self-hosted Access application that covers the address at Cloudflare's edge, or None."""
        with self.lock:
            apps = [dict(a) for a in self.apps.values()]
        for app in apps:
            if (app.get("type") or "self_hosted") != "self_hosted":
                continue
            for d in app.get("self_hosted_domains") or ([app["domain"]] if app.get("domain") else []):
                dhost, _, dpath = d.strip().lower().partition("/")
                if not (dhost == host or (dhost.startswith("*.") and host.endswith(dhost[1:]))):
                    continue
                prefix = "/" + dpath.strip("/")
                if dpath.strip("/") and not (path == prefix or path.startswith(prefix + "/")):
                    continue
                return app
        return None

    @staticmethod
    def allows(app, email):
        """Whether the application's allow policies let this email address in (email, email_domain, everyone)."""
        if not email:
            return False
        email = email.lower()
        for p in app.get("policies") or []:
            if p.get("decision", "allow") != "allow":
                continue
            for rule in p.get("include") or []:
                if "everyone" in rule:
                    return True
                if (rule.get("email") or {}).get("email", "").lower() == email:
                    return True
                if email.endswith("@" + (rule.get("email_domain") or {}).get("domain", "\0").lower()):
                    return True
        return False

    def access_token(self, app, email):
        """The Cf-Access-Jwt-Assertion Cloudflare adds for a signed-in visitor: from the team's issuer, for the app's aud."""
        now = int(time.time())
        header = {"alg": "RS256", "kid": "gbe2e", "typ": "JWT"}
        claims = {"aud": [app["aud"]], "email": email, "exp": now + 86400, "iat": now, "nbf": now,
                  "iss": f"https://{self.team or 'unknown'}.cloudflareaccess.com", "type": "app", "sub": str(uuid.uuid4())}
        return ".".join([b64url(json.dumps(header).encode()), b64url(json.dumps(claims).encode()), b64url(b"gbe2e-not-a-signature")])

    def edge(self, url, method="GET", headers=None, body=b"", client_ip="203.0.113.10", signed_in_as=None, verify_tls=True, timeout=None):
        """A request to url, as Cloudflare's edge and the tunnel handle it. Returns {"status", "headers", "body", "error",
        "route"}: status is None when it never got an answer (error says why), and route says what it met (DNS, Access,
        the connector, and the rule and service cloudflared used). Over https, Cloudflare's free certificate covers the
        zone and one level below it: a deeper name fails the client's certificate check (verify_tls) unless the zone has
        Total TLS.

        timeout: how long the client waits for its answer, all told (GitHub gives a delivery 10 seconds). When that
        passes the client has hung up: no status, and the error "timed out". Without one, the connector's answer may
        take up to 45 seconds."""
        deadline = None if timeout is None else time.monotonic() + timeout

        def left(longest):
            return longest if deadline is None else max(0.05, min(longest, deadline - time.monotonic()))

        u = urllib.parse.urlsplit(url)
        scheme = (u.scheme or "https").lower()
        host = (u.hostname or "").lower()
        path = u.path or "/"
        target = path + (f"?{u.query}" if u.query else "")
        route = {"host": host}
        out = {"status": None, "headers": {}, "body": "", "error": None, "route": route}
        dns = self.resolve(host)
        route["dns"] = dns["chain"]
        if not dns["ok"]:
            out["error"] = dns["error"]
            return out
        try:
            port = u.port
        except ValueError:
            port = -1
        if port is not None and port not in EDGE_PORTS.get(scheme, set()):
            out["error"] = f"Couldn't connect to {host} port {u.netloc.rpartition(':')[2]}: Cloudflare's proxy doesn't listen there"
            return out
        below = host[:-len(dns["zone"]) - 1] if host != dns["zone"] else ""
        if scheme == "https" and verify_tls and "." in below and not dns["total_tls"]:
            out["error"] = (f"SSL certificate problem: Cloudflare's certificate covers {dns['zone']} and *.{dns['zone']}, not {host} "
                            "(that needs Total TLS or an advanced certificate)")
            return out

        def answer(status, note, extra=None, text=""):
            route["note"] = note
            out.update(status=status, headers=extra or {}, body=text)
            return out

        sent = {k: v for k, v in (headers or {}).items() if k.lower() not in ("host", "content-length")}
        app = self.access_app_for(host, path)
        if app is not None:
            route["access_app"] = app.get("name")
            if not self.allows(app, signed_in_as):
                login = (f"https://{self.team or 'unknown'}.cloudflareaccess.com/cdn-cgi/access/login/{host}"
                         f"?kid={app['aud']}&redirect_url={urllib.parse.quote(target, safe='')}")
                return answer(302, f"Cloudflare Access ({app.get('name')}) sends a visitor who isn't signed in to its login page",
                              {"Location": login})
            sent["Cf-Access-Jwt-Assertion"] = self.access_token(app, signed_in_as)
            sent["Cf-Access-Authenticated-User-Email"] = signed_in_as
        if dns["tunnel"] != self.tunnel_id:
            return answer(530, f"{host} leads to tunnel {dns['tunnel']}, which no connector here runs (error 1033)", text="error code: 1033")
        with self.lock:
            live = self.connector_live()
            connector = dict(self.connector) if self.connector else None
        if not live:
            return answer(530, "the tunnel has no connector running (the fake cloudflared isn't polling): error 1033", text="error code: 1033")
        route["connector"] = f"{connector['address']}:{connector['port']} running version {connector['version']}"

        # What Cloudflare adds: the visitor's address (CF-Connecting-IP; X-Forwarded-For gets it appended), the scheme, a ray.
        forwarded = next((v for k, v in sent.items() if k.lower() == "x-forwarded-for"), None)
        sent = {k: v for k, v in sent.items() if k.lower() not in ("cf-connecting-ip", "x-forwarded-for", "x-forwarded-proto", "cf-ray",
                                                                   "cf-visitor", "cdn-loop", "cf-ipcountry")}
        sent.update({"Cf-Connecting-Ip": client_ip, "X-Forwarded-For": f"{forwarded}, {client_ip}" if forwarded else client_ip,
                     "X-Forwarded-Proto": scheme, "Cf-Visitor": json.dumps({"scheme": scheme}), "Cf-Ray": secrets.token_hex(8) + "-E2E",
                     "Cf-Ipcountry": "US", "Cdn-Loop": "cloudflare"})
        try:
            # A connector that went away answers nothing: the connect itself gives up quickly, the answer may take a while.
            conn = http.client.HTTPConnection(connector["address"], connector["port"], timeout=left(5))
            try:
                conn.connect()
                conn.sock.settimeout(left(45))
                conn.putrequest(method, target, skip_host=True, skip_accept_encoding=True)
                conn.putheader("Host", host)
                for k, v in sent.items():
                    conn.putheader(k, v)
                if body or method in ("POST", "PUT", "PATCH"):
                    conn.putheader("Content-Length", str(len(body or b"")))
                conn.endheaders(body or None)
                conn.sock.settimeout(left(45))
                resp = conn.getresponse()
                data = resp.read()
            finally:
                conn.close()
        except OSError as e:
            if isinstance(e, TimeoutError) and deadline is not None and time.monotonic() >= deadline - 0.1:
                route["note"] = f"no answer within the client's {timeout:g} seconds: it hung up"
                out["error"] = "timed out"
                return out
            return answer(530, f"the connector at {connector['address']}:{connector['port']} couldn't be reached ({e}): error 1033",
                          text="error code: 1033")
        debug = {k[len("X-E2E-"):].lower().replace("-", "_"): v for k, v in resp.getheaders() if k.lower().startswith("x-e2e-")}
        route["cloudflared"] = debug
        out.update(status=resp.status, body=data[:4000].decode("utf-8", "replace"),
                   headers={k: v for k, v in resp.getheaders() if not k.lower().startswith("x-e2e-")})
        return out

    # ---- reading back ----

    def state(self):
        with self.lock:
            return {
                "account_id": self.account_id, "tunnel_id": self.tunnel_id,
                "tunnel": {"version": self.version, "config": self.config},
                "zones": [self.zone_json(z) for z in self.zones.values()],
                "dns_records": [dict(r) for r in self.dns.values()],
                "access_apps": [self.app_json(x) | {"made_by": x.get("made_by")} for x in self.apps.values()],
                "team": self.team,
                "tokens": [{k: v for k, v in t.items() if k != "token"} | {"tag": t["token"][:6]} for t in self.tokens.values()],
                "list_includes_policies": self.list_includes_policies,
            } | self.connector_state()

    def add_zone(self, name, status="active", total_tls=False):
        """A zone of the account. total_tls: Cloudflare's certificates cover every name in it, not just one level down."""
        with self.lock:
            name = name.lower().rstrip(".")
            for z in self.zones.values():
                if z["name"] == name:
                    return z
            z = {"id": hex32(), "name": name, "status": status, "created_on": iso(), "total_tls": bool(total_tls)}
            self.zones[z["id"]] = z
            return z


def ingress_problem(ingress):
    """What Cloudflare's API refuses in a tunnel's ingress: no rules, a rule without a service, or a last rule that doesn't
    match every request. (cloudflared checks much more when it takes the configuration: see ingress.py.)"""
    if ingress is None:
        return None
    if not isinstance(ingress, list) or not ingress:
        return "ingress must be a non-empty list of rules"
    for rule in ingress:
        if not isinstance(rule, dict) or not rule.get("service"):
            return "every ingress rule needs a service"
    last = ingress[-1]
    if last.get("hostname") not in (None, "", "*") or last.get("path"):
        return "The last ingress rule must match all URLs (i.e. it should not have a hostname or path filter)"
    return None
