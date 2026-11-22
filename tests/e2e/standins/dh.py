"""The Docker Hub stand-in, and a registry of one's own: what Gitberth's image search and tag picker call, and nothing
more (design D3). Answers are in the real services' shapes (research-dockerhub.md §1.2, §2.2, §4), from seeded
repositories whose names are real public images and whose numbers are illustrative.

- hub.docker.com: Docker Hub's search (GET /api/search/v4: query, from, size up to 100, type) and its tags API (GET
  /v2/namespaces/<ns>/repositories/<repo>/tags: page, page_size up to 100, ordering, name as a substring), newest push
  first; a private or unknown repository is its 404 {"message": "object not found", "errinfo": {}}.
- index.docker.io: Docker Hub's basic search (GET /v1/search: q, n), the one Docker's own daemon calls.
- registry.e2e.test: the Distribution API with its bearer-token flow. Without a token, /v2/... is a 401 whose
  WWW-Authenticate names the realm https://registry.e2e.test/token; the token service gives a token for a public
  repository to anyone, and a private one's only with its seeded user (Basic). /v2/<repo>/tags/list is in lexical order,
  5 at most to a page, with a relative Link to the next; /v2/<repo>/manifests/<tag or digest> is an OCI index (linux/
  amd64, linux/arm64/v8), then a platform's manifest, then /v2/<repo>/blobs/<digest> its config, for a repository seeded
  with one (else MANIFEST_UNKNOWN).

registry-1.docker.io and auth.docker.io are not the stand-in's: New app's read of a Docker Hub image's config goes on
to the real Docker Hub as it always has. Any other path on these hosts is no route: a 404, and the call is marked
unrouted. Every call is recorded as kind dockerhub or registry, with the repository it's about (call["repo"]).

The control API (/__control/dockerhub/...): PUT /repo seeds or replaces a repository; POST /answer makes the next calls
to a host under a path answer a status (a 429 with Retry-After, a 503), carry low rate-limit headers, or drop the
connection; POST /__control/reset puts the default seed back."""
import base64
import gzip
import hashlib
import json
import re
import secrets
import threading
import time
import urllib.parse
from datetime import datetime, timedelta, timezone

from common import ApiError, Response, log

HUB = "docker.io"
REGISTRY = "registry.e2e.test"
# The hosts the stand-in answers for, and the kind each call is recorded as.
HOSTS = {"hub.docker.com": "dockerhub", "index.docker.io": "dockerhub", REGISTRY: "registry"}

TAGS_PATH = re.compile(r"^/v2/namespaces/(?P<ns>[a-z0-9][a-z0-9._-]*)/repositories/(?P<repo>[a-z0-9][a-z0-9._-]*)/tags/?$")
REPO_PART = r"[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*"
REGISTRY_PATH = re.compile(rf"^/v2/(?P<repo>{REPO_PART}(?:/{REPO_PART})*)/(?P<what>tags/list|manifests/(?P<ref>[^/]+)|blobs/(?P<digest>[^/]+))$")

INDEX = "application/vnd.oci.image.index.v1+json"
MANIFEST = "application/vnd.oci.image.manifest.v1+json"
CONFIG = "application/vnd.oci.image.config.v1+json"
# A registry's page of tags/list, at most: small, so a few tags already take a Link to the next page.
REGISTRY_PAGE = 5
ARCH_LABELS = {"amd64": "x86-64", "arm64": "ARM 64", "arm": "ARM", "386": "x86", "ppc64le": "PowerPC 64 LE", "s390x": "IBM Z",
               "riscv64": "RISC-V 64", "mips64le": "MIPS 64 LE"}
ALL_ARCHS = ["amd64", "arm", "arm64", "386", "mips64le", "ppc64le", "riscv64", "s390x"]
BOTH = ["linux/amd64", "linux/arm64/v8"]


def utc_now():
    return datetime.now(timezone.utc)


def hub_time(dt):
    """Docker Hub's timestamps: 2026-11-20T01:52:05.376546Z."""
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def digest_of(data):
    return "sha256:" + hashlib.sha256(data if isinstance(data, bytes) else data.encode()).hexdigest()


def pull_bucket(n):
    """Docker Hub's v4 pull_count: "1B+", "500M+", ... or the exact count when it's small."""
    for limit, text in ((1_000_000_000, "1B+"), (500_000_000, "500M+"), (100_000_000, "100M+"), (50_000_000, "50M+"), (10_000_000, "10M+"),
                        (5_000_000, "5M+"), (1_000_000, "1M+"), (500_000, "500K+"), (100_000, "100K+"), (50_000, "50K+"), (10_000, "10K+")):
        if n >= limit:
            return text
    return str(n)


