"""The GitLab stand-in: one GitLab, answering as https://gitlab.e2e.test (a self-managed GitLab's address) and as
https://gitlab.com, so Gitberth's default instance leads here too and never to the real one. Each answer names the host
it was asked through, as a GitLab with that external URL would.

- REST API v4 (/api/v4), as GitLab answers what Gitberth calls (the contract): the token's user and the token itself,
  projects (in nested groups or a user's namespace; public, internal or private; by numeric ID or URL-encoded path),
  branches, compare, merge requests (from the same project or from a fork), commit statuses (with GitLab's state
  machine), merge request notes (GitLab's own system notes among them), project hooks (whose token is never shown) and
  CI/CD variables with environment scopes. Offset paging with GitLab's X-* headers and Link; GitLab's error bodies
  (401, 403 naming the scopes an endpoint takes, 404, Grape's 400s, 409, 422) and GitLab.com's API rate limits.
- Tokens: personal, project and group access tokens with GitLab's scopes, an expiry date and revocation, acting as their
  user with the role it has (guest to owner) in a project or its groups; "oauth" is an OAuth2 access token.
- git over smart HTTP (git http-backend over bare repos) at https://<host>/<path_with_namespace>.git, with the token as
  Basic credentials (oauth2:<token>, or any username with an access token). A merge request's head is
  refs/merge-requests/<iid>/head in its target project, a fork's commits fetched in.
- Webhooks: a push, or a merge request opened, changed, closed, reopened, merged or approved through the control API,
  sends GitLab's delivery (Push Hook, Merge Request Hook) to each of the project's hooks that asked for it, with
  X-Gitlab-Token, X-Gitlab-Event and X-Gitlab-Event-UUID, where its URL leads: through the Cloudflare stand-in's edge and
  the tunnel, as the GitHub stand-in's deliveries go. GitLab waits 10 seconds for an answer.
- The control API (/__control/gitlab/..., control_routes) plays the people using GitLab, and reads back what Gitberth
  did. Every call is recorded (kind "gitlab" for the API, "gitlab-git" for git) with the project it's about; no token
  ever goes to the log."""
import base64
import datetime
import hashlib
import http.client
import ipaddress
import json
import os
import re
import secrets
import shutil
import string
import subprocess
import tempfile
import threading
import time
import urllib.parse
import uuid
from concurrent.futures import ThreadPoolExecutor
from concurrent.futures import wait as wait_futures
from email.utils import formatdate
from http import HTTPStatus

from common import ApiError, Response, log, log_exception
from gh import describe_git_request, git, tag

# The names the stand-in answers as GitLab. The first is a self-managed GitLab's address, the one the scenarios use.
HOSTS = ("gitlab.e2e.test", "gitlab.com")
DEFAULT_HOST = HOSTS[0]
VERSION = "19.5.2"
REVISION = "4f1c2b9a7de"
ZERO = "0" * 40
API = "/api/v4"

GUEST, REPORTER, DEVELOPER, MAINTAINER, OWNER = 10, 20, 30, 40, 50
LEVELS = {"guest": GUEST, "reporter": REPORTER, "developer": DEVELOPER, "maintainer": MAINTAINER, "owner": OWNER}
SCOPES = ("api", "read_api", "read_user", "read_repository", "write_repository", "read_registry", "write_registry",
          "create_runner", "manage_runner", "k8s_proxy", "ai_features", "self_rotate", "read_service_ping", "sudo", "admin_mode")
TOKEN_KINDS = ("personal", "project", "group", "oauth")
VISIBILITY = {"private": 0, "internal": 10, "public": 20}
# A project hook's events, as the API names them; push and merge requests are the ones the stand-in sends.
HOOK_EVENTS = ("push_events", "tag_push_events", "merge_requests_events", "issues_events", "confidential_issues_events",
               "note_events", "confidential_note_events", "job_events", "pipeline_events", "wiki_page_events",
               "deployment_events", "feature_flag_events", "releases_events", "milestone_events", "emoji_events",
               "resource_access_token_events", "vulnerability_events", "repository_update_events")
EVENT_HEADERS = {"push": "Push Hook", "merge_request": "Merge Request Hook"}
# GitLab.com's webhooks come from 34.74.90.64/28; behind Cloudflare, Gitberth sees one of them as CF-Connecting-IP.
HOOK_SENDER = "34.74.90.{}"
# A commit status's states, and the state machine event each one is (CommitStatus, Ci::HasStatus).
STATUS_EVENTS = {"pending": "enqueue", "running": "run", "success": "success", "failed": "drop", "canceled": "cancel", "skipped": "skip"}
STATUS_FROM = {
    "enqueue": {"created", "skipped", "manual", "scheduled"},
    "run": {"pending"},
    "success": {"created", "waiting_for_resource", "preparing", "pending", "running"},
    "drop": {"created", "waiting_for_resource", "preparing", "pending", "running", "manual", "scheduled"},
    "cancel": {"created", "waiting_for_resource", "preparing", "pending", "running", "manual", "scheduled"},
    "skip": {"created", "waiting_for_resource", "preparing", "pending"},
}
STATUS_TO = {"enqueue": "pending", "run": "running", "success": "success", "drop": "failed", "cancel": "canceled", "skip": "skipped"}
GIT_DENIED = ("HTTP Basic: Access denied. If a password was provided for Git authentication, the password was incorrect or "
              "you're required to use a token instead of a password. If a token was provided, it was either incorrect, "
              "expired, or improperly scoped. See https://{host}/help/topics/git/troubleshooting_git.md"
              "#error-on-git-fetch-http-basic-access-denied")
GIT_NOT_FOUND = "The project you were looking for could not be found or you don't have permission to view it."
GIT_NO_DOWNLOAD = "You are not allowed to download code from this project."
INSUFFICIENT_SCOPE = "The request requires higher privileges than provided by the access token."
DRAFT = re.compile(r"^\s*(\[draft\]|\(draft\)|draft:)\s*", re.I)
# The local network GitLab's webhooks don't call by default (besides loopback and link-local): RFC 1918, the shared
# address space, and IPv6's unique local addresses.
LOCAL_NETWORKS = [ipaddress.ip_network(n) for n in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "fc00::/7")]
VARIABLE_KEY = re.compile(r"^[A-Za-z0-9_]{1,255}$")
MASKABLE = re.compile(r"^[A-Za-z0-9+/=@:.~_-]{8,}$")

# git over smart HTTP: /<namespace>/<project>(.git)/info/refs and the like; namespaces nest.
GIT_PATH = re.compile(r"^/(?P<path>(?!api/)[^?]+?)(?:\.git)?(?P<rest>/(?:info/refs|git-upload-pack|git-receive-pack|HEAD|objects/.+))$")
API_PROJECT = re.compile(r"^/api/v4/projects/(?P<id>[^/]+)")


# ---- GitLab's ways ----

def utc_now():
    return datetime.datetime.now(datetime.timezone.utc)


def api_time(t):
    """GitLab's API timestamps: UTC with milliseconds (2026-11-16T10:20:30.123Z)."""
    return None if t is None else t.strftime("%Y-%m-%dT%H:%M:%S.") + f"{t.microsecond // 1000:03d}Z"


def xml_time(t):
    """A merge request hook's object_attributes timestamps (2026-11-16T10:20:30Z)."""
    return None if t is None else t.strftime("%Y-%m-%dT%H:%M:%SZ")


def ruby_time(t):
    """A hook's changes timestamps, as Ruby prints a time (2026-11-16 10:20:30 UTC)."""
    return None if t is None else t.strftime("%Y-%m-%d %H:%M:%S UTC")


def git_time(text):
    """A commit's date in the commit's own offset, with milliseconds: 2026-11-16T10:20:30.000+00:00."""
    m = re.match(r"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(.*)$", text or "")
    return f"{m[1]}.000{m[2]}" if m else text


def gitlab_json(obj):
    """JSON as GitLab (Rails) writes it: UTF-8 as it is, with <, > and & (and the two line separators) escaped."""
    text = json.dumps(obj, ensure_ascii=False, separators=(",", ":"))
    for ch, esc in (("<", "\\u003c"), (">", "\\u003e"), ("&", "\\u0026"), ("\u2028", "\\u2028"), ("\u2029", "\\u2029")):
        text = text.replace(ch, esc)
    return text.encode("utf-8")


def reason(status):
    try:
        return HTTPStatus(status).phrase
    except ValueError:
        return "Error"


class GitLabError(Exception):
    """An answer as GitLab gives it: a status, a body (a dict in GitLab's shape, or text) and headers."""

    def __init__(self, status, body, headers=None):
        super().__init__(f"{status} {body}")
        self.status = status
        self.body = body
        self.headers = dict(headers or {})


def not_found(what=None):
    """GitLab's 404s: not_found!('Project') is "404 Project Not Found"; a record it couldn't find, "404 Not found"."""
    return GitLabError(404, {"message": f"404 {what} Not Found" if what else "404 Not found"})


def forbidden():
    return GitLabError(403, {"message": "403 Forbidden"})


def unauthorized():
    return GitLabError(401, {"message": "401 Unauthorized"})


def missing(name):
    return GitLabError(400, {"error": f"{name} is missing"})


def invalid(name, problem="does not have a valid value"):
    return GitLabError(400, {"error": f"{name} {problem}"})


def params_of(req):
    """A request's parameters as GitLab's Grape reads them: the query string, with the body's (JSON or form data) over it."""
    p = dict(req.query)
    if req.body:
        ctype = (req.headers.get("Content-Type") or "").split(";")[0].strip().lower()
        if ctype == "application/x-www-form-urlencoded":
            p.update({k: v[-1] for k, v in urllib.parse.parse_qs(req.body.decode("utf-8", "replace"), keep_blank_values=True).items()})
        else:
            try:
                body = json.loads(req.body.decode("utf-8"))
            except (ValueError, UnicodeDecodeError):
                raise GitLabError(400, {"message": "400 Bad request - The request body isn't valid JSON"})
            if isinstance(body, dict):
                p.update(body)
    return p


_TRUE = {"1", "t", "true", "y", "yes", "on"}
_FALSE = {"0", "f", "false", "n", "no", "off"}


def boolean(p, name, default=None):
    """A Boolean parameter as Grape coerces it; anything else is its 400."""
    if p.get(name) is None:
        return default
    v = p[name]
    if isinstance(v, bool):
        return v
    s = str(v).strip().lower()
    if s in _TRUE:
        return True
    if s in _FALSE:
        return False
    raise invalid(name, "is invalid")


def text_param(p, name, required=False, allow_blank=True):
    v = p.get(name)
    if v is None:
        if required:
            raise missing(name)
        return None
    if isinstance(v, (dict, list)):
        raise invalid(name, "is invalid")
    v = str(v) if not isinstance(v, str) else v
    if not allow_blank and not v.strip():
        raise invalid(name, "is empty")
    return v


def api_token_of(req):
    """The token an API request carries, however GitLab takes it: PRIVATE-TOKEN, Authorization: Bearer (an access token
    or an OAuth one), or ?private_token= / ?access_token=. The API ignores Basic credentials."""
    v = (req.headers.get("PRIVATE-TOKEN") or "").strip()
    if v:
        return v
    scheme, _, cred = (req.headers.get("Authorization") or "").strip().partition(" ")
    if scheme.lower() == "bearer" and cred.strip():
        return cred.strip()
    for k in ("private_token", "access_token"):
        if req.query.get(k):
            return req.query[k]
    return None


def basic_credentials(req):
    """git's Basic credentials (username, password), or None."""
    scheme, _, cred = (req.headers.get("Authorization") or "").strip().partition(" ")
    if scheme.lower() != "basic":
        return None
    try:
        user, _, password = base64.b64decode(cred.strip()).decode("utf-8", "replace").partition(":")
    except ValueError:
        return ("", "")
    return user, password


def token_of(req):
    """The token on a call's record: the API's, or git's password."""
    creds = basic_credentials(req)
    return api_token_of(req) or (creds[1] if creds and creds[1] else None)


def wildcard(pattern):
    """GitLab's wildcard branch filter: * matches anything, slashes too."""
    return re.compile("^" + ".*".join(re.escape(part) for part in pattern.split("*")) + "$")


def new_token_value(kind):
    """A token in GitLab's format: an access token is glpat- and 20 characters; an OAuth token is 64 hex digits."""
    if kind == "oauth":
        return secrets.token_hex(32)
    alphabet = string.ascii_letters + string.digits + "_-"
    return "glpat" + "-" + "".join(secrets.choice(alphabet) for _ in range(20))


def template_files(templates, template):
    root = os.path.join(templates, template)
    if not os.path.isdir(root):
        raise ApiError(404, f"No app template '{template}'")
    files = {}
    for d, _, names in os.walk(root):
        for n in names:
            full = os.path.join(d, n)
            with open(full, "rb") as f:
                files[os.path.relpath(full, root).replace(os.sep, "/")] = f.read()
    return files


# ---- the model ----

class User:
    def __init__(self, uid, username, name=None, bot=False):
        self.id = uid
        self.username = username
        self.name = name or username
        self.bot = bot
        self.email = f"{username}@noreply.{DEFAULT_HOST}" if bot else f"{username}@example.com"
        self.created_at = utc_now()


class Token:
    """An access token: personal (its user's own), project or group (a bot user that's a member of that project or
    group), or oauth (an OAuth2 access token of its user). scopes are GitLab's; expires_at is a date (YYYY-MM-DD), and
    the token stops working on that day."""

    def __init__(self, value, tid, kind, user, scopes, expires_at=None, name="gbe2e", target=None, access_level=None):
        self.value = value
        self.id = tid
        self.kind = kind
        self.user = user
        self.scopes = list(scopes)
        self.expires_at = expires_at
        self.name = name
        self.target = target
        self.access_level = access_level
        self.revoked = False
        self.used = 0
        self.created_at = utc_now()
        self.last_used_at = None

    def expired(self, today=None):
        if not self.expires_at:
            return False
        try:
            day = datetime.date.fromisoformat(str(self.expires_at)[:10])
        except ValueError:
            return False
        return (today or utc_now().date()) >= day

    def refused(self):
        return self.revoked or self.expired()

    def allows(self, scopes, method):
        """Whether one of the endpoint's scopes (name, GET and HEAD only) is the token's: None takes any."""
        if scopes is None:
            return True
        return any(name in self.scopes and (not get_only or method in ("GET", "HEAD")) for name, get_only in scopes)

    def public(self):
        return {"kind": self.kind, "username": self.user.username, "user_id": self.user.id, "bot": self.user.bot, "name": self.name,
                "scopes": self.scopes, "expires_at": self.expires_at, "expired": self.expired(), "revoked": self.revoked,
                "target": self.target, "access_level": self.access_level, "used": self.used, "tag": tag(self.value)}


class Namespace:
    """A group (kind "group", nested under its parent) or a user's own namespace (kind "user")."""

    def __init__(self, nid, full_path, kind, parent=None, visibility="public", owner=None):
        self.id = nid
        self.full_path = full_path
        self.path = full_path.rsplit("/", 1)[-1]
        self.name = self.path
        self.kind = kind
        self.parent = parent
        self.visibility = visibility
        self.owner = owner
        self.members = {}
        self.created_at = utc_now()

    def chain(self):
        n = self
        while n is not None:
            yield n
            n = n.parent


