"""The stand-ins' server. HTTPS on 443 answers for github.com (git), api.github.com and api.cloudflare.com, chosen by
Host; plain HTTP on 8099 is the control API (/__control/...) the scenarios and the fake cloudflared use. Every call to
443 is recorded, and failures can be injected per host, method and path.

GitHub's webhook deliveries go through the Cloudflare stand-in's edge (DNS, Access) to the fake cloudflared, which
routes them by the tunnel ingress it runs: each of its polls tells the stand-in where it is and which version it runs.

State that has to outlive a restart (the test CA, the admin password, the Cloudflare account and tunnel IDs, the tunnel
configuration's version) is in /state; the CA certificate is copied to /trust for the CA bundles. Everything else is in
memory and goes on reset."""
import base64
import email
import email.policy
import json
import os
import re
import secrets
import signal
import socket
import ssl
import sys
import threading
import time
import traceback
import uuid
from concurrent.futures import wait as wait_futures
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import certs  # noqa: E402
from cf import Cloudflare, ingress_problem  # noqa: E402
from common import ApiError, Failures, Recorder, Request, Response, injected_response, injected_status, log, log_exception, send  # noqa: E402
from gh import GitHub  # noqa: E402

STATE = os.environ.get("STANDIN_STATE", "/state")
TRUST = os.environ.get("STANDIN_TRUST", "/trust")
TEMPLATES = os.environ.get("STANDIN_TEMPLATES", "/app/apps")
REPOS = os.environ.get("STANDIN_REPOS", "/var/lib/standin/git")
GITBERTH_URL = os.environ.get("E2E_GITBERTH_URL", "http://gitberth:8080")


def load_rig():
    """The rig's lasting secrets, made once per state volume: never committed, never printed."""
    path = os.path.join(STATE, "rig.json")
    rig = {}
    if os.path.exists(path):
        with open(path) as f:
            rig = json.load(f)
    changed = False
    defaults = {
        "admin_password": lambda: secrets.token_urlsafe(18),
        "cf_account_id": lambda: secrets.token_hex(16),
        "cf_tunnel_id": lambda: str(uuid.uuid4()),
        "cf_tunnel_secret": lambda: base64.b64encode(secrets.token_bytes(32)).decode(),
    }
    for k, make in defaults.items():
        if not rig.get(k):
            rig[k] = make()
            changed = True
    if changed:
        os.makedirs(STATE, exist_ok=True)
        with open(path + ".tmp", "w") as f:
            json.dump(rig, f)
        os.replace(path + ".tmp", path)
        os.chmod(path, 0o600)
    with open(os.path.join(STATE, "admin-password"), "w") as f:
        f.write(rig["admin_password"])
    os.chmod(os.path.join(STATE, "admin-password"), 0o600)
    return rig


def cloudflared_token(rig):
    """A tunnel token as Cloudflare hands it out: base64 of {"a": account, "t": tunnel, "s": secret}."""
    raw = json.dumps({"a": rig["cf_account_id"], "t": rig["cf_tunnel_id"], "s": rig["cf_tunnel_secret"]}, separators=(",", ":"))
    return base64.b64encode(raw.encode()).decode()


RIG = load_rig()
REC = Recorder()
FAILS = Failures()
CF = Cloudflare(RIG["cf_account_id"], RIG["cf_tunnel_id"], STATE)
GH = GitHub(REPOS, TEMPLATES, REC, GITBERTH_URL, edge=CF.edge)


# ---- notifications: where Gitberth sends them ----

# Discord's and Slack's webhooks, an ntfy server and any webhook: each POST is recorded (kind "notify") and answered as
# the service does, or with what a scenario set (/__control/notify/answer).
NOTIFY_HOSTS = {"discord.com": 204, "hooks.slack.com": 200, "ntfy.e2e.test": 200, "notify.e2e.test": 200}
NOTIFY_ANSWERS = {}  # host -> [status, times left or None]
NOTIFY_LOCK = threading.Lock()


def notify_answer(req):
    with NOTIFY_LOCK:
        answer = NOTIFY_ANSWERS.get(req.host)
        if answer:
            status, left = answer
            if left is not None:
                answer[1] = left - 1
                if answer[1] <= 0:
                    NOTIFY_ANSWERS.pop(req.host, None)
            return Response.json(status, {"message": "set by the scenario"})
    status = NOTIFY_HOSTS[req.host]
    if req.method != "POST":
        return Response.json(405, {"message": "405: Method Not Allowed"})
    if status == 204:
        return Response(204)
    return Response.text(200, "ok") if req.host == "hooks.slack.com" else Response.json(200, {"id": secrets.token_hex(6)})