def compact(obj):
    return json.dumps(obj, separators=(",", ":")).encode()


class Repo:
    """One seeded repository: its tags ({name, digest, pushed, platforms, size}), whether it's private (and its user),
    the config a registry's manifests lead to, and what Docker Hub's search says of it (None: the search never finds it)."""

    def __init__(self, registry, repository, tags, private=False, user=None, password=None, config=None, search=None):
        self.registry = registry
        self.repository = repository
        self.tags = tags
        self.private = private
        self.user = user
        self.password = password
        self.config = config
        self.search = search
        # A registry's manifests and configs by digest, and the index every tag names: (its bytes, its digest).
        self.blobs = {}
        self.index = None

    @property
    def pulled(self):
        """The name as pulled: nginx for library/nginx, louislam/uptime-kuma, registry.e2e.test/team/api."""
        if self.registry != HUB:
            return f"{self.registry}/{self.repository}"
        return self.repository.removeprefix("library/")

    def state(self):
        search = dict(self.search, updated=hub_time(self.search["updated"])) if self.search and "updated" in self.search else self.search
        return {"registry": self.registry, "repository": self.repository, "private": self.private, "user": self.user,
                "config": self.config, "search": search,
                "tags": [dict(t, pushed=hub_time(t["pushed"])) for t in sorted(self.tags, key=lambda t: t["pushed"], reverse=True)]}


def tag(spec, key, now):
    """A tag as the control API takes one: {name, digest?, pushed? (ISO, or days ago), platforms?, size?}."""
    name = str(spec["name"])
    pushed = spec.get("pushed")
    if isinstance(pushed, (int, float)):
        pushed = now - timedelta(days=pushed)
    elif isinstance(pushed, str) and pushed:
        pushed = datetime.fromisoformat(pushed.replace("Z", "+00:00"))
    else:
        pushed = now - timedelta(days=1)
    platforms = spec.get("platforms")
    return {"name": name, "digest": spec.get("digest") or digest_of(f"{key}:{name}"), "pushed": pushed,
            "platforms": list(BOTH if platforms is None else platforms), "size": int(spec.get("size") or 26_100_000)}


def group(names, label, days, platforms=None, size=None):
    """Tags that name one image (one digest), as the seeds write them."""
    d = digest_of(f"seed:{label}")
    return [{"name": n, "digest": d, "pushed": days, "platforms": platforms or BOTH, "size": size} for n in names]


def search_facts(badge, stars, pulls, description, archs, updated_days, created="2015-03-10T12:00:00Z", kind="image", title=None):
    return {"badge": badge, "stars": stars, "pulls": pulls, "description": description, "architectures": archs,
            "updated_days": updated_days, "created": created, "type": kind, "title": title, "archived": False}


