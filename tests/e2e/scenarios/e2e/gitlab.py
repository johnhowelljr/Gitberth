"""The GitLab stand-in for the scenarios: play the people using GitLab (projects in nested groups from the app templates,
commits, branches, forks, merge requests and their approvals, CI/CD variables, hooks made by hand, members, tokens with
scopes) and read back what Gitberth asked of it (its API calls and git requests, commit statuses, notes, hooks,
variables, deliveries). The runner resolves gitlab.e2e.test and gitlab.com to the stand-in and trusts the rig's CA, so
GitLab.rest gets the answers Gitberth gets."""
import base64
import json
import urllib.parse

from .net import call
from .standin import RUNNER_AGENT
from .wait import until

HOST = "gitlab.e2e.test"


def quote(path):
    """A project's path_with_namespace as GitLab's API names it: URL-encoded, slashes too (e2e%2Fteam%2Fapp)."""
    return urllib.parse.quote(path, safe="")


def basic_auth(token, username="oauth2"):
    """The Authorization header git sends GitLab for a token (git -c http.extraHeader="Authorization: ..."): oauth2 and
    the token, as the contract has Gitberth send it."""
    return "Basic " + base64.b64encode(f"{username}:{token}".encode()).decode()


class GitLab:
    """The GitLab stand-in, as the people using GitLab see it. One GitLab answers as https://gitlab.e2e.test (a
    self-managed instance's address, the one used here) and as https://gitlab.com (Gitberth's default instance); its
    answers name the host they were asked through."""

    HOST = HOST
    URL = f"https://{HOST}"
    LEVELS = ("guest", "reporter", "developer", "maintainer", "owner")

    def __init__(self, standins, host=HOST):
        self.s = standins
        self.host = host
        self.url = f"https://{host}"

    # people, tokens, options

    def token(self, username="e2e-bot", scopes=("api",), expires_at=None, kind="personal", project=None, group=None,
              access_level=None, name=None):
        """A token GitLab accepts, for the API and git. kind: personal (the user's own), project or group (a bot that's a
        member of that project or group, with access_level: maintainer by default), or oauth (an OAuth2 token, for which
        /personal_access_tokens/self is a 404). scopes are GitLab's: api, read_api, read_user, read_repository,
        write_repository, ... expires_at: a date (YYYY-MM-DD), from which GitLab refuses it. Returns the token."""
        body = {"username": username, "scopes": list(scopes), "expires_at": expires_at, "kind": kind, "project": project,
                "group": group, "access_level": access_level, "name": name}
        return self.s.api("POST", "/gitlab/tokens", {k: v for k, v in body.items() if v is not None})["token"]

    def change_token(self, token, **changes):
        """scopes=[...], revoked=True, expires_at="2026-01-01"."""
        return self.s.api("PATCH", f"/gitlab/tokens/{token}", changes)

    def tokens(self):
        """Every token's public side (kind, username, scopes, expiry, how often it was used, a tag), never its value."""
        return self.s.api("GET", "/gitlab/tokens")["tokens"]

    def add_member(self, target, username, access_level="developer"):
        """A user's role (guest, reporter, developer, maintainer, owner) in a project (its path) or a group
        ("group:<full path>"), which its subgroups and projects inherit."""
        return self.s.api("POST", "/gitlab/members", {"target": target, "username": username, "access_level": access_level})

    def options(self, **changes):
        """The stand-in's settings: self_endpoint=False (a GitLab before 15.5, without /personal_access_tokens/self),
        webhook_timeout (seconds GitLab waits for a delivery's answer, 10), authenticated_limit and unauthenticated_limit
        (API requests a minute per user and per address without a token: 2,000 and 500, as GitLab.com's). None puts a
        default back; with no changes it only reads them."""
        return self.s.api("PUT", "/gitlab/options", changes) if changes else self.s.api("GET", "/gitlab/options")

    def reset_options(self):
        return self.options(self_endpoint=None, webhook_timeout=None, authenticated_limit=None, unauthenticated_limit=None)

    def rate_limits(self):
        return self.s.api("GET", "/gitlab/rate-limits")

    def reset_rate_limits(self):
        """Every rate-limit window starts again."""
        return self.s.api("DELETE", "/gitlab/rate-limits")

    def throttle(self, path=r"^/api/v4/", times=1, retry_after=60, method=None, host=None):
        """GitLab's 429 on demand, for the next `times` matching calls: Retry later, with Retry-After (seconds) and
        GitLab's RateLimit-* headers. path is a regex on the URL path (git's too). Returns the injected rule's id."""
        return self.s.fail(path, status=429, host=host or self.host, method=method, times=times, headers={"Retry-After": str(retry_after)})

    def rest(self, verb, path, token=None, body=None, how="header", host=None, headers=None):
        """GitLab's REST API as the stand-in answers it: rest("GET", f"/projects/{quote(path)}/merge_requests?state=opened",
        token). The token goes as PRIVATE-TOKEN (how="header"), Authorization: Bearer ("bearer") or ?private_token=
        ("query"). Returns (status, headers, parsed JSON, or the text when it isn't JSON)."""
        h = {"User-Agent": RUNNER_AGENT, "Accept": "application/json"}
        url = f"https://{host or self.host}/api/v4{path}"
        if token and how == "header":
            h["PRIVATE-TOKEN"] = token
        elif token and how == "bearer":
            h["Authorization"] = f"Bearer {token}"
        elif token and how == "query":
            url += ("&" if "?" in url else "?") + urllib.parse.urlencode({"private_token": token})
        h.update(headers or {})
        status, hdrs, text = call(verb, url, body, h)
        try:
            return status, hdrs, json.loads(text) if text else None
        except ValueError:
            return status, hdrs, text

    # projects

    def create_project(self, path, template="testapp", files=None, visibility="public", branch="main", owner="e2e-bot"):
        """A project at path (its namespace first: e2e/team/app makes the groups e2e and e2e/team, which the owner owns;
        e2e-bot/app is in e2e-bot's own namespace) with one commit on `branch`: the template's files (apps/<template>)
        plus `files` ({path: text}). visibility: public, internal or private."""
        self.s.api("POST", "/gitlab/projects", {"path": path, "template": template, "files": files or {}, "visibility": visibility,
                                                "branch": branch, "owner": owner})
        return Project(self, path)

    def project(self, path):
        return Project(self, path)

    def deliveries(self, since=0, project=None, event=None):
        """Every delivery after the marker, as sent and as answered: event is push or merge_request."""
        return self.s.api("GET", "/gitlab/deliveries", query={"since": since, "project": project, "event": event})["deliveries"]

    def calls(self, since=0, path=None, method=None, kind="gitlab", project=None, by_gitberth=True):
        """Recorded calls to GitLab after the marker (kind gitlab: the API; gitlab-git: git), however they were answered;
        the scenarios' own rest() calls are left out unless by_gitberth is False."""
        calls = self.s.calls(since=since, kind=kind, path=path, method=method, repo=project)
        return [c for c in calls if (c.get("headers") or {}).get("User-Agent") != RUNNER_AGENT] if by_gitberth else calls