class Mailbox:
    """The rig's mail server (smtp.e2e.test:2525): plain SMTP, any sign-in taken (and kept, so a scenario sees it came),
    every mail kept with its envelope and text."""

    def __init__(self):
        self.lock = threading.Lock()
        self.mails = []

    def add(self, mail):
        with self.lock:
            mail["seq"] = REC.next_seq()
            self.mails.append(mail)

    def since(self, seq=0):
        with self.lock:
            return [m for m in self.mails if m["seq"] > seq]

    def clear(self):
        with self.lock:
            self.mails.clear()


MAIL = Mailbox()


def smtp_session(conn, addr):
    conn.settimeout(30)
    f = conn.makefile("rwb")

    def say(line):
        f.write((line + "\r\n").encode())
        f.flush()

    mail = {"from": None, "to": [], "auth": None, "client": addr[0]}
    try:
        say("220 smtp.e2e.test ESMTP gbe2e")
        while True:
            line = f.readline()
            if not line:
                return
            text = line.decode("utf-8", "replace").rstrip("\r\n")
            verb = text.split(" ", 1)[0].upper()
            if verb in ("EHLO", "HELO"):
                f.write(b"250-smtp.e2e.test\r\n250-AUTH PLAIN LOGIN\r\n250 8BITMIME\r\n")
                f.flush()
            elif verb == "AUTH":
                parts = text.split(" ")
                mech = parts[1].upper() if len(parts) > 1 else ""
                if mech == "PLAIN":
                    if len(parts) >= 3:
                        creds = parts[2]
                    else:
                        say("334 ")
                        creds = f.readline().strip().decode()
                    raw = base64.b64decode(creds).split(b"\0")
                    mail["auth"] = raw[1].decode() if len(raw) > 1 else None
                    say("235 2.7.0 Authentication successful")
                elif mech == "LOGIN":
                    if len(parts) >= 3:
                        user = base64.b64decode(parts[2]).decode()
                    else:
                        say("334 VXNlcm5hbWU6")
                        user = base64.b64decode(f.readline().strip()).decode()
                    say("334 UGFzc3dvcmQ6")
                    f.readline()
                    mail["auth"] = user
                    say("235 2.7.0 Authentication successful")
                else:
                    say("504 5.5.4 Unrecognized authentication type")
            elif verb == "MAIL":
                mail["from"] = text.split(":", 1)[1].strip().strip("<>").split(">")[0]
                say("250 2.1.0 OK")
            elif verb == "RCPT":
                mail["to"].append(text.split(":", 1)[1].strip().strip("<>").split(">")[0])
                say("250 2.1.5 OK")
            elif verb == "DATA":
                say("354 End data with <CR><LF>.<CR><LF>")
                data = []
                while True:
                    l = f.readline()
                    if not l or l in (b".\r\n", b".\n"):
                        break
                    data.append(l[1:] if l.startswith(b"..") else l)
                mail["data"] = b"".join(data).decode("utf-8", "replace")
                # As a mail program shows it: the subject and the text, decoded (base64, quoted-printable, encoded words).
                try:
                    parsed = email.message_from_string(mail["data"], policy=email.policy.default)
                    mail["subject"] = str(parsed["subject"] or "")
                    body = parsed.get_body(preferencelist=("plain",))
                    mail["text"] = body.get_content() if body is not None else ""
                except Exception as e:  # noqa: BLE001 - a mail the rig can't read is still kept, with why
                    mail["subject"], mail["text"] = None, f"(unreadable: {e})"
                MAIL.add(dict(mail))
                mail = {"from": None, "to": [], "auth": mail["auth"], "client": addr[0]}
                say("250 2.0.0 OK: queued")
            elif verb == "RSET":
                mail = {"from": None, "to": [], "auth": mail["auth"], "client": addr[0]}
                say("250 2.0.0 OK")
            elif verb == "NOOP":
                say("250 2.0.0 OK")
            elif verb == "QUIT":
                say("221 2.0.0 Bye")
                return
            else:
                say("502 5.5.2 Command not recognized")
    except (OSError, ValueError):
        return
    finally:
        try:
            conn.close()
        except OSError:
            pass