def default_seed():
    """The repositories every rig starts with (design 8.3): real public names, illustrative numbers."""
    nginx_old = [{"name": f"1.{minor}.{patch}", "pushed": 25 + (30 - minor) * 9 + (9 - patch)} for minor in range(20, 30) for patch in range(10)]
    nginx_old += [{"name": f"1.30.{patch}", "pushed": 22 + (6 - patch)} for patch in range(7)]
    nginx = (group(["latest", "mainline", "1", "1.31", "1.31.2"], "nginx-a", 2)
             + group(["stable", "1.30", "1.30.7"], "nginx-b", 0.8)  # stable's rebuild: pushed last, ranked third
             + group(["alpine", "mainline-alpine", "1.31-alpine", "1.31.2-alpine", "1.31.2-alpine3.24"], "nginx-c", 2, size=21_500_000)
             + group(["stable-alpine", "stable-alpine3.24", "1.30.7-alpine", "1.30.7-alpine3.24"], "nginx-d", 0.8, size=21_400_000)
             + group(["1.31.1"], "nginx-e", 21) + group(["1.31.1-alpine"], "nginx-f", 21, size=21_400_000)
             + group(["1.32.0-rc.1"], "nginx-rc", 5)
             + group(["1.31.2-perl"], "nginx-perl", 2, platforms=["linux/arm64/v8"], size=31_000_000)
             + group(["windowsservercore-ltsc2025"], "nginx-win", 2, platforms=["windows/amd64"], size=2_300_000_000)
             + [{"name": "sha256-" + hashlib.sha256(b"seed:nginx-a").hexdigest() + ".sig", "pushed": 2, "platforms": [], "size": 1_200}]
             + nginx_old)
    redis = (group(["8.2.3-alpine", "8.2-alpine", "8-alpine", "alpine"], "redis-a", 3, size=17_800_000)
             + group(["8.2.3", "8.2", "8", "latest"], "redis-b", 3, size=49_600_000)
             + group(["8.0.4-alpine", "8.0-alpine"], "redis-c", 3, size=17_500_000) + group(["8.0.4", "8.0"], "redis-d", 3, size=49_100_000)
             + group(["7.4.6-alpine", "7.4-alpine", "7-alpine"], "redis-e", 3, size=16_900_000) + group(["7.4.6", "7.4", "7"], "redis-f", 3, size=46_800_000))
    kuma_platforms = ["linux/amd64", "linux/arm64/v8", "linux/arm/v7"]
    # As the uptime-kuma template pins it (2.5.11), so the docs' pictures agree with each other.
    kuma = (group(["2.5.11", "2.5", "2", "latest"], "kuma-a", 9, kuma_platforms, 160_200_000)
            + group(["2.5.10"], "kuma-b", 30, kuma_platforms, 160_100_000) + group(["2.4.3", "2.4"], "kuma-c", 75, kuma_platforms, 159_800_000)
            + group(["1.23.16", "1.23", "1"], "kuma-d", 210, kuma_platforms, 147_300_000)
            + group(["1.23.15"], "kuma-e", 260, kuma_platforms, 147_200_000)
            + group(["2.6.0-beta.1", "beta"], "kuma-f", 5, kuma_platforms, 161_100_000))
    postgres = (group(["18.1", "18", "latest"], "pg-a", 1, size=158_400_000) + group(["18.1-alpine", "18-alpine", "alpine"], "pg-b", 1, size=107_900_000)
                + group(["17.7", "17"], "pg-c", 1, size=153_800_000) + group(["17.7-alpine", "17-alpine"], "pg-d", 1, size=103_200_000)
                + group(["16.11", "16"], "pg-e", 1, size=151_700_000))
    api = (group(["1", "1.27", "1.27.3"], "api-a", 4) + group(["1.27.2"], "api-b", 30) + group(["1.26", "1.26.5"], "api-c", 60)
           + group(["1.28.0-rc1"], "api-d", 2) + group(["latest"], "api-a2", 4) + group(["stable"], "api-c2", 60) + group(["sha-4f9c2e1"], "api-e", 1))
    api_config = {"ExposedPorts": {"8080/tcp": {}}, "Env": ["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "PORT=8080"],
                  "Cmd": ["/app/api"], "Volumes": {"/data": {}}}
    return [
        dict(registry=HUB, repository="library/nginx", tags=nginx,
             search=search_facts("official", 21427, 13_624_900_407, "Official build of Nginx.", ALL_ARCHS, 2, "2014-06-05T19:14:45Z")),
        dict(registry=HUB, repository="linuxserver/nginx",
             search=search_facts("open_source", 219, 31_400_000, "An Nginx container, brought to you by LinuxServer.io.", ["amd64", "arm64"], 1)),
        dict(registry=HUB, repository="dtagdevsec/nginx",
             search=search_facts("open_source", 3, 1_100_000, "Nginx, built for the T-Pot honeypot platform.", ["amd64", "arm64"], 21)),
        dict(registry=HUB, repository="arm64v8/nginx",
             search=search_facts("none", 74, 12_000_000, "Official build of Nginx.", ["arm64"], 2)),
        # A Docker Hardened Image: type dhi, which type=image keeps out (it pulls from dhi.io, with a sign-in).
        dict(registry=HUB, repository="dhi/nginx",
             search=search_facts("hardened", 112, 5_200_000, "Docker Hardened Images for Nginx.", ["amd64", "arm64"], 1, kind="dhi", title="Nginx")),
        dict(registry=HUB, repository="library/postgres", tags=postgres,
             search=search_facts("official", 14211, 1_000_000_000 + 512_000_000,
                                 "The PostgreSQL object-relational database system provides reliability and data integrity.", ALL_ARCHS, 1,
                                 "2014-06-05T19:30:12Z")),
        dict(registry=HUB, repository="cimg/postgres",
             search=search_facts("verified_publisher", 6, 152_000_000, "The CircleCI convenience image for PostgreSQL.", ["amd64", "arm64"], 4)),
        dict(registry=HUB, repository="circleci/postgres",
             search=search_facts("verified_publisher", 31, 121_000_000, "CircleCI's legacy PostgreSQL image.", ["amd64"], 400)),
        dict(registry=HUB, repository="kasmweb/postgres",
             search=search_facts("verified_publisher", 1, 2_300_000, "PostgreSQL for Kasm Workspaces.", ["amd64", "arm64"], 12)),
        dict(registry=HUB, repository="library/redis", tags=redis,
             search=search_facts("official", 13302, 1_000_000_000 + 9_000_000,
                                 "Redis is the world's fastest data platform for caching, vector search, and NoSQL databases.", ALL_ARCHS, 3,
                                 "2014-06-05T20:04:50Z")),
        dict(registry=HUB, repository="louislam/uptime-kuma", tags=kuma,
             search=search_facts("none", 1104, 162_000_000, "A fancy self-hosted monitoring tool", ["amd64", "arm64", "arm"], 9, "2021-07-13T11:05:21Z")),
        dict(registry=REGISTRY, repository="team/api", tags=api, config=api_config),
    ]


class DockerHub:
    HOSTS = HOSTS

    def __init__(self):
        self.lock = threading.RLock()
        self.reset()

    # ---- state ----

    def reset(self):
        with self.lock:
            self.repos = {}
            self.tokens = {}
            self.answers = []
            self._answer_id = 0
            for spec in default_seed():
                self.seed(spec)

    def seed(self, b):
        """Seeds or replaces one repository (the control API's PUT /repo)."""
        registry = (b.get("registry") or HUB).lower()
        if registry not in (HUB, REGISTRY):
            raise ApiError(400, f"registry is {HUB} or {REGISTRY}, not {registry}")
        repository = str(b["repository"]).strip().lower()
        if registry == HUB and "/" not in repository:
            repository = "library/" + repository
        now = utc_now()
        key = f"{registry}/{repository}"
        search = b.get("search")
        if search is not None:
            search = dict(search_facts("none", 0, 0, "", ["amd64", "arm64"], 1), **search)
        repo = Repo(registry, repository, [tag(t, key, now) for t in (b.get("tags") or [])], bool(b.get("private")), b.get("user"),
                    b.get("password"), b.get("config"), search)
        if repo.search is not None:
            repo.search["updated"] = now - timedelta(days=float(repo.search.get("updated_days") or 0))
        if repo.config is not None:
            self.build_blobs(repo, now)
        with self.lock:
            self.repos[(registry, repository)] = repo
        return repo

    def repo(self, registry, repository):
        registry = (registry or HUB).lower()
        repository = repository.strip().lower()
        if registry == HUB and "/" not in repository:
            repository = "library/" + repository
        with self.lock:
            return self.repos.get((registry, repository))

    def remove(self, registry, repository):
        r = self.repo(registry, repository)
        with self.lock:
            if r is not None:
                self.repos.pop((r.registry, r.repository), None)
        return r is not None

    def add_answer(self, b):
        """The next `times` calls to host under path get this answer: a status (429 with Retry-After, x-ratelimit-reset in
        reset_in seconds, a 5xx), or, without a status, the usual answer with x-ratelimit-remaining and -reset as given;
        close drops the connection instead."""
        host = (b.get("host") or "").lower()
        if host not in HOSTS:
            raise ApiError(400, f"host is one of {', '.join(sorted(HOSTS))}, not {host!r}")
        with self.lock:
            self._answer_id += 1
            a = {"id": self._answer_id, "host": host, "path": b.get("path") or "/", "status": b.get("status"), "retry_after": b.get("retry_after"),
                 "reset_in": b.get("reset_in"), "remaining": b.get("remaining"), "close": bool(b.get("close")), "left": int(b.get("times") or 1), "hits": 0}
            self.answers.append(a)
            return dict(a)

    def take_answer(self, req):
        with self.lock:
            for a in self.answers:
                if a["host"] == req.host and req.path.startswith(a["path"]) and a["left"] > 0:
                    a["left"] -= 1
                    a["hits"] += 1
                    return dict(a)
        return None

    # ---- what a call is about ----

    def kind_of(self, req):
        return HOSTS[req.host]

    def describe(self, req, call):
        """Notes which repository the call is about (call["repo"]: library/nginx, team/api), before anything answers it."""
        m = TAGS_PATH.match(req.path)
        if m:
            call["repo"] = f"{m['ns']}/{m['repo']}"
            return
        m = REGISTRY_PATH.match(req.path)
        if m:
            call["repo"] = m["repo"]
            return
        scope = req.query.get("scope") or ""
        if scope.startswith("repository:"):
            call["repo"] = scope.split(":")[1]

    # ---- the HTTPS side ----

    def api(self, req, call):
        """The answer to one call; None drops the connection (an answer set with close)."""
        answer = self.take_answer(req)
        if answer is not None:
            call["injected"] = f"dockerhub-answer-{answer['id']}"
            if answer["close"]:
                return None
            if answer["status"]:
                return self.set_answer(answer)
        resp = self.hub(req, call) if call["kind"] == "dockerhub" else self.registry(req, call)
        if answer is not None and resp is not None:
            if answer["remaining"] is not None:
                resp.headers["x-ratelimit-remaining"] = str(answer["remaining"])
            if answer["reset_in"] is not None:
                resp.headers["x-ratelimit-reset"] = str(int(time.time() + float(answer["reset_in"])))
        return resp

    def set_answer(self, a):
        status = int(a["status"])
        headers = {"x-ratelimit-limit": "180", "x-ratelimit-remaining": str(a["remaining"] if a["remaining"] is not None else 0 if status == 429 else 150)}
        if a["retry_after"] is not None:
            headers["Retry-After"] = str(a["retry_after"])
        if a["reset_in"] is not None:
            headers["x-ratelimit-reset"] = str(int(time.time() + float(a["reset_in"])))
        if a["host"] == REGISTRY:
            return Response.json(status, {"errors": [{"code": "TOOMANYREQUESTS" if status == 429 else "UNKNOWN", "message": "set by the scenario"}]}, headers)
        # Docker Hub's own 429 (its abuse limit) and its errors are short.
        return Response.text(status, "Too Many Requests" if status == 429 else "Service Unavailable" if status == 503 else "Internal Server Error", headers)

    @staticmethod
    def unrouted(req, call):
        call["unrouted"] = True
        log(f"no stand-in route: {req.method} {req.host}{req.path}")
        return Response.text(404, f"The rig has no stand-in for {req.host}{req.path}")

    # ---- Docker Hub ----

    def hub(self, req, call):
        if req.method != "GET":
            return self.unrouted(req, call)
        if req.host == "index.docker.io":
            resp = self.search_v1(req) if req.path in ("/v1/search", "/v1/search/") else self.unrouted(req, call)
            resp.headers.update({"x-ratelimit-limit": "3000, 3000;w=60", "x-ratelimit-remaining": "2990", "x-ratelimit-reset": "41"})
            return resp
        if req.path in ("/api/search/v4", "/api/search/v4/"):
            resp = self.search_v4(req)
        elif (m := TAGS_PATH.match(req.path)) is not None:
            resp = self.hub_tags(req, m["ns"], m["repo"])
        else:
            return self.unrouted(req, call)
        resp.headers.update({"x-ratelimit-limit": "180", "x-ratelimit-remaining": "176", "x-ratelimit-reset": str(int(time.time()) + 60)})
        return self.compressed(req, resp)

    @staticmethod
    def compressed(req, resp):
        """gzip, as Docker Hub's answers are when the client asks for it (vary: accept-encoding)."""
        if "gzip" in (req.headers.get("Accept-Encoding") or "").lower() and len(resp.body) > 1024:
            resp.body = gzip.compress(resp.body)
            resp.headers["Content-Encoding"] = "gzip"
            resp.headers["Vary"] = "Accept-Encoding"
        return resp

    def found(self, query, kind=None):
        """The repositories Docker Hub's search finds: the query in the name as pulled or in the description (any case);
        the exact name first, then names that start with it, then by pulls."""
        q = query.strip().lower()
        with self.lock:
            hits = [r for r in self.repos.values() if r.search is not None and not r.private and (kind is None or r.search["type"] == kind)
                    and (q in r.pulled.lower() or q in r.search["description"].lower())]
        return sorted(hits, key=lambda r: (0 if r.pulled == q else 1 if r.pulled.startswith(q) else 2, -r.search["pulls"], r.pulled))

    def search_v4(self, req):
        q = req.query.get("query") or ""
        start = max(0, int(req.query.get("from") or 0))
        size = min(max(1, int(req.query.get("size") or 25)), 100)
        hits = self.found(q, req.query.get("type") or None)
        page = hits[start:start + size]
        out = {"total": len(hits), "results": [self.v4_entry(r) for r in page]}
        if page:
            out["search_after"] = [len(hits) - start - len(page), page[-1].repository]
        return Response.json(200, out)

    @staticmethod
    def v4_entry(r):
        s = r.search
        ns, name = r.repository.split("/", 1)
        logo = f"https://logos.e2e.test/{ns}/{name}/logo.png"
        return {"id": r.repository, "name": s.get("title") or name, "slug": r.pulled, "type": s["type"],
                "publisher": {"id": hashlib.md5(ns.encode()).hexdigest(), "name": ns},
                "created_at": s["created"], "updated_at": hub_time(s["updated"]), "short_description": s["description"],
                "badge": s["badge"], "star_count": s["stars"], "pull_count": pull_bucket(s["pulls"]), "raw_pull_count": s["pulls"],
                "operating_systems": [{"name": "linux", "label": "Linux"}],
                "architectures": [{"name": a, "label": ARCH_LABELS.get(a, a)} for a in s["architectures"]],
                "logo_url": {"large": logo, "small": logo}, "categories": [], "archived": bool(s.get("archived")),
                "content_types": ["image"], "score": s["pulls"] // 1000}

    def search_v1(self, req):
        q = req.query.get("q") or ""
        n = min(max(1, int(req.query.get("n") or 25)), 100)
        hits = self.found(q, "image")
        results = [{"name": r.pulled, "description": r.search["description"], "star_count": r.search["stars"], "pull_count": r.search["pulls"],
                    "is_official": r.search["badge"] == "official", "is_automated": False, "is_trusted": False} for r in hits[:n]]
        return Response.json(200, {"num_pages": max(1, -(-len(hits) // n)), "num_results": len(hits), "page_size": n, "page": 1, "query": q,
                                   "results": results})

    def hub_tags(self, req, ns, name):
        repo = self.repo(HUB, f"{ns}/{name}")
        if repo is None or repo.private or not repo.tags:
            return Response.json(404, {"message": "object not found", "errinfo": {}})
        page = max(1, int(req.query.get("page") or 1))
        size = min(max(1, int(req.query.get("page_size") or 10)), 100)
        text = (req.query.get("name") or "").lower()
        tags = sorted(repo.tags, key=lambda t: (t["pushed"], t["name"]), reverse=True)
        if req.query.get("ordering") == "name":
            tags = sorted(tags, key=lambda t: t["name"], reverse=True)
        if text:
            tags = [t for t in tags if text in t["name"].lower()]
        chunk = tags[(page - 1) * size:page * size]
        base = f"https://hub.docker.com/v2/namespaces/{ns}/repositories/{name}/tags"

        def link(p):
            return base + "?" + urllib.parse.urlencode(dict(req.query, page=str(p)))

        return Response.json(200, {"count": len(tags), "next": link(page + 1) if page * size < len(tags) else None,
                                   "previous": link(page - 1) if page > 1 else None, "results": [self.hub_tag(t) for t in chunk]})

    @staticmethod
    def hub_tag(t):
        pushed = hub_time(t["pushed"])
        images = []
        for p in t["platforms"]:
            os_name, arch, *rest = p.split("/")
            images.append({"architecture": arch, "features": "", "variant": rest[0] if rest else None, "digest": digest_of(f"{t['digest']}|{p}"),
                           "os": os_name, "os_features": "", "os_version": "10.0.26100.4652" if os_name == "windows" else None,
                           "size": t["size"] if arch == "amd64" else max(1, t["size"] - 1_300_000), "status": "active",
                           "last_pulled": hub_time(utc_now() - timedelta(minutes=7)), "last_pushed": pushed})
        # A multi-platform tag carries BuildKit's attestations too, as unknown/unknown images.
        if len(t["platforms"]) > 1:
            images += [{"architecture": "unknown", "features": "", "variant": None, "digest": digest_of(f"{t['digest']}|att|{p}"), "os": "unknown",
                        "os_features": "", "os_version": None, "size": 17_900, "status": "active", "last_pulled": None, "last_pushed": pushed}
                       for p in t["platforms"]]
        full = next((i["size"] for i in images if i["architecture"] == "amd64"), images[0]["size"] if images else t["size"])
        return {"creator": 1156886, "id": int(hashlib.sha256(t["name"].encode()).hexdigest()[:8], 16), "images": images,
                "last_updated": pushed, "last_updater": 1156886, "last_updater_username": "publisher", "name": t["name"], "repository": 21171,
                "full_size": full, "v2": True, "tag_status": "active", "tag_last_pulled": hub_time(utc_now() - timedelta(minutes=7)),
                "tag_last_pushed": pushed, "media_type": INDEX if len(t["platforms"]) > 1 else MANIFEST, "content_type": "image",
                "digest": t["digest"]}

    # ---- registry.e2e.test ----

    def registry(self, req, call):
        h = {"Docker-Distribution-Api-Version": "registry/2.0"}
        if req.method not in ("GET", "HEAD"):
            return self.unrouted(req, call)
        if req.path == "/token":
            return self.token(req)
        if req.path in ("/v2", "/v2/"):
            if self.grants(req) is None:
                return self.challenge(None)
            return Response.json(200, {}, h)
        m = REGISTRY_PATH.match(req.path)
        if not m:
            return self.unrouted(req, call)
        name = m["repo"]
        grants = self.grants(req)
        if grants is None or name not in grants:
            return self.challenge(name, "insufficient_scope" if grants is not None else None)
        repo = self.repo(REGISTRY, name)
        if repo is None:
            return Response.json(404, {"errors": [{"code": "NAME_UNKNOWN", "message": "repository name not known to registry", "detail": {"name": name}}]}, h)
        if m["what"] == "tags/list":
            return self.tags_list(req, repo, h)
        if m["ref"] is not None:
            return self.manifest(repo, m["ref"], h)
        blob = repo.blobs.get(m["digest"])
        if blob is None:
            return Response.json(404, {"errors": [{"code": "BLOB_UNKNOWN", "message": "blob unknown to registry", "detail": m["digest"]}]}, h)
        return Response(200, blob[1], dict(h, **{"Content-Type": "application/octet-stream", "Docker-Content-Digest": m["digest"]}))

    @staticmethod
    def challenge(name, error=None):
        value = f'Bearer realm="https://{REGISTRY}/token",service="{REGISTRY}"'
        if name:
            value += f',scope="repository:{name}:pull"'
        if error:
            value += f',error="{error}"'
        detail = [{"Type": "repository", "Class": "", "Name": name, "Action": "pull"}] if name else None
        return Response.json(401, {"errors": [{"code": "UNAUTHORIZED", "message": "authentication required", "detail": detail}]},
                             {"WWW-Authenticate": value, "Docker-Distribution-Api-Version": "registry/2.0"})

    def grants(self, req):
        """The repositories the request's bearer token may pull; None without a valid token."""
        scheme, _, value = (req.headers.get("Authorization") or "").partition(" ")
        if scheme.lower() != "bearer":
            return None
        with self.lock:
            t = self.tokens.get(value.strip())
        if t is None or t["expires"] < time.time():
            return None
        return t["repos"]

    def token(self, req):
        """The token service: a token for the scope's repositories, a public one's for anyone, a private one's only for its
        seeded user (Basic). A sign-in that matches no seeded user is refused."""
        user = None
        scheme, _, cred = (req.headers.get("Authorization") or "").partition(" ")
        if scheme.lower() == "basic":
            try:
                name, _, password = base64.b64decode(cred.strip()).decode("utf-8", "replace").partition(":")
            except ValueError:
                name, password = "", ""
            with self.lock:
                known = any(r.user == name and r.password == password for r in self.repos.values() if r.registry == REGISTRY and r.user)
            if not known:
                return Response.json(401, {"details": "incorrect username or password"})
            user = (name, password)
        repos = set()
        for scope in (req.query.get("scope") or "").split(" "):
            parts = scope.split(":")
            if len(parts) < 3 or parts[0] != "repository":
                continue
            r = self.repo(REGISTRY, parts[1])
            if r is None or not r.private or user == (r.user, r.password):
                repos.add(parts[1])
        value = secrets.token_urlsafe(24)
        with self.lock:
            self.tokens[value] = {"repos": repos, "expires": time.time() + 300}
        return Response.json(200, {"token": value, "access_token": value, "expires_in": 300, "issued_at": hub_time(utc_now())})

    @staticmethod
    def tags_list(req, repo, h):
        asked = req.query.get("n")
        if asked is not None and (not asked.isdigit() or int(asked) < 1 or int(asked) > 1000):
            return Response.json(400, {"errors": [{"code": "PAGINATION_NUMBER_INVALID", "message": "invalid number of results requested",
                                                   "detail": {"n": asked}}]}, h)
        n = min(int(asked or 100), REGISTRY_PAGE)
        names = sorted({t["name"] for t in repo.tags})
        last = req.query.get("last")
        if last:
            names = [x for x in names if x > last]
        chunk = names[:n]
        headers = dict(h)
        if len(names) > n:
            query = urllib.parse.urlencode({"last": chunk[-1], **({"n": asked} if asked else {})})
            headers["Link"] = f'</v2/{repo.repository}/tags/list?{query}>; rel="next"'
        return Response.json(200, {"name": repo.repository, "tags": chunk}, headers)

    def manifest(self, repo, ref, h):
        if repo.config is None:
            return Response.json(404, {"errors": [{"code": "MANIFEST_UNKNOWN", "message": "manifest unknown", "detail": {"Tag": ref}}]}, h)
        if ref.startswith("sha256:"):
            blob = repo.blobs.get(ref)
            if blob is None or blob[0] not in (INDEX, MANIFEST):
                return Response.json(404, {"errors": [{"code": "MANIFEST_UNKNOWN", "message": "manifest unknown", "detail": {"Digest": ref}}]}, h)
            return Response(200, blob[1], dict(h, **{"Content-Type": blob[0], "Docker-Content-Digest": ref}))
        if not any(t["name"] == ref for t in repo.tags):
            return Response.json(404, {"errors": [{"code": "MANIFEST_UNKNOWN", "message": "manifest unknown", "detail": {"Tag": ref}}]}, h)
        index, index_digest = repo.index
        return Response(200, index, dict(h, **{"Content-Type": INDEX, "Docker-Content-Digest": index_digest}))

    @staticmethod
    def build_blobs(repo, now):
        """What a registry's manifests lead to: one config per platform, its manifest, and the index naming both."""
        entries = []
        for platform in BOTH:
            _, arch, *rest = platform.split("/")
            config = compact({"architecture": arch, "os": "linux", "created": hub_time(now - timedelta(days=4)), "config": repo.config,
                              "rootfs": {"type": "layers", "diff_ids": []}, **({"variant": rest[0]} if rest else {})})
            config_digest = digest_of(config)
            repo.blobs[config_digest] = (CONFIG, config)
            manifest = compact({"schemaVersion": 2, "mediaType": MANIFEST,
                                "config": {"mediaType": CONFIG, "digest": config_digest, "size": len(config)}, "layers": []})
            manifest_digest = digest_of(manifest)
            repo.blobs[manifest_digest] = (MANIFEST, manifest)
            p = {"architecture": arch, "os": "linux", **({"variant": rest[0]} if rest else {})}
            entries.append({"mediaType": MANIFEST, "digest": manifest_digest, "size": len(manifest), "platform": p})
        index = compact({"schemaVersion": 2, "mediaType": INDEX, "manifests": entries})
        index_digest = digest_of(index)
        repo.blobs[index_digest] = (INDEX, index)
        repo.index = (index, index_digest)


# ---- the control API's Docker Hub part ----

def control_routes(dh):
    """The control API's routes, /__control/dockerhub/...: (method, compiled pattern, handler(req, **groups))."""
    routes = []

    def route(method, pattern):
        def add(fn):
            routes.append((method, re.compile("^/__control/dockerhub" + pattern + "$"), fn))
            return fn
        return add

    def body(req):
        return req.json({}) or {}

    @route("PUT", "/repo")
    def c_repo(req):
        """{registry (docker.io or registry.e2e.test), repository, tags: [{name, digest?, pushed? (ISO, or days ago),
        platforms?, size?}], private?, user?, password?, config?: {ExposedPorts, Volumes, Env, Cmd, Healthcheck}, search?:
        {badge, stars, pulls, description, architectures, updated_days}}: tags with the same digest name one image."""
        return dh.seed(body(req)).state()

    @route("GET", "/repo")
    def c_get_repo(req):
        r = dh.repo(req.query.get("registry"), req.query.get("repository") or "")
        if r is None:
            raise ApiError(404, f"No repository {req.query.get('repository')} in the Docker Hub stand-in")
        return r.state()

    @route("DELETE", "/repo")
    def c_remove_repo(req):
        return {"removed": dh.remove(req.query.get("registry"), req.query.get("repository") or "")}

    @route("POST", "/answer")
    def c_answer(req):
        """{host, path, status?, retry_after?, reset_in?, remaining?, close?, times}: the next `times` calls to host under
        path that answer (see DockerHub.add_answer)."""
        return dh.add_answer(body(req))

    @route("GET", "/answers")
    def c_answers(req):
        with dh.lock:
            return {"answers": [dict(a) for a in dh.answers]}

    @route("DELETE", "/answers")
    def c_clear_answers(req):
        with dh.lock:
            dh.answers.clear()
        return {"ok": True}

    @route("POST", "/reset")
    def c_reset(req):
        """The default seed again, no answers set and no tokens: this stand-in's part of POST /__control/reset, for a
        scenario that re-seeded a repository (the others' state stays)."""
        dh.reset()
        return {"ok": True}

    return routes
