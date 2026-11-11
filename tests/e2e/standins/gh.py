"""The GitHub stand-in.

- git over smart HTTP: git http-backend over bare repos, as https://github.com/<owner>/<repo>.git. Pull requests' heads are
  real refs (refs/pull/<n>/head), the fork's commits fetched in for a fork's pull request.
- REST, as api.github.com answers what Gitberth calls (GitHubApi.cs, GitHubWrites.cs, GitHubPulls.cs, RepoVariables.cs):
  /user, repo variables, pulls, commit statuses, issue comments, repo hooks; with fine-grained tokens' permissions
  (read or write per permission, the repos they select, and every public repo's public data readable by any token).
- Webhooks: a push, a branch created or deleted, or a pull request change made through the control API sends GitHub's
  delivery (headers, HMAC signatures, JSON payload) to each matching hook. Each delivery goes where its hook's URL
  leads in the rig, through the Cloudflare stand-in's edge and the fake cloudflared (DNS record, Access, the tunnel's
  ingress), so it reaches Gitberth only when that route is right, as in real life.
- The control API (/__control/github/...) plays the people using GitHub: repos from the app templates, commits, branches,
  forks, pull requests, tokens, and reads back what Gitberth did.
"""
import base64
import datetime
import gzip
import hashlib
import hmac
import http.client
import json
import os
import re
import shutil
import subprocess
import tempfile
import threading
import time
import urllib.parse
import uuid
from concurrent.futures import ThreadPoolExecutor

from common import GITHUB_LIMITS, ApiError, Response, github_limit_message, github_rate_headers, iso, log, log_exception, token_of

WEB = "https://github.com"
API = "https://api.github.com"
DOCS = "https://docs.github.com/rest"
ZERO = "0" * 40
PERMISSIONS = ("contents", "metadata", "variables", "statuses", "pull_requests", "issues", "webhooks")
# GitHub's hook deliveries come from its "hooks" ranges; behind Cloudflare, Gitberth sees that as CF-Connecting-IP.
HOOK_SENDER = "140.82.115.{}"
# GitHub waits this long for a delivery's answer, all told; then it hangs up and counts the delivery as failed.
DELIVERY_TIMEOUT = 10


def node_id(prefix, n):
    return prefix + base64.b64encode(f"{prefix}{n}".encode()).decode().rstrip("=")


def git(*args, cwd=None, env=None, input=None, check=True):
    full_env = dict(os.environ)
    full_env.update({
        "GIT_AUTHOR_NAME": "E2E Bot", "GIT_AUTHOR_EMAIL": "e2e-bot@example.com",
        "GIT_COMMITTER_NAME": "E2E Bot", "GIT_COMMITTER_EMAIL": "e2e-bot@example.com",
        "GIT_TERMINAL_PROMPT": "0",
    })
    full_env.update(env or {})
    r = subprocess.run(["git", *args], cwd=cwd, env=full_env, input=input, capture_output=True)
    if check and r.returncode != 0:
        raise ApiError(409, f"git {args[0]} failed: {r.stderr.decode('utf-8', 'replace').strip()}")
    return r.stdout.decode("utf-8", "replace")


class Token:
    """A token GitHub accepts. Its permissions are a fine-grained token's repository permissions: True or "write" (read
    and write), "read" (read-only), False or "none" (no access); every one is "write" unless given. repos: the only
    repos it selects (None: all). Like any fine-grained token it can still read every public repo's public data."""

    def __init__(self, value, login, user_id, expires=None, permissions=None, repos=None, kind="fine-grained"):
        self.value = value
        self.login = login
        self.user_id = user_id
        self.expires = expires
        self.permissions = {p: True for p in PERMISSIONS}
        self.permissions.update(permissions or {})
        self.repos = {r.lower() for r in repos} if repos else None
        self.kind = kind
        self.revoked = False
        self.used = 0

    def expired(self, now=None):
        """Whether the expiry GitHub states for it (github-authentication-token-expiration: "2026-12-01 00:00:00 UTC",
        or with an offset like "-0500") has passed. GitHub then refuses it, as it does a revoked one."""
        if not self.expires:
            return False
        text = str(self.expires).strip()
        try:
            if text.endswith(" UTC"):
                at = datetime.datetime.strptime(text[:-4], "%Y-%m-%d %H:%M:%S").replace(tzinfo=datetime.timezone.utc)
            else:
                at = datetime.datetime.strptime(text, "%Y-%m-%d %H:%M:%S %z")
        except ValueError:
            return False
        return (now or datetime.datetime.now(datetime.timezone.utc)) >= at

    def refused(self):
        """GitHub turns it away: revoked, or past its expiry."""
        return self.revoked or self.expired()

    def sees(self, repo):
        return self.repos is None or repo.key in self.repos

    def can(self, permission, write=False):
        value = self.permissions.get(permission, True)
        level = 2 if value is True or value == "write" else 1 if value == "read" else 0
        return level >= (2 if write else 1)

    def public(self):
        return {"login": self.login, "expires": self.expires, "expired": self.expired(), "permissions": self.permissions,
                "repos": sorted(self.repos) if self.repos else None, "kind": self.kind, "revoked": self.revoked,
                "used": self.used, "tag": tag(self.value)}


def tag(token):
    return None if not token else hashlib.sha256(token.encode()).hexdigest()[:8]


class RateLimits:
    """GitHub's primary rate limit on its REST API (the "core" resource), counted as GitHub counts it: 60 requests an hour
    for each address that sends no token (or one GitHub refuses), 5,000 for each user with a token (all of a user's
    tokens together). A window starts with its first request and ends an hour later, at x-ratelimit-reset. A request
    past the limit gets GitHub's 403 and isn't counted. GET /rate_limit never counts."""

    ANONYMOUS = 60
    USER = 5000

    def __init__(self):
        self._lock = threading.Lock()
        self._windows = {}

    @staticmethod
    def key(req, tok):
        """Who a request counts against: its token's user, or the address it came from."""
        return ("user", tok.user_id) if tok is not None else ("ip", req.client)

    def take(self, key, count=True):
        """Counts one request against key (unless count is false). Returns (allowed, rate), rate being {limit, used,
        reset, anonymous, who} after it: who is the address, or "user ID <id>", as GitHub's message names it."""
        limit = self.USER if key[0] == "user" else self.ANONYMOUS
        now = time.time()
        with self._lock:
            w = self._windows.get(key)
            if w is None or now >= w["reset"]:
                w = {"used": 0, "reset": int(now) + 3600}
                if count:
                    self._windows[key] = w
            allowed = w["used"] < limit
            if allowed and count:
                w["used"] += 1
            used, reset = w["used"], w["reset"]
        return allowed, {"limit": limit, "used": used, "reset": reset, "anonymous": key[0] == "ip",
                         "who": key[1] if key[0] == "ip" else f"user ID {key[1]}"}

    def peek(self, key):
        return self.take(key, count=False)[1]

    def state(self):
        with self._lock:
            return [{"key": f"{k[0]}:{k[1]}", "used": w["used"], "reset": w["reset"]} for k, w in self._windows.items()]

    def clear(self):
        with self._lock:
            self._windows.clear()


class Pull:
    def __init__(self, gh, number, title, body, user, head_repo_key, head_owner, head_ref, head_sha, base_ref, base_sha, draft):
        self.number = number
        self.id = gh.next_id("pull")
        self.node_id = node_id("PR_", self.id)
        self.title = title
        self.body = body
        self.user = user
        self.head_repo_key = head_repo_key
        self.head_owner = head_owner
        self.head_ref = head_ref
        self.head_sha = head_sha
        self.base_ref = base_ref
        self.base_sha = base_sha
        self.draft = draft
        self.state = "open"
        self.merged = False
        self.merged_at = None
        self.merged_by = None
        self.merge_commit_sha = None
        self.closed_at = None
        self.created_at = iso()
        self.updated_at = self.created_at


class Repo:
    def __init__(self, gh, owner, name, private, default_branch, fork_of=None):
        self.owner = owner
        self.name = name
        self.key = f"{owner}/{name}".lower()
        self.id = gh.next_id("repo")
        self.node_id = node_id("R_", self.id)
        self.private = private
        self.default_branch = default_branch
        self.fork_of = fork_of
        self.created_at = iso()
        self.updated_at = self.created_at
        self.pushed_at = self.created_at
        self.path = os.path.join(gh.root, owner.lower(), name.lower() + ".git")
        self.variables = {}
        self.hooks = {}
        self.statuses = []
        self.comments = {}
        self.pulls = {}
        self.next_number = 1

    @property
    def full_name(self):
        return f"{self.owner}/{self.name}"