def serve_smtp(port=2525):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("0.0.0.0", port))
    s.listen(16)
    while True:
        conn, addr = s.accept()
        threading.Thread(target=smtp_session, args=(conn, addr), daemon=True).start()


# ---- the HTTPS side: what Gitberth calls ----

def handle_api(req):
    kind = "git" if req.host in ("github.com", "www.github.com") else "github" if req.host == "api.github.com" \
        else "cloudflare" if req.host == "api.cloudflare.com" else "notify" if req.host in NOTIFY_HOSTS else "other"
    call = REC.start(req, kind)
    try:
        # Which repo the call is about (and for git, what it asked for), before anything answers it: a call an injected
        # failure answers, or that GitHub refuses, is still found by its repo.
        if kind in ("git", "github"):
            GH.describe(req, call)
        rule = FAILS.match(req)
        if rule:
            call["injected"] = rule["id"]
            if rule["delay_ms"]:
                time.sleep(rule["delay_ms"] / 1000)
            if rule["action"] == "close":
                call["status"] = 0
                return None
            if injected_status(rule, req.host):
                resp = injected_response(rule, req.host, GH.rate_state(req) if kind == "github" else None)
                call["status"] = resp.status
                return resp
        if kind == "git":
            resp = GH.git_http(req, call)
        elif kind == "github":
            resp = GH.api(req, call)
        elif kind == "cloudflare":
            resp = CF.api(req, call)
        elif kind == "notify":
            resp = notify_answer(req)
        else:
            call["unrouted"] = True
            log(f"no stand-in route: {req.method} {req.host}{req.path}")
            resp = Response.text(404, f"The rig has no stand-in for {req.host}")
        call["status"] = resp.status
        return resp
    except Exception:
        log_exception(f"{req.method} {req.host}{req.path}")
        call["status"] = 500
        return Response.json(500, {"message": "The stand-in failed: " + traceback.format_exc().splitlines()[-1]})


# ---- the control API: what the scenarios do and read ----

def body_of(req):
    return req.json({}) or {}


def wait_for(jobs, req):
    """Waits for the deliveries a change caused (unless ?wait=0), so Gitberth has heard of it when the call returns."""
    if req.query.get("wait") in ("0", "false"):
        return []
    done, _ = wait_futures(jobs, timeout=30)
    return [j.result() for j in done if j.result() is not None]


def summary(deliveries):
    return [{"event": d["event"], "action": d["action"], "hook_id": d["hook_id"], "url": d["url"], "status": d["status"], "error": d["error"],
             "seq": d["seq"]} for d in deliveries]


CONTROL = []


def route(method, pattern):
    def add(fn):
        CONTROL.append((method, re.compile("^/__control" + pattern + "$"), fn))
        return fn
    return add


@route("GET", "/health")
def c_health(req):
    return {"ok": True, "seq": REC.seq}


@route("GET", "/seq")
def c_seq(req):
    return {"seq": REC.seq}


@route("POST", "/reset")
def c_reset(req):
    GH.reset()
    CF.reset()
    FAILS.remove()
    REC.clear()
    MAIL.clear()
    with NOTIFY_LOCK:
        NOTIFY_ANSWERS.clear()
    return {"ok": True, "seq": REC.seq, "tunnel_version": CF.version}


@route("POST", "/notify/answer")
def c_notify_answer(req):
    """How a notification host answers from now on: {host, status, times} (times: that many calls, else until reset); status null puts it back."""
    b = body_of(req)
    host = b["host"]
    if host not in NOTIFY_HOSTS:
        raise ApiError(400, f"{host} isn't a notification host: {', '.join(NOTIFY_HOSTS)}")
    with NOTIFY_LOCK:
        if b.get("status") is None:
            NOTIFY_ANSWERS.pop(host, None)
        else:
            NOTIFY_ANSWERS[host] = [int(b["status"]), b.get("times")]
    return {"ok": True}


@route("GET", "/notify/mail")
def c_notify_mail(req):
    return {"mails": MAIL.since(int(req.query.get("since") or 0)), "seq": REC.seq}