class MergeRequest:
    def __init__(self, mid, iid, target, source, source_id, source_branch, target_branch, title, description, author, sha):
        self.id = mid
        self.iid = iid
        self.target = target
        self.source = source
        # Kept as it was: a merge request still names its source project after a fork is deleted.
        self.source_id = source_id
        self.source_branch = source_branch
        self.target_branch = target_branch
        self.title = title
        self.description = description
        self.author = author
        self.sha = sha
        self.state = "opened"
        self.created_at = self.updated_at = utc_now()
        self.merged_at = self.closed_at = None
        self.merge_user = self.closed_by = None
        self.merge_commit_sha = self.squash_commit_sha = None
        self.approved_by = []
        self.last_edited_at = None
        self.should_remove_source_branch = False

    @property
    def draft(self):
        return bool(DRAFT.match(self.title or ""))

    @property
    def state_id(self):
        return {"opened": 1, "closed": 2, "merged": 3, "locked": 4}[self.state]


class Project:
    def __init__(self, gl, pid, namespace, path, visibility, default_branch, creator, forked_from=None):
        self.id = pid
        self.namespace = namespace
        self.path = path
        self.name = path
        self.full_path = f"{namespace.full_path}/{path}"
        self.key = self.full_path.lower()
        self.visibility = visibility
        self.default_branch = default_branch
        self.archived = False
        self.creator = creator
        self.forked_from = forked_from
        self.members = {}
        self.hooks = {}
        self.variables = []
        self.statuses = []
        self.pipelines = {}
        self.mrs = {}
        self.notes = {}
        self.next_iid = 1
        self.created_at = self.last_activity_at = utc_now()
        self.repo = os.path.join(gl.root, *self.key.split("/")) + ".git"


class Throttle:
    """GitLab.com's API rate limits, as Rack::Attack counts them before GitLab routes a request: a fixed one-minute window,
    2,000 requests for each user whose token GitLab takes, 500 for each address without one (or with one it refuses). A
    request past the limit gets the 429 (and still counts)."""

    PERIOD = 60
    DEFAULTS = {"user": 2000, "ip": 500}

    def __init__(self):
        self.lock = threading.Lock()
        self.windows = {}
        self.limits = dict(self.DEFAULTS)

    def take(self, key, count=True):
        now = int(time.time())
        window = now // self.PERIOD
        with self.lock:
            observed = self.windows.get((key, window), 0) + (1 if count else 0)
            if count:
                self.windows[(key, window)] = observed
                for old in [k for k in self.windows if k[1] < window]:
                    del self.windows[old]
            limit = self.limits[key[0]]
        retry_after = self.PERIOD - now % self.PERIOD
        return observed <= limit, {"name": "throttle_authenticated_api" if key[0] == "user" else "throttle_unauthenticated_api",
                                   "limit": limit, "observed": observed, "reset": now + retry_after, "retry_after": retry_after}

    def clear(self):
        with self.lock:
            self.windows.clear()

    def state(self):
        with self.lock:
            return [{"key": f"{k[0][0]}:{k[0][1]}", "window": k[1], "observed": n} for k, n in self.windows.items()]


def rate_headers(rate, throttled=False):
    """GitLab's RateLimit-* headers; a throttled answer also names the throttle and says when to try again."""
    h = {"RateLimit-Limit": str(rate["limit"]), "RateLimit-Observed": str(rate["observed"]),
         "RateLimit-Remaining": str(max(0, rate["limit"] - rate["observed"])), "RateLimit-Reset": str(rate["reset"]),
         "RateLimit-ResetTime": formatdate(rate["reset"], usegmt=True)}
    if throttled:
        h.update({"RateLimit-Name": rate["name"], "Retry-After": str(rate["retry_after"])})
    return h


def throttled_response(rate):
    """Rack::Attack's answer once a limit is used up: 429, "Retry later" as plain text, Retry-After and RateLimit-*."""
    return Response(429, b"Retry later\n", {"Content-Type": "text/plain", **rate_headers(rate, throttled=True)})


# ---- the stand-in ----

ID = r"(?P<id>[^/]+)"
# The scopes an endpoint takes, as (scope, for GET and HEAD only): api for anything, read_api to read; /user also
# read_user. GitLab's 403 names them all, in this order.
READ_SCOPES = (("api", False), ("read_api", True))
USER_SCOPES = (("read_user", True), ("api", False), ("read_api", True))
ANY_SCOPE = None
ROUTES = [
    ("GET", r"/user", "get_user", USER_SCOPES),
    ("GET", r"/personal_access_tokens/self", "token_self", ANY_SCOPE),
    ("GET", r"/version", "get_version", USER_SCOPES),
    ("GET", r"/metadata", "get_version", USER_SCOPES),
    ("GET", rf"/projects/{ID}", "get_project", READ_SCOPES),
    ("GET", rf"/projects/{ID}/repository/branches", "list_branches", READ_SCOPES),
    ("GET", rf"/projects/{ID}/repository/branches/(?P<branch>[^/]+)", "get_branch", READ_SCOPES),
    ("GET", rf"/projects/{ID}/repository/compare", "compare", READ_SCOPES),
    ("GET", rf"/projects/{ID}/repository/commits/(?P<sha>[^/]+)", "get_commit", READ_SCOPES),
    ("GET", rf"/projects/{ID}/repository/commits/(?P<sha>[^/]+)/statuses", "list_statuses", READ_SCOPES),
    ("POST", rf"/projects/{ID}/statuses/(?P<sha>[^/]+)", "create_status", READ_SCOPES),
    ("GET", rf"/projects/{ID}/merge_requests", "list_mrs", READ_SCOPES),
    ("GET", rf"/projects/{ID}/merge_requests/(?P<iid>\d+)", "get_mr", READ_SCOPES),
    ("GET", rf"/projects/{ID}/merge_requests/(?P<iid>\d+)/notes", "list_notes", READ_SCOPES),
    ("POST", rf"/projects/{ID}/merge_requests/(?P<iid>\d+)/notes", "create_note", READ_SCOPES),
    ("GET", rf"/projects/{ID}/merge_requests/(?P<iid>\d+)/notes/(?P<note>\d+)", "get_note", READ_SCOPES),
    ("PUT", rf"/projects/{ID}/merge_requests/(?P<iid>\d+)/notes/(?P<note>\d+)", "update_note", READ_SCOPES),
    ("DELETE", rf"/projects/{ID}/merge_requests/(?P<iid>\d+)/notes/(?P<note>\d+)", "delete_note", READ_SCOPES),
    ("GET", rf"/projects/{ID}/hooks", "list_hooks", READ_SCOPES),
    ("POST", rf"/projects/{ID}/hooks", "create_hook", READ_SCOPES),
    ("GET", rf"/projects/{ID}/hooks/(?P<hook>\d+)", "get_hook", READ_SCOPES),
    ("PUT", rf"/projects/{ID}/hooks/(?P<hook>\d+)", "update_hook", READ_SCOPES),
    ("DELETE", rf"/projects/{ID}/hooks/(?P<hook>\d+)", "delete_hook", READ_SCOPES),
    ("GET", rf"/projects/{ID}/variables", "list_variables", READ_SCOPES),
    ("POST", rf"/projects/{ID}/variables", "create_variable", READ_SCOPES),
    ("GET", rf"/projects/{ID}/variables/(?P<key>[^/]+)", "get_variable", READ_SCOPES),
    ("PUT", rf"/projects/{ID}/variables/(?P<key>[^/]+)", "update_variable", READ_SCOPES),
    ("DELETE", rf"/projects/{ID}/variables/(?P<key>[^/]+)", "delete_variable", READ_SCOPES),
]
_COMPILED = [(m, re.compile(p + "$"), fn, scopes) for m, p, fn, scopes in ROUTES]


