"""Clients for the stand-ins' control API: play the people on GitHub and in Cloudflare's dashboard, inject failures, and
read back what Gitberth asked for (every call is recorded with a sequence number; take one with seq() as a marker)."""
import json
import urllib.parse

from . import config
from .net import call, json_call
from .wait import until


# The scenarios' own calls carry this User-Agent, so what Gitberth asked for is told apart from them.
RUNNER_AGENT = "gbe2e-runner"


def rest(base, verb, path, token=None, body=None, scheme="Bearer"):
    """A call to the stand-in's HTTPS side as Gitberth makes it: the runner resolves api.github.com and api.cloudflare.com
    to the stand-in and trusts the rig's CA. Returns (status, headers, parsed JSON or None)."""
    headers = {"User-Agent": RUNNER_AGENT, "Accept": "application/vnd.github+json"}
    if token:
        headers["Authorization"] = f"{scheme} {token}"
    status, hdrs, text = call(verb, base + path, body, headers)
    try:
        return status, hdrs, json.loads(text) if text else None
    except ValueError:
        return status, hdrs, text


class StandIns:
    """What both stand-ins share: the call record, the markers, injected failures, reset."""

    def __init__(self, url=config.STANDIN_URL):
        self.url = url

    def api(self, verb, route, body=None, query=None):
        """One control API call: route is the part after /__control, query a dict (None values left out)."""
        q = {k: v for k, v in (query or {}).items() if v is not None}
        url = f"{self.url}/__control{route}" + (f"?{urllib.parse.urlencode(q)}" if q else "")
        return json_call(verb, url, body)

    def wait_up(self, timeout=60):
        return until(lambda: self.api("GET", "/health"), timeout, "the stand-ins to answer")

    def seq(self):
        """A marker: calls and deliveries after it have a higher seq."""
        return self.api("GET", "/seq")["seq"]

    def calls(self, since=0, host=None, method=None, path=None, kind=None, repo=None, ls_remote=None, ls_remote_attempts=None,
              unrouted=None):
        """Recorded calls to the stand-ins' HTTPS side. kind is git, github or cloudflare; path is a regex. A call is
        found by its repo however it was answered (an injected failure, GitHub's 401 or 404 included). ls_remote: the
        ls-refs of `git ls-remote`; ls_remote_attempts: every look with it, refused ones too; unrouted: the calls no
        stand-in route answered (the scenarios fail on those)."""
        return self.api("GET", "/calls", query={"since": since, "host": host, "method": method, "path": path, "kind": kind, "repo": repo,
                                               "ls_remote": "1" if ls_remote else None,
                                               "ls_remote_attempts": "1" if ls_remote_attempts else None,
                                               "unrouted": "1" if unrouted else None})["calls"]

    def fail(self, path, status=None, host=None, method=None, body=None, headers=None, delay_ms=0, times=None, action=None, query=None,
             rate_limit=None):
        """Injects a failure: requests to host (github.com, api.github.com, api.cloudflare.com; any by default) with the method
        and a path matching the regex get status (with body and headers), or a delay, or (action="close") a dropped
        connection; for `times` calls, or until cleared. Returns the rule's id.

        Without a body the answer comes in the host's own shape: GitHub's {message, documentation_url, status} with its
        x-ratelimit-* headers, Cloudflare's envelope with an error code. rate_limit makes it the service's rate limit
        (status optional): "primary" is GitHub's 403 with x-ratelimit-remaining 0 and the reset time, "secondary" its
        403 with retry-after and the secondary-limit message (status=429 for that variant); on api.cloudflare.com either
        is Cloudflare's 429 (error 971)."""
        rule = {"path": path, "status": status, "host": host, "method": method, "body": body, "headers": headers or {},
                "delay_ms": delay_ms, "times": times, "action": action, "query": query, "rate_limit": rate_limit}
        return self.api("POST", "/failures", {k: v for k, v in rule.items() if v is not None})["id"]

    def clear_failures(self, rule_id=None):
        self.api("DELETE", "/failures" if rule_id is None else f"/failures/{rule_id}")

    def failures(self):
        return self.api("GET", "/failures")["failures"]

    def reset(self):
        return self.api("POST", "/reset", {})