def ls_remote_attempt(call):
    """Whether a git call is one look at a repo with `git ls-remote` (a watcher pass, or New app's Check): its ls-refs,
    whatever the answer; or its first request, GET .../info/refs?service=git-upload-pack, when that wasn't answered 200
    (GitHub's 401 or 404, an injected failure), since a look refused there gets no further. A clone refused at its first
    request counts too: that request is the same."""
    g = call.get("git") or {}
    if g.get("ls_remote"):
        return True
    return (call["method"] == "GET" and g.get("service") == "git-upload-pack" and (g.get("path") or "").endswith("/info/refs")
            and call.get("status") is not None and call["status"] != 200)


@route("GET", "/calls")
def c_calls(req):
    """Recorded calls: since a marker, by host, method, path (a regex), kind, repo; ls_remote=1 for the ls-refs of
    `git ls-remote`, ls_remote_attempts=1 for every look with it (refused ones too: ls_remote_attempt), unrouted=1 for
    those no stand-in route answered."""
    q = req.query
    calls = REC.calls(since=int(q.get("since") or 0), host=q.get("host"), method=q.get("method"), path=q.get("path"), kind=q.get("kind"))
    if q.get("repo"):
        calls = [c for c in calls if c.get("repo") == q["repo"].lower()]
    if q.get("ls_remote") in ("1", "true"):
        calls = [c for c in calls if (c.get("git") or {}).get("ls_remote")]
    if q.get("ls_remote_attempts") in ("1", "true"):
        calls = [c for c in calls if ls_remote_attempt(c)]
    if q.get("unrouted") in ("1", "true"):
        calls = [c for c in calls if c.get("unrouted")]
    return {"calls": calls, "seq": REC.seq}


@route("GET", "/failures")
def c_failures(req):
    return {"failures": FAILS.list()}


@route("POST", "/failures")
def c_add_failure(req):
    try:
        return FAILS.add(body_of(req))
    except re.error as e:
        raise ApiError(400, f"Bad path pattern: {e}")
    except ValueError as e:
        raise ApiError(400, str(e))


@route("DELETE", "/failures")
def c_clear_failures(req):
    FAILS.remove()
    return {"ok": True}


@route("DELETE", r"/failures/(?P<id>\d+)")
def c_remove_failure(req, id):
    FAILS.remove(int(id))
    return {"ok": True}


# GitHub

@route("POST", "/github/tokens")
def c_gh_token(req):
    t = GH.add_token(body_of(req))
    return {"token": t.value, **t.public()}


@route("DELETE", "/github/tokens/(?P<token>[^/]+)")
def c_gh_revoke(req, token):
    t = GH.tokens.get(token)
    if t is None:
        raise ApiError(404, "No such token")
    t.revoked = True
    return t.public()


@route("PATCH", "/github/tokens/(?P<token>[^/]+)")
def c_gh_token_change(req, token):
    t = GH.tokens.get(token)
    if t is None:
        raise ApiError(404, "No such token")
    b = body_of(req)
    if "permissions" in b:
        t.permissions.update(b["permissions"])
    if "repos" in b:
        t.repos = {r.lower() for r in b["repos"]} if b["repos"] else None
    if "revoked" in b:
        t.revoked = bool(b["revoked"])
    if "expires" in b:
        t.expires = b["expires"]
    return t.public()


@route("GET", "/github/tokens")
def c_gh_tokens(req):
    return {"tokens": [t.public() for t in GH.tokens.values()]}


@route("GET", "/github/rate-limits")
def c_gh_rate_limits(req):
    """The primary rate limit's windows: per address without a token, per user with one."""
    return {"windows": GH.limits.state()}


@route("DELETE", "/github/rate-limits")
def c_gh_reset_rate_limits(req):
    """Every window starts again (a check that used up a limit puts it back for the scenarios after it)."""
    GH.limits.clear()
    return {"ok": True}


@route("POST", "/github/repos")
def c_gh_create(req):
    b = body_of(req)
    files = {k: (v if v is None else str(v)) for k, v in (b.get("files") or {}).items()}
    repo = GH.create_repo(b["owner"], b["name"], files=files, template=b.get("template"), private=b.get("private", False),
                          branch=b.get("branch") or "main", message=b.get("message") or "Initial commit")
    with GH.lock:
        return GH.repo_state(repo)


def gh_repo(o, r):
    with GH.lock:
        return GH.repo(o, r)