class GitHub:
    def __init__(self, root, templates, recorder, gitberth_url, edge=None):
        self.root = root
        self.templates = templates
        self.rec = recorder
        # Where a delivery sent with via_tunnel=False goes: straight to Gitberth's container.
        self.gitberth_url = gitberth_url.rstrip("/")
        # Cloudflare's edge (Cloudflare.edge): how every other delivery reaches its URL, as it would in real life.
        self.edge = edge
        self.lock = threading.RLock()
        self.pool = ThreadPoolExecutor(max_workers=4, thread_name_prefix="delivery")
        self._ids = {}
        self.reset()

    # ---- state ----

    def reset(self):
        with self.lock:
            self.repos = {}
            self.tokens = {}
            self.users = {}
            self.deliveries = []
            self.limits = RateLimits()
            # Comment and status IDs are past 2**31, as GitHub's are now: a 32-bit ID would break on them.
            self._ids = {"repo": 700000000, "pull": 900000000, "hook": 400000000, "comment": 2600000000, "status": 3000000000}
            shutil.rmtree(self.root, ignore_errors=True)
            os.makedirs(self.root, exist_ok=True)

    def next_id(self, kind):
        with self.lock:
            self._ids[kind] = self._ids.get(kind, 1) + 1
            return self._ids[kind]

    def user_json(self, login):
        uid = self.users.setdefault(login.lower(), 5000000 + int(hashlib.sha256(login.lower().encode()).hexdigest()[:6], 16))
        return {"login": login, "id": uid, "node_id": node_id("U_", uid), "avatar_url": f"https://avatars.githubusercontent.com/u/{uid}?v=4",
                "url": f"{API}/users/{login}", "html_url": f"{WEB}/{login}", "type": "User", "site_admin": False}

    def repo_json(self, repo):
        full = repo.full_name
        return {
            "id": repo.id, "node_id": repo.node_id, "name": repo.name, "full_name": full, "private": repo.private,
            "owner": self.user_json(repo.owner), "html_url": f"{WEB}/{full}", "description": None, "fork": repo.fork_of is not None,
            "url": f"{API}/repos/{full}", "hooks_url": f"{API}/repos/{full}/hooks", "pulls_url": f"{API}/repos/{full}/pulls{{/number}}",
            "statuses_url": f"{API}/repos/{full}/statuses/{{sha}}", "git_url": f"git://github.com/{full}.git",
            "ssh_url": f"git@github.com:{full}.git", "clone_url": f"{WEB}/{full}.git", "created_at": repo.created_at,
            "updated_at": repo.updated_at, "pushed_at": repo.pushed_at, "default_branch": repo.default_branch,
            "visibility": "private" if repo.private else "public", "archived": False, "disabled": False, "has_issues": True,
            "allow_forking": True, "is_template": False, "topics": [],
        }

    def repo(self, owner, name, missing_ok=False):
        r = self.repos.get(f"{owner}/{name}".lower())
        if r is None and not missing_ok:
            raise ApiError(404, f"No repo {owner}/{name} in the stand-in")
        return r

    def branches(self, repo):
        out = git("--git-dir", repo.path, "for-each-ref", "--format=%(refname:lstrip=2) %(objectname)", "refs/heads/")
        return dict(line.rsplit(" ", 1) for line in out.splitlines() if " " in line)

    def branch_sha(self, repo, branch):
        return self.branches(repo).get(branch)

    def has_commit(self, repo, sha):
        return re.fullmatch(r"[0-9a-fA-F]{7,64}", sha or "") is not None and \
            subprocess.run(["git", "--git-dir", repo.path, "cat-file", "-e", f"{sha}^{{commit}}"], capture_output=True).returncode == 0

    def commit_json(self, repo, sha):
        fmt = "%H%x1f%T%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%B"
        f = git("--git-dir", repo.path, "log", "-1", f"--format={fmt}", sha).rstrip("\n").split("\x1f")
        changes = git("--git-dir", repo.path, "diff-tree", "--root", "--no-commit-id", "--name-status", "-r", sha)
        added, removed, modified = [], [], []
        for line in changes.splitlines():
            kind, _, path = line.partition("\t")
            (added if kind == "A" else removed if kind == "D" else modified).append(path)
        return {
            "id": f[0], "tree_id": f[1], "distinct": True, "message": f[7].strip(), "timestamp": f[4],
            "url": f"{WEB}/{repo.full_name}/commit/{f[0]}",
            "author": {"name": f[2], "email": f[3], "username": "e2e-bot"},
            "committer": {"name": f[5], "email": f[6], "username": "e2e-bot"},
            "added": added, "removed": removed, "modified": modified,
        }

    # ---- tokens ----

    def add_token(self, spec):
        value = spec.get("token") or ("gbe2e_" + uuid.uuid4().hex)
        login = spec.get("login") or "e2e-bot"
        t = Token(value, login, self.user_json(login)["id"], spec.get("expires"), spec.get("permissions"), spec.get("repos"),
                  spec.get("kind") or "fine-grained")
        with self.lock:
            self.tokens[value] = t
        return t

    def _auth(self, req):
        """The caller's token: None when it sent none; 401 when it sent one GitHub doesn't know (or that was revoked, or
        has expired)."""
        value = token_of(req.headers)
        if value is None:
            return None
        t = self.tokens.get(value)
        if t is None or t.refused():
            raise ApiError(401, "Bad credentials")
        t.used += 1
        return t

    def rate_state(self, req):
        """The caller's primary rate limit as it stands, without counting this request: for an injected answer."""
        value = token_of(req.headers)
        tok = self.tokens.get(value) if value else None
        return self.limits.peek(RateLimits.key(req, None if tok is None or tok.refused() else tok))

    # ---- what a call is about ----

    GIT_PATH = re.compile(r"^/(?P<o>[^/]+)/(?P<r>[^/]+?)(?:\.git)?(?P<rest>/(?:info/refs|git-upload-pack|git-receive-pack|HEAD|objects/.*))$")
    API_REPO = re.compile(r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)")

    def describe(self, req, call):
        """Notes on the call's record which repo it's about, and for git what it asked for (describe_git_request), from
        the request alone. It's done before anything answers the call, so one an injected failure answered, or that
        GitHub refused (401, 404), is still found by its repo."""
        m = self.GIT_PATH.match(req.path) if call["kind"] == "git" else self.API_REPO.match(req.path)
        if not m:
            return
        call["repo"] = f"{urllib.parse.unquote(m['o'])}/{urllib.parse.unquote(m['r'])}".lower()
        if call["kind"] == "git":
            call["git"] = describe_git_request(m["rest"], req)

    @staticmethod
    def unrouted(req, call):
        """A call no stand-in route answers: GitHub's 404 goes back, which Gitberth would read as a missing permission or
        a repo it can't see. So it's logged and marked on its record (unrouted), and the scenarios fail on it."""
        call["unrouted"] = True
        log(f"no stand-in route: {req.method} {req.host}{req.path}")

    # ---- git over smart HTTP ----

    def git_http(self, req, call):
        m = self.GIT_PATH.match(req.path)
        if not m:
            self.unrouted(req, call)
            return Response.text(404, "Not Found")
        owner, name, rest = urllib.parse.unquote(m["o"]), urllib.parse.unquote(m["r"]), m["rest"]
        call["repo"] = f"{owner}/{name}".lower()
        if "git" not in call:
            call["git"] = describe_git_request(rest, req)
        value = token_of(req.headers)
        tok = self.tokens.get(value) if value else None
        if value and (tok is None or tok.refused()):
            return Response.text(401, "Invalid username or token. Password authentication is not supported for Git operations.\n",
                                 {"WWW-Authenticate": 'Basic realm="GitHub"'})
        repo = self.repos.get(call["repo"])
        allowed = repo is not None and (not repo.private or (tok is not None and tok.sees(repo) and tok.can("contents")))
        if not allowed:
            # Without credentials GitHub asks for them rather than say whether a private repo exists.
            if tok is None:
                return Response.text(401, "", {"WWW-Authenticate": 'Basic realm="GitHub"'})
            return Response.text(404, "Repository not found.\n")
        if rest.endswith("git-receive-pack") or req.query.get("service") == "git-receive-pack":
            return Response.text(403, "The rig takes pushes through its control API.\n")
        env = {
            "GIT_PROJECT_ROOT": os.path.dirname(repo.path), "GIT_HTTP_EXPORT_ALL": "1",
            "PATH_INFO": "/" + os.path.basename(repo.path) + rest, "REQUEST_METHOD": req.method,
            "QUERY_STRING": req.query_string, "CONTENT_TYPE": req.headers.get("Content-Type") or "",
            "CONTENT_LENGTH": str(len(req.body)), "REMOTE_ADDR": req.client,
            "PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": os.environ.get("HOME", "/root"),
        }
        if req.headers.get("Git-Protocol"):
            env["HTTP_GIT_PROTOCOL"] = req.headers["Git-Protocol"]
        if req.headers.get("Content-Encoding"):
            env["HTTP_CONTENT_ENCODING"] = req.headers["Content-Encoding"]
        if tok is not None:
            env["REMOTE_USER"] = tok.login
        p = subprocess.run(["git", "http-backend"], input=req.body, env=env, capture_output=True, timeout=120)
        out = p.stdout
        head, sep, body = out.partition(b"\r\n\r\n")
        if not sep:
            head, sep, body = out.partition(b"\n\n")
        if not sep:
            return Response.text(500, "git http-backend: " + p.stderr.decode("utf-8", "replace"))
        status, headers = 200, {}
        for line in head.decode("latin-1").splitlines():
            k, _, v = line.partition(":")
            if k.strip().lower() == "status":
                status = int(v.strip().split()[0])
            elif k.strip():
                headers[k.strip()] = v.strip()
        return Response(status, body, headers)

    # ---- REST ----

    ROUTES = [
        ("GET", r"^/user$", "get_user"),
        ("GET", r"^/rate_limit$", "rate_limit"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)$", "get_repo"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/branches$", "list_branches"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/actions/variables$", "list_variables"),
        ("POST", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/actions/variables$", "create_variable"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/actions/variables/(?P<name>[^/]+)$", "get_variable"),
        ("PATCH", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/actions/variables/(?P<name>[^/]+)$", "update_variable"),
        ("DELETE", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/actions/variables/(?P<name>[^/]+)$", "delete_variable"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/pulls$", "list_pulls"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/pulls/(?P<n>\d+)$", "get_pull"),
        ("POST", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/statuses/(?P<sha>[^/]+)$", "create_status"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/commits/(?P<ref>.+)/statuses$", "list_statuses"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/commits/(?P<ref>.+)/status$", "combined_status"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/issues/(?P<n>\d+)/comments$", "list_comments"),
        ("POST", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/issues/(?P<n>\d+)/comments$", "create_comment"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/issues/comments/(?P<id>\d+)$", "get_comment"),
        ("PATCH", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/issues/comments/(?P<id>\d+)$", "update_comment"),
        ("DELETE", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/issues/comments/(?P<id>\d+)$", "delete_comment"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/hooks$", "list_hooks"),
        ("POST", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/hooks$", "create_hook"),
        ("GET", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/hooks/(?P<id>\d+)$", "get_hook"),
        ("PATCH", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/hooks/(?P<id>\d+)$", "update_hook"),
        ("DELETE", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/hooks/(?P<id>\d+)$", "delete_hook"),
        ("POST", r"^/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/hooks/(?P<id>\d+)/pings$", "ping_hook"),
    ]
    _compiled = [(m, re.compile(p), fn) for m, p, fn in ROUTES]

    def api(self, req, call):
        if "repo" not in call:
            self.describe(req, call)
        tok, rate = None, None
        counts = req.path != "/rate_limit"
        try:
            try:
                tok = self._auth(req)
            finally:
                # Counted against the caller's limit; a request whose token GitHub refuses counts against its address,
                # as one without a token does.
                allowed, rate = self.limits.take(RateLimits.key(req, tok), count=counts)
            if counts and not allowed:
                call["rate_limited"] = True
                raise ApiError(403, github_limit_message(rate), extra={"documentation_url": GITHUB_LIMITS + "#about-the-primary-rate-limit"})
            for method, rx, fn in self._compiled:
                m = rx.match(req.path)
                if m and method == req.method:
                    args = {k: urllib.parse.unquote(v) for k, v in m.groupdict().items()}
                    with self.lock:
                        status, body, headers = getattr(self, fn)(req, tok, **args)
                    return self._answer(status, body, tok, rate, headers)
            self.unrouted(req, call)
            raise ApiError(404, "Not Found")
        except ApiError as e:
            extra = dict(e.extra)
            headers = extra.pop("_headers", None)
            docs = extra.pop("documentation_url", DOCS)
            body = {"message": e.message}
            body.update(extra)
            body.update({"documentation_url": docs, "status": str(e.status)})
            return self._answer(e.status, body, tok, rate, headers)

    def _answer(self, status, body, tok, rate, headers=None):
        h = {
            "X-GitHub-Api-Version-Selected": "2022-11-28", "X-GitHub-Media-Type": "github.v3; format=json",
            "X-GitHub-Request-Id": uuid.uuid4().hex[:20].upper(), **github_rate_headers(rate),
        }
        if tok is not None and tok.expires:
            h["github-authentication-token-expiration"] = tok.expires
        if tok is not None and tok.kind == "classic":
            h["X-OAuth-Scopes"] = "repo, admin:repo_hook"
        h.update(headers or {})
        if status == 204 or body is None:
            return Response(status, b"", h)
        return Response.json(status, body, h)

    def _repo_for(self, tok, o, r, permission=None, write=False, public=False):
        """The repo, as GitHub lets this caller at it:
        - one that doesn't exist, or a private one the token doesn't select (or without a token): 404;
        - public=True is a read of a public repo's public data (the repo, branches, pull requests, commit statuses, issue
          comments): anyone may make it, with any token or none, whatever repos the token selects;
        - anything else needs a token (401 without one), one that selects the repo (403 for a public repo it doesn't), and
          `permission`: a name, or a tuple of alternatives of which any one will do (403 naming them otherwise)."""
        repo = self.repos.get(f"{o}/{r}".lower())
        if repo is None:
            raise ApiError(404, "Not Found")
        open_read = public and not write and not repo.private
        if tok is None:
            if repo.private:
                raise ApiError(404, "Not Found")
            if open_read:
                return repo
            raise ApiError(401, "Requires authentication")
        if not tok.sees(repo):
            if repo.private:
                raise ApiError(404, "Not Found")
            if open_read:
                return repo
            raise self._refused(permission, write)
        alternatives = (permission,) if isinstance(permission, str) else tuple(permission or ())
        if not open_read and alternatives and not any(tok.can(p, write) for p in alternatives):
            raise self._refused(permission, write)
        return repo

    @staticmethod
    def _refused(permission, write):
        """GitHub's 403 for a fine-grained token that lacks what the endpoint needs (alternatives separated by ';')."""
        alternatives = (permission,) if isinstance(permission, str) else tuple(permission or ())
        accepted = "; ".join(f"{p}={'write' if write else 'read'}" for p in alternatives) or "metadata=read"
        return ApiError(403, "Resource not accessible by personal access token", extra={"_headers": {"X-Accepted-GitHub-Permissions": accepted}})

    @staticmethod
    def _page(req, items, default=30):
        try:
            per = max(1, min(int(req.query.get("per_page") or default), 100))
            page = max(1, int(req.query.get("page") or 1))
        except ValueError:
            raise ApiError(400, "Invalid pagination")
        last = max(1, (len(items) + per - 1) // per)
        links = []

        def link(p, rel):
            q = dict(req.query, page=str(p), per_page=str(per))
            return f'<{API}{req.path}?{urllib.parse.urlencode(q)}>; rel="{rel}"'

        if page < last:
            links += [link(page + 1, "next"), link(last, "last")]
        if page > 1:
            links += [link(1, "first"), link(page - 1, "prev")]
        return items[(page - 1) * per: page * per], ({"Link": ", ".join(links)} if links else {})

    # /user
    def get_user(self, req, tok):
        if tok is None:
            raise ApiError(401, "Requires authentication")
        u = self.user_json(tok.login)
        u.update({"name": "E2E Bot", "company": None, "blog": "", "location": None, "email": None, "bio": None,
                  "public_repos": 0, "followers": 0, "following": 0, "created_at": "2020-01-01T00:00:00Z", "updated_at": iso()})
        return 200, u, None

    def rate_limit(self, req, tok):
        # It doesn't count against the limit it shows.
        rate = self.limits.peek(RateLimits.key(req, tok))
        core = {"limit": rate["limit"], "used": rate["used"], "remaining": max(0, rate["limit"] - rate["used"]), "reset": rate["reset"]}
        return 200, {"resources": {"core": core}, "rate": core}, None

    def get_repo(self, req, tok, o, r):
        return 200, self.repo_json(self._repo_for(tok, o, r, public=True)), None

    def list_branches(self, req, tok, o, r):
        repo = self._repo_for(tok, o, r, "contents", public=True)
        items = [{"name": b, "commit": {"sha": s, "url": f"{API}/repos/{repo.full_name}/commits/{s}"}, "protected": False}
                 for b, s in sorted(self.branches(repo).items())]
        chunk, headers = self._page(req, items)
        return 200, chunk, headers

    # repo variables (Settings > Secrets and variables > Actions > Variables)
    def list_variables(self, req, tok, o, r):
        repo = self._repo_for(tok, o, r, "variables")
        items = sorted(repo.variables.values(), key=lambda v: v["name"])
        chunk, headers = self._page(req, items, default=10)
        return 200, {"total_count": len(items), "variables": chunk}, headers

    def get_variable(self, req, tok, o, r, name):
        repo = self._repo_for(tok, o, r, "variables")
        v = repo.variables.get(name.upper())
        if v is None:
            raise ApiError(404, "Not Found")
        return 200, v, None

    def _check_variable(self, name, value):
        errors = []
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", name or ""):
            errors.append("name must contain only alphanumeric characters or underscores and must not start with a number")
        elif name.upper().startswith("GITHUB_"):
            errors.append("name must not start with GITHUB_")
        if value is None or value == "":
            errors.append("value can't be blank")
        if errors:
            raise ApiError(422, "Invalid request.\n\n" + "\n".join(errors))

    def create_variable(self, req, tok, o, r):
        repo = self._repo_for(tok, o, r, "variables", write=True)
        b = req.json({}) or {}
        self._check_variable(b.get("name"), b.get("value"))
        key = b["name"].upper()
        if key in repo.variables:
            raise ApiError(409, "Already exists - Variable already exists")
        repo.variables[key] = {"name": key, "value": str(b["value"]), "created_at": iso(), "updated_at": iso()}
        return 201, {}, None

    def update_variable(self, req, tok, o, r, name):
        repo = self._repo_for(tok, o, r, "variables", write=True)
        v = repo.variables.get(name.upper())
        if v is None:
            raise ApiError(404, "Not Found")
        b = req.json({}) or {}
        new_name = (b.get("name") or v["name"])
        self._check_variable(new_name, b.get("value", v["value"]))
        del repo.variables[name.upper()]
        repo.variables[new_name.upper()] = {"name": new_name.upper(), "value": str(b.get("value", v["value"])),
                                            "created_at": v["created_at"], "updated_at": iso()}
        return 204, None, None

    def delete_variable(self, req, tok, o, r, name):
        repo = self._repo_for(tok, o, r, "variables", write=True)
        if repo.variables.pop(name.upper(), None) is None:
            raise ApiError(404, "Not Found")
        return 204, None, None

    # pulls
    def pull_json(self, repo, pr, full=False):
        head_repo = self.repos.get(pr.head_repo_key) if pr.head_repo_key else None
        full_name = repo.full_name
        d = {
            "url": f"{API}/repos/{full_name}/pulls/{pr.number}", "id": pr.id, "node_id": pr.node_id,
            "html_url": f"{WEB}/{full_name}/pull/{pr.number}", "diff_url": f"{WEB}/{full_name}/pull/{pr.number}.diff",
            "patch_url": f"{WEB}/{full_name}/pull/{pr.number}.patch", "issue_url": f"{API}/repos/{full_name}/issues/{pr.number}",
            "commits_url": f"{API}/repos/{full_name}/pulls/{pr.number}/commits",
            "comments_url": f"{API}/repos/{full_name}/issues/{pr.number}/comments",
            "statuses_url": f"{API}/repos/{full_name}/statuses/{pr.head_sha}",
            "number": pr.number, "state": pr.state, "locked": False, "title": pr.title, "user": self.user_json(pr.user),
            "body": pr.body, "labels": [], "milestone": None, "active_lock_reason": None, "created_at": pr.created_at,
            "updated_at": pr.updated_at, "closed_at": pr.closed_at, "merged_at": pr.merged_at, "merge_commit_sha": pr.merge_commit_sha,
            "assignee": None, "assignees": [], "requested_reviewers": [], "requested_teams": [],
            "head": {"label": f"{pr.head_owner}:{pr.head_ref}", "ref": pr.head_ref, "sha": pr.head_sha,
                     "user": self.user_json(pr.head_owner), "repo": self.repo_json(head_repo) if head_repo else None},
            "base": {"label": f"{repo.owner}:{pr.base_ref}", "ref": pr.base_ref, "sha": pr.base_sha,
                     "user": self.user_json(repo.owner), "repo": self.repo_json(repo)},
            "author_association": "OWNER" if pr.user.lower() == repo.owner.lower() else "CONTRIBUTOR",
            "auto_merge": None, "draft": pr.draft,
        }
        if full:
            d.update({"merged": pr.merged, "mergeable": True if pr.state == "open" else None, "rebaseable": pr.state == "open",
                      "mergeable_state": "clean" if pr.state == "open" else "unknown",
                      "merged_by": self.user_json(pr.merged_by) if pr.merged_by else None,
                      "comments": sum(1 for c in repo.comments.values() if c["issue"] == pr.number), "review_comments": 0,
                      "maintainer_can_modify": False, "commits": 1, "additions": 1, "deletions": 0, "changed_files": 1})
        return d

    def list_pulls(self, req, tok, o, r):
        repo = self._repo_for(tok, o, r, "pull_requests", public=True)
        state = req.query.get("state") or "open"
        if state not in ("open", "closed", "all"):
            raise ApiError(422, "Validation Failed", extra={"errors": [{"resource": "PullRequest", "field": "state", "code": "invalid"}]})
        pulls = [p for p in repo.pulls.values() if state == "all" or p.state == state]
        if req.query.get("base"):
            pulls = [p for p in pulls if p.base_ref == req.query["base"]]
        if req.query.get("head"):
            owner_ref = req.query["head"]
            pulls = [p for p in pulls if f"{p.head_owner}:{p.head_ref}".lower() == owner_ref.lower()]
        reverse = (req.query.get("direction") or "desc") == "desc"
        pulls.sort(key=lambda p: p.number, reverse=reverse)
        chunk, headers = self._page(req, [self.pull_json(repo, p) for p in pulls])
        return 200, chunk, headers

    def get_pull(self, req, tok, o, r, n):
        # One pull request reads with Contents as well as with Pull requests (GitHub lists it under both); the list only
        # with Pull requests.
        repo = self._repo_for(tok, o, r, ("pull_requests", "contents"), public=True)
        pr = repo.pulls.get(int(n))
        if pr is None:
            raise ApiError(404, "Not Found")
        return 200, self.pull_json(repo, pr, full=True), None

    # commit statuses
    def status_json(self, repo, s):
        return {k: v for k, v in s.items() if k not in ("sha", "repo", "token")}

    def create_status(self, req, tok, o, r, sha):
        repo = self._repo_for(tok, o, r, "statuses", write=True)
        b = req.json({}) or {}
        errors = []
        if b.get("state") not in ("error", "failure", "pending", "success"):
            errors.append({"resource": "Status", "code": "custom", "field": "state", "message": "state is not included in the list"})
        desc = b.get("description")
        if desc is not None and len(desc) > 140:
            errors.append({"resource": "Status", "code": "custom", "field": "description",
                           "message": "description is too long (maximum is 140 characters)"})
        if errors:
            raise ApiError(422, "Validation Failed", extra={"errors": errors})
        if not self.has_commit(repo, sha):
            raise ApiError(422, f"No commit found for SHA: {sha}")
        full_sha = git("--git-dir", repo.path, "rev-parse", f"{sha}^{{commit}}").strip()
        sid = self.next_id("status")
        s = {
            "url": f"{API}/repos/{repo.full_name}/statuses/{full_sha}", "avatar_url": None, "id": sid, "node_id": node_id("SC_", sid),
            "state": b["state"], "description": desc, "target_url": b.get("target_url"), "context": b.get("context") or "default",
            "created_at": iso(), "updated_at": iso(), "creator": self.user_json(tok.login), "sha": full_sha, "token": tag(tok.value),
        }
        repo.statuses.append(s)
        return 201, self.status_json(repo, s), None

    def _resolve(self, repo, ref):
        b = self.branch_sha(repo, ref)
        if b:
            return b
        if self.has_commit(repo, ref):
            return git("--git-dir", repo.path, "rev-parse", f"{ref}^{{commit}}").strip()
        raise ApiError(404, "Not Found")

    def list_statuses(self, req, tok, o, r, ref):
        repo = self._repo_for(tok, o, r, "statuses", public=True)
        sha = self._resolve(repo, ref)
        items = [self.status_json(repo, s) for s in reversed(repo.statuses) if s["sha"] == sha]
        chunk, headers = self._page(req, items)
        return 200, chunk, headers

    def combined_status(self, req, tok, o, r, ref):
        repo = self._repo_for(tok, o, r, "statuses", public=True)
        sha = self._resolve(repo, ref)
        latest = {}
        for s in repo.statuses:
            if s["sha"] == sha:
                latest[s["context"]] = s
        states = [s["state"] for s in latest.values()]
        state = "failure" if any(x in ("failure", "error") for x in states) else "pending" if (not states or "pending" in states) else "success"
        return 200, {"state": state, "statuses": [self.status_json(repo, s) for s in latest.values()], "sha": sha,
                     "total_count": len(latest), "repository": self.repo_json(repo)}, None

    # issue comments (a pull request is an issue)
    def comment_json(self, repo, c):
        return {
            "id": c["id"], "node_id": node_id("IC_", c["id"]), "url": f"{API}/repos/{repo.full_name}/issues/comments/{c['id']}",
            "html_url": f"{WEB}/{repo.full_name}/pull/{c['issue']}#issuecomment-{c['id']}", "body": c["body"],
            "user": self.user_json(c["user"]), "created_at": c["created_at"], "updated_at": c["updated_at"],
            "issue_url": f"{API}/repos/{repo.full_name}/issues/{c['issue']}", "author_association": "OWNER",
        }

    # A pull request's comments are its issue's: GitHub takes either Issues or Pull requests for them.
    COMMENTS = ("issues", "pull_requests")

    def list_comments(self, req, tok, o, r, n):
        repo = self._repo_for(tok, o, r, self.COMMENTS, public=True)
        if int(n) not in repo.pulls:
            raise ApiError(404, "Not Found")
        items = [self.comment_json(repo, c) for c in sorted(repo.comments.values(), key=lambda c: c["id"]) if c["issue"] == int(n)]
        chunk, headers = self._page(req, items)
        return 200, chunk, headers

    def create_comment(self, req, tok, o, r, n):
        repo = self._repo_for(tok, o, r, self.COMMENTS, write=True)
        if int(n) not in repo.pulls:
            raise ApiError(404, "Not Found")
        b = req.json({}) or {}
        if not b.get("body"):
            raise ApiError(422, "Validation Failed", extra={"errors": [{"resource": "IssueComment", "code": "missing_field", "field": "body"}]})
        if len(b["body"]) > 65536:
            raise ApiError(422, "Validation Failed", extra={"errors": [{"resource": "IssueComment", "code": "custom", "field": "body",
                                                                         "message": "body is too long (maximum is 65536 characters)"}]})
        cid = self.next_id("comment")
        c = {"id": cid, "issue": int(n), "body": b["body"], "user": tok.login, "created_at": iso(), "updated_at": iso(), "edits": 0,
             "token": tag(tok.value)}
        repo.comments[cid] = c
        return 201, self.comment_json(repo, c), None

    def get_comment(self, req, tok, o, r, id):
        repo = self._repo_for(tok, o, r, self.COMMENTS, public=True)
        c = repo.comments.get(int(id))
        if c is None:
            raise ApiError(404, "Not Found")
        return 200, self.comment_json(repo, c), None

    def update_comment(self, req, tok, o, r, id):
        repo = self._repo_for(tok, o, r, self.COMMENTS, write=True)
        c = repo.comments.get(int(id))
        if c is None:
            raise ApiError(404, "Not Found")
        b = req.json({}) or {}
        if not b.get("body"):
            raise ApiError(422, "Validation Failed", extra={"errors": [{"resource": "IssueComment", "code": "missing_field", "field": "body"}]})
        c["body"] = b["body"]
        c["updated_at"] = iso()
        c["edits"] += 1
        return 200, self.comment_json(repo, c), None

    def delete_comment(self, req, tok, o, r, id):
        repo = self._repo_for(tok, o, r, self.COMMENTS, write=True)
        if repo.comments.pop(int(id), None) is None:
            raise ApiError(404, "Not Found")
        return 204, None, None

    # repo hooks
    def hook_json(self, repo, h, reveal=False):
        config = dict(h["config"])
        if config.get("secret") and not reveal:
            config["secret"] = "********"
        return {
            "type": "Repository", "id": h["id"], "name": h["name"], "active": h["active"], "events": list(h["events"]),
            "config": config, "updated_at": h["updated_at"], "created_at": h["created_at"],
            "url": f"{API}/repos/{repo.full_name}/hooks/{h['id']}", "test_url": f"{API}/repos/{repo.full_name}/hooks/{h['id']}/test",
            "ping_url": f"{API}/repos/{repo.full_name}/hooks/{h['id']}/pings",
            "deliveries_url": f"{API}/repos/{repo.full_name}/hooks/{h['id']}/deliveries",
            "last_response": h.get("last_response") or {"code": None, "status": "unused", "message": None},
        }

    @staticmethod
    def _hook_config(config):
        """A hook's config as GitHub keeps it, or its 422 when there's no URL."""
        if not isinstance(config, dict) or not config.get("url"):
            raise ApiError(422, "Validation Failed", extra={"errors": [{"resource": "Hook", "code": "custom", "message": "Config must contain url"}]})
        return {"url": config.get("url"), "content_type": config.get("content_type") or "form",
                "insecure_ssl": str(config.get("insecure_ssl") or "0"), **({"secret": config["secret"]} if config.get("secret") else {})}

    @staticmethod
    def check_unique(repo, config, events, exclude=None):
        """GitHub's rule: hooks may share a config (its URL) only when none of their events overlap ("*" overlaps all)."""
        for h in repo.hooks.values():
            if h["id"] == exclude or h["config"].get("url") != config.get("url"):
                continue
            if "*" in h["events"] or "*" in events or set(h["events"]) & set(events):
                raise ApiError(422, "Validation Failed", extra={"errors": [{"resource": "Hook", "code": "custom", "message": "Hook already exists on this repository"}]})

    def list_hooks(self, req, tok, o, r):
        repo = self._repo_for(tok, o, r, "webhooks")
        items = [self.hook_json(repo, h) for h in sorted(repo.hooks.values(), key=lambda h: h["id"])]
        chunk, headers = self._page(req, items)
        return 200, chunk, headers

    def create_hook(self, req, tok, o, r):
        repo = self._repo_for(tok, o, r, "webhooks", write=True)
        b = req.json({}) or {}
        if b.get("name") not in (None, "web"):
            raise ApiError(422, "Validation Failed", extra={"errors": [{"resource": "Hook", "code": "custom", "field": "name", "message": "Name is invalid"}]})
        config = self._hook_config(b.get("config"))
        events = list(b["events"]) if b.get("events") is not None else ["push"]
        h = self.add_hook(repo, config, events, b.get("active", True), "api", tag(tok.value))
        self.pool.submit(self._safe_deliver, repo, h, "ping", self._ping_payload(repo, h, tok.login))
        return 201, self.hook_json(repo, h), None

    def add_hook(self, repo, config, events, active, made_by, token_tag=None):
        """A new hook, made by `made_by`: "api" (through the REST API: Gitberth's), "pasted" (Gitberth's, made by hand from
        what its Settings shows) or "foreign" (another service's). Whether a delivery reaches Gitberth is never up to
        that: deliveries go where the hook's URL leads (see deliver)."""
        config = self._hook_config(config)
        self.check_unique(repo, config, events)
        hid = self.next_id("hook")
        h = {"id": hid, "name": "web", "active": bool(active), "events": list(events), "config": config,
             "created_at": iso(), "updated_at": iso(), "made_by": made_by, "token": token_tag}
        repo.hooks[hid] = h
        return h

    @staticmethod
    def gitberth_hook(repo):
        """The hook a delivery the control API sends goes through unless one is named: the newest one made through the
        API (Gitberth's), else the newest pasted by hand from Gitberth's Settings; never another service's."""
        for kind in ("api", "pasted"):
            mine = [h for h in repo.hooks.values() if h.get("made_by") == kind]
            if mine:
                return max(mine, key=lambda h: h["id"])
        return None

    def get_hook(self, req, tok, o, r, id):
        repo = self._repo_for(tok, o, r, "webhooks")
        h = repo.hooks.get(int(id))
        if h is None:
            raise ApiError(404, "Not Found")
        return 200, self.hook_json(repo, h), None

    def update_hook(self, req, tok, o, r, id):
        repo = self._repo_for(tok, o, r, "webhooks", write=True)
        h = repo.hooks.get(int(id))
        if h is None:
            raise ApiError(404, "Not Found")
        b = req.json({}) or {}
        # The config is replaced as a whole: a secret that isn't sent again is gone, as on GitHub.
        config = self._hook_config(b["config"]) if "config" in b else h["config"]
        events = list(b["events"]) if b.get("events") is not None else list(h["events"])
        for e in b.get("add_events") or []:
            if e not in events:
                events.append(e)
        for e in b.get("remove_events") or []:
            if e in events:
                events.remove(e)
        self.check_unique(repo, config, events, exclude=h["id"])
        h["config"], h["events"] = config, events
        if "active" in b:
            h["active"] = bool(b["active"])
        h["updated_at"] = iso()
        h["token"] = tag(tok.value)
        return 200, self.hook_json(repo, h), None

    def delete_hook(self, req, tok, o, r, id):
        repo = self._repo_for(tok, o, r, "webhooks", write=True)
        if repo.hooks.pop(int(id), None) is None:
            raise ApiError(404, "Not Found")
        return 204, None, None

    def ping_hook(self, req, tok, o, r, id):
        repo = self._repo_for(tok, o, r, "webhooks", write=True)
        h = repo.hooks.get(int(id))
        if h is None:
            raise ApiError(404, "Not Found")
        self.pool.submit(self._safe_deliver, repo, h, "ping", self._ping_payload(repo, h, tok.login))
        return 204, None, None

    # ---- what people do on GitHub (the control API's actions) ----

    def create_repo(self, owner, name, files=None, template=None, private=False, branch="main", message="Initial commit"):
        with self.lock:
            if self.repos.get(f"{owner}/{name}".lower()):
                raise ApiError(409, f"{owner}/{name} exists already")
            repo = Repo(self, owner, name, bool(private), branch)
            os.makedirs(os.path.dirname(repo.path), exist_ok=True)
            git("init", "--bare", "-q", "-b", branch, repo.path)
            self.repos[repo.key] = repo
            content = self.template_files(template) if template else {}
            content.update(files or {})
            self._commit(repo, branch, content, message, base=None)
            return repo

    def template_files(self, template):
        root = os.path.join(self.templates, template)
        if not os.path.isdir(root):
            raise ApiError(404, f"No app template '{template}'")
        files = {}
        for d, _, names in os.walk(root):
            for n in names:
                full = os.path.join(d, n)
                rel = os.path.relpath(full, root).replace(os.sep, "/")
                with open(full, "rb") as f:
                    files[rel] = f.read()
        return files

    def _commit(self, repo, branch, files, message, base=None, force=False, author=None):
        """Commits the files (None deletes one) on top of the branch, or of base, and moves the branch there. A branch
        that doesn't exist yet starts from base, else from the default branch (an empty repo's first commit from nothing)."""
        before = self.branch_sha(repo, branch)
        start = base if base is not None else before if before is not None else self.branch_sha(repo, repo.default_branch)
        env = {}
        if author:
            env.update({"GIT_AUTHOR_NAME": author.get("name", "E2E Bot"), "GIT_AUTHOR_EMAIL": author.get("email", "e2e-bot@example.com")})
        with tempfile.TemporaryDirectory(prefix="gbe2e-commit-") as work:
            git("init", "-q", work)
            if start:
                start_sha = self.branch_sha(repo, start) or start
                git("fetch", "-q", "--no-tags", repo.path, start_sha, cwd=work)
                git("checkout", "-q", "--detach", "FETCH_HEAD", cwd=work)
            for path, content in (files or {}).items():
                target = os.path.join(work, path)
                if content is None:
                    if os.path.isdir(target):
                        shutil.rmtree(target)
                    elif os.path.exists(target):
                        os.remove(target)
                    continue
                os.makedirs(os.path.dirname(target) or work, exist_ok=True)
                with open(target, "wb") as f:
                    f.write(content if isinstance(content, (bytes, bytearray)) else str(content).encode())
            git("add", "-A", cwd=work)
            git("commit", "-q", "--allow-empty", "-m", message, cwd=work, env=env)
            sha = git("rev-parse", "HEAD", cwd=work).strip()
            push = ["push", "-q"] + (["--force"] if force or base is not None else []) + [repo.path, f"HEAD:refs/heads/{branch}"]
            git(*push, cwd=work)
        repo.pushed_at = iso()
        return sha, before

    def commit(self, repo, branch, files, message, base=None, force=False, author=None, pusher="e2e-bot"):
        with self.lock:
            sha, before = self._commit(repo, branch, files, message, base=base, force=force, author=author)
            jobs = []
            if before is None:
                # A new branch pushed: GitHub sends create as well as push.
                jobs += self._emit(repo, "create", {"ref": branch, "ref_type": "branch", "master_branch": repo.default_branch,
                                                    "description": None, "pusher_type": "user", "repository": self.repo_json(repo),
                                                    "sender": self.user_json(pusher)})
            jobs += self._after_push(repo, branch, before, sha, forced=bool(force or (base is not None and before is not None)), pusher=pusher)
        return {"sha": sha, "before": before, "branch": branch, "created": before is None}, jobs

    def create_branch(self, repo, name, start=None, pusher="e2e-bot"):
        with self.lock:
            if self.branch_sha(repo, name):
                raise ApiError(409, f"Branch {name} exists already")
            sha = self.branch_sha(repo, start or repo.default_branch) or (start if start and self.has_commit(repo, start) else None)
            if not sha:
                raise ApiError(404, f"Nothing to branch from: {start}")
            git("--git-dir", repo.path, "update-ref", f"refs/heads/{name}", sha)
            jobs = self._emit(repo, "create", {"ref": name, "ref_type": "branch", "master_branch": repo.default_branch,
                                               "description": None, "pusher_type": "user", "repository": self.repo_json(repo),
                                               "sender": self.user_json(pusher)})
            jobs += self._after_push(repo, name, None, sha, forced=False, pusher=pusher)
            return {"sha": sha}, jobs

    def delete_branch(self, repo, name, pusher="e2e-bot"):
        with self.lock:
            sha = self.branch_sha(repo, name)
            if not sha:
                raise ApiError(404, f"No branch {name}")
            if name == repo.default_branch:
                raise ApiError(422, f"{name} is the default branch, which GitHub doesn't delete")
            git("--git-dir", repo.path, "update-ref", "-d", f"refs/heads/{name}")
            jobs = self._emit(repo, "delete", {"ref": name, "ref_type": "branch", "pusher_type": "user",
                                               "repository": self.repo_json(repo), "sender": self.user_json(pusher)})
            jobs += self._after_push(repo, name, sha, None, forced=False, pusher=pusher)
            return {"deleted": name, "sha": sha}, jobs

    def fork(self, repo, owner):
        with self.lock:
            if self.repos.get(f"{owner}/{repo.name}".lower()):
                raise ApiError(409, f"{owner}/{repo.name} exists already")
            f = Repo(self, owner, repo.name, repo.private, repo.default_branch, fork_of=repo.key)
            os.makedirs(os.path.dirname(f.path), exist_ok=True)
            git("clone", "--bare", "-q", repo.path, f.path)
            self.repos[f.key] = f
            return f

    def delete_repo(self, repo):
        with self.lock:
            self.repos.pop(repo.key, None)
            shutil.rmtree(repo.path, ignore_errors=True)

    def open_pull(self, repo, head, base=None, title=None, body=None, draft=False, user=None):
        with self.lock:
            base = base or repo.default_branch
            base_sha = self.branch_sha(repo, base)
            if not base_sha:
                raise ApiError(422, f"No base branch {base}")
            if ":" in head:
                head_owner, head_ref = head.split(":", 1)
                head_repo = self.repo(head_owner, repo.name)
            else:
                head_owner, head_ref, head_repo = repo.owner, head, repo
            head_sha = self.branch_sha(head_repo, head_ref)
            if not head_sha:
                raise ApiError(422, f"No head branch {head}")
            number = repo.next_number
            repo.next_number += 1
            pr = Pull(self, number, title or head_ref, body or "", user or head_owner, head_repo.key, head_owner, head_ref, head_sha,
                      base, base_sha, bool(draft))
            repo.pulls[number] = pr
            self._set_pull_ref(repo, pr)
            jobs = self._emit(repo, "pull_request", self._pull_payload(repo, pr, "opened", pr.user))
            return pr, jobs

    def _set_pull_ref(self, repo, pr):
        head_repo = self.repos.get(pr.head_repo_key) if pr.head_repo_key else None
        if head_repo is None:
            return
        if head_repo is repo:
            git("--git-dir", repo.path, "update-ref", f"refs/pull/{pr.number}/head", pr.head_sha)
        else:
            git("--git-dir", repo.path, "fetch", "-q", "--no-tags", head_repo.path, f"+refs/heads/{pr.head_ref}:refs/pull/{pr.number}/head")

    def update_pull(self, repo, pr, title=None, body=None, state=None, user="e2e-bot"):
        with self.lock:
            jobs = []
            changes = {}
            if title is not None and title != pr.title:
                changes["title"] = {"from": pr.title}
                pr.title = title
            if body is not None and body != pr.body:
                changes["body"] = {"from": pr.body}
                pr.body = body
            pr.updated_at = iso()
            if changes:
                payload = self._pull_payload(repo, pr, "edited", user)
                payload["changes"] = changes
                jobs += self._emit(repo, "pull_request", payload)
            if state == "closed" and pr.state == "open":
                pr.state = "closed"
                pr.closed_at = iso()
                jobs += self._emit(repo, "pull_request", self._pull_payload(repo, pr, "closed", user))
            elif state == "open" and pr.state == "closed" and not pr.merged:
                pr.state = "open"
                pr.closed_at = None
                jobs += self._emit(repo, "pull_request", self._pull_payload(repo, pr, "reopened", user))
            return pr, jobs

    def merge_pull(self, repo, pr, method="merge", delete_branch=False, user="e2e-bot"):
        with self.lock:
            if pr.state != "open":
                raise ApiError(405, "Pull Request is not mergeable")
            before = self.branch_sha(repo, pr.base_ref)
            with tempfile.TemporaryDirectory(prefix="gbe2e-merge-") as work:
                git("init", "-q", work)
                git("fetch", "-q", "--no-tags", repo.path, f"refs/heads/{pr.base_ref}", cwd=work)
                git("checkout", "-q", "--detach", "FETCH_HEAD", cwd=work)
                git("fetch", "-q", "--no-tags", repo.path, f"refs/pull/{pr.number}/head", cwd=work)
                head = git("rev-parse", "FETCH_HEAD", cwd=work).strip()
                if method == "squash":
                    git("merge", "-q", "--squash", head, cwd=work)
                    git("commit", "-q", "--allow-empty", "-m", f"{pr.title} (#{pr.number})", cwd=work)
                else:
                    git("merge", "-q", "--no-ff", "-m", f"Merge pull request #{pr.number} from {pr.head_owner}/{pr.head_ref}\n\n{pr.title}", head, cwd=work)
                sha = git("rev-parse", "HEAD", cwd=work).strip()
                git("push", "-q", repo.path, f"HEAD:refs/heads/{pr.base_ref}", cwd=work)
            repo.pushed_at = iso()
            pr.state, pr.merged, pr.merged_at, pr.closed_at, pr.merged_by, pr.merge_commit_sha = "closed", True, iso(), iso(), user, sha
            pr.updated_at = iso()
            jobs = self._after_push(repo, pr.base_ref, before, sha, forced=False, pusher=user)
            jobs += self._emit(repo, "pull_request", self._pull_payload(repo, pr, "closed", user))
            if delete_branch and pr.head_repo_key == repo.key:
                _, more = self.delete_branch(repo, pr.head_ref, pusher=user)
                jobs += more
            return sha, jobs

    def _after_push(self, repo, branch, before, after, forced, pusher):
        """GitHub's side of a push: the push delivery, and each open pull request whose head (or base) moved."""
        jobs = []
        commits = []
        if after:
            span = after if not before else f"{before}..{after}"
            shas = git("--git-dir", repo.path, "rev-list", "--reverse", "-n", "20", span, check=False).split()
            commits = [self.commit_json(repo, s) for s in shas]
        payload = {
            "ref": f"refs/heads/{branch}", "before": before or ZERO, "after": after or ZERO,
            "repository": self.repo_json(repo), "pusher": {"name": pusher, "email": f"{pusher}@users.noreply.github.com"},
            "sender": self.user_json(pusher), "created": before is None, "deleted": after is None, "forced": bool(forced),
            "base_ref": None, "compare": f"{WEB}/{repo.full_name}/compare/{(before or ZERO)[:12]}...{(after or ZERO)[:12]}",
            "commits": commits, "head_commit": commits[-1] if commits else None,
        }
        jobs += self._emit(repo, "push", payload)
        # Pull requests from this branch, in this repo or (for a fork) in the repo it was forked from.
        for base in self.repos.values():
            for pr in base.pulls.values():
                if pr.state != "open":
                    continue
                if pr.head_repo_key == repo.key and pr.head_ref == branch and after and after != pr.head_sha:
                    old = pr.head_sha
                    pr.head_sha = after
                    pr.updated_at = iso()
                    self._set_pull_ref(base, pr)
                    p = self._pull_payload(base, pr, "synchronize", pusher)
                    p["before"], p["after"] = old, after
                    jobs += self._emit(base, "pull_request", p)
                elif base is repo and pr.base_ref == branch and after:
                    pr.base_sha = after
        return jobs

    def _pull_payload(self, repo, pr, action, sender):
        return {"action": action, "number": pr.number, "pull_request": self.pull_json(repo, pr, full=True),
                "repository": self.repo_json(repo), "sender": self.user_json(sender)}

    def _ping_payload(self, repo, h, sender):
        return {"zen": "Keep it logically awesome.", "hook_id": h["id"], "hook": self.hook_json(repo, h),
                "repository": self.repo_json(repo), "sender": self.user_json(sender)}

    # ---- webhook deliveries ----

    def _emit(self, repo, event, payload):
        """Queues the event's delivery to each of the repo's active hooks that asked for it; returns the futures."""
        jobs = []
        for h in list(repo.hooks.values()):
            if h["active"] and (event in h["events"] or "*" in h["events"]):
                jobs.append(self.pool.submit(self._safe_deliver, repo, h, event, payload))
        return jobs

    def _safe_deliver(self, repo, hook, event, payload, **overrides):
        try:
            return self.deliver(repo, hook, event, payload, **overrides)
        except Exception:
            log_exception("delivery")
            return None

    def deliver(self, repo, hook, event, payload, signature=None, secret=None, cf_connecting_ip="default", url=None, raw_body=None,
                extra_headers=None, method="POST", content_type=None, via_tunnel=True):
        """One delivery, as GitHub sends it, recorded with the answer it got. The overrides make a delivery GitHub
        wouldn't (a wrong signature, another sender, another body).

        It goes where the hook's URL leads, as GitHub, Cloudflare and cloudflared would take it (Cloudflare.edge): the
        host's DNS record in a stand-in zone must be proxied and lead to the tunnel, a Cloudflare Access application on
        the address stops it (302 to its login), and the connector routes it by the ingress it runs. So it reaches Gitberth
        only through a route to Gitberth's service; otherwise the record says what it met (no DNS record, a 404 rule, a
        502 from an origin cloudflared couldn't reach). cf_connecting_ip is the sender's address as Cloudflare sees it.

        via_tunnel=False sends it straight to Gitberth's container instead (endpoint-only checks), with the hook URL's
        host as Host and cf_connecting_ip as CF-Connecting-IP (None for none).

        The payload is JSON in UTF-8, unicode as it is (GitHub doesn't escape it), or form data (payload=<the JSON>) for a
        hook whose content type is form; one sent without a hook is JSON, as Gitberth's own hooks are, unless
        content_type says otherwise ("form", "json", or any Content-Type to send as it is). GitHub waits 10 seconds for
        the answer, all told: after that it has hung up, and the record has no status and the error "timed out"."""
        config = hook["config"] if hook else {}
        kind = content_type or ((config.get("content_type") or "form") if hook else "json")
        ctype = {"json": "application/json", "form": "application/x-www-form-urlencoded"}.get(kind, kind)
        data = json.dumps(payload, separators=(",", ":"), ensure_ascii=False).encode("utf-8") if payload is not None else b""
        body = raw_body.encode("utf-8") if isinstance(raw_body, str) else raw_body if raw_body is not None else (
            urllib.parse.urlencode({"payload": data.decode("utf-8")}).encode() if ctype == "application/x-www-form-urlencoded" else data)
        delivery_id = str(uuid.uuid4())
        headers = {
            "X-GitHub-Event": event, "X-GitHub-Delivery": delivery_id, "User-Agent": "GitHub-Hookshot/e2e",
            "Content-Type": ctype, "Accept": "*/*",
        }
        if hook:
            headers.update({"X-GitHub-Hook-ID": str(hook["id"]), "X-GitHub-Hook-Installation-Target-ID": str(repo.id),
                            "X-GitHub-Hook-Installation-Target-Type": "repository"})
        key = secret if secret is not None else config.get("secret")
        if key:
            headers["X-Hub-Signature"] = "sha1=" + hmac.new(key.encode(), body, hashlib.sha1).hexdigest()
            headers["X-Hub-Signature-256"] = "sha256=" + hmac.new(key.encode(), body, hashlib.sha256).hexdigest()
        if signature == "none":
            headers.pop("X-Hub-Signature", None)
            headers.pop("X-Hub-Signature-256", None)
        elif signature == "bad":
            headers["X-Hub-Signature-256"] = "sha256=" + hmac.new(b"not-the-secret", body, hashlib.sha256).hexdigest()
        elif signature:
            headers["X-Hub-Signature-256"] = signature
        sender_ip = HOOK_SENDER.format(1 + (int(time.time()) % 200)) if cf_connecting_ip == "default" else cf_connecting_ip
        headers.update(extra_headers or {})
        hook_url = url or config.get("url") or ""
        u = urllib.parse.urlsplit(hook_url)
        tunnel = via_tunnel and self.edge is not None
        record = {
            "seq": self.rec.next_seq(), "at": time.time(), "id": delivery_id, "repo": repo.key if repo else None,
            "hook_id": hook["id"] if hook else None, "event": event, "action": (payload or {}).get("action") if isinstance(payload, dict) else None,
            "url": hook_url, "host": u.hostname, "headers": headers, "payload": payload, "body_size": len(body),
            "via_tunnel": tunnel, "sender_ip": sender_ip, "route": None,
            "status": None, "response_headers": None, "response_body": None, "error": None, "duration_ms": None,
        }
        started = time.time()
        deadline = time.monotonic() + DELIVERY_TIMEOUT
        if tunnel:
            # GitHub checks the host's certificate unless the hook says insecure_ssl "1".
            result = self.edge(hook_url, method, headers, body, client_ip=sender_ip or HOOK_SENDER.format(1),
                               verify_tls=str(config.get("insecure_ssl") or "0") != "1", timeout=DELIVERY_TIMEOUT)
            record["route"] = result["route"]
            record["error"] = result["error"]
            if result["status"] is not None:
                record.update(status=result["status"], response_headers=result["headers"], response_body=result["body"][:2000])
        else:
            # What cloudflared would have passed on, for an endpoint-only check.
            headers.setdefault("X-Forwarded-Proto", u.scheme or "https")
            if sender_ip:
                headers["CF-Connecting-IP"] = sender_ip
            target = urllib.parse.urlsplit(self.gitberth_url)
            try:
                conn = http.client.HTTPConnection(target.hostname, target.port or 80, timeout=DELIVERY_TIMEOUT)
                try:
                    send_headers = dict(headers)
                    send_headers["Host"] = u.netloc
                    send_headers["Content-Length"] = str(len(body))
                    try:
                        conn.request(method, (u.path or "/") + (f"?{u.query}" if u.query else ""), body=body, headers=send_headers)
                    except (BrokenPipeError, ConnectionResetError):
                        # Gitberth answered (a 413, say) before it read the whole body: its answer still counts.
                        if conn.sock is None:
                            raise
                    conn.sock.settimeout(max(0.05, deadline - time.monotonic()))
                    resp = conn.getresponse()
                    text = resp.read()
                    record["status"] = resp.status
                    record["response_headers"] = dict(resp.getheaders())
                    record["response_body"] = text[:2000].decode("utf-8", "replace")
                finally:
                    conn.close()
            except TimeoutError:
                record["error"] = "timed out"
            except OSError as e:
                record["error"] = f"{type(e).__name__}: {e}"
        record["duration_ms"] = int((time.time() - started) * 1000)
        if hook:
            hook["last_response"] = {"code": record["status"], "status": "active" if (record["status"] or 0) < 400 and record["status"] else "failed",
                                     "message": "OK" if record["status"] and record["status"] < 300 else (record["error"] or f"HTTP {record['status']}")}
        with self.lock:
            self.deliveries.append(record)
            if len(self.deliveries) > 5000:
                del self.deliveries[:1000]
        log(f"delivery {event} for {record['repo']} to {hook_url}: {record['status'] or record['error']}")
        return record

    # ---- reading back ----

    def repo_state(self, repo, reveal=True):
        return {
            "full_name": repo.full_name, "id": repo.id, "private": repo.private, "default_branch": repo.default_branch,
            "fork_of": repo.fork_of, "branches": self.branches(repo),
            "pull_refs": dict(line.split(" ", 1) for line in git("--git-dir", repo.path, "for-each-ref",
                                                                     "--format=%(refname) %(objectname)", "refs/pull/").splitlines() if " " in line),
            "pulls": [self.pull_json(repo, p, full=True) for p in sorted(repo.pulls.values(), key=lambda p: p.number)],
            "hooks": [dict(self.hook_json(repo, h, reveal=reveal), made_by=h.get("made_by"), token=h.get("token")) for h in repo.hooks.values()],
            "statuses": [dict(s) for s in repo.statuses],
            "comments": [dict(self.comment_json(repo, c), issue=c["issue"], edits=c["edits"], token=c.get("token")) for c in repo.comments.values()],
            "variables": sorted(repo.variables.values(), key=lambda v: v["name"]),
        }


def describe_git_request(rest, req):
    """What a git request asked for: the service, and for protocol v2 the command (ls-refs, fetch) and its ref prefixes."""
    info = {"path": rest, "service": req.query.get("service") or rest.strip("/").split("/")[-1], "protocol": req.headers.get("Git-Protocol")}
    if req.method == "POST" and rest.endswith("git-upload-pack") and req.body:
        try:
            data = req.body
            if (req.headers.get("Content-Encoding") or "").lower() == "gzip":
                data = gzip.decompress(data)
            lines = []
            i = 0
            while i + 4 <= len(data):
                n = int(data[i:i + 4], 16)
                if n < 4:
                    lines.append(None)
                    i += 4
                    continue
                lines.append(data[i + 4:i + n].decode("utf-8", "replace").rstrip("\n"))
                i += n
            commands = [l[8:] for l in lines if l and l.startswith("command=")]
            info["command"] = commands[0] if commands else ("fetch" if any(l and l.startswith("want ") for l in lines) else None)
            info["ref_prefixes"] = [l[11:] for l in lines if l and l.startswith("ref-prefix ")]
            info["wants"] = [l[5:45] for l in lines if l and l.startswith("want ")]
            info["deepen"] = next((l for l in lines if l and l.startswith("deepen")), None)
        except (ValueError, OSError):
            info["command"] = "unreadable"
    # `git ls-remote <url>` with no patterns lists every ref: ls-refs without a ref prefix. Fetches and clones name theirs.
    info["ls_remote"] = info.get("command") == "ls-refs" and not info.get("ref_prefixes")
    return info