class Notify:
    """Where Gitberth's notifications go: Discord's and Slack's webhooks, an ntfy server and any webhook (each POST is a
    recorded call of kind "notify"), and the mail server smtp.e2e.test:2525 (every mail kept)."""

    DISCORD = "https://discord.com/api/webhooks/1234567/e2e-discord-secret"
    SLACK = "https://hooks.slack.com/services/T0E2E/B0E2E/e2e-slack-secret"
    NTFY = "https://ntfy.e2e.test/gitberth-e2e"
    WEBHOOK = "https://notify.e2e.test/hooks/gitberth"
    SMTP_HOST, SMTP_PORT = "smtp.e2e.test", 2525

    def __init__(self, standins):
        self.s = standins

    def received(self, since=0, host=None):
        return self.s.calls(since, host=host, kind="notify")

    def mails(self, since=0):
        return self.s.api("GET", "/notify/mail", query={"since": since})["mails"]

    def answer(self, host, status, times=None):
        """How the host answers from now on (times: that many calls); status None puts it back."""
        self.s.api("POST", "/notify/answer", {"host": host, "status": status, "times": times})

    def wait(self, since, host, check=lambda calls: bool(calls), what=None, timeout=60):
        """The calls to the host since the marker, once check(calls) holds."""
        return until(lambda: (lambda c: c if check(c) else None)(self.received(since, host)), timeout,
                     what or f"a notification at {host}", seen=lambda: [(c["path"], c.get("body") or c.get("body_text")) for c in self.received(since, host)])

    def wait_mail(self, since, check=lambda mails: bool(mails), what="a mail", timeout=60):
        return until(lambda: (lambda m: m if check(m) else None)(self.mails(since)), timeout, what, seen=lambda: self.mails(since))


class GitHub:
    """The GitHub stand-in, as the people using GitHub see it."""

    def __init__(self, standins):
        self.s = standins

    def token(self, login="e2e-bot", permissions=None, repos=None, expires=None, kind="fine-grained"):
        """A token GitHub accepts (for /user, the API and git), as a fine-grained token: every repository permission is
        read and write unless permissions says otherwise ({"statuses": False} or "none" takes one away, "read" makes it
        read-only; the names are contents, metadata, variables, statuses, pull_requests, issues, webhooks). repos: the only
        repos it selects. Like any fine-grained token it still reads every public repo's public data (the repo, its pull
        requests, statuses and comments), but writes to those, and their hooks and variables, get 403. Returns the token."""
        body = {"login": login, "permissions": permissions, "repos": repos, "expires": expires, "kind": kind}
        return self.s.api("POST", "/github/tokens", {k: v for k, v in body.items() if v is not None})["token"]

    def change_token(self, token, **changes):
        """permissions={...}, repos=[...], revoked=True, expires="2026-12-01 00:00:00 UTC"."""
        return self.s.api("PATCH", f"/github/tokens/{token}", changes)

    def create_repo(self, name, owner="e2e", template="testapp", files=None, private=False, branch="main"):
        """A repo with one commit on `branch`: the template's files (apps/<template>) plus `files` ({path: text})."""
        self.s.api("POST", "/github/repos", {"owner": owner, "name": name, "template": template, "files": files or {},
                                              "private": private, "branch": branch})
        return Repo(self, owner, name)

    def repo(self, full_name):
        owner, name = full_name.split("/", 1)
        return Repo(self, owner, name)

    def deliveries(self, since=0, repo=None, event=None):
        return self.s.api("GET", "/github/deliveries", query={"since": since, "repo": repo, "event": event})["deliveries"]

    def rate_limits(self):
        """The primary rate limit's windows as the stand-in counts them: 60 requests an hour per address without a token,
        5,000 per user with one ([{key: "ip:<address>" or "user:<id>", used, reset}])."""
        return self.s.api("GET", "/github/rate-limits")["windows"]

    def reset_rate_limits(self):
        """Every rate-limit window starts again: a scenario that used a limit up puts it back for the others."""
        return self.s.api("DELETE", "/github/rate-limits")

    @staticmethod
    def rest(verb, path, token=None, body=None):
        """GitHub's REST API as the stand-in answers it: rest("GET", "/repos/e2e/x/pulls?state=open", token). Without a
        token it counts against the runner's address, 60 an hour."""
        return rest("https://api.github.com", verb, path, token, body)