@route("GET", "/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)")
def c_gh_repo(req, o, r):
    with GH.lock:
        return GH.repo_state(GH.repo(o, r))


@route("DELETE", "/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)")
def c_gh_delete(req, o, r):
    GH.delete_repo(gh_repo(o, r))
    return {"deleted": f"{o}/{r}"}


@route("POST", "/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/commits")
def c_gh_commit(req, o, r):
    b = body_of(req)
    files = {k: (v if v is None else str(v)) for k, v in (b.get("files") or {}).items()}
    result, jobs = GH.commit(gh_repo(o, r), b.get("branch") or gh_repo(o, r).default_branch, files, b.get("message") or "A change",
                             base=b.get("base"), force=bool(b.get("force")), author=b.get("author"), pusher=b.get("pusher") or "e2e-bot")
    result["deliveries"] = summary(wait_for(jobs, req))
    return result


@route("POST", "/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/branches")
def c_gh_branch(req, o, r):
    b = body_of(req)
    result, jobs = GH.create_branch(gh_repo(o, r), b["name"], b.get("from"), pusher=b.get("pusher") or "e2e-bot")
    result["deliveries"] = summary(wait_for(jobs, req))
    return result


@route("DELETE", "/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/branches")
def c_gh_delete_branch(req, o, r):
    name = req.query.get("name") or body_of(req).get("name")
    result, jobs = GH.delete_branch(gh_repo(o, r), name)
    result["deliveries"] = summary(wait_for(jobs, req))
    return result


@route("POST", "/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/forks")
def c_gh_fork(req, o, r):
    fork = GH.fork(gh_repo(o, r), body_of(req)["owner"])
    with GH.lock:
        return GH.repo_state(fork)


@route("POST", "/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/pulls")
def c_gh_open_pull(req, o, r):
    b = body_of(req)
    repo = gh_repo(o, r)
    pr, jobs = GH.open_pull(repo, b["head"], b.get("base"), b.get("title"), b.get("body"), b.get("draft", False), b.get("user"))
    deliveries = wait_for(jobs, req)
    with GH.lock:
        return dict(GH.pull_json(repo, pr, full=True), deliveries=summary(deliveries))


@route("PATCH", r"/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/pulls/(?P<n>\d+)")
def c_gh_update_pull(req, o, r, n):
    b = body_of(req)
    repo = gh_repo(o, r)
    pr = repo.pulls.get(int(n))
    if pr is None:
        raise ApiError(404, f"No pull request #{n}")
    pr, jobs = GH.update_pull(repo, pr, b.get("title"), b.get("body"), b.get("state"))
    deliveries = wait_for(jobs, req)
    with GH.lock:
        return dict(GH.pull_json(repo, pr, full=True), deliveries=summary(deliveries))


@route("POST", r"/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/pulls/(?P<n>\d+)/merge")
def c_gh_merge(req, o, r, n):
    b = body_of(req)
    repo = gh_repo(o, r)
    pr = repo.pulls.get(int(n))
    if pr is None:
        raise ApiError(404, f"No pull request #{n}")
    sha, jobs = GH.merge_pull(repo, pr, b.get("method") or "merge", bool(b.get("delete_branch")))
    return {"sha": sha, "merged": True, "deliveries": summary(wait_for(jobs, req))}


@route("PUT", "/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/variables")
def c_gh_variables(req, o, r):
    repo = gh_repo(o, r)
    with GH.lock:
        for name, value in body_of(req).items():
            if value is None:
                repo.variables.pop(name.upper(), None)
            else:
                repo.variables[name.upper()] = {"name": name.upper(), "value": str(value), "created_at": "2026-01-01T00:00:00Z",
                                                "updated_at": "2026-01-01T00:00:00Z"}
        return {"variables": sorted(repo.variables.values(), key=lambda v: v["name"])}


@route("DELETE", r"/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/comments/(?P<id>\d+)")
def c_gh_delete_comment(req, o, r, id):
    repo = gh_repo(o, r)
    with GH.lock:
        if repo.comments.pop(int(id), None) is None:
            raise ApiError(404, f"No comment {id}")
    return {"deleted": int(id)}