class Project:
    def __init__(self, gl, path):
        self.gl = gl
        self.s = gl.s
        self.path = path
        self.encoded = quote(path)
        # What a person pastes into Gitberth, and what git clones.
        self.url = f"{gl.url}/{path}"
        self.clone_url = f"{gl.url}/{path}.git"
        self._id = None

    def __repr__(self):
        return f"Project({self.path})"

    def _p(self, rest=""):
        return f"/gitlab/projects/{self.encoded}{rest}"

    @property
    def id(self):
        if self._id is None:
            self._id = self.state()["id"]
        return self._id

    def state(self):
        """Everything about it: id, visibility, branches, its merge requests and their refs, hooks (with their tokens),
        statuses, notes, variables (with their values), members."""
        return self.s.api("GET", self._p())

    def branches(self):
        return self.state()["branches"]

    def branch_sha(self, branch="main"):
        return self.branches().get(branch)

    def mr_refs(self):
        """{"refs/merge-requests/<iid>/head": sha}"""
        return self.state()["mr_refs"]

    def change(self, archived=None, visibility=None, default_branch=None):
        """Someone changes its settings: archives it, changes its visibility or its default branch."""
        body = {"archived": archived, "visibility": visibility, "default_branch": default_branch}
        return self.s.api("PATCH", self._p(), {k: v for k, v in body.items() if v is not None})

    def delete(self):
        return self.s.api("DELETE", self._p())

    def commit(self, files, branch="main", message=None, base=None, force=False, wait=True, user=None, author=None):
        """Commits files ({path: text, {"symlink": target} for a link, or None to delete}) on the branch (made from base when
        it doesn't exist), as a push would: the Push Hook (and a Merge Request Hook, update, for each open merge request
        from the branch) goes to every hook that asked for it. With wait, it returns once those were answered. Returns
        {"sha", "before", "deliveries", ...}."""
        body = {"branch": branch, "files": files, "message": message or f"Change on {branch}", "base": base, "force": force,
                "user": user, "author": author}
        return self.s.api("POST", self._p("/commits"), {k: v for k, v in body.items() if v is not None},
                          query={"wait": None if wait else "0"})

    def create_branch(self, name, start=None):
        return self.s.api("POST", self._p("/branches"), {"name": name, "from": start})

    def delete_branch(self, name):
        return self.s.api("DELETE", self._p("/branches"), query={"name": name})

    def fork(self, namespace=None, owner="e2e-forker"):
        """A fork in namespace (the owner's own by default), owned by owner. It's <namespace>/<this project's name>: two
        projects of the same name forked into one namespace collide (409), so give them names of their own (uniq)."""
        state = self.s.api("POST", self._p("/forks"), {"namespace": namespace or owner, "owner": owner})
        return Project(self.gl, state["path_with_namespace"])

    # merge requests

    def open_mr(self, source_branch, target_branch=None, title=None, description=None, draft=False, source=None, author=None, wait=True):
        """A merge request into this project, from its own source_branch or from a fork's (source: the fork's Project).
        draft puts "Draft: " before the title. Returns it as the API shows it, with the deliveries it caused."""
        body = {"source_branch": source_branch, "target_branch": target_branch, "title": title, "description": description,
                "draft": draft, "source_project": source.path if source is not None else None, "author": author}
        return self.s.api("POST", self._p("/merge_requests"), {k: v for k, v in body.items() if v is not None},
                          query={"wait": None if wait else "0"})

    def update_mr(self, iid, title=None, description=None, state_event=None, draft=None, user=None):
        """state_event: close or reopen; draft: True or False marks it a draft or ready."""
        body = {"title": title, "description": description, "state_event": state_event, "draft": draft, "user": user}
        return self.s.api("PUT", self._p(f"/merge_requests/{iid}"), {k: v for k, v in body.items() if v is not None})

    def close_mr(self, iid):
        return self.update_mr(iid, state_event="close")

    def reopen_mr(self, iid):
        return self.update_mr(iid, state_event="reopen")

    def merge_mr(self, iid, squash=False, remove_source_branch=False):
        """Merges it (a merge commit; squash makes one commit of it), as someone would in GitLab: the target branch's Push
        Hook and the Merge Request Hook (merge); remove_source_branch then deletes a same-project source branch."""
        return self.s.api("POST", self._p(f"/merge_requests/{iid}/merge"), {"squash": squash, "should_remove_source_branch": remove_source_branch})

    def approve_mr(self, iid, user="e2e-reviewer"):
        return self.s.api("POST", self._p(f"/merge_requests/{iid}/approve"), {"user": user})

    def unapprove_mr(self, iid, user="e2e-reviewer"):
        return self.s.api("POST", self._p(f"/merge_requests/{iid}/unapprove"), {"user": user})

    def merge_requests(self):
        return self.state()["merge_requests"]

    def add_note(self, iid, body, author="e2e-reviewer"):
        """Someone comments on the merge request."""
        return self.s.api("POST", self._p(f"/merge_requests/{iid}/notes"), {"body": body, "author": author})

    def delete_note(self, note_id):
        """Someone deletes a note (Gitberth's comment, say) in GitLab."""
        return self.s.api("DELETE", self._p(f"/notes/{note_id}"))

    # settings people change by hand

    def set_variables(self, *items):
        """CI/CD variables as someone sets them in GitLab: each {key, value, environment_scope ("*" by default), masked,
        protected, raw, hidden, variable_type (env_var or file)}; value None removes that key in that scope."""
        return self.s.api("PUT", self._p("/variables"), list(items))["variables"]

    def set_variable(self, key, value, environment_scope="*", **flags):
        return self.set_variables(dict(flags, key=key, value=value, environment_scope=environment_scope))

    def add_member(self, username, access_level="developer"):
        return self.s.api("POST", self._p("/members"), {"username": username, "access_level": access_level})

    def add_foreign_hook(self, url, token=None, push_events=True, merge_requests_events=False):
        """Another service's webhook, which Gitberth must leave alone. Its deliveries go where its URL leads, like every
        hook's (nowhere, unless the rig's DNS and tunnel send its host somewhere)."""
        return self.s.api("POST", self._p("/hooks"), {"url": url, "token": token, "push_events": push_events,
                                                      "merge_requests_events": merge_requests_events, "kind": "foreign"})

    def add_pasted_hook(self, url, token, push_events=True, merge_requests_events=True):
        """Gitberth's webhook made by hand in GitLab, from what its Settings shows to paste."""
        return self.s.api("POST", self._p("/hooks"), {"url": url, "token": token, "push_events": push_events,
                                                      "merge_requests_events": merge_requests_events, "kind": "pasted"})

    # what's there now

    def statuses(self, sha=None, name=None):
        """Its commit statuses as GitLab keeps them (oldest first): status, name, ref, target_url, description, who
        posted each (author) and with which token (its tag)."""
        return self.s.api("GET", "/gitlab/statuses", query={"project": self.path, "sha": sha, "name": name})["statuses"]

    def notes(self, mr=None, system=None):
        """Its merge requests' notes (oldest first), GitLab's own system notes among them unless system=False."""
        return self.s.api("GET", "/gitlab/notes", query={"project": self.path, "mr": mr, "system": "0" if system is False else None})["notes"]

    def hooks(self):
        """Its hooks with their tokens, who made each (made_by: api, pasted or foreign) and the host it was made through."""
        return self.s.api("GET", "/gitlab/hooks", query={"project": self.path})["hooks"]

    def variables(self):
        return self.state()["variables"]

    def deliveries(self, since=0, event=None):
        return self.gl.deliveries(since, self.path, event)

    def send_delivery(self, event="push", token=None, payload=None, hook_id=None, via_tunnel=True, url=None, headers=None, branch=None,
                      mr=None, action=None, raw_body=None, method="POST", cf_connecting_ip="default", event_header=None):
        """Sends a delivery now, as GitLab would, and returns its record with the answer it got (status, response_*,
        error, route). event: push (the branch's last commit, pushed) or merge_request (the merge request mr, or the
        newest, with action: update by default). It goes through hook_id, else Gitberth's hook: the newest made through
        the API, else the newest made by hand (never another service's); with url and no hook of Gitberth's, through
        none. It goes where the URL leads, through Cloudflare and the tunnel; via_tunnel=False sends it straight to
        Gitberth's container. token: None (the hook's own), "none" (no X-Gitlab-Token), "bad" or any text instead.
        GitLab waits 10 seconds for the answer: a later one is recorded with no status and the error "timed out"."""
        body = {"project": self.path, "event": event, "token": token, "payload": payload, "hook_id": hook_id, "via_tunnel": via_tunnel,
                "url": url, "headers": headers, "branch": branch, "mr": mr, "action": action, "raw_body": raw_body, "method": method,
                "cf_connecting_ip": cf_connecting_ip, "event_header": event_header}
        return self.s.api("POST", "/gitlab/deliveries", {k: v for k, v in body.items() if v is not None or k == "cf_connecting_ip"})

    def resend_delivery(self, delivery_id):
        """GitLab's Resend Request for a recorded delivery (its "id"): the same body, Idempotency-Key and
        X-Gitlab-Event-UUID, through its hook as it is now. GitLab refuses it once the hook's URL has changed (422 here)."""
        return self.s.api("POST", "/gitlab/deliveries", {"project": self.path, "resend": delivery_id})

    # what Gitberth did with it

    def git_calls(self, since=0):
        """Every git request about the project after the marker, however it was answered."""
        return self.s.calls(since=since, kind="gitlab-git", repo=self.path)

    def ls_remotes(self, since=0):
        """`git ls-remote`s of the project (a watcher pass, or New app's Check) that got as far as asking for its refs."""
        return self.s.calls(since=since, kind="gitlab-git", repo=self.path, ls_remote=True)

    def ls_remote_attempts(self, since=0):
        """Every look at the project with `git ls-remote` after the marker, refused ones too (GET .../info/refs answered
        with anything but 200: GitLab's 401, 403 or 404, or an injected failure)."""
        return self.s.calls(since=since, kind="gitlab-git", repo=self.path, ls_remote_attempts=True)

    def api_calls(self, since=0, path=None, method=None, by_gitberth=True):
        """Gitberth's API calls about the project (by its path or its ID), however they were answered; path is a regex on
        the URL path. The scenario's own rest() calls are left out unless by_gitberth is False."""
        return self.gl.calls(since, path=path, method=method, project=self.path, by_gitberth=by_gitberth)

    def wait_watcher_passes(self, count, since, timeout=120):
        """Waits until the repo watcher has looked at the project `count` times after the marker (one ls-remote per pass,
        failed ones included), and returns the last of those calls."""
        return until(lambda: (lambda c: c[count - 1] if len(c) >= count else None)(self.ls_remote_attempts(since)), timeout,
                     f"{count} watcher passes over {self.path}",
                     seen=lambda: f"{len(self.ls_remote_attempts(since))} looks with ls-remote: "
                                  f"{[(c['method'], c['path'], c['status']) for c in self.git_calls(since)][-8:]}")

    def wait_mrs_listed(self, since, timeout=120, by_gitberth=True):
        """Waits for Gitberth to ask GitLab for the project's merge requests, and returns the first such call, however it
        was answered."""
        return until(lambda: self.api_calls(since, path=r"/merge_requests$", method="GET", by_gitberth=by_gitberth), timeout,
                     f"Gitberth to list {self.path}'s merge requests", seen=lambda: self.api_calls(since, by_gitberth=by_gitberth))[0]