class Repo:
    def __init__(self, gh, owner, name):
        self.gh = gh
        self.s = gh.s
        self.owner = owner
        self.name = name
        self.full_name = f"{owner}/{name}"
        self.url = f"https://github.com/{owner}/{name}"

    def _p(self, rest=""):
        return f"/github/repos/{self.owner}/{self.name}{rest}"

    def __repr__(self):
        return f"Repo({self.full_name})"

    def state(self):
        """Everything about it: branches, pull request refs, pulls, hooks (with secrets), statuses, comments, variables."""
        return self.s.api("GET", self._p())

    def branches(self):
        return self.state()["branches"]

    def branch_sha(self, branch="main"):
        return self.branches().get(branch)

    def commit(self, files, branch="main", message=None, base=None, force=False, wait=True):
        """Commits files ({path: text, or None to delete}) on the branch (made from base when it doesn't exist), as a push
        would: GitHub's push delivery (and pull_request synchronize for its open pull requests) goes to every hook.
        With wait, it returns once those deliveries were answered. Returns {"sha", "before", "deliveries", ...}."""
        return self.s.api("POST", self._p("/commits"), {"branch": branch, "files": files, "message": message or f"Change on {branch}",
                                                        "base": base, "force": force}, query={"wait": None if wait else "0"})

    def create_branch(self, name, start=None):
        return self.s.api("POST", self._p("/branches"), {"name": name, "from": start})

    def delete_branch(self, name):
        return self.s.api("DELETE", self._p("/branches"), query={"name": name})

    def fork(self, owner):
        self.s.api("POST", self._p("/forks"), {"owner": owner})
        return Repo(self.gh, owner, self.name)

    def delete(self):
        return self.s.api("DELETE", self._p())

    def open_pull(self, head, base=None, title=None, body=None, draft=False):
        """head: a branch of this repo, or "<fork owner>:<branch>". Returns the pull request as the REST API shows it."""
        return self.s.api("POST", self._p("/pulls"), {"head": head, "base": base, "title": title, "body": body, "draft": draft})

    def update_pull(self, number, title=None, body=None, state=None):
        return self.s.api("PATCH", self._p(f"/pulls/{number}"), {k: v for k, v in {"title": title, "body": body, "state": state}.items() if v is not None})

    def close_pull(self, number):
        return self.update_pull(number, state="closed")

    def reopen_pull(self, number):
        return self.update_pull(number, state="open")

    def merge_pull(self, number, method="merge", delete_branch=False):
        return self.s.api("POST", self._p(f"/pulls/{number}/merge"), {"method": method, "delete_branch": delete_branch})

    def set_variables(self, **values):
        """The repo's GitHub (Actions) variables, as someone sets them on GitHub; None removes one."""
        return self.s.api("PUT", self._p("/variables"), values)

    def delete_comment(self, comment_id):
        """Someone deletes Gitberth's comment on GitHub."""
        return self.s.api("DELETE", self._p(f"/comments/{comment_id}"))

    def add_foreign_hook(self, url, events=("push",), secret=None):
        """Another service's webhook, which Gitberth must leave alone. Its deliveries go where its URL leads, like every
        hook's (nowhere, unless the rig's DNS and tunnel send its host somewhere)."""
        return self.s.api("POST", self._p("/hooks"), {"url": url, "events": list(events), "secret": secret, "kind": "foreign"})

    def add_pasted_hook(self, url, secret, events=("push", "pull_request", "delete")):
        """Gitberth's webhook made by hand on GitHub, from what Settings shows to paste."""
        return self.s.api("POST", self._p("/hooks"), {"url": url, "events": list(events), "secret": secret, "kind": "pasted"})

    def statuses(self, sha=None, context=None):
        return self.s.api("GET", "/github/statuses", query={"repo": self.full_name, "sha": sha, "context": context})["statuses"]

    def comments(self, pr=None):
        return self.s.api("GET", "/github/comments", query={"repo": self.full_name, "pr": pr})["comments"]

    def hooks(self):
        return self.s.api("GET", "/github/hooks", query={"repo": self.full_name})["hooks"]

    def deliveries(self, since=0, event=None):
        return self.gh.deliveries(since, self.full_name, event)

    def send_delivery(self, event="push", signature=None, secret=None, cf_connecting_ip="default", payload=None, raw_body=None,
                      headers=None, method="POST", url=None, branch=None, content_type=None, hook_id=None, via_tunnel=True):
        """Sends a delivery now, as GitHub would, and returns its record with the answer it got (status, response_*, error,
        route). It goes through hook_id, else Gitberth's hook: the newest made through the API, else the newest pasted
        by hand (never another service's). It goes where the hook's URL (or url) leads, through Cloudflare and the
        tunnel: it reaches Gitberth only if the host's DNS and the tunnel's rules send it there (see route).
        via_tunnel=False sends it straight to Gitberth's container instead, for endpoint-only checks.
        signature: None (the hook's own), "bad", "none" or a header value; cf_connecting_ip: the sender's address, which
        Cloudflare passes on as CF-Connecting-IP (None, for no such header at all, only with via_tunnel=False).
        The body is JSON in UTF-8 (or form data for a hook whose content type is form); without a hook (url and secret
        only) it's JSON, unless content_type is "form" (or any Content-Type, sent as it is). GitHub gives the answer 10
        seconds: a later one is recorded as GitHub records it, with no status and the error "timed out"."""
        body = {"repo": self.full_name, "event": event, "signature": signature, "secret": secret, "cf_connecting_ip": cf_connecting_ip,
                "payload": payload, "raw_body": raw_body, "headers": headers, "method": method, "url": url, "branch": branch,
                "content_type": content_type, "hook_id": hook_id, "via_tunnel": via_tunnel}
        return self.s.api("POST", "/github/deliveries", {k: v for k, v in body.items() if v is not None or k == "cf_connecting_ip"})

    # What Gitberth did with the repo

    def git_calls(self, since=0):
        """Every git request about the repo after the marker, however it was answered."""
        return self.s.calls(since=since, kind="git", repo=self.full_name)

    def ls_remotes(self, since=0):
        """Gitberth's `git ls-remote` of the repo (a watcher pass, or New app's Check) that got as far as asking for its
        refs (ls-refs), after the marker."""
        return self.s.calls(since=since, kind="git", repo=self.full_name, ls_remote=True)

    def ls_remote_attempts(self, since=0):
        """Every look at the repo with `git ls-remote` after the marker: the ls_remotes, and those refused at their first
        request (GET .../info/refs?service=git-upload-pack answered with anything but 200: GitHub's 401 or 404, or an
        injected failure), which a failing watcher pass gets no further than. A clone refused there counts too."""
        return self.s.calls(since=since, kind="git", repo=self.full_name, ls_remote_attempts=True)

    def api_calls(self, since=0, path=None, method=None, by_gitberth=True):
        """Gitberth's REST calls about the repo, however they were answered (an injected failure, a 401 for a revoked
        token, a 403 or 404 included); path is a regex on the URL path. The scenario's own github.rest() calls are left
        out, unless by_gitberth is False."""
        calls = self.s.calls(since=since, kind="github", repo=self.full_name, path=path, method=method)
        return [c for c in calls if (c.get("headers") or {}).get("User-Agent") != RUNNER_AGENT] if by_gitberth else calls

    def wait_watcher_passes(self, count, since, timeout=120):
        """Waits until the repo watcher has looked at the repo `count` times after the marker (one ls-remote per pass,
        failed ones included: see ls_remote_attempts), and returns the last of those calls. With the rig's 10-second
        interval a pass comes at least every 10 s."""
        return until(lambda: (lambda c: c[count - 1] if len(c) >= count else None)(self.ls_remote_attempts(since)), timeout,
                     f"{count} watcher passes over {self.full_name}",
                     seen=lambda: f"{len(self.ls_remote_attempts(since))} looks with ls-remote: "
                                  f"{[(c['method'], c['path'], c['status']) for c in self.git_calls(since)][-8:]}")

    def wait_pulls_listed(self, since, timeout=120, by_gitberth=True):
        """Waits for Gitberth to ask GitHub for the repo's open pull requests (the previews' part of a watcher pass), and
        returns the first such call, however it was answered. The scenario's own calls don't count, unless by_gitberth is
        False."""
        return until(lambda: self.api_calls(since, path=r"/pulls$", method="GET", by_gitberth=by_gitberth), timeout,
                     f"Gitberth to list {self.full_name}'s pull requests",
                     seen=lambda: self.api_calls(since, by_gitberth=by_gitberth))[0]