@route("POST", "/github/repos/(?P<o>[^/]+)/(?P<r>[^/]+)/hooks")
def c_gh_seed_hook(req, o, r):
    """A hook someone made by hand in GitHub: kind "foreign" (another service's, which Gitberth must never touch) or
    "pasted" (Gitberth's own, from what its Settings shows to paste). Like every hook, its deliveries go where its URL
    leads. GitHub's rule against two hooks with the same URL and an event in common holds here too (422)."""
    b = body_of(req)
    kind = b.get("kind") or ("pasted" if b.get("deliver") else "foreign")
    if kind not in ("foreign", "pasted"):
        raise ApiError(400, "kind is foreign or pasted")
    repo = gh_repo(o, r)
    with GH.lock:
        h = GH.add_hook(repo, {"url": b["url"], "content_type": b.get("content_type") or "json", "secret": b.get("secret")},
                        b.get("events") or ["push"], b.get("active", True), kind)
        return dict(GH.hook_json(repo, h, reveal=True), made_by=kind)


@route("GET", "/github/statuses")
def c_gh_statuses(req):
    repo = gh_repo(*req.query["repo"].split("/", 1))
    with GH.lock:
        items = [dict(s) for s in repo.statuses]
    if req.query.get("sha"):
        items = [s for s in items if s["sha"].startswith(req.query["sha"])]
    if req.query.get("context"):
        items = [s for s in items if s["context"] == req.query["context"]]
    return {"statuses": items}


@route("GET", "/github/comments")
def c_gh_comments(req):
    repo = gh_repo(*req.query["repo"].split("/", 1))
    with GH.lock:
        items = [dict(GH.comment_json(repo, c), issue=c["issue"], edits=c["edits"], token=c.get("token")) for c in repo.comments.values()]
    if req.query.get("pr"):
        items = [c for c in items if c["issue"] == int(req.query["pr"])]
    return {"comments": items}


@route("GET", "/github/hooks")
def c_gh_hooks(req):
    repo = gh_repo(*req.query["repo"].split("/", 1))
    with GH.lock:
        return {"hooks": [dict(GH.hook_json(repo, h, reveal=True), made_by=h.get("made_by"), token=h.get("token")) for h in repo.hooks.values()]}


@route("GET", "/github/deliveries")
def c_gh_deliveries(req):
    since = int(req.query.get("since") or 0)
    with GH.lock:
        items = [d for d in GH.deliveries if d["seq"] > since]
    if req.query.get("repo"):
        items = [d for d in items if d["repo"] == req.query["repo"].lower()]
    if req.query.get("event"):
        items = [d for d in items if d["event"] == req.query["event"]]
    return {"deliveries": items}


@route("POST", "/github/deliveries")
def c_gh_send_delivery(req):
    """Any delivery: GitHub's own for an event (signed with the hook's secret), or one GitHub wouldn't send (a wrong
    signature, no signature, another event, another sender IP, a raw body, a GET). It goes through hook_id, else
    Gitberth's hook (the newest made through the API, else the newest pasted by hand; never another service's), or with
    url and secret through none: then it's JSON, as Gitberth's hooks are, unless content_type is "form". It goes where
    the URL leads, through Cloudflare and the tunnel, unless via_tunnel is false (straight to Gitberth)."""
    b = body_of(req)
    repo = gh_repo(*b["repo"].split("/", 1))
    via_tunnel = b.get("via_tunnel", True) is not False
    if via_tunnel and "cf_connecting_ip" in b and b["cf_connecting_ip"] is None:
        raise ApiError(400, "Through Cloudflare a delivery always has CF-Connecting-IP (the sender's address); "
                            "send it with via_tunnel=false to leave it out")
    with GH.lock:
        if b.get("hook_id"):
            hook = repo.hooks.get(int(b["hook_id"]))
            if hook is None:
                raise ApiError(404, f"{repo.full_name} has no hook {b['hook_id']}")
        else:
            hook = GH.gitberth_hook(repo)
        payload = b.get("payload")
        if payload is None and b.get("event") in ("push", "ping", "delete", "create", "pull_request", "issues", "star"):
            payload = {"repository": GH.repo_json(repo), "sender": GH.user_json("e2e-bot")}
            if b["event"] == "push":
                branch = b.get("branch") or repo.default_branch
                sha = GH.branch_sha(repo, branch)
                payload.update({"ref": f"refs/heads/{branch}", "before": sha, "after": sha, "commits": [], "head_commit": None,
                                "created": False, "deleted": False, "forced": False})
    if hook is None and not b.get("url"):
        raise ApiError(409, f"{repo.full_name} has no hook of Gitberth's yet (made through the API, or pasted by hand); "
                            "pass hook_id, or url and secret to send one anyway")
    record = GH.deliver(repo, hook, b.get("event") or "push", payload, signature=b.get("signature"), secret=b.get("secret"),
                        cf_connecting_ip=b.get("cf_connecting_ip", "default"), url=b.get("url"), raw_body=b.get("raw_body"),
                        extra_headers=b.get("headers"), method=b.get("method") or "POST", content_type=b.get("content_type"),
                        via_tunnel=via_tunnel)
    return record