class GitLab:
    HOSTS = HOSTS

    def __init__(self, root, templates, recorder, gitberth_url, edge=None):
        self.root = root
        self.templates = templates
        self.rec = recorder
        # Where a delivery sent with via_tunnel=False goes: straight to Gitberth's container.
        self.gitberth_url = gitberth_url.rstrip("/")
        # Cloudflare's edge (Cloudflare.edge): how every other delivery reaches its URL, as it would in real life.
        self.edge = edge
        self.lock = threading.RLock()
        self.pool = ThreadPoolExecutor(max_workers=4, thread_name_prefix="gitlab-delivery")
        self.reset()

    # ---- state ----

    def reset(self):
        with self.lock:
            self.users = {}
            self.tokens = {}
            self.namespaces = {}
            self.projects = {}
            self.by_id = {}
            self.deliveries = []
            self.throttle = Throttle()
            # Options a scenario may set: an older GitLab without /personal_access_tokens/self (15.4 and before), and
            # how long GitLab waits for a delivery's answer (10 seconds by default).
            self.options = {"self_endpoint": True, "webhook_timeout": 10}
            # IDs as large as GitLab.com's: note and status (CI job) IDs are past 2**31 there.
            self._ids = {"user": 31000000, "namespace": 92000000, "project": 61000000, "mr": 380000000, "note": 2600000000,
                         "status": 9100000000, "pipeline": 1500000000, "hook": 55000000, "token": 12000000}
            shutil.rmtree(self.root, ignore_errors=True)
            os.makedirs(self.root, exist_ok=True)

    def next_id(self, kind):
        with self.lock:
            self._ids[kind] += 1
            return self._ids[kind]

    def user(self, username, name=None, bot=False):
        with self.lock:
            u = self.users.get(username.lower())
            if u is None:
                u = User(self.next_id("user"), username, name, bot)
                self.users[username.lower()] = u
            return u

    def find_project(self, ident):
        ident = str(ident)
        return self.by_id.get(int(ident)) if ident.isdigit() else self.projects.get(ident.lower())

    def project(self, ident):
        """A project for the control API (404 if there's none)."""
        p = self.find_project(ident)
        if p is None:
            raise ApiError(404, f"No GitLab project {ident} in the stand-in")
        return p

    # ---- who may do what ----

    def level(self, user, p):
        """The user's role in the project: its own membership, or the most of its namespace's groups (and their parents);
        a user's own namespace is theirs."""
        if user is None:
            return 0
        name = user.username.lower()
        best = p.members.get(name, 0)
        for n in p.namespace.chain():
            if n.kind == "user" and (n.owner or "").lower() == name:
                best = max(best, OWNER)
            best = max(best, n.members.get(name, 0))
        return best

    def can_read_project(self, user, p):
        if p.visibility == "public":
            return True
        if user is None:
            return False
        if p.visibility == "internal" and not user.bot:
            return True
        return self.level(user, p) >= GUEST

    def can_read_code(self, user, p):
        """The repository and merge requests: anyone's in a public project, any signed-in user's in an internal one,
        a reporter's (or more) in a private one."""
        if p.visibility == "public":
            return True
        if user is None:
            return False
        if p.visibility == "internal" and not user.bot:
            return True
        return self.level(user, p) >= REPORTER

    # ---- what a call is about ----

    def kind_of(self, req):
        return "gitlab-git" if GIT_PATH.match(req.path) else "gitlab"

    def describe(self, req, call):
        """Notes on the call's record which project it's about ("repo": its path_with_namespace, lowercase, also when it
        was asked for by ID) and the token it carried, and for git what it asked for. It's done before anything answers
        the call, so one an injected failure answered, or that GitLab refused, is still found by its project."""
        call["token"] = token_of(req)
        for k in ("private_token", "access_token"):
            if k in (call.get("query") or {}):
                call["query"] = dict(call["query"], **{k: "[FILTERED]"})
        if call["kind"] == "gitlab-git":
            call["body_text"] = None
            m = GIT_PATH.match(req.path)
            if m:
                call["repo"] = urllib.parse.unquote(m["path"]).lower()
                call["git"] = describe_git_request(m["rest"], req)
            return
        m = API_PROJECT.match(req.path)
        if m:
            ident = urllib.parse.unquote(m["id"])
            p = self.find_project(ident)
            call["repo"] = p.key if p else ident.lower()

    @staticmethod
    def unrouted(req, call):
        """A call no stand-in route answers: GitLab's own 404 goes back ({"error": "404 Not Found"}, as for a route GitLab
        doesn't have), which Gitberth would read as a project or permission it can't see. So it's logged and marked on
        its record (unrouted), and the scenarios fail on it."""
        call["unrouted"] = True
        log(f"no stand-in route: {req.method} {req.host}{req.path}")

    def injected_response(self, rule, req):
        """An injected failure's answer in GitLab's shape, or None for a rule that only delays: status 429 (or any
        rate_limit) is Rack::Attack's 429 (Retry later, with Retry-After, from the rule's own Retry-After header if it
        has one, and RateLimit-*); another status without a body is {"message": "<status> <reason>"} from the API and a
        line of text from git."""
        status = int(rule["status"]) if rule.get("status") else 429 if rule.get("rate_limit") else None
        if status is None:
            return None
        headers = dict(rule.get("headers") or {})
        body = rule.get("body")
        if status == 429 and body is None:
            value = api_token_of(req)
            tok = self.tokens.get(value) if value else None
            key = ("user", tok.user.id) if tok is not None and not tok.refused() else ("ip", req.client)
            given = next((v for k, v in headers.items() if k.lower() == "retry-after"), None)
            _, rate = self.throttle.take(key, count=False)
            retry = int(given) if given is not None and str(given).isdigit() else rate["retry_after"]
            rate.update(observed=rate["limit"] + 1, retry_after=retry, reset=int(time.time()) + retry)
            out = throttled_response(rate)
            out.headers.update(headers)
            return out
        if body is None:
            if self.kind_of(req) == "gitlab-git":
                body = f"{reason(status)}\n"
            else:
                body = {"message": f"{status} {reason(status)}"}
        if isinstance(body, (dict, list)):
            return Response(status, gitlab_json(body), {"Content-Type": "application/json", **headers})
        headers.setdefault("Content-Type", "text/plain; charset=utf-8")
        return Response(status, str(body).encode(), headers)

    # ---- git over smart HTTP ----

    def git_token(self, password, p):
        """The token git's password is, as GitLab takes it for git: one it knows, active, and for a bot (a project or
        group token) only on a project it can see. Any other password is refused (401)."""
        t = self.tokens.get(password) if password else None
        if t is None or t.refused():
            return None
        if t.user.bot and p is not None and not self.can_read_project(t.user, p):
            return None
        return t

    def git_http(self, req, call):
        m = GIT_PATH.match(req.path)
        if not m:
            self.unrouted(req, call)
            return Response.text(404, "Not Found")
        key, rest = urllib.parse.unquote(m["path"]).lower(), m["rest"]
        call["repo"] = key
        call.setdefault("git", describe_git_request(rest, req))
        p = self.projects.get(key)
        denied = Response.text(401, GIT_DENIED.format(host=req.host), {"WWW-Authenticate": 'Basic realm="GitLab"'})
        creds = basic_credentials(req)
        tok, user = None, None
        if creds is not None:
            tok = self.git_token(creds[1], p)
            if tok is None:
                return denied
            tok.used += 1
            tok.last_used_at = utc_now()
            user = tok.user
        elif p is None or not self.can_read_code(None, p):
            # Without credentials GitLab asks for them rather than say whether a project exists.
            return denied
        if p is None or not self.can_read_project(user, p):
            return Response.text(404, GIT_NOT_FOUND)
        # The token's user downloads with a repository scope (or api); anyone may download a public project's code, as a
        # guest would, whatever the token's scopes.
        scoped = tok is None or any(s in tok.scopes for s in ("api", "read_repository", "write_repository"))
        if not (scoped and self.can_read_code(user, p)) and not self.can_read_code(None, p):
            return Response.text(403, GIT_NO_DOWNLOAD)
        if rest.endswith("git-receive-pack") or req.query.get("service") == "git-receive-pack":
            return Response.text(403, "The rig takes pushes through its control API.\n")
        env = {
            "GIT_PROJECT_ROOT": self.root, "GIT_HTTP_EXPORT_ALL": "1",
            "PATH_INFO": "/" + os.path.relpath(p.repo, self.root).replace(os.sep, "/") + rest, "REQUEST_METHOD": req.method,
            "QUERY_STRING": req.query_string, "CONTENT_TYPE": req.headers.get("Content-Type") or "",
            "CONTENT_LENGTH": str(len(req.body)), "REMOTE_ADDR": req.client,
            "PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": os.environ.get("HOME", "/root"),
        }
        if req.headers.get("Git-Protocol"):
            env["HTTP_GIT_PROTOCOL"] = req.headers["Git-Protocol"]
        if req.headers.get("Content-Encoding"):
            env["HTTP_CONTENT_ENCODING"] = req.headers["Content-Encoding"]
        if user is not None:
            env["REMOTE_USER"] = user.username
        out = subprocess.run(["git", "http-backend"], input=req.body, env=env, capture_output=True, timeout=120)
        head, sep, body = out.stdout.partition(b"\r\n\r\n")
        if not sep:
            head, sep, body = out.stdout.partition(b"\n\n")
        if not sep:
            return Response.text(500, "git http-backend: " + out.stderr.decode("utf-8", "replace"))
        status, headers = 200, {}
        for line in head.decode("latin-1").splitlines():
            k, _, v = line.partition(":")
            if k.strip().lower() == "status":
                status = int(v.strip().split()[0])
            elif k.strip():
                headers[k.strip()] = v.strip()
        return Response(status, body, headers)

    # ---- REST ----

    def api(self, req, call):
        if "repo" not in call:
            self.describe(req, call)
        rate = None
        try:
            value = api_token_of(req)
            known = self.tokens.get(value) if value else None
            good = known if known is not None and not known.refused() else None
            # Rack::Attack counts a request before GitLab routes or authenticates it.
            allowed, rate = self.throttle.take(("user", good.user.id) if good else ("ip", req.client))
            if not allowed:
                call["rate_limited"] = True
                return throttled_response(rate)
            found = self._route(req)
            if found is None:
                self.unrouted(req, call)
                raise GitLabError(404, {"error": "404 Not Found"})
            fn, args, scopes = found
            tok = self._authenticate(value, known)
            if tok is not None and not tok.allows(scopes, req.method):
                raise GitLabError(403, {"error": "insufficient_scope", "error_description": INSUFFICIENT_SCOPE,
                                        "scope": " ".join(name for name, _ in scopes)})
            with self.lock:
                status, body, headers = getattr(self, fn)(req, tok, **args)
            return self._answer(status, body, rate, headers)
        except GitLabError as e:
            return self._answer(e.status, e.body, rate, e.headers)

    @staticmethod
    def _route(req):
        """(handler name, its URL parts unquoted, its scopes) for an /api/v4 request, or None. A HEAD goes where its GET
        would."""
        if not req.path.startswith(API + "/"):
            return None
        sub = req.path[len(API):]
        for method, rx, fn, scopes in _COMPILED:
            m = rx.match(sub)
            if m and (method == req.method or (method == "GET" and req.method == "HEAD")):
                return fn, {k: urllib.parse.unquote(v) for k, v in m.groupdict().items()}, scopes
        return None

    @staticmethod
    def _authenticate(value, t):
        """The caller's token: None without one; 401 for one GitLab doesn't know, or that has expired or was revoked."""
        if value is None:
            return None
        if t is None:
            raise unauthorized()
        for problem, text in ((t.expired(), "Token is expired. You can either do re-authorization or token refresh."),
                              (t.revoked, "Token was revoked. You have to re-authorize from the user.")):
            if problem:
                raise GitLabError(401, {"error": "invalid_token", "error_description": text},
                                  {"WWW-Authenticate": f'Bearer realm="", error="invalid_token", error_description="{text}"'})
        t.used += 1
        t.last_used_at = utc_now()
        return t

    @staticmethod
    def _answer(status, body, rate, headers=None):
        rid = uuid.uuid4().hex[:26].upper()
        h = {"Cache-Control": "max-age=0, private, must-revalidate", "Vary": "Origin, Accept-Encoding", "X-Request-Id": rid,
             "X-Gitlab-Meta": json.dumps({"correlation_id": rid, "version": "1"}, separators=(",", ":")), "X-Runtime": "0.012345"}
        if rate:
            h.update(rate_headers(rate))
        h.update(headers or {})
        if status == 204 or body is None:
            return Response(status, b"", h)
        if isinstance(body, str):
            return Response(status, body.encode(), {"Content-Type": "text/plain", **h})
        return Response(status, gitlab_json(body), {"Content-Type": "application/json", **h})

    @staticmethod
    def _page(req, items):
        """Offset paging as GitLab does it: per_page (20 by default, 100 at most) and page, with X-Page, X-Per-Page,
        X-Next-Page and X-Prev-Page (empty on the last and the first page), X-Total, X-Total-Pages (at least 1) and Link."""
        q = req.query
        try:
            page = int(q.get("page") or 1)
        except ValueError:
            raise invalid("page", "is invalid")
        try:
            per = int(q.get("per_page") or 20)
        except ValueError:
            raise invalid("per_page", "is invalid")
        if per == 0:
            raise invalid("per_page", "has a value not allowed")
        per = 20 if per < 0 else min(per, 100)
        page = max(page, 1)
        total = len(items)
        pages = max(1, -(-total // per))
        nxt = page + 1 if page < pages else None
        prv = page - 1 if page > 1 else None

        def href(n):
            params = dict(q, page=str(n), per_page=str(per))
            return f"<https://{req.host}{req.path}?{urllib.parse.urlencode(sorted(params.items()))}>"

        links = ([f'{href(prv)}; rel="prev"'] if prv else []) + ([f'{href(nxt)}; rel="next"'] if nxt else []) \
            + [f'{href(1)}; rel="first"', f'{href(pages)}; rel="last"']
        headers = {"X-Page": str(page), "X-Per-Page": str(per), "X-Next-Page": str(nxt or ""), "X-Prev-Page": str(prv or ""),
                   "X-Total": str(total), "X-Total-Pages": str(pages), "Link": ", ".join(links)}
        return items[(page - 1) * per: page * per], headers

    def _project(self, tok, ident):
        """The project as GitLab lets this caller see it: 404 Project Not Found for one that doesn't exist or that it can't
        see (a private one without a membership, an internal one without signing in)."""
        p = self.find_project(ident)
        if p is None or not self.can_read_project(tok.user if tok else None, p):
            raise not_found("Project")
        return p

    def _code(self, tok, ident):
        p = self._project(tok, ident)
        if not self.can_read_code(tok.user if tok else None, p):
            raise forbidden()
        return p

    def _role(self, tok, ident, needed, write=True):
        """A project the caller must be signed in for (401), see (404) and have the role for (403)."""
        if tok is None:
            raise unauthorized()
        p = self._project(tok, ident)
        if self.level(tok.user, p) < needed:
            raise forbidden()
        if write and p.archived:
            raise forbidden()
        return p

    # JSON shapes

    def user_basic(self, u, host):
        md5 = hashlib.md5(u.email.encode()).hexdigest()
        return {"id": u.id, "username": u.username, "public_email": None, "name": u.name, "state": "active", "locked": False,
                "avatar_url": f"https://secure.gravatar.com/avatar/{md5}?s=80&d=identicon", "web_url": f"https://{host}/{u.username}"}

    def namespace_json(self, n, host):
        web = f"https://{host}/{n.full_path}" if n.kind == "user" else f"https://{host}/groups/{n.full_path}"
        return {"id": n.id, "name": n.name, "path": n.path, "kind": n.kind, "full_path": n.full_path,
                "parent_id": n.parent.id if n.parent else None, "avatar_url": None, "web_url": web}

    def project_json(self, p, host, user=None, basic=False):
        web = f"https://{host}/{p.full_path}"
        d = {
            "id": p.id, "description": None, "name": p.name, "name_with_namespace": f"{' / '.join(n.name for n in reversed(list(p.namespace.chain())))} / {p.name}",
            "path": p.path, "path_with_namespace": p.full_path, "created_at": api_time(p.created_at), "default_branch": p.default_branch,
            "tag_list": [], "topics": [], "ssh_url_to_repo": f"git@{host}:{p.full_path}.git", "http_url_to_repo": f"{web}.git",
            "web_url": web, "readme_url": None, "forks_count": sum(1 for x in self.projects.values() if x.forked_from == p.key),
            "avatar_url": None, "star_count": 0, "last_activity_at": api_time(p.last_activity_at),
            "namespace": self.namespace_json(p.namespace, host),
        }
        if basic:
            return d
        api = f"https://{host}/api/v4/projects/{p.id}"
        d.update({
            "container_registry_image_prefix": f"registry.{host}/{p.key}",
            "_links": {"self": api, "issues": f"{api}/issues", "merge_requests": f"{api}/merge_requests", "repo_branches": f"{api}/repository/branches",
                       "labels": f"{api}/labels", "events": f"{api}/events", "members": f"{api}/members", "cluster_agents": f"{api}/cluster_agents"},
            "packages_enabled": True, "empty_repo": False, "archived": p.archived, "visibility": p.visibility,
            "resolve_outdated_diff_discussions": False, "container_registry_enabled": True, "issues_enabled": True,
            "merge_requests_enabled": True, "wiki_enabled": True, "jobs_enabled": True, "snippets_enabled": True,
            "can_create_merge_request_in": user is not None and self.level(user, p) >= DEVELOPER,
            "issues_access_level": "enabled", "repository_access_level": "enabled", "merge_requests_access_level": "enabled",
            "builds_access_level": "enabled", "shared_runners_enabled": True, "lfs_enabled": True, "creator_id": self.user(p.creator).id,
            "import_status": "none", "open_issues_count": 0, "ci_config_path": None, "public_jobs": True, "shared_with_groups": [],
            "only_allow_merge_if_pipeline_succeeds": False, "allow_merge_on_skipped_pipeline": None, "request_access_enabled": True,
            "only_allow_merge_if_all_discussions_are_resolved": False, "remove_source_branch_after_merge": True,
            "printing_merge_request_link_enabled": True, "merge_method": "merge", "squash_option": "default_off",
            "auto_devops_enabled": False, "emails_enabled": True, "mirror": False,
        })
        if p.namespace.kind == "user":
            d["owner"] = self.user_basic(self.user(p.namespace.owner), host)
        if p.forked_from and p.forked_from in self.projects:
            d["forked_from_project"] = self.project_json(self.projects[p.forked_from], host, basic=True)
        if user is not None:
            direct = p.members.get(user.username.lower())
            groups = max([n.members.get(user.username.lower(), 0) for n in p.namespace.chain()] + [0])
            if p.namespace.kind == "user" and (p.namespace.owner or "").lower() == user.username.lower():
                direct = max(direct or 0, OWNER)
            d["permissions"] = {"project_access": {"access_level": direct, "notification_level": 3} if direct else None,
                                "group_access": {"access_level": groups, "notification_level": 3} if groups else None}
        return d

    def commits_json(self, p, shas, host):
        """GitLab's commit entity for each SHA, in the order given."""
        if not shas:
            return []
        fmt = "%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%B%x1e"
        out = git("--git-dir", p.repo, "log", "--no-walk=unsorted", f"--format={fmt}", *shas)
        web = f"https://{host}/{p.full_path}"
        found = []
        for rec in out.split("\x1e"):
            rec = rec.strip("\n")
            if not rec:
                continue
            f = rec.split("\x1f")
            message = f[8].rstrip("\n")
            found.append({
                "id": f[0], "short_id": f[0][:8], "created_at": git_time(f[7]), "parent_ids": f[1].split(), "title": message.split("\n", 1)[0],
                "message": message, "author_name": f[2], "author_email": f[3], "authored_date": git_time(f[4]), "committer_name": f[5],
                "committer_email": f[6], "committed_date": git_time(f[7]), "trailers": {}, "extended_trailers": {},
                "web_url": f"{web}/-/commit/{f[0]}",
            })
        return found

    def commit_json(self, p, sha, host):
        return self.commits_json(p, [sha], host)[0]

    def branches(self, p):
        out = git("--git-dir", p.repo, "for-each-ref", "--format=%(refname:lstrip=2) %(objectname)", "refs/heads/")
        return dict(line.rsplit(" ", 1) for line in out.splitlines() if " " in line)

    def branch_sha(self, p, branch):
        return self.branches(p).get(branch)

    def resolve(self, p, ref):
        """A branch, tag or commit SHA as a commit's full SHA, or None."""
        if not ref or ref.startswith("-"):
            return None
        r = subprocess.run(["git", "--git-dir", p.repo, "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"], capture_output=True, text=True)
        return r.stdout.strip() if r.returncode == 0 else None

    def branch_json(self, p, name, sha, host, user):
        level = self.level(user, p)
        merged = name != p.default_branch and subprocess.run(
            ["git", "--git-dir", p.repo, "merge-base", "--is-ancestor", sha, f"refs/heads/{p.default_branch}"], capture_output=True).returncode == 0
        protected = name == p.default_branch
        return {"name": name, "commit": self.commit_json(p, sha, host), "merged": merged, "protected": protected,
                "developers_can_push": False, "developers_can_merge": False,
                "can_push": level >= (MAINTAINER if protected else DEVELOPER), "default": name == p.default_branch,
                "web_url": f"https://{host}/{p.full_path}/-/tree/{name}"}

    def mr_json(self, mr, host, full=False, simple=False):
        target = self.projects.get(mr.target)
        web = f"https://{host}/{target.full_path}/-/merge_requests/{mr.iid}"
        if simple:
            return {"id": mr.id, "iid": mr.iid, "project_id": target.id, "title": mr.title, "description": mr.description,
                    "state": mr.state, "created_at": api_time(mr.created_at), "updated_at": api_time(mr.updated_at), "web_url": web}
        author = self.user_basic(self.user(mr.author), host)
        status = "can_be_merged" if mr.state == "opened" else "unchecked"
        detailed = "not_open" if mr.state != "opened" else "draft_status" if mr.draft else "mergeable"
        d = {
            "id": mr.id, "iid": mr.iid, "project_id": target.id, "title": mr.title, "description": mr.description, "state": mr.state,
            "created_at": api_time(mr.created_at), "updated_at": api_time(mr.updated_at), "merged_by": self.user_basic(self.user(mr.merge_user), host) if mr.merge_user else None,
            "merge_user": self.user_basic(self.user(mr.merge_user), host) if mr.merge_user else None, "merged_at": api_time(mr.merged_at),
            "merge_after": None, "prepared_at": api_time(mr.created_at),
            "closed_by": self.user_basic(self.user(mr.closed_by), host) if mr.closed_by else None, "closed_at": api_time(mr.closed_at),
            "target_branch": mr.target_branch, "source_branch": mr.source_branch, "user_notes_count": sum(1 for n in target.notes.values() if n["mr"] == mr.iid and not n["system"]),
            "upvotes": 0, "downvotes": 0, "author": author, "assignees": [], "assignee": None, "reviewers": [],
            "source_project_id": mr.source_id, "target_project_id": target.id, "labels": [], "draft": mr.draft,
            "imported": False, "imported_from": "none", "work_in_progress": mr.draft, "milestone": None, "merge_when_pipeline_succeeds": False,
            "merge_status": status, "detailed_merge_status": detailed, "sha": mr.sha, "merge_commit_sha": mr.merge_commit_sha,
            "squash_commit_sha": mr.squash_commit_sha, "discussion_locked": None, "should_remove_source_branch": mr.should_remove_source_branch,
            "force_remove_source_branch": False, "reference": f"!{mr.iid}",
            "references": {"short": f"!{mr.iid}", "relative": f"!{mr.iid}", "full": f"{target.full_path}!{mr.iid}"}, "web_url": web,
            "time_stats": {"time_estimate": 0, "total_time_spent": 0, "human_time_estimate": None, "human_total_time_spent": None},
            "squash": False, "squash_on_merge": False, "task_completion_status": {"count": 0, "completed_count": 0}, "has_conflicts": False,
            "blocking_discussions_resolved": True, "approvals_before_merge": None,
        }
        if full:
            base = subprocess.run(["git", "--git-dir", target.repo, "merge-base", f"refs/heads/{mr.target_branch}", f"refs/merge-requests/{mr.iid}/head"],
                                  capture_output=True, text=True).stdout.strip() or None
            d.update({"subscribed": False, "changes_count": "1", "latest_build_started_at": None, "latest_build_finished_at": None,
                      "first_deployed_to_production_at": None, "pipeline": None, "head_pipeline": None,
                      "diff_refs": {"base_sha": base, "head_sha": mr.sha, "start_sha": self.branch_sha(target, mr.target_branch)},
                      "merge_error": None, "first_contribution": False, "user": {"can_merge": False}})
        return d

    def note_json(self, p, n, host):
        mr = p.mrs[n["mr"]]
        return {"id": n["id"], "type": None, "body": n["body"], "attachment": None, "author": self.user_basic(self.user(n["author"]), host),
                "created_at": api_time(n["created_at"]), "updated_at": api_time(n["updated_at"]), "system": n["system"],
                "noteable_id": mr.id, "noteable_type": "MergeRequest", "project_id": p.id, "resolvable": False, "confidential": False,
                "internal": False, "imported": False, "imported_from": "none", "noteable_iid": mr.iid, "commands_changes": {}}

    def hook_json(self, p, h):
        d = {"id": h["id"], "url": h["url"], "name": h.get("name") or "", "description": h.get("description") or "",
             "created_at": api_time(h["created_at"]), "project_id": p.id, "alert_status": "executable", "disabled_until": None,
             "url_variables": [], "push_events_branch_filter": h.get("push_events_branch_filter"),
             "branch_filter_strategy": h.get("branch_filter_strategy") or "wildcard", "custom_webhook_template": None,
             "custom_headers": [{"key": k} for k in (h.get("custom_headers") or {})], "enable_ssl_verification": h["enable_ssl_verification"]}
        d.update({e: h["events"].get(e, False) for e in HOOK_EVENTS})
        return d

    @staticmethod
    def variable_json(v):
        return {"variable_type": v["variable_type"], "key": v["key"], "value": None if v["hidden"] else v["value"],
                "protected": v["protected"], "masked": v["masked"], "hidden": v["hidden"], "raw": v["raw"],
                "environment_scope": v["environment_scope"], "description": v["description"]}

    def status_json(self, p, s, host):
        return {"id": s["id"], "sha": s["sha"], "ref": s["ref"], "status": s["status"], "name": s["name"], "target_url": s["target_url"],
                "description": s["description"], "created_at": api_time(s["created_at"]), "started_at": api_time(s["started_at"]),
                "finished_at": api_time(s["finished_at"]), "allow_failure": False, "coverage": s["coverage"], "pipeline_id": s["pipeline_id"],
                "author": self.user_basic(self.user(s["author"]), host)}

    # /user and the token

    def get_user(self, req, tok):
        if tok is None:
            raise unauthorized()
        u = tok.user
        d = self.user_basic(u, req.host)
        d.update({"created_at": api_time(u.created_at), "bio": "", "location": "", "skype": "", "linkedin": "", "twitter": "",
                  "discord": "", "website_url": "", "organization": "", "job_title": "", "pronouns": None, "bot": u.bot,
                  "work_information": None, "local_time": None, "last_sign_in_at": api_time(u.created_at), "confirmed_at": api_time(u.created_at),
                  "last_activity_on": utc_now().date().isoformat(), "email": u.email, "theme_id": 3, "color_scheme_id": 1,
                  "projects_limit": 100000, "current_sign_in_at": api_time(u.created_at), "identities": [], "can_create_group": not u.bot,
                  "can_create_project": not u.bot, "two_factor_enabled": False, "external": False, "private_profile": False,
                  "commit_email": u.email})
        return 200, d, None

    def token_self(self, req, tok):
        """The token itself (any scope will do). An older GitLab (options self_endpoint=false) doesn't have the endpoint,
        and it isn't one for an OAuth token: 404 either way."""
        if tok is None:
            raise unauthorized()
        if not self.options["self_endpoint"]:
            raise GitLabError(404, {"error": "404 Not Found"})
        if tok.kind == "oauth":
            raise GitLabError(404, {"message": "404 Not Found"})
        return 200, {"id": tok.id, "name": tok.name, "revoked": tok.revoked, "created_at": api_time(tok.created_at), "description": None,
                     "scopes": tok.scopes, "user_id": tok.user.id, "last_used_at": api_time(tok.last_used_at), "last_used_ips": [],
                     "active": not tok.refused(), "expires_at": tok.expires_at}, None

    def get_version(self, req, tok):
        if tok is None:
            raise unauthorized()
        return 200, {"version": f"{VERSION}-ee", "revision": REVISION, "kas": {"enabled": True, "externalUrl": f"wss://{req.host}/-/kubernetes-agent/",
                                                                              "externalK8sProxyUrl": f"https://{req.host}/-/kubernetes-agent/k8s-proxy/", "version": VERSION},
                     "enterprise": True}, None

    # projects, branches, commits

    def get_project(self, req, tok, id):
        p = self._project(tok, id)
        return 200, self.project_json(p, req.host, tok.user if tok else None), None

    def list_branches(self, req, tok, id):
        p = self._code(tok, id)
        items = sorted(self.branches(p).items())
        search = req.query.get("search")
        if search:
            if search.startswith("^"):
                items = [(n, s) for n, s in items if n.startswith(search[1:])]
            elif search.endswith("$"):
                items = [(n, s) for n, s in items if n.endswith(search[:-1])]
            else:
                items = [(n, s) for n, s in items if search.lower() in n.lower()]
        chunk, headers = self._page(req, items)
        user = tok.user if tok else None
        return 200, [self.branch_json(p, n, s, req.host, user) for n, s in chunk], headers

    def get_branch(self, req, tok, id, branch):
        p = self._code(tok, id)
        sha = self.branch_sha(p, branch)
        if sha is None:
            raise not_found("Branch")
        return 200, self.branch_json(p, branch, sha, req.host, tok.user if tok else None), None

    def get_commit(self, req, tok, id, sha):
        p = self._code(tok, id)
        full = self.resolve(p, sha)
        if full is None:
            raise not_found("Commit")
        d = self.commit_json(p, full, req.host)
        stat = git("--git-dir", p.repo, "show", "--numstat", "--format=", full)
        adds = dels = 0
        for line in stat.splitlines():
            a, _, rest = line.partition("\t")
            b = rest.partition("\t")[0]
            adds += int(a) if a.isdigit() else 0
            dels += int(b) if b.isdigit() else 0
        d.update({"stats": {"additions": adds, "deletions": dels, "total": adds + dels}, "status": None, "project_id": p.id, "last_pipeline": None})
        return 200, d, None

    def compare(self, req, tok, id):
        p = self._code(tok, id)
        params = params_of(req)
        frm, to = text_param(params, "from", required=True), text_param(params, "to", required=True)
        straight = boolean(params, "straight", False)
        base_sha, head_sha = self.resolve(p, frm), self.resolve(p, to)
        if base_sha is None or head_sha is None:
            raise not_found("Ref")
        start = base_sha
        if not straight:
            start = subprocess.run(["git", "--git-dir", p.repo, "merge-base", base_sha, head_sha], capture_output=True, text=True).stdout.strip() or base_sha
        shas = git("--git-dir", p.repo, "rev-list", "--reverse", f"{base_sha}..{head_sha}").split() if base_sha != head_sha else []
        commits = self.commits_json(p, shas, req.host)
        diffs = self.diffs(p, start, head_sha) if start != head_sha else []
        return 200, {"commit": commits[-1] if commits else None, "commits": commits, "diffs": diffs, "compare_timeout": False,
                     "compare_same_ref": base_sha == head_sha, "web_url": f"https://{req.host}/{p.full_path}/-/compare/{base_sha}...{head_sha}"}, None

    def diffs(self, p, a, b):
        """GitLab's diff entries between two commits: old and new path (renames found), modes, flags and the hunks."""
        raw = git("--git-dir", p.repo, "diff", "--raw", "--no-abbrev", "-M", "-z", a, b)
        parts = raw.split("\0")
        entries, i = [], 0
        while i < len(parts) and parts[i].startswith(":"):
            meta = parts[i][1:].split()
            kind = meta[4][0]
            if kind in "RC":
                old, new, i = parts[i + 1], parts[i + 2], i + 3
            else:
                old = new = parts[i + 1]
                i += 2
            entries.append((meta[0], meta[1], kind, old, new))
        patch = git("--git-dir", p.repo, "diff", "-M", "--no-color", a, b)
        hunks = {}
        for section in re.split(r"(?m)^(?=diff --git )", patch):
            if not section.startswith("diff --git "):
                continue
            body = section.split("\n@@", 1)
            new_path = None
            for line in section.splitlines():
                if line.startswith("+++ "):
                    new_path = line[6:] if line.startswith("+++ b/") else None
                elif line.startswith("rename to "):
                    new_path = line[len("rename to "):]
            old_path = next((line[6:] for line in section.splitlines() if line.startswith("--- a/")), None)
            hunks[new_path or old_path] = ("@@" + body[1]) if len(body) == 2 else ""
        out = []
        for a_mode, b_mode, kind, old, new in entries:
            out.append({"diff": hunks.get(new if kind != "D" else old, ""), "collapsed": False, "too_large": False,
                        "new_path": new, "old_path": old, "a_mode": a_mode, "b_mode": b_mode, "new_file": kind == "A",
                        "renamed_file": kind == "R", "deleted_file": kind == "D", "generated_file": False})
        return out

    # commit statuses

    def list_statuses(self, req, tok, id, sha):
        """A commit's statuses: by default the latest of each name; all=true every one. GitLab finds them by the full
        SHA as given."""
        p = self._code(tok, id)
        if self.resolve(p, sha) is None:
            raise not_found("Commit")
        params = params_of(req)
        items = [s for s in p.statuses if s["sha"] == sha]
        if params.get("ref"):
            items = [s for s in items if s["ref"] == params["ref"]]
        if params.get("name"):
            items = [s for s in items if s["name"] == params["name"]]
        if not boolean(params, "all", False):
            latest = {}
            for s in items:
                latest[(s["name"], s["ref"], s["pipeline_id"])] = s
            items = sorted(latest.values(), key=lambda s: s["id"])
        chunk, headers = self._page(req, items)
        return 200, [self.status_json(p, s, req.host) for s in chunk], headers

    def create_status(self, req, tok, id, sha):
        """A commit status, as GitLab keeps one: a status of the same name, ref and user that's still pending or running
        moves on (GitLab's state machine: 400 for a move it doesn't make, such as running to running); otherwise a new
        one starts. Developers and up may post them."""
        p = self._role(tok, id, DEVELOPER)
        params = params_of(req)
        state = text_param(params, "state", required=True)
        if state not in STATUS_EVENTS:
            raise invalid("state")
        coverage = params.get("coverage")
        try:
            coverage = float(coverage) if coverage not in (None, "") else None
        except (TypeError, ValueError):
            raise invalid("coverage", "is invalid")
        full = self.resolve(p, sha)
        if full is None:
            raise not_found("Commit")
        name = text_param(params, "name") or text_param(params, "context") or "default"
        ref = text_param(params, "ref")
        if not ref:
            contains = git("--git-dir", p.repo, "branch", "--contains", full, "--format=%(refname:lstrip=2)", check=False).split()
            ref = sorted(contains)[0] if contains else None
        target_url, description = text_param(params, "target_url"), text_param(params, "description")
        errors = {}
        if description is not None and len(description) > 255:
            errors["description"] = ["is too long (maximum is 255 characters)"]
        if target_url:
            u = urllib.parse.urlsplit(target_url)
            if len(target_url) > 255:
                errors["target_url"] = ["is too long (maximum is 255 characters)"]
            elif u.scheme not in ("http", "https"):
                errors["target_url"] = ["is blocked: Only allowed schemes are http, https"]
            elif not u.hostname:
                errors["target_url"] = ["must be a valid URL"]
        if errors:
            raise GitLabError(400, {"message": errors})
        pipeline_id = p.pipelines.get((full, ref)) or p.pipelines.setdefault((full, ref), self.next_id("pipeline"))
        current = next((s for s in reversed(p.statuses) if s["sha"] == full and s["ref"] == ref and s["name"] == name
                        and s["author"] == tok.user.username and s["status"] in ("pending", "running")), None)
        s = current or {"id": self.next_id("status"), "sha": full, "ref": ref, "name": name, "status": "created", "author": tok.user.username,
                        "created_at": utc_now(), "started_at": None, "finished_at": None, "pipeline_id": pipeline_id, "token": tag(tok.value)}
        before = s["status"]
        if state == "running" and before in STATUS_FROM["enqueue"]:
            before = "pending"
        event = STATUS_EVENTS[state]
        if before not in STATUS_FROM[event]:
            raise GitLabError(400, {"message": f'Cannot transition status via :{event} from :{before} (Reason(s): Status cannot transition via "{event}")'})
        s.update(status=STATUS_TO[event], target_url=target_url if target_url is not None else s.get("target_url"),
                 description=description if description is not None else s.get("description"),
                 coverage=coverage if coverage is not None else s.get("coverage"))
        if s["status"] == "running" and s["started_at"] is None:
            s["started_at"] = utc_now()
        if s["status"] in ("success", "failed", "canceled", "skipped"):
            s["finished_at"] = utc_now()
        if current is None:
            p.statuses.append(s)
        p.last_activity_at = utc_now()
        return 201, self.status_json(p, s, req.host), None

    # merge requests and their notes

    def _mr(self, tok, id, iid):
        p = self._code(tok, id)
        mr = p.mrs.get(int(iid))
        if mr is None:
            raise not_found()
        return p, mr

    def list_mrs(self, req, tok, id):
        p = self._code(tok, id)
        params = params_of(req)
        state = params.get("state") or "all"
        if state not in ("opened", "closed", "locked", "merged", "all"):
            raise invalid("state")
        mrs = [m for m in p.mrs.values() if state == "all" or m.state == state]
        for f in ("source_branch", "target_branch"):
            if params.get(f):
                mrs = [m for m in mrs if getattr(m, f) == params[f]]
        if params.get("author_username"):
            mrs = [m for m in mrs if m.author.lower() == params["author_username"].lower()]
        iids = [int(v) for k, v in req.query.items() if k in ("iids[]", "iids") and str(v).isdigit()]
        if iids:
            mrs = [m for m in mrs if m.iid in iids]
        if params.get("wip") in ("yes", "no"):
            mrs = [m for m in mrs if m.draft == (params["wip"] == "yes")]
        order = params.get("order_by") or "created_at"
        if order not in ("created_at", "updated_at", "title", "merged_at", "label_priority", "milestone_due", "popularity", "priority"):
            raise invalid("order_by")
        sort = params.get("sort") or "desc"
        if sort not in ("asc", "desc"):
            raise invalid("sort")
        key = {"updated_at": lambda m: (m.updated_at, m.id), "title": lambda m: (m.title, m.id)}.get(order, lambda m: (m.created_at, m.id))
        mrs.sort(key=key, reverse=sort == "desc")
        chunk, headers = self._page(req, mrs)
        simple = params.get("view") == "simple"
        return 200, [self.mr_json(m, req.host, simple=simple) for m in chunk], headers

    def get_mr(self, req, tok, id, iid):
        _, mr = self._mr(tok, id, iid)
        d = self.mr_json(mr, req.host, full=True)
        if tok is not None:
            target = self.projects[mr.target]
            d["user"] = {"can_merge": self.level(tok.user, target) >= DEVELOPER and mr.state == "opened"}
        return 200, d, None

    def list_notes(self, req, tok, id, iid):
        p, mr = self._mr(tok, id, iid)
        params = params_of(req)
        sort = params.get("sort") or "desc"
        order = params.get("order_by") or "created_at"
        if sort not in ("asc", "desc"):
            raise invalid("sort")
        if order not in ("created_at", "updated_at"):
            raise invalid("order_by")
        notes = sorted((n for n in p.notes.values() if n["mr"] == mr.iid), key=lambda n: (n[order], n["id"]), reverse=sort == "desc")
        chunk, headers = self._page(req, notes)
        return 200, [self.note_json(p, n, req.host) for n in chunk], headers

    def create_note(self, req, tok, id, iid):
        if tok is None:
            raise unauthorized()
        p, mr = self._mr(tok, id, iid)
        if p.archived:
            raise forbidden()
        body = text_param(params_of(req), "body", required=True, allow_blank=False)
        if len(body) > 1000000:
            raise GitLabError(400, {"message": '400 Bad request - Note {:note=>["is too long (maximum is 1000000 characters)"]}'})
        n = self.add_note(p, mr, body, tok.user.username, token=tag(tok.value))
        return 201, self.note_json(p, n, req.host), None

    def get_note(self, req, tok, id, iid, note):
        p, mr = self._mr(tok, id, iid)
        n = p.notes.get(int(note))
        if n is None or n["mr"] != mr.iid:
            raise not_found("Note")
        return 200, self.note_json(p, n, req.host), None

    def update_note(self, req, tok, id, iid, note):
        """Only its author edits a note (403 for anyone else, and for GitLab's own system notes)."""
        if tok is None:
            raise unauthorized()
        p, mr = self._mr(tok, id, iid)
        n = p.notes.get(int(note))
        if n is None or n["mr"] != mr.iid:
            raise not_found("Note")
        if n["system"] or n["author"].lower() != tok.user.username.lower() or p.archived:
            raise forbidden()
        body = text_param(params_of(req), "body", required=True, allow_blank=False)
        n.update(body=body, updated_at=utc_now(), edits=n["edits"] + 1, token=tag(tok.value))
        return 200, self.note_json(p, n, req.host), None

    def delete_note(self, req, tok, id, iid, note):
        if tok is None:
            raise unauthorized()
        p, mr = self._mr(tok, id, iid)
        n = p.notes.get(int(note))
        if n is None or n["mr"] != mr.iid:
            raise not_found("Note")
        if n["system"] or (n["author"].lower() != tok.user.username.lower() and self.level(tok.user, p) < MAINTAINER):
            raise forbidden()
        del p.notes[n["id"]]
        return 204, None, None

    # project hooks

    def _hook(self, p, hook_id):
        h = p.hooks.get(int(hook_id))
        if h is None:
            raise not_found()
        return h

    def list_hooks(self, req, tok, id):
        p = self._role(tok, id, MAINTAINER, write=False)
        chunk, headers = self._page(req, sorted(p.hooks.values(), key=lambda h: h["id"]))
        return 200, [self.hook_json(p, h) for h in chunk], headers

    def get_hook(self, req, tok, id, hook):
        p = self._role(tok, id, MAINTAINER, write=False)
        return 200, self.hook_json(p, self._hook(p, hook)), None

    @staticmethod
    def _hook_url(url):
        """A hook's URL as GitLab takes it: http or https, to a host GitLab may call. By default GitLab refuses requests
        to the local network from webhooks: localhost, a private, loopback or link-local address, or a name that doesn't
        resolve publicly (here: one without a dot) is its 422."""
        u = urllib.parse.urlsplit(url or "")
        host = (u.hostname or "").lower()
        try:
            address = ipaddress.ip_address(host)
        except ValueError:
            address = None
        local = (address is not None and (address.is_loopback or address.is_link_local or address.is_unspecified
                                          or any(address in n for n in LOCAL_NETWORKS if n.version == address.version))) \
            or host == "localhost" or host.endswith(".localhost") or (address is None and "." not in host)
        if u.scheme not in ("http", "https") or not host or local:
            raise GitLabError(422, {"error": "Invalid url given"})
        return url

    @staticmethod
    def _hook_fields(params, h):
        """The hook with the fields the request sets (a copy: nothing changes until they're all valid)."""
        h = dict(h, events=dict(h["events"]))
        for e in HOOK_EVENTS:
            v = boolean(params, e)
            if v is not None:
                h["events"][e] = v
        v = boolean(params, "enable_ssl_verification")
        if v is not None:
            h["enable_ssl_verification"] = v
        for f in ("name", "description", "push_events_branch_filter", "branch_filter_strategy"):
            if f in params:
                h[f] = text_param(params, f)
        if h.get("branch_filter_strategy") not in (None, "wildcard", "regex", "all_branches"):
            raise invalid("branch_filter_strategy")
        if isinstance(params.get("custom_headers"), list):
            h["custom_headers"] = {c["key"]: str(c.get("value") or "") for c in params["custom_headers"] if isinstance(c, dict) and c.get("key")}
        return h

    def create_hook(self, req, tok, id):
        """A project hook (maintainers and up). Push events are on unless the request says otherwise, the rest off; the
        token is kept and never shown."""
        p = self._role(tok, id, MAINTAINER, write=False)
        params = params_of(req)
        url = self._hook_url(text_param(params, "url", required=True))
        h = self._hook_fields(params, self.new_hook(p, url, text_param(params, "token") or None, "api", req.host, token_tag=tag(tok.value), add=False))
        p.hooks[h["id"]] = h
        return 201, self.hook_json(p, h), None

    def new_hook(self, p, url, token, made_by, instance, token_tag=None, events=None, enable_ssl_verification=True, add=True):
        """A project hook, made by `made_by`: "api" (Gitberth's, through the REST API), "pasted" (Gitberth's, made by hand
        in GitLab from what its Settings shows) or "foreign" (another service's). Its deliveries go where its URL leads,
        whoever made it. instance: the GitLab host it was made through, which its deliveries name."""
        h = {"id": self.next_id("hook"), "uuid": str(uuid.uuid4()), "url": url, "token": token, "made_by": made_by,
             "instance": instance, "created_at": utc_now(), "enable_ssl_verification": bool(enable_ssl_verification),
             "events": {e: e == "push_events" for e in HOOK_EVENTS}, "custom_headers": {}, "token_tag": token_tag, "updated_by": token_tag}
        h["events"].update(events or {})
        if add:
            p.hooks[h["id"]] = h
        return h

    def update_hook(self, req, tok, id, hook):
        """GitLab keeps what isn't sent, but a hook whose URL changes without a token sent with it loses its token."""
        p = self._role(tok, id, MAINTAINER, write=False)
        h = self._hook(p, hook)
        params = params_of(req)
        changed = self._hook_fields(params, h)
        if "url" in params:
            changed["url"] = self._hook_url(text_param(params, "url"))
            if changed["url"] != h["url"] and "token" not in params:
                changed["token"] = None
        if "token" in params:
            changed["token"] = text_param(params, "token") or None
        h.update(changed, updated_by=tag(tok.value))
        return 200, self.hook_json(p, h), None

    def delete_hook(self, req, tok, id, hook):
        p = self._role(tok, id, MAINTAINER, write=False)
        del p.hooks[self._hook(p, hook)["id"]]
        return 204, None, None

    # CI/CD variables

    def _scope_filter(self, params):
        f = params.get("filter")
        if isinstance(f, dict) and f.get("environment_scope") is not None:
            return str(f["environment_scope"])
        return params.get("filter[environment_scope]")

    def _variable(self, p, key, params):
        """The variable a key (and filter[environment_scope]) names: 404 when there's none, 409 when the key alone names
        more than one."""
        scope = self._scope_filter(params)
        found = [v for v in p.variables if v["key"] == key and (scope is None or v["environment_scope"] == scope)]
        if not found:
            raise not_found("Variable")
        if len(found) > 1:
            raise GitLabError(409, {"message": "There are multiple variables with provided parameters. Please use 'filter[environment_scope]'"})
        return found[0]

    def list_variables(self, req, tok, id):
        p = self._role(tok, id, MAINTAINER, write=False)
        chunk, headers = self._page(req, list(p.variables))
        return 200, [self.variable_json(v) for v in chunk], headers

    def get_variable(self, req, tok, id, key):
        p = self._role(tok, id, MAINTAINER, write=False)
        return 200, self.variable_json(self._variable(p, key, params_of(req))), None

    @staticmethod
    def _check_variable(p, v, exclude=None):
        errors = {}
        if not VARIABLE_KEY.match(v["key"]):
            errors["key"] = ["can contain only letters, digits and '_'."]
        if v["masked"] and not MASKABLE.match(v["value"] or ""):
            errors["value"] = ["is invalid"]
        if any(x is not exclude and x["key"] == v["key"] and x["environment_scope"] == v["environment_scope"] for x in p.variables):
            errors.setdefault("key", []).append(f"({v['key']}) has already been taken")
        if errors:
            raise GitLabError(400, {"message": errors})

    def _variable_fields(self, params, v):
        for f in ("protected", "masked", "raw"):
            b = boolean(params, f)
            if b is not None:
                v[f] = b
        if "value" in params:
            v["value"] = text_param(params, "value") or ""
        if "variable_type" in params:
            if params["variable_type"] not in ("env_var", "file"):
                raise invalid("variable_type")
            v["variable_type"] = params["variable_type"]
        if params.get("environment_scope") is not None:
            v["environment_scope"] = text_param(params, "environment_scope")
        if "description" in params:
            v["description"] = text_param(params, "description")

    def create_variable(self, req, tok, id):
        p = self._role(tok, id, MAINTAINER, write=False)
        params = params_of(req)
        key = text_param(params, "key", required=True)
        text_param(params, "value", required=True)
        v = {"key": key, "value": "", "variable_type": "env_var", "protected": False, "masked": False, "hidden": False, "raw": False,
             "environment_scope": "*", "description": None, "token": tag(tok.value)}
        self._variable_fields(params, v)
        if boolean(params, "masked_and_hidden", False):
            v["masked"] = v["hidden"] = True
        self._check_variable(p, v)
        p.variables.append(v)
        return 201, self.variable_json(v), None

    def update_variable(self, req, tok, id, key):
        p = self._role(tok, id, MAINTAINER, write=False)
        params = params_of(req)
        v = self._variable(p, key, params)
        changed = dict(v)
        self._variable_fields(params, changed)
        self._check_variable(p, changed, exclude=v)
        v.update(changed, token=tag(tok.value))
        return 200, self.variable_json(v), None

    def delete_variable(self, req, tok, id, key):
        p = self._role(tok, id, MAINTAINER, write=False)
        v = self._variable(p, key, params_of(req))
        p.variables.remove(v)
        return 204, None, None

    # ---- what people do on GitLab (the control API's actions) ----

    def add_token(self, spec):
        kind = spec.get("kind") or "personal"
        if kind not in TOKEN_KINDS:
            raise ApiError(400, f"kind is one of {', '.join(TOKEN_KINDS)}")
        scopes = list(spec.get("scopes") or ["api"])
        bad = [s for s in scopes if s not in SCOPES]
        if bad:
            raise ApiError(400, f"GitLab has no scope {', '.join(bad)}: {', '.join(SCOPES)}")
        value = spec.get("token") or new_token_value(kind)
        name = spec.get("name") or "gbe2e"
        with self.lock:
            target, level = None, None
            if kind in ("project", "group"):
                level = LEVELS.get(str(spec.get("access_level") or "maintainer").lower()) or int(spec.get("access_level"))
                if kind == "project":
                    p = self.project(spec.get("project") or "")
                    user = self.user(f"project_{p.id}_bot_{secrets.token_hex(16)}", name, bot=True)
                    p.members[user.username.lower()] = level
                    target = p.full_path
                else:
                    n = self.namespaces.get(str(spec.get("group") or "").lower())
                    if n is None or n.kind != "group":
                        raise ApiError(404, f"No GitLab group {spec.get('group')}")
                    user = self.user(f"group_{n.id}_bot_{secrets.token_hex(16)}", name, bot=True)
                    n.members[user.username.lower()] = level
                    target = n.full_path
            else:
                user = self.user(spec.get("username") or "e2e-bot")
            t = Token(value, self.next_id("token"), kind, user, scopes, spec.get("expires_at"), name, target, level)
            self.tokens[value] = t
            return t

    def namespace(self, full_path, owner, visibility="public"):
        """The namespace at full_path, made if it's missing: the owner's own (when it's their username) or groups, each
        made by the owner, who owns it."""
        with self.lock:
            n = self.namespaces.get(full_path.lower())
            if n is not None:
                return n
            if "/" not in full_path and full_path.lower() == owner.lower():
                n = Namespace(self.next_id("namespace"), full_path, "user", owner=owner)
            else:
                parent = self.namespace(full_path.rsplit("/", 1)[0], owner, visibility) if "/" in full_path else None
                if parent is not None and parent.kind == "user":
                    raise ApiError(400, f"{parent.full_path} is a user's namespace, which has no subgroups")
                n = Namespace(self.next_id("namespace"), full_path, "group", parent, visibility)
                if parent is None or parent.kind != "group":
                    n.members[owner.lower()] = OWNER
            self.namespaces[full_path.lower()] = n
            return n

    def add_member(self, target, username, level):
        """A user's role in a project, or in a group (target "group:<full path>")."""
        level = LEVELS.get(str(level).lower()) or int(level)
        with self.lock:
            self.user(username)
            if target.startswith("group:"):
                n = self.namespaces.get(target[6:].lower())
                if n is None or n.kind != "group":
                    raise ApiError(404, f"No GitLab group {target[6:]}")
                n.members[username.lower()] = level
            else:
                self.project(target).members[username.lower()] = level
        return {"target": target, "username": username, "access_level": level}

    def create_project(self, full_path, owner="e2e-bot", files=None, template=None, visibility="public", branch="main",
                       message="Initial commit", forked_from=None):
        if visibility not in VISIBILITY:
            raise ApiError(400, f"visibility is one of {', '.join(VISIBILITY)}")
        if "/" not in full_path:
            raise ApiError(400, "A project's path has a namespace: <group or user>/<project>")
        with self.lock:
            if full_path.lower() in self.projects:
                raise ApiError(409, f"{full_path} exists already")
            ns_path, path = full_path.rsplit("/", 1)
            self.user(owner)
            ns = self.namespace(ns_path, owner)
            p = Project(self, self.next_id("project"), ns, path, visibility, branch, owner, forked_from)
            if self.level(self.user(owner), p) < OWNER:
                p.members[owner.lower()] = OWNER
            os.makedirs(os.path.dirname(p.repo), exist_ok=True)
            self.projects[p.key] = p
            self.by_id[p.id] = p
            if forked_from is None:
                git("init", "--bare", "-q", "-b", branch, p.repo)
                content = template_files(self.templates, template) if template else {}
                content.update(files or {})
                self._commit(p, branch, content, message, owner)
            return p

    def fork(self, p, namespace, owner):
        with self.lock:
            full = f"{namespace}/{p.path}"
            f = self.create_project(full, owner, visibility=p.visibility, branch=p.default_branch, forked_from=p.key)
            git("clone", "--bare", "-q", p.repo, f.repo)
            return f

    def delete_project(self, p):
        with self.lock:
            self.projects.pop(p.key, None)
            self.by_id.pop(p.id, None)
            shutil.rmtree(p.repo, ignore_errors=True)

    def _commit(self, p, branch, files, message, user, base=None, force=False, author=None):
        """Commits the files (None deletes one; {"symlink": target} is a link) on top of the branch, or of base, and moves
        the branch there. A branch that doesn't exist yet starts from base, else from the default branch."""
        before = self.branch_sha(p, branch)
        start = base if base is not None else before if before is not None else self.branch_sha(p, p.default_branch)
        u = self.user(user)
        env = {"GIT_AUTHOR_NAME": (author or {}).get("name", u.name), "GIT_AUTHOR_EMAIL": (author or {}).get("email", u.email),
               "GIT_COMMITTER_NAME": u.name, "GIT_COMMITTER_EMAIL": u.email}
        with tempfile.TemporaryDirectory(prefix="gbe2e-gitlab-commit-") as work:
            git("init", "-q", work)
            if start:
                git("fetch", "-q", "--no-tags", p.repo, self.branch_sha(p, start) or start, cwd=work)
                git("checkout", "-q", "--detach", "FETCH_HEAD", cwd=work)
            for path, content in (files or {}).items():
                target = os.path.join(work, path)
                if content is None:
                    if os.path.isdir(target):
                        shutil.rmtree(target)
                    elif os.path.lexists(target):
                        os.remove(target)
                    continue
                os.makedirs(os.path.dirname(target) or work, exist_ok=True)
                if isinstance(content, dict) and "symlink" in content:
                    if os.path.lexists(target):
                        os.remove(target)
                    os.symlink(content["symlink"], target)
                    continue
                with open(target, "wb") as fh:
                    fh.write(content if isinstance(content, (bytes, bytearray)) else str(content).encode())
            git("add", "-A", cwd=work)
            git("commit", "-q", "--allow-empty", "-m", message, cwd=work, env=env)
            sha = git("rev-parse", "HEAD", cwd=work).strip()
            git("push", "-q", *(["--force"] if force or base is not None else []), p.repo, f"HEAD:refs/heads/{branch}", cwd=work)
        p.last_activity_at = utc_now()
        return sha, before

    def commit(self, p, branch, files, message, user="e2e-bot", base=None, force=False, author=None):
        with self.lock:
            sha, before = self._commit(p, branch, files, message, user, base=base, force=force, author=author)
            jobs = self._after_push(p, branch, before, sha, user)
            return {"sha": sha, "before": before, "branch": branch, "created": before is None}, jobs

    def create_branch(self, p, name, start=None, user="e2e-bot"):
        with self.lock:
            if self.branch_sha(p, name):
                raise ApiError(409, f"Branch {name} exists already")
            sha = self.resolve(p, start or p.default_branch)
            if not sha:
                raise ApiError(404, f"Nothing to branch from: {start}")
            git("--git-dir", p.repo, "update-ref", f"refs/heads/{name}", sha)
            return {"sha": sha}, self._after_push(p, name, None, sha, user)

    def delete_branch(self, p, name, user="e2e-bot"):
        with self.lock:
            sha = self.branch_sha(p, name)
            if not sha:
                raise ApiError(404, f"No branch {name}")
            if name == p.default_branch:
                raise ApiError(422, f"{name} is the default branch, which GitLab doesn't delete")
            git("--git-dir", p.repo, "update-ref", "-d", f"refs/heads/{name}")
            return {"deleted": name, "sha": sha}, self._after_push(p, name, sha, None, user)

    def add_note(self, p, mr, body, author, system=False, token=None):
        n = {"id": self.next_id("note"), "mr": mr.iid, "body": body, "author": author, "system": system, "created_at": utc_now(),
             "updated_at": utc_now(), "edits": 0, "token": token}
        n["updated_at"] = n["created_at"]
        p.notes[n["id"]] = n
        return n

    def _set_mr_ref(self, mr):
        target, source = self.projects.get(mr.target), self.projects.get(mr.source)
        if target is None or source is None:
            return
        if source is target:
            git("--git-dir", target.repo, "update-ref", f"refs/merge-requests/{mr.iid}/head", mr.sha)
        else:
            # The fork's commits come into the target project, as GitLab fetches them for a fork's merge request.
            git("--git-dir", target.repo, "fetch", "-q", "--no-tags", source.repo, f"+refs/heads/{mr.source_branch}:refs/merge-requests/{mr.iid}/head")

    def open_mr(self, p, source_branch, target_branch=None, title=None, description="", draft=False, source=None, author="e2e-bot"):
        with self.lock:
            src = source or p
            target_branch = target_branch or p.default_branch
            if not self.branch_sha(p, target_branch):
                raise ApiError(422, f"No target branch {target_branch} in {p.full_path}")
            sha = self.branch_sha(src, source_branch)
            if not sha:
                raise ApiError(422, f"No source branch {source_branch} in {src.full_path}")
            for m in p.mrs.values():
                if m.state == "opened" and m.source == src.key and m.source_branch == source_branch and m.target_branch == target_branch:
                    raise ApiError(409, f"Another open merge request already exists for this source branch: !{m.iid}")
            title = title or source_branch
            if draft and not DRAFT.match(title):
                title = f"Draft: {title}"
            mr = MergeRequest(self.next_id("mr"), p.next_iid, p.key, src.key, src.id, source_branch, target_branch, title, description or "",
                              author, sha)
            p.next_iid += 1
            p.mrs[mr.iid] = mr
            self._set_mr_ref(mr)
            p.last_activity_at = utc_now()
            changes = {"merge_status": {"previous": "preparing", "current": "unchecked"},
                       "updated_at": {"previous": None, "current": ruby_time(mr.updated_at)}}
            return mr, self._emit_mr(p, mr, "open", author, changes)

    def update_mr(self, p, mr, title=None, description=None, state_event=None, draft=None, user="e2e-bot"):
        with self.lock:
            jobs, changes = [], {}
            was_draft = mr.draft
            new_title = mr.title if title is None else title
            if draft is True and not DRAFT.match(new_title):
                new_title = f"Draft: {new_title}"
            elif draft is False:
                new_title = DRAFT.sub("", new_title, count=1)
            if new_title != mr.title:
                changes["title"] = {"previous": mr.title, "current": new_title}
                old = mr.title
                mr.title = new_title
                if mr.draft != was_draft:
                    changes["draft"] = {"previous": was_draft, "current": mr.draft}
                    self.add_note(p, mr, f"marked this merge request as **{'draft' if mr.draft else 'ready'}**", user, system=True)
                if DRAFT.sub("", old, count=1) != DRAFT.sub("", new_title, count=1):
                    self.add_note(p, mr, f"changed title from **{old}** to **{new_title}**", user, system=True)
            if description is not None and description != mr.description:
                changes["description"] = {"previous": mr.description, "current": description}
                mr.description = description
                self.add_note(p, mr, "changed the description", user, system=True)
            if changes:
                before = mr.updated_at
                mr.updated_at = mr.last_edited_at = utc_now()
                changes.update({"updated_at": {"previous": ruby_time(before), "current": ruby_time(mr.updated_at)},
                                "last_edited_at": {"previous": None, "current": ruby_time(mr.last_edited_at)}})
                jobs += self._emit_mr(p, mr, "update", user, changes)
            if state_event == "close" and mr.state == "opened":
                mr.state, mr.closed_at, mr.closed_by, mr.updated_at = "closed", utc_now(), user, utc_now()
                self.add_note(p, mr, "closed", user, system=True)
                jobs += self._emit_mr(p, mr, "close", user, {"state_id": {"previous": 1, "current": 2}})
            elif state_event == "reopen" and mr.state == "closed":
                mr.state, mr.closed_at, mr.closed_by, mr.updated_at = "opened", None, None, utc_now()
                self.add_note(p, mr, "reopened", user, system=True)
                jobs += self._emit_mr(p, mr, "reopen", user, {"state_id": {"previous": 2, "current": 1}})
            elif state_event not in (None, "close", "reopen"):
                raise ApiError(400, "state_event is close or reopen")
            return mr, jobs

    def merge_mr(self, p, mr, squash=False, remove_source_branch=False, user="e2e-bot"):
        with self.lock:
            if mr.state != "opened" or mr.draft:
                raise ApiError(405, "405 Method Not Allowed: only an open merge request that isn't a draft merges")
            src = self.projects.get(mr.source)
            before = self.branch_sha(p, mr.target_branch)
            u = self.user(user)
            env = {"GIT_AUTHOR_NAME": u.name, "GIT_AUTHOR_EMAIL": u.email, "GIT_COMMITTER_NAME": u.name, "GIT_COMMITTER_EMAIL": u.email}
            with tempfile.TemporaryDirectory(prefix="gbe2e-gitlab-merge-") as work:
                git("init", "-q", work)
                git("fetch", "-q", "--no-tags", p.repo, f"refs/heads/{mr.target_branch}", cwd=work)
                git("checkout", "-q", "--detach", "FETCH_HEAD", cwd=work)
                git("fetch", "-q", "--no-tags", p.repo, f"refs/merge-requests/{mr.iid}/head", cwd=work)
                head = git("rev-parse", "FETCH_HEAD", cwd=work).strip()
                if squash:
                    # One commit with all of the merge request's changes, its title as the message.
                    git("merge", "-q", "--squash", head, cwd=work, env=env)
                    git("commit", "-q", "--allow-empty", "-m", mr.title, cwd=work, env=env)
                    merged = mr.squash_commit_sha = git("rev-parse", "HEAD", cwd=work).strip()
                else:
                    message = (f"Merge branch '{mr.source_branch}' into '{mr.target_branch}'\n\n{mr.title}\n\n"
                               f"See merge request {p.full_path}!{mr.iid}")
                    git("merge", "-q", "--no-ff", "-m", message, head, cwd=work, env=env)
                    merged = git("rev-parse", "HEAD", cwd=work).strip()
                git("push", "-q", p.repo, f"HEAD:refs/heads/{mr.target_branch}", cwd=work)
            mr.state, mr.merged_at, mr.merge_user, mr.updated_at = "merged", utc_now(), user, utc_now()
            mr.merge_commit_sha = None if squash else merged
            p.last_activity_at = utc_now()
            jobs = self._after_push(p, mr.target_branch, before, merged, user)
            self.add_note(p, mr, "merged", user, system=True)
            jobs += self._emit_mr(p, mr, "merge", user, {"state_id": {"previous": 1, "current": 3}})
            if remove_source_branch and src is p and mr.source_branch != p.default_branch:
                mr.should_remove_source_branch = True
                _, more = self.delete_branch(p, mr.source_branch, user)
                jobs += more
            return merged, jobs

    def approve_mr(self, p, mr, user="e2e-bot", approve=True):
        """Someone approves the merge request (or takes the approval back): with no approval rules, one approval makes it
        approved, so GitLab sends approved (or unapproved)."""
        with self.lock:
            if approve and user not in mr.approved_by:
                mr.approved_by.append(user)
                self.add_note(p, mr, "approved this merge request", user, system=True)
                return mr, self._emit_mr(p, mr, "approved", user, {})
            if not approve and user in mr.approved_by:
                mr.approved_by.remove(user)
                self.add_note(p, mr, "unapproved this merge request", user, system=True)
                return mr, self._emit_mr(p, mr, "unapproved", user, {})
            return mr, []

    def set_variables(self, p, items):
        """CI/CD variables as someone sets them in GitLab's settings: each {key, value, environment_scope ("*"), masked,
        protected, raw, hidden, variable_type, description}; value None removes the key in that scope."""
        with self.lock:
            for item in items:
                scope = item.get("environment_scope") or "*"
                existing = next((v for v in p.variables if v["key"] == item["key"] and v["environment_scope"] == scope), None)
                if item.get("value") is None:
                    if existing:
                        p.variables.remove(existing)
                    continue
                if item.get("variable_type") not in (None, "env_var", "file"):
                    raise ApiError(400, "variable_type is env_var or file")
                v = {"key": item["key"], "environment_scope": scope, "value": str(item["value"]), "variable_type": item.get("variable_type") or "env_var",
                     "protected": bool(item.get("protected")), "masked": bool(item.get("masked") or item.get("hidden")),
                     "hidden": bool(item.get("hidden")), "raw": bool(item.get("raw")), "description": item.get("description"), "token": None}
                self._check_variable(p, v, exclude=existing)
                if existing is None:
                    p.variables.append(v)
                else:
                    existing.update(v)
            return [dict(v) for v in p.variables]

    def _after_push(self, p, branch, before, after, user):
        """GitLab's side of a push: the Push Hook, and for each open merge request from this branch whose head moved, its
        refs/merge-requests/<iid>/head, a system note and a Merge Request Hook (update, with oldrev)."""
        jobs = self._emit(p, "push", self.push_payload(p, branch, before, after, user), branch=branch)
        for target in list(self.projects.values()):
            for mr in target.mrs.values():
                if mr.state != "opened" or mr.source != p.key or mr.source_branch != branch or not after or after == mr.sha:
                    continue
                old, mr.sha = mr.sha, after
                mr.updated_at = utc_now()
                self._set_mr_ref(mr)
                shas = git("--git-dir", target.repo, "rev-list", "--reverse", f"{old}..{after}", check=False).split()
                if not shas:
                    shas = [after]
                titles = "".join(f"<li>{c['short_id']} - {c['title']}</li>" for c in self.commits_json(target, shas, DEFAULT_HOST))
                self.add_note(target, mr, f"added {len(shas)} commit{'s' if len(shas) != 1 else ''}\n\n<ul>{titles}</ul>\n\n"
                                          f"[Compare with previous version](/{target.full_path}/-/merge_requests/{mr.iid}/diffs?start_sha={old})",
                              user, system=True)
                jobs += self._emit_mr(target, mr, "update", user, {"updated_at": {"previous": None, "current": ruby_time(mr.updated_at)}}, oldrev=old)
        return jobs

    # ---- webhook payloads ----

    def hook_project(self, p, host):
        web = f"https://{host}/{p.full_path}"
        ssh = f"git@{host}:{p.full_path}.git"
        return {"id": p.id, "name": p.name, "description": None, "web_url": web, "avatar_url": None, "git_ssh_url": ssh,
                "git_http_url": f"{web}.git", "namespace": p.namespace.name, "visibility_level": VISIBILITY[p.visibility],
                "path_with_namespace": p.full_path, "default_branch": p.default_branch, "ci_config_path": None,
                "homepage": web, "url": ssh, "ssh_url": ssh, "http_url": f"{web}.git"}

    def hook_commits(self, p, shas, host):
        """A push's commits as its hook lists them: id, message, title, timestamp, url, author and the files each added,
        modified and removed (against its first parent)."""
        out = []
        for c in self.commits_json(p, shas, host):
            parent = c["parent_ids"][0] if c["parent_ids"] else None
            args = ["--git-dir", p.repo, "diff-tree", "-r", "--no-commit-id", "--name-status"] + ([parent, c["id"]] if parent else ["--root", c["id"]])
            added, modified, removed = [], [], []
            for line in git(*args).splitlines():
                kind, _, path = line.partition("\t")
                (added if kind == "A" else removed if kind == "D" else modified).append(path)
            out.append({"id": c["id"], "message": c["message"], "title": c["title"], "timestamp": c["authored_date"].replace(".000", ""),
                        "url": c["web_url"], "author": {"name": c["author_name"], "email": c["author_email"]},
                        "added": added, "modified": modified, "removed": removed})
        return out

    def push_payload(self, p, branch, before, after, user, host=DEFAULT_HOST):
        """The Push Hook's body. A new branch has before = 40 zeros and lists the commits no other branch has; a deleted
        one has after = 40 zeros and no commits. commits holds the newest 20, oldest first; total_commits_count all."""
        if after is None:
            shas = []
        elif before is None:
            others = [s for b, s in self.branches(p).items() if b != branch]
            shas = git("--git-dir", p.repo, "rev-list", "--reverse", after, *[f"^{s}" for s in others], check=False).split()
        else:
            shas = git("--git-dir", p.repo, "rev-list", "--reverse", f"{before}..{after}", check=False).split()
        u = self.user(user)
        md5 = hashlib.md5(u.email.encode()).hexdigest()
        return {
            "object_kind": "push", "event_name": "push", "before": before or ZERO, "after": after or ZERO, "ref": f"refs/heads/{branch}",
            "ref_protected": branch == p.default_branch, "checkout_sha": after, "message": None, "user_id": u.id, "user_name": u.name,
            "user_username": u.username, "user_email": "[REDACTED]", "user_avatar": f"https://secure.gravatar.com/avatar/{md5}?s=80&d=identicon",
            "project_id": p.id, "project": self.hook_project(p, host), "commits": self.hook_commits(p, shas[-20:], host),
            "total_commits_count": len(shas), "push_options": {},
            "repository": {"name": p.name, "url": f"git@{host}:{p.full_path}.git", "description": None, "homepage": f"https://{host}/{p.full_path}",
                           "git_http_url": f"https://{host}/{p.full_path}.git", "git_ssh_url": f"git@{host}:{p.full_path}.git",
                           "visibility_level": VISIBILITY[p.visibility]},
        }

    def mr_payload(self, p, mr, action, user, changes, oldrev=None, host=DEFAULT_HOST):
        """The Merge Request Hook's body for an action: open, update (oldrev when its code changed), close, reopen, merge,
        approved, unapproved."""
        u = self.user(user)
        source = self.projects.get(mr.source) or p
        last = self.commits_json(source if self.resolve(source, mr.sha) else p, [mr.sha], host)[0]
        md5 = hashlib.md5(u.email.encode()).hexdigest()
        attrs = {
            "id": mr.id, "iid": mr.iid, "target_branch": mr.target_branch, "source_branch": mr.source_branch, "source_project_id": mr.source_id,
            "author_id": self.user(mr.author).id, "assignee_ids": [], "assignee_id": None, "reviewer_ids": [], "title": mr.title,
            "created_at": xml_time(mr.created_at), "updated_at": xml_time(mr.updated_at), "last_edited_at": xml_time(mr.last_edited_at),
            "last_edited_by_id": None, "milestone_id": None, "state_id": mr.state_id, "state": mr.state, "blocking_discussions_resolved": True,
            "work_in_progress": mr.draft, "draft": mr.draft, "first_contribution": False, "merge_status": "can_be_merged" if mr.state == "opened" else "unchecked",
            "target_project_id": p.id, "description": mr.description, "prepared_at": xml_time(mr.created_at), "total_time_spent": 0,
            "time_change": 0, "human_total_time_spent": None, "human_time_change": None, "human_time_estimate": None, "time_estimate": 0,
            "head_pipeline_id": None, "merge_commit_sha": mr.merge_commit_sha, "merge_error": None, "merge_params": {"force_remove_source_branch": "0"},
            "merge_user_id": self.user(mr.merge_user).id if mr.merge_user else None, "merge_when_pipeline_succeeds": False,
            "url": f"https://{host}/{p.full_path}/-/merge_requests/{mr.iid}", "source": self.hook_project(source, host),
            "target": self.hook_project(p, host),
            "last_commit": {"id": last["id"], "message": last["message"], "title": last["title"], "timestamp": last["authored_date"].replace(".000", ""),
                            "url": last["web_url"], "author": {"name": last["author_name"], "email": last["author_email"]}},
            "labels": [], "action": action, "detailed_merge_status": "not_open" if mr.state != "opened" else "draft_status" if mr.draft else "mergeable",
        }
        if oldrev:
            attrs["oldrev"] = oldrev
        return {
            "object_kind": "merge_request", "event_type": "merge_request",
            "user": {"id": u.id, "name": u.name, "username": u.username, "avatar_url": f"https://secure.gravatar.com/avatar/{md5}?s=80&d=identicon",
                     "email": "[REDACTED]"},
            "project": self.hook_project(p, host), "object_attributes": attrs, "labels": [], "changes": changes,
            "repository": {"name": p.name, "url": f"git@{host}:{p.full_path}.git", "description": None, "homepage": f"https://{host}/{p.full_path}"},
        }

    # ---- deliveries ----

    @staticmethod
    def wants(h, event, branch=None):
        """Whether a hook asked for the event: push_events (within its branch filter) or merge_requests_events."""
        if event == "push":
            if not h["events"].get("push_events"):
                return False
            pattern, strategy = h.get("push_events_branch_filter"), h.get("branch_filter_strategy") or "wildcard"
            if branch is None or not pattern or strategy == "all_branches":
                return True
            if strategy == "regex":
                try:
                    return re.search(pattern, branch) is not None
                except re.error:
                    return False
            return wildcard(pattern).match(branch) is not None
        return bool(h["events"].get("merge_requests_events"))

    def _emit(self, p, event, payload, branch=None):
        """Queues the event's delivery to each of the project's hooks that asked for it; returns the futures. The hooks of
        one event share its X-Gitlab-Event-UUID."""
        event_uuid = str(uuid.uuid4())
        return [self.pool.submit(self._safe_deliver, p, h, event, self._for_hook(payload, h), event_uuid=event_uuid)
                for h in list(p.hooks.values()) if self.wants(h, event, branch)]

    def _emit_mr(self, p, mr, action, user, changes, oldrev=None):
        return self._emit(p, "merge_request", self.mr_payload(p, mr, action, user, changes, oldrev))

    @staticmethod
    def _for_hook(payload, h):
        """A payload names the GitLab its hook was made through (the host the API call came to)."""
        host = h.get("instance") or DEFAULT_HOST
        if host == DEFAULT_HOST:
            return payload
        return json.loads(json.dumps(payload).replace(f"https://{DEFAULT_HOST}/", f"https://{host}/").replace(f"git@{DEFAULT_HOST}:", f"git@{host}:"))

    def _safe_deliver(self, p, hook, event, payload, **overrides):
        try:
            return self.deliver(p, hook, event, payload, **overrides)
        except Exception:
            log_exception("gitlab delivery")
            return None

    def deliver(self, p, hook, event, payload, token=None, cf_connecting_ip="default", url=None, raw_body=None, extra_headers=None,
                method="POST", via_tunnel=True, event_uuid=None, event_header=None, idempotency_key=None):
        """One delivery, as GitLab sends it, recorded with the answer it got. The body is JSON (Rails' encoding), with
        GitLab's headers: X-Gitlab-Event, X-Gitlab-Token (the hook's token, when it has one), X-Gitlab-Event-UUID,
        X-Gitlab-Webhook-UUID, X-Gitlab-Instance, Idempotency-Key and webhook-id (the same; a resent delivery keeps its
        own), webhook-timestamp. token overrides it: "none" sends none, "bad" a wrong one, any other text that text.

        It goes where the URL leads, as GitLab, Cloudflare and cloudflared would take it (Cloudflare.edge: the host's DNS
        record, Access, the tunnel's rules), so it reaches Gitberth only through a route to Gitberth's service; otherwise
        the record says what it met. via_tunnel=False sends it straight to Gitberth's container instead (endpoint-only
        checks). GitLab waits options["webhook_timeout"] seconds (10) for the answer: after that the record has no status
        and the error "timed out"."""
        body = raw_body.encode("utf-8") if isinstance(raw_body, str) else raw_body if raw_body is not None else gitlab_json(payload)
        key = idempotency_key or str(uuid.uuid4())
        instance = (hook or {}).get("instance") or DEFAULT_HOST
        headers = {"Content-Type": "application/json", "User-Agent": f"GitLab/{VERSION}",
                   "X-Gitlab-Event": event_header or EVENT_HEADERS.get(event, f"{event.replace('_', ' ').title()} Hook"),
                   "X-Gitlab-Webhook-UUID": (hook or {}).get("uuid") or str(uuid.uuid4()), "X-Gitlab-Instance": f"https://{instance}",
                   "X-Gitlab-Event-UUID": event_uuid or str(uuid.uuid4()), "Idempotency-Key": key, "webhook-id": key,
                   "webhook-timestamp": str(int(time.time()))}
        secret = (hook or {}).get("token")
        if token == "none":
            secret = None
        elif token == "bad":
            secret = "not-the-hook-token"
        elif token is not None:
            secret = token
        if secret:
            headers["X-Gitlab-Token"] = secret
        headers.update((hook or {}).get("custom_headers") or {})
        headers.update(extra_headers or {})
        sender_ip = HOOK_SENDER.format(65 + int(time.time()) % 14) if cf_connecting_ip == "default" else cf_connecting_ip
        hook_url = url or (hook or {}).get("url") or ""
        u = urllib.parse.urlsplit(hook_url)
        tunnel = via_tunnel and self.edge is not None
        timeout = self.options["webhook_timeout"]
        record = {
            "seq": self.rec.next_seq(), "at": time.time(), "id": key, "event_uuid": headers["X-Gitlab-Event-UUID"],
            "repo": p.key if p else None, "project": p.full_path if p else None, "hook_id": hook["id"] if hook else None,
            "event": event, "action": ((payload or {}).get("object_attributes") or {}).get("action") if isinstance(payload, dict) else None,
            "url": hook_url, "host": u.hostname, "headers": headers, "payload": payload, "body_size": len(body), "via_tunnel": tunnel,
            "sender_ip": sender_ip, "route": None, "status": None, "response_headers": None, "response_body": None, "error": None,
            "duration_ms": None,
        }
        started = time.time()
        deadline = time.monotonic() + timeout
        if tunnel:
            result = self.edge(hook_url, method, headers, body, client_ip=sender_ip or HOOK_SENDER.format(65),
                               verify_tls=(hook or {}).get("enable_ssl_verification", True), timeout=timeout)
            record["route"] = result["route"]
            record["error"] = result["error"]
            if result["status"] is not None:
                record.update(status=result["status"], response_headers=result["headers"], response_body=result["body"][:2000])
        else:
            headers.setdefault("X-Forwarded-Proto", u.scheme or "https")
            if sender_ip:
                headers["CF-Connecting-IP"] = sender_ip
            target = urllib.parse.urlsplit(self.gitberth_url)
            try:
                conn = http.client.HTTPConnection(target.hostname, target.port or 80, timeout=timeout)
                try:
                    send_headers = dict(headers, Host=u.netloc, **{"Content-Length": str(len(body))})
                    try:
                        conn.request(method, (u.path or "/") + (f"?{u.query}" if u.query else ""), body=body, headers=send_headers)
                    except (BrokenPipeError, ConnectionResetError):
                        if conn.sock is None:
                            raise
                    conn.sock.settimeout(max(0.05, deadline - time.monotonic()))
                    resp = conn.getresponse()
                    text = resp.read()
                    record.update(status=resp.status, response_headers=dict(resp.getheaders()),
                                  response_body=text[:2000].decode("utf-8", "replace"))
                finally:
                    conn.close()
            except TimeoutError:
                record["error"] = "timed out"
            except OSError as e:
                record["error"] = f"{type(e).__name__}: {e}"
        record["duration_ms"] = int((time.time() - started) * 1000)
        with self.lock:
            self.deliveries.append(record)
            if len(self.deliveries) > 5000:
                del self.deliveries[:1000]
        # The log names the hook's host and path, never its token or the URL's query and credentials.
        log(f"gitlab delivery {headers['X-Gitlab-Event']} for {record['project']} to {u.hostname}{u.path}: {record['status'] or record['error']}")
        return record

    @staticmethod
    def gitberth_hook(p):
        """The hook a delivery the control API sends goes through unless one is named: the newest made through the API
        (Gitberth's), else the newest made by hand from Gitberth's Settings; never another service's."""
        for kind in ("api", "pasted"):
            mine = [h for h in p.hooks.values() if h.get("made_by") == kind]
            if mine:
                return max(mine, key=lambda h: h["id"])
        return None

    # ---- reading back ----

    def hook_state(self, p, h):
        return dict(self.hook_json(p, h), token=h["token"], made_by=h["made_by"], instance=h["instance"], uuid=h["uuid"],
                    made_with=h["token_tag"], changed_with=h["updated_by"])

    def note_state(self, p, n):
        return dict(self.note_json(p, n, DEFAULT_HOST), mr=n["mr"], edits=n["edits"], token=n["token"])

    def status_state(self, p, s):
        return dict(self.status_json(p, s, DEFAULT_HOST), project=p.full_path, token=s["token"])

    def project_state(self, p):
        refs = git("--git-dir", p.repo, "for-each-ref", "--format=%(refname) %(objectname)", "refs/merge-requests/")
        return {
            "path_with_namespace": p.full_path, "id": p.id, "visibility": p.visibility, "default_branch": p.default_branch,
            "archived": p.archived, "forked_from": self.projects[p.forked_from].full_path if p.forked_from in self.projects else p.forked_from,
            "namespace": {"full_path": p.namespace.full_path, "kind": p.namespace.kind, "id": p.namespace.id},
            "http_url_to_repo": f"https://{DEFAULT_HOST}/{p.full_path}.git", "branches": self.branches(p),
            "mr_refs": dict(line.split(" ", 1) for line in refs.splitlines() if " " in line),
            "merge_requests": [dict(self.mr_json(m, DEFAULT_HOST, full=True), approved_by=list(m.approved_by)) for m in sorted(p.mrs.values(), key=lambda m: m.iid)],
            "hooks": [self.hook_state(p, h) for h in sorted(p.hooks.values(), key=lambda h: h["id"])],
            "statuses": [self.status_state(p, s) for s in p.statuses],
            "notes": [self.note_state(p, n) for n in sorted(p.notes.values(), key=lambda n: n["id"])],
            "variables": [dict(v) for v in p.variables],
            "members": dict(p.members),
        }


# ---- the control API's GitLab part ----

def _wait(jobs, req):
    """Waits for the deliveries a change caused (unless ?wait=0), so Gitberth has heard of it when the call returns."""
    if req.query.get("wait") in ("0", "false") or not jobs:
        return []
    done, _ = wait_futures(jobs, timeout=30)
    return [j.result() for j in done if j.result() is not None]


def _summary(deliveries):
    return [{"event": d["event"], "action": d["action"], "hook_id": d["hook_id"], "url": d["url"], "status": d["status"],
             "error": d["error"], "seq": d["seq"]} for d in sorted(deliveries, key=lambda d: d["seq"])]


def _body(req):
    return req.json({}) or {}


def control_routes(gl):
    """The control API's GitLab routes, /__control/gitlab/...: (method, compiled pattern, handler(req, **groups)). A
    project in a path is its path_with_namespace URL-encoded (e2e%2Fteam%2Fapp), or its ID."""
    routes = []

    def route(method, pattern):
        def add(fn):
            routes.append((method, re.compile("^/__control/gitlab" + pattern + "$"), fn))
            return fn
        return add

    def project(ident):
        with gl.lock:
            return gl.project(urllib.parse.unquote(ident))

    def mr_of(p, iid):
        mr = p.mrs.get(int(iid))
        if mr is None:
            raise ApiError(404, f"{p.full_path} has no merge request !{iid}")
        return mr

    P = r"/projects/(?P<ident>[^/]+)"

    @route("POST", "/tokens")
    def c_token(req):
        """{username, scopes, expires_at, kind (personal, project, group, oauth), project or group, access_level, name}"""
        t = gl.add_token(_body(req))
        return {"token": t.value, **t.public()}

    @route("PATCH", "/tokens/(?P<token>[^/]+)")
    def c_token_change(req, token):
        t = gl.tokens.get(token)
        if t is None:
            raise ApiError(404, "No such token")
        b = _body(req)
        if "scopes" in b:
            bad = [s for s in b["scopes"] or [] if s not in SCOPES]
            if bad:
                raise ApiError(400, f"GitLab has no scope {', '.join(bad)}")
            t.scopes = list(b["scopes"] or [])
        if "revoked" in b:
            t.revoked = bool(b["revoked"])
        if "expires_at" in b:
            t.expires_at = b["expires_at"]
        return t.public()

    @route("GET", "/tokens")
    def c_tokens(req):
        return {"tokens": [t.public() for t in gl.tokens.values()]}

    @route("POST", "/members")
    def c_member(req):
        """{target: a project's path or "group:<path>", username, access_level: guest, reporter, developer, maintainer, owner}"""
        b = _body(req)
        return gl.add_member(b["target"], b["username"], b.get("access_level") or "developer")

    @route("GET", "/options")
    def c_options(req):
        return dict(gl.options, limits=dict(gl.throttle.limits))

    @route("PUT", "/options")
    def c_set_options(req):
        """{self_endpoint: false (a GitLab older than 15.5), webhook_timeout: seconds, authenticated_limit and
        unauthenticated_limit: requests a minute (null puts the default back)}"""
        b = _body(req)
        with gl.lock:
            for k in ("self_endpoint", "webhook_timeout"):
                if k in b:
                    gl.options[k] = b[k] if b[k] is not None else {"self_endpoint": True, "webhook_timeout": 10}[k]
            for k, which in (("authenticated_limit", "user"), ("unauthenticated_limit", "ip")):
                if k in b:
                    gl.throttle.limits[which] = int(b[k]) if b[k] is not None else Throttle.DEFAULTS[which]
        return c_options(req)

    @route("GET", "/rate-limits")
    def c_rate_limits(req):
        return {"windows": gl.throttle.state(), "limits": dict(gl.throttle.limits)}

    @route("DELETE", "/rate-limits")
    def c_reset_rate_limits(req):
        gl.throttle.clear()
        return {"ok": True}

    @route("POST", "/projects")
    def c_create(req):
        """{path (with its namespace: e2e/team/app), owner, template, files, visibility, branch}"""
        b = _body(req)
        files = {k: (v if v is None else str(v)) for k, v in (b.get("files") or {}).items()}
        p = gl.create_project(b["path"], b.get("owner") or "e2e-bot", files=files, template=b.get("template"),
                              visibility=b.get("visibility") or "public", branch=b.get("branch") or "main",
                              message=b.get("message") or "Initial commit")
        with gl.lock:
            return gl.project_state(p)

    @route("GET", P)
    def c_project(req, ident):
        p = project(ident)
        with gl.lock:
            return gl.project_state(p)

    @route("PATCH", P)
    def c_project_change(req, ident):
        """{archived, visibility, default_branch}: someone changes the project's settings."""
        p = project(ident)
        b = _body(req)
        with gl.lock:
            if "archived" in b:
                p.archived = bool(b["archived"])
            if "visibility" in b:
                if b["visibility"] not in VISIBILITY:
                    raise ApiError(400, f"visibility is one of {', '.join(VISIBILITY)}")
                p.visibility = b["visibility"]
            if b.get("default_branch"):
                if not gl.branch_sha(p, b["default_branch"]):
                    raise ApiError(404, f"No branch {b['default_branch']}")
                p.default_branch = b["default_branch"]
            return gl.project_state(p)

    @route("DELETE", P)
    def c_delete(req, ident):
        p = project(ident)
        gl.delete_project(p)
        return {"deleted": p.full_path}

    @route("POST", P + "/commits")
    def c_commit(req, ident):
        """{branch, files ({path: text, None to delete, {"symlink": target}}), message, base, force, author {name, email},
        user (who pushes)}"""
        p = project(ident)
        b = _body(req)
        files = {k: (v if v is None or isinstance(v, dict) and "symlink" in v else str(v)) for k, v in (b.get("files") or {}).items()}
        result, jobs = gl.commit(p, b.get("branch") or p.default_branch, files, b.get("message") or "A change", b.get("user") or "e2e-bot",
                                 base=b.get("base"), force=bool(b.get("force")), author=b.get("author"))
        result["deliveries"] = _summary(_wait(jobs, req))
        return result

    @route("POST", P + "/branches")
    def c_branch(req, ident):
        b = _body(req)
        result, jobs = gl.create_branch(project(ident), b["name"], b.get("from"), b.get("user") or "e2e-bot")
        result["deliveries"] = _summary(_wait(jobs, req))
        return result

    @route("DELETE", P + "/branches")
    def c_delete_branch(req, ident):
        name = req.query.get("name") or _body(req).get("name")
        result, jobs = gl.delete_branch(project(ident), name, req.query.get("user") or "e2e-bot")
        result["deliveries"] = _summary(_wait(jobs, req))
        return result

    @route("POST", P + "/forks")
    def c_fork(req, ident):
        """{namespace (the fork's: a user's or a group), owner}"""
        b = _body(req)
        owner = b.get("owner") or "e2e-forker"
        f = gl.fork(project(ident), b.get("namespace") or owner, owner)
        with gl.lock:
            return gl.project_state(f)

    @route("POST", P + "/merge_requests")
    def c_open_mr(req, ident):
        """{source_branch, target_branch, title, description, draft, source_project (a fork's path), author}"""
        p = project(ident)
        b = _body(req)
        with gl.lock:
            source = gl.project(b["source_project"]) if b.get("source_project") else None
        mr, jobs = gl.open_mr(p, b["source_branch"], b.get("target_branch"), b.get("title"), b.get("description"), bool(b.get("draft")),
                              source, b.get("author") or (source.creator if source else "e2e-bot"))
        deliveries = _wait(jobs, req)
        with gl.lock:
            return dict(gl.mr_json(mr, DEFAULT_HOST, full=True), deliveries=_summary(deliveries))

    @route("PUT", P + r"/merge_requests/(?P<iid>\d+)")
    def c_update_mr(req, ident, iid):
        """{title, description, state_event (close, reopen), draft (true or false), user}"""
        p = project(ident)
        b = _body(req)
        mr, jobs = gl.update_mr(p, mr_of(p, iid), b.get("title"), b.get("description"), b.get("state_event"), b.get("draft"),
                                b.get("user") or "e2e-bot")
        deliveries = _wait(jobs, req)
        with gl.lock:
            return dict(gl.mr_json(mr, DEFAULT_HOST, full=True), deliveries=_summary(deliveries))

    @route("POST", P + r"/merge_requests/(?P<iid>\d+)/merge")
    def c_merge_mr(req, ident, iid):
        """{squash, should_remove_source_branch, user}"""
        p = project(ident)
        b = _body(req)
        sha, jobs = gl.merge_mr(p, mr_of(p, iid), bool(b.get("squash")), bool(b.get("should_remove_source_branch")), b.get("user") or "e2e-bot")
        return {"sha": sha, "merged": True, "deliveries": _summary(_wait(jobs, req))}

    @route("POST", P + r"/merge_requests/(?P<iid>\d+)/(?P<what>approve|unapprove)")
    def c_approve_mr(req, ident, iid, what):
        p = project(ident)
        mr, jobs = gl.approve_mr(p, mr_of(p, iid), _body(req).get("user") or "e2e-reviewer", approve=what == "approve")
        return {"approved_by": list(mr.approved_by), "deliveries": _summary(_wait(jobs, req))}

    @route("POST", P + r"/merge_requests/(?P<iid>\d+)/notes")
    def c_add_note(req, ident, iid):
        """Someone comments on the merge request: {body, author}."""
        p = project(ident)
        b = _body(req)
        with gl.lock:
            n = gl.add_note(p, mr_of(p, iid), b["body"], b.get("author") or "e2e-reviewer")
            return gl.note_state(p, n)

    @route("DELETE", P + r"/notes/(?P<note>\d+)")
    def c_delete_note(req, ident, note):
        """Someone deletes a note (Gitberth's comment, say) in GitLab."""
        p = project(ident)
        with gl.lock:
            if p.notes.pop(int(note), None) is None:
                raise ApiError(404, f"No note {note}")
        return {"deleted": int(note)}

    @route("PUT", P + "/variables")
    def c_variables(req, ident):
        """[{key, value (null removes it), environment_scope, masked, protected, raw, hidden, variable_type}]"""
        b = req.json([]) or []
        if not isinstance(b, list):
            raise ApiError(400, "a list of variables")
        try:
            return {"variables": gl.set_variables(project(ident), b)}
        except GitLabError as e:
            raise ApiError(e.status, json.dumps(e.body))

    @route("POST", P + "/members")
    def c_project_member(req, ident):
        b = _body(req)
        return gl.add_member(project(ident).full_path, b["username"], b.get("access_level") or "developer")

    @route("POST", P + "/hooks")
    def c_seed_hook(req, ident):
        """A hook someone made by hand in GitLab: kind "foreign" (another service's, which Gitberth must leave alone) or
        "pasted" (Gitberth's own, from what its Settings shows). {url, token, push_events, merge_requests_events,
        enable_ssl_verification, kind}"""
        p = project(ident)
        b = _body(req)
        kind = b.get("kind") or "foreign"
        if kind not in ("foreign", "pasted"):
            raise ApiError(400, "kind is foreign or pasted")
        events = {e: bool(b[e]) for e in HOOK_EVENTS if e in b}
        with gl.lock:
            h = gl.new_hook(p, b["url"], b.get("token"), kind, b.get("instance") or DEFAULT_HOST, events=events,
                            enable_ssl_verification=b.get("enable_ssl_verification", True))
            return gl.hook_state(p, h)

    @route("GET", "/statuses")
    def c_statuses(req):
        p = project(req.query["project"])
        with gl.lock:
            items = [gl.status_state(p, s) for s in p.statuses]
        if req.query.get("sha"):
            items = [s for s in items if s["sha"].startswith(req.query["sha"])]
        if req.query.get("name"):
            items = [s for s in items if s["name"] == req.query["name"]]
        return {"statuses": items}

    @route("GET", "/notes")
    def c_notes(req):
        p = project(req.query["project"])
        with gl.lock:
            items = [gl.note_state(p, n) for n in sorted(p.notes.values(), key=lambda n: n["id"])]
        if req.query.get("mr"):
            items = [n for n in items if n["mr"] == int(req.query["mr"])]
        if req.query.get("system") in ("0", "false"):
            items = [n for n in items if not n["system"]]
        return {"notes": items}

    @route("GET", "/hooks")
    def c_hooks(req):
        p = project(req.query["project"])
        with gl.lock:
            return {"hooks": [gl.hook_state(p, h) for h in sorted(p.hooks.values(), key=lambda h: h["id"])]}

    @route("GET", "/deliveries")
    def c_deliveries(req):
        since = int(req.query.get("since") or 0)
        with gl.lock:
            items = [d for d in gl.deliveries if d["seq"] > since]
        if req.query.get("project"):
            items = [d for d in items if d["repo"] == urllib.parse.unquote(req.query["project"]).lower()]
        if req.query.get("event"):
            items = [d for d in items if d["event"] == req.query["event"]]
        return {"deliveries": sorted(items, key=lambda d: d["seq"])}

    def resend(p, delivery_id):
        """GitLab's Resend Request: the delivery again, with the same body, Idempotency-Key and X-Gitlab-Event-UUID, through
        its hook as it is now (the hook's token now); refused, as GitLab refuses it, when the hook's URL has changed."""
        with gl.lock:
            old = next((d for d in gl.deliveries if d["id"] == delivery_id and d["repo"] == p.key), None)
            if old is None:
                raise ApiError(404, f"{p.full_path} has no delivery {delivery_id}")
            hook = p.hooks.get(old["hook_id"]) if old["hook_id"] else None
            if hook is None or hook["url"] != old["url"]:
                raise ApiError(422, "The hook's URL has changed (or the hook is gone): GitLab can't resend the request")
        return gl.deliver(p, hook, old["event"], old["payload"], via_tunnel=old["via_tunnel"], event_uuid=old["event_uuid"],
                          idempotency_key=old["id"])

    @route("POST", "/deliveries")
    def c_send_delivery(req):
        """Any delivery: GitLab's own for an event (push: the branch's last commit pushed; merge_request: the merge
        request `mr` with `action`, update by default), or one GitLab wouldn't send (another token or none, a raw body,
        other headers, another sender IP, a GET). It goes through hook_id, else Gitberth's hook (the newest made through
        the API, else the newest made by hand; never another service's), or with url and no hook through none. It goes
        where the URL leads, through Cloudflare and the tunnel, unless via_tunnel is false (straight to Gitberth).
        {"project", "resend": <a delivery's id>} sends that delivery again, as GitLab's Resend Request does."""
        b = _body(req)
        with gl.lock:
            p = gl.project(b["project"])
        if b.get("resend"):
            return resend(p, b["resend"])
        via_tunnel = b.get("via_tunnel", True) is not False
        if via_tunnel and "cf_connecting_ip" in b and b["cf_connecting_ip"] is None:
            raise ApiError(400, "Through Cloudflare a delivery always has CF-Connecting-IP (the sender's address); "
                                "send it with via_tunnel=false to leave it out")
        # push or merge_request; GitLab's header names ("Push Hook") are taken too.
        event = {v: k for k, v in EVENT_HEADERS.items()}.get(b.get("event"), b.get("event") or "push")
        with gl.lock:
            if b.get("hook_id"):
                hook = p.hooks.get(int(b["hook_id"]))
                if hook is None:
                    raise ApiError(404, f"{p.full_path} has no hook {b['hook_id']}")
            else:
                hook = gl.gitberth_hook(p)
            payload = b.get("payload")
            if payload is None and event == "push":
                branch = b.get("branch") or p.default_branch
                after = gl.branch_sha(p, branch)
                if after is None:
                    raise ApiError(404, f"No branch {branch}")
                parent = gl.resolve(p, f"{after}^")
                payload = gl.push_payload(p, branch, parent or None, after, b.get("user") or "e2e-bot", host=(hook or {}).get("instance") or DEFAULT_HOST)
                if parent is None:
                    payload["before"] = ZERO
            elif payload is None and event == "merge_request":
                if not b.get("mr") and not p.mrs:
                    raise ApiError(404, f"{p.full_path} has no merge request to send a Merge Request Hook about")
                mr = mr_of(p, b.get("mr") or max(p.mrs))
                payload = gl.mr_payload(p, mr, b.get("action") or "update", b.get("user") or "e2e-bot", {},
                                        host=(hook or {}).get("instance") or DEFAULT_HOST)
        if hook is None and not b.get("url"):
            raise ApiError(409, f"{p.full_path} has no hook of Gitberth's yet (made through the API, or by hand); pass hook_id, "
                                "or url to send one anyway")
        return gl.deliver(p, hook, event, payload, token=b.get("token"), cf_connecting_ip=b.get("cf_connecting_ip", "default"),
                          url=b.get("url"), raw_body=b.get("raw_body"), extra_headers=b.get("headers"), method=b.get("method") or "POST",
                          via_tunnel=via_tunnel, event_header=b.get("event_header"))

    return routes