class Cloudflare:
    """The Cloudflare stand-in: one account with one tunnel (the fake cloudflared runs it), and Cloudflare's edge."""

    # Cloudflare's permission groups, by the stand-in's names:
    #   tunnel       Account › Cloudflare Tunnel › Edit
    #   dns          Zone › DNS › Edit
    #   zone         Zone › Zone › Read
    #   access_apps  Account › Access: Apps and Policies › Edit (Access applications)
    #   access_org   Account › Access: Organizations, Identity Providers, and Groups › Read (the Zero Trust team domain)
    ALL = ("tunnel", "dns", "zone", "access_apps", "access_org")
    # Exactly what Gitberth's Settings tells people to grant (CloudflareApi.Permissions plus AccessPermission, which names
    # both Access groups): the token a user makes. Scenarios about protection use it, so they see what such a token gets.
    AS_SETTINGS_ASKS = ("tunnel", "dns", "zone", "access_apps", "access_org")
    # The token Settings asked for before 1.1's fix: Access applications only, so the team domain can't be read.
    APPS_ONLY = ("tunnel", "dns", "zone", "access_apps")
    # What Settings asks for when domains aren't protected.
    WITHOUT_ACCESS = ("tunnel", "dns", "zone")

    def __init__(self, standins):
        self.s = standins

    def state(self):
        return self.s.api("GET", "/cloudflare/state")

    @staticmethod
    def rest(verb, path, token=None, body=None):
        """Cloudflare's API as the stand-in answers it: rest("GET", "zones?name=example.com", token)."""
        return rest("https://api.cloudflare.com/client/v4/", verb, path.lstrip("/"), token, body)

    def token(self, permissions=None, kind="user"):
        """An API token Cloudflare accepts, with these permission groups (Cloudflare.ALL by default; see AS_SETTINGS_ASKS
        for the one a user makes from Settings). Returns the token."""
        body = {"kind": kind}
        if permissions is not None:
            body["permissions"] = list(permissions)
        return self.s.api("POST", "/cloudflare/tokens", body)["token"]

    def settings_token(self):
        """A token with exactly the permissions Gitberth's Settings lists."""
        return self.token(self.AS_SETTINGS_ASKS)

    def connector(self):
        """The fake cloudflared as the stand-in last heard from it: {"connector": {address, port, version (the one it
        runs), network, seen_seconds_ago, live}, "version" (the tunnel's current one), "runs_current"}."""
        return self.s.api("GET", "/cloudflare/connector")

    def wait_runs(self, version=None, timeout=30, network=None):
        """Waits until the fake cloudflared runs the tunnel's configuration of this version or a later one (the current
        one by default), and returns the version it runs. A configuration cloudflared refuses never runs: its log says
        why (engine.logs("cloudflare-tunnel")). After engine.recreate_fake_cloudflared, pass its network: until the new
        one polls, the stand-in still knows the old one for a few seconds."""
        target = version if version is not None else self.tunnel()["version"]

        def runs():
            state = self.connector()
            c = state.get("connector")
            # Never ahead of the stand-in: such a connector ignores every version the stand-in has.
            return c if c and c["live"] and target <= c["version"] <= state["version"] and network in (None, c["network"]) else None

        where = f" on the network {network}" if network else ""
        found = until(runs, timeout, f"the fake cloudflared{where} to run version {target} of the tunnel's configuration", seen=self.connector)
        return found["version"]

    def visit(self, url, method="GET", headers=None, body=None, client_ip=None, signed_in_as=None):
        """A request to url as a visitor makes it, through Cloudflare's edge and the tunnel: {status, headers, body, error,
        route}. route tells what it met: the DNS records, an Access application, the connector, and the rule and
        service cloudflared used (route["cloudflared"]). signed_in_as: the email address the visitor signed in to
        Cloudflare Access with; without one an Access application answers 302 to its login page."""
        return self.s.api("POST", "/cloudflare/visit", {k: v for k, v in {"url": url, "method": method, "headers": headers, "body": body,
                                                                          "client_ip": client_ip, "signed_in_as": signed_in_as}.items() if v is not None})

    def change_token(self, token, **changes):
        return self.s.api("PATCH", f"/cloudflare/tokens/{token}", changes)

    def zone(self, name, status="active", total_tls=False):
        """A zone of the tunnel's account. Over https its names get through one level below it (app.example.com), as with
        Cloudflare's free certificate; total_tls covers deeper ones too."""
        return self.s.api("POST", "/cloudflare/zones", {"name": name, "status": status, "total_tls": total_tls})

    def set_team(self, name):
        """Zero Trust set up with this team name (name.cloudflareaccess.com); None: not set up."""
        return self.s.api("DELETE", "/cloudflare/team") if name is None else self.s.api("PUT", "/cloudflare/team", {"name": name})

    def options(self, list_includes_policies=None):
        return self.s.api("PUT", "/cloudflare/options", {"list_includes_policies": list_includes_policies})

    def tunnel(self):
        """{"version": n, "config": {...}}"""
        return self.state()["tunnel"]

    def ingress(self):
        return self.tunnel()["config"].get("ingress", [])

    def rules_for(self, hostname):
        return [r for r in self.ingress() if (r.get("hostname") or "").lower() == hostname.lower()]

    def set_tunnel_config(self, config):
        """Someone edits the tunnel in Cloudflare's dashboard."""
        return self.s.api("PUT", "/cloudflare/tunnel/config", {"config": config})

    def dns_records(self, name=None):
        records = self.state()["dns_records"]
        return [r for r in records if name is None or r["name"] == name.lower()]

    def add_dns_record(self, zone, type, name, content, proxied=True, comment=None):
        """A DNS record someone made in the dashboard. For a host that leads to the tunnel:
        add_dns_record(zone, "CNAME", host, f"{tunnel_id}.cfargotunnel.com")."""
        return self.s.api("POST", "/cloudflare/dns", {"zone": zone, "type": type, "name": name, "content": content,
                                                      "proxied": proxied, "comment": comment})

    def delete_dns_record(self, record_id):
        """Someone deletes a DNS record in the dashboard."""
        return self.s.api("DELETE", f"/cloudflare/dns/{record_id}")

    def access_apps(self):
        return self.state()["access_apps"]

    def add_access_app(self, name, domain, emails=()):
        """An Access application someone made in the dashboard."""
        return self.s.api("POST", "/cloudflare/access/apps", {"name": name, "domain": domain, "type": "self_hosted",
                                                              "policies": [{"name": "Allow", "decision": "allow",
                                                                            "include": [{"email": {"email": e}} for e in emails]}]})

    def delete_access_app(self, app_id):
        """Someone deletes an Access application in the dashboard."""
        return self.s.api("DELETE", f"/cloudflare/access/apps/{app_id}")

    def cloudflared_token(self):
        from .net import call
        return call("GET", f"{self.s.url}/__control/cloudflare/cloudflared-token")[2].strip()