# Cloudflare

@route("GET", "/cloudflare/state")
def c_cf_state(req):
    return CF.state()


@route("GET", "/cloudflare/cloudflared-token")
def c_cf_cloudflared_token(req):
    return Response.text(200, cloudflared_token(RIG))


@route("GET", "/cloudflare/tunnels/(?P<id>[^/]+)/config")
def c_cf_tunnel_config(req, id):
    """The fake cloudflared's poll: the tunnel's configuration. Its query says which version it runs (running) and where
    its edge port is (edge), so Cloudflare's edge can send it traffic; the address is the one it polls from."""
    if id != CF.tunnel_id:
        raise ApiError(404, "No such tunnel")
    if req.query.get("edge"):
        CF.register_connector(req.client, int(req.query["edge"]), int(req.query.get("running") or -1), req.query.get("network"))
    with CF.lock:
        return {"version": CF.version, "config": CF.config}


@route("GET", "/cloudflare/connector")
def c_cf_connector(req):
    """The connector (the fake cloudflared): where it is, the version it runs, whether it still polls."""
    return CF.connector_state()


@route("PUT", "/cloudflare/tunnel/config")
def c_cf_set_config(req):
    """The tunnel's configuration as someone edits it in Cloudflare's dashboard."""
    config = body_of(req).get("config")
    problem = ingress_problem((config or {}).get("ingress"))
    if not isinstance(config, dict) or problem:
        raise ApiError(400, problem or "config must be an object")
    return {"version": CF.set_config(config)}


@route("POST", "/cloudflare/visit")
def c_cf_visit(req):
    """A request to a URL as a visitor makes it, through Cloudflare's edge and the tunnel (see Cloudflare.edge):
    {url, method, headers, body, client_ip, signed_in_as}. signed_in_as is an email address the visitor signed in to
    Cloudflare Access with (an Access application lets them in only when its policies allow it)."""
    b = body_of(req)
    body = b.get("body")
    data = body.encode() if isinstance(body, str) else json.dumps(body).encode() if body is not None else b""
    return CF.edge(b["url"], (b.get("method") or "GET").upper(), b.get("headers") or {}, data,
                   client_ip=b.get("client_ip") or "203.0.113.10", signed_in_as=b.get("signed_in_as"))


@route("POST", "/cloudflare/tokens")
def c_cf_token(req):
    return CF.add_token(body_of(req))


@route("PATCH", "/cloudflare/tokens/(?P<token>[^/]+)")
def c_cf_token_change(req, token):
    t = CF.tokens.get(token)
    if t is None:
        raise ApiError(404, "No such token")
    b = body_of(req)
    for k in ("permissions", "status", "kind"):
        if k in b:
            t[k] = b[k]
    return {k: v for k, v in t.items() if k != "token"}


@route("POST", "/cloudflare/zones")
def c_cf_zone(req):
    """A zone of the account (active unless status says otherwise); total_tls: certificates for names more than one
    level below it too."""
    b = body_of(req)
    return CF.zone_json(CF.add_zone(b["name"], b.get("status") or "active", bool(b.get("total_tls"))))


@route("PUT", "/cloudflare/team")
def c_cf_team(req):
    CF.team = body_of(req).get("name") or None
    return {"team": CF.team}


@route("DELETE", "/cloudflare/team")
def c_cf_no_team(req):
    CF.team = None
    return {"team": None}


@route("PUT", "/cloudflare/options")
def c_cf_options(req):
    b = body_of(req)
    if "list_includes_policies" in b:
        CF.list_includes_policies = bool(b["list_includes_policies"])
    return {"list_includes_policies": CF.list_includes_policies}


@route("POST", "/cloudflare/dns")
def c_cf_dns(req):
    """A DNS record someone made by hand."""
    b = body_of(req)
    with CF.lock:
        zone = next((z for z in CF.zones.values() if z["name"] == b["zone"]), None)
        if zone is None:
            raise ApiError(404, f"No zone {b['zone']}")
        return CF.add_record(zone, b)


@route("DELETE", "/cloudflare/dns/(?P<id>[0-9a-f]{32})")
def c_cf_delete_dns(req, id):
    """Someone deletes a DNS record in the dashboard."""
    with CF.lock:
        if CF.dns.pop(id, None) is None:
            raise ApiError(404, f"No DNS record {id}")
    return {"deleted": id}


@route("POST", "/cloudflare/access/apps")
def c_cf_seed_app(req):
    """An Access application made in Cloudflare's dashboard."""
    with CF.lock:
        return CF.make_app(body_of(req), "control")


@route("DELETE", "/cloudflare/access/apps/(?P<id>[^/]+)")
def c_cf_delete_app(req, id):
    with CF.lock:
        if CF.apps.pop(id, None) is None:
            raise ApiError(404, f"No Access application {id}")
    return {"deleted": id}


def handle_control(req):
    for method, rx, fn in CONTROL:
        m = rx.match(req.path)
        if m and method == req.method:
            try:
                out = fn(req, **m.groupdict())
            except ApiError as e:
                # With what GitHub would have said in detail (its errors list), so a refusal explains itself.
                details = {k: v for k, v in e.extra.items() if not k.startswith("_")}
                return Response.json(e.status, {"error": e.message, **details})
            except KeyError as e:
                return Response.json(400, {"error": f"missing field {e}"})
            except Exception as e:
                log_exception(f"control {req.method} {req.path}")
                return Response.json(500, {"error": f"{type(e).__name__}: {e}"})
            return out if isinstance(out, Response) else Response.json(200, out)
    return Response.json(404, {"error": f"No control route {req.method} {req.path}"})


# ---- servers ----

def make_handler(dispatch, name):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        server_version = "gbe2e-standin"
        sys_version = ""
        # An idle kept-alive connection is closed after this many seconds.
        timeout = 120

        def _serve(self):
            try:
                req = Request(self)
            except Exception:
                log_exception(f"{name}: reading a request")
                self.close_connection = True
                return
            resp = dispatch(req)
            if resp is None:
                self.close_connection = True
                try:
                    self.connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                return
            send(self, resp)

        do_GET = do_POST = do_PUT = do_PATCH = do_DELETE = do_HEAD = _serve

        def log_message(self, fmt, *args):
            if os.environ.get("STANDIN_VERBOSE"):
                log(name, self.address_string(), fmt % args)

    return Handler


class TlsServer(ThreadingHTTPServer):
    """The TLS handshake happens in the request's own thread, so a slow client never holds up the others."""
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address, handler, context):
        self.context = context
        super().__init__(address, handler)

    def finish_request(self, request, client_address):
        request.settimeout(30)
        try:
            tls = self.context.wrap_socket(request, server_side=True)
        except (ssl.SSLError, OSError) as e:
            log(f"TLS handshake with {client_address[0]} failed: {e}")
            return
        try:
            self.RequestHandlerClass(tls, client_address, self)
        finally:
            try:
                tls.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            tls.close()


class PlainServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def main():
    # PID 1 in its container: without a handler, docker stop would wait out its timeout.
    signal.signal(signal.SIGTERM, lambda *_: os._exit(0))
    crt, key = certs.ensure(STATE, TRUST)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(crt, key)
    https = TlsServer(("0.0.0.0", 443), make_handler(handle_api, "https"), context)
    control = PlainServer(("0.0.0.0", 8099), make_handler(handle_control, "control"))
    threading.Thread(target=https.serve_forever, name="https", daemon=True).start()
    threading.Thread(target=serve_smtp, name="smtp", daemon=True).start()
    log("stand-ins up: HTTPS on 443 for github.com, api.github.com, api.cloudflare.com and the notification hosts; SMTP on 2525; "
        f"control on 8099; deliveries go through the tunnel's connector (or straight to {GITBERTH_URL} when a scenario asks)")
    control.serve_forever()


if __name__ == "__main__":
    main()
