"""Scenario 0: the rig itself, before the scenarios lean on it. Scenarios 1 and 20 don't touch these parts:
- the GitHub stand-in's pull requests, from a branch and from a fork, with their refs/pull/<n>/head refs; merging and
  closing them; its REST answers over TLS, as Gitberth reads them;
- GitHub's rules as a fine-grained token meets them: public repos readable outside the token's repos and nothing more,
  comments under Issues or Pull requests, one pull request under Contents too, hooks that may share a URL only without
  a common event; IDs past 2**31; its primary rate limits, per address without a token and per user with one;
- injected failures answering in each service's own shape, rate limits included; Cloudflare's two Access permission
  groups;
- every call found by its repo however it was answered (an injected failure, a refusal), and a failed look with
  ls-remote counting as a watcher pass; a call no stand-in route answers marked as such;
- webhook deliveries, signed with the hook's secret over UTF-8 as GitHub sends it, going where their URL leads: nowhere
  without DNS, through the tunnel's rules to Gitberth's /hooks/github (or the tunnel's 404) when routed, straight to
  Gitberth only when asked; GitHub's 10 seconds for an answer;
- the fake cloudflared taking a configuration only as cloudflared would (logging to stderr, as it does), checking Access
  on a protected rule, and reaching only its own network's services when it's off the host's;
- Gitberth finding the fake cloudflared from its token, and checking a Cloudflare token with the stand-in.
"""
import hashlib
import hmac
import json
import re
import secrets
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from e2e import config
from e2e.engine import STANDIN
from e2e.wait import until


def header(headers, name):
    return next((v for k, v in (headers or {}).items() if k.lower() == name.lower()), None)


def test_pull_requests_forks_and_merges(github, gh_token, uniq):
    repo = github.create_repo(uniq("rig"))
    repo.create_branch("feature")
    first = repo.commit({"marker.txt": "feature"}, branch="feature")["sha"]
    n = repo.open_pull("feature", title="A feature")["number"]
    assert repo.state()["pull_refs"][f"refs/pull/{n}/head"] == first
    # A new commit on its branch moves the pull request's head, and its ref.
    second = repo.commit({"marker.txt": "feature, again"}, branch="feature")["sha"]
    assert repo.state()["pull_refs"][f"refs/pull/{n}/head"] == second

    # A fork's pull request: the fork's commit comes into the base repo as refs/pull/<m>/head.
    fork = repo.fork("e2e-forker")
    fork.create_branch("from-fork")
    forked = fork.commit({"marker.txt": "from a fork"}, branch="from-fork")["sha"]
    m = repo.open_pull("e2e-forker:from-fork", title="From a fork")["number"]
    assert repo.state()["pull_refs"][f"refs/pull/{m}/head"] == forked

    # The REST list, as Gitberth reads it: a fork's head repo differs from the base repo.
    status, headers, pulls = github.rest("GET", f"/repos/{repo.full_name}/pulls?state=open&per_page=100&page=1", gh_token)
    assert status == 200, pulls
    by_number = {p["number"]: p for p in pulls}
    assert by_number[n]["head"]["repo"]["full_name"] == repo.full_name and by_number[n]["head"]["sha"] == second
    assert by_number[m]["head"]["repo"]["full_name"] == f"e2e-forker/{repo.name}" and by_number[m]["head"]["sha"] == forked
    assert by_number[m]["base"]["repo"]["full_name"] == repo.full_name
    assert int(headers.get("x-ratelimit-remaining", "0")) > 0

    # Merged: main moves to the merge commit, and the pull request says merged. Closed: not merged.
    merged = repo.merge_pull(n)["sha"]
    assert repo.branch_sha("main") == merged
    repo.close_pull(m)
    _, _, one = github.rest("GET", f"/repos/{repo.full_name}/pulls/{n}", gh_token)
    _, _, other = github.rest("GET", f"/repos/{repo.full_name}/pulls/{m}", gh_token)
    assert (one["state"], one["merged"]) == ("closed", True)
    assert (other["state"], other["merged"]) == ("closed", False)
    assert github.rest("GET", f"/repos/{repo.full_name}/pulls?state=open", gh_token)[2] == []
    # GitHub's 401 for a token it doesn't know, and 404 for a repo that isn't there.
    assert github.rest("GET", "/user", "not-a-token")[0] == 401
    assert github.rest("GET", f"/repos/{repo.owner}/no-such-repo/pulls", gh_token)[0] == 404


def test_tokens_and_hooks_follow_githubs_rules(github, gh_token, uniq):
    public = github.create_repo(uniq("pub"))
    private = github.create_repo(uniq("prv"), private=True)
    elsewhere = github.create_repo(uniq("oth"))
    public.create_branch("feature")
    public.commit({"marker.txt": "feature"}, branch="feature")
    n = public.open_pull("feature")["number"]

    # A fine-grained token that selects another repo still reads a public repo's public data, as every token does...
    narrow = github.token(repos=[elsewhere.full_name])
    assert github.rest("GET", f"/repos/{public.full_name}/pulls?state=open", narrow)[0] == 200
    assert github.rest("GET", f"/repos/{public.full_name}/pulls/{n}", narrow)[2]["merged"] is False
    assert github.rest("GET", f"/repos/{public.full_name}/issues/{n}/comments", narrow)[0] == 200
    # ...but nothing else of it: no writes, no hooks, no variables.
    status, headers, body = github.rest("POST", f"/repos/{public.full_name}/issues/{n}/comments", narrow, {"body": "hello"})
    assert status == 403 and body["message"] == "Resource not accessible by personal access token", body
    assert github.rest("GET", f"/repos/{public.full_name}/hooks", narrow)[0] == 403
    assert github.rest("GET", f"/repos/{public.full_name}/actions/variables", narrow)[0] == 403
    # A private repo it doesn't select doesn't exist for it.
    assert github.rest("GET", f"/repos/{private.full_name}/pulls", narrow)[0] == 404

    # A pull request's comments take either Issues or Pull requests.
    issues_only = github.token(permissions={"pull_requests": False})
    status, _, comment = github.rest("POST", f"/repos/{public.full_name}/issues/{n}/comments", issues_only, {"body": "hello"})
    assert status == 201, comment
    # Its ID is past 2**31, as GitHub's comment IDs are now: one read into 32 bits would break.
    assert comment["id"] > 2 ** 31, comment["id"]
    neither = github.token(permissions={"pull_requests": "read", "issues": "read"})
    status, headers, _ = github.rest("POST", f"/repos/{public.full_name}/issues/{n}/comments", neither, {"body": "hello"})
    assert status == 403 and header(headers, "X-Accepted-GitHub-Permissions") == "issues=write; pull_requests=write", headers
    # On a private repo, reading them needs one of the two as well.
    private.create_branch("feature")
    private.commit({"marker.txt": "feature"}, branch="feature")
    p = private.open_pull("feature")["number"]
    no_read = github.token(permissions={"pull_requests": False, "issues": False})
    assert github.rest("GET", f"/repos/{private.full_name}/issues/{p}/comments", no_read)[0] == 403
    assert github.rest("GET", f"/repos/{private.full_name}/issues/{p}/comments", issues_only)[0] == 200

    # One pull request reads with Contents too (GitHub lists it under both Contents and Pull requests), so a token
    # without Pull requests still learns whether it was merged; their list needs Pull requests.
    contents_only = github.token(permissions={k: False for k in ("pull_requests", "issues", "statuses", "variables", "webhooks")})
    status, _, pull = github.rest("GET", f"/repos/{private.full_name}/pulls/{p}", contents_only)
    assert status == 200 and pull["merged"] is False, pull
    assert github.rest("GET", f"/repos/{private.full_name}/pulls?state=open", contents_only)[0] == 403
    no_contents = github.token(permissions={"pull_requests": False, "contents": False})
    assert github.rest("GET", f"/repos/{private.full_name}/pulls/{p}", no_contents)[0] == 403

    # Two hooks may share a URL only when no event is common to both ("*" has them all).
    hooks = f"/repos/{public.full_name}/hooks"
    config_ = {"url": "https://ci.example.org/hooks/github", "content_type": "json"}
    status, _, first = github.rest("POST", hooks, gh_token, {"events": ["push"], "config": config_})
    assert status == 201, first
    assert github.rest("POST", hooks, gh_token, {"events": ["pull_request", "push"], "config": config_})[0] == 422
    status, _, second = github.rest("POST", hooks, gh_token, {"events": ["pull_request"], "config": config_})
    assert status == 201, second
    assert github.rest("POST", hooks, gh_token, {"events": ["*"], "config": config_})[0] == 422
    # An update that would make them overlap is refused the same way.
    assert github.rest("PATCH", f"{hooks}/{second['id']}", gh_token, {"add_events": ["push"]})[0] == 422


def test_githubs_rate_limits(github, gh_token, standins, uniq):
    """GitHub's primary rate limit: 60 requests an hour from an address without a token, its headers counting down, then
    GitHub's 403 naming the address until the window's reset; a token's user has 5,000 of its own. /rate_limit doesn't
    count. An injected rate limit answers a request without a token in the same form."""
    repo = github.create_repo(uniq("rig"))
    path = f"/repos/{repo.full_name}/pulls?state=open"
    github.reset_rate_limits()
    try:
        mark = standins.seq()
        status, headers, _ = github.rest("GET", path)
        assert status == 200, headers
        counted = [header(headers, f"x-ratelimit-{k}") for k in ("limit", "remaining", "used", "resource")]
        assert counted == ["60", "59", "1", "core"], headers
        reset = int(header(headers, "x-ratelimit-reset"))
        assert time.time() < reset <= time.time() + 3600, reset
        address = standins.calls(mark, host="api.github.com")[-1]["client"]
        for n in range(2, 61):
            status, headers, _ = github.rest("GET", path)
            assert status == 200 and header(headers, "x-ratelimit-remaining") == str(60 - n), (n, status, headers)
        status, headers, body = github.rest("GET", path)
        assert status == 403 and header(headers, "x-ratelimit-remaining") == "0" and header(headers, "x-ratelimit-reset") == str(reset), headers
        assert body["message"].startswith(f"API rate limit exceeded for {address}. (But here's the good news"), body
        # /rate_limit still answers, and doesn't count.
        status, _, limits = github.rest("GET", "/rate_limit")
        assert status == 200 and limits["resources"]["core"]["remaining"] == 0 and limits["rate"]["used"] == 60, limits
        # A token's user has a limit of its own.
        status, headers, _ = github.rest("GET", path, gh_token)
        assert status == 200 and header(headers, "x-ratelimit-limit") == "5000" and int(header(headers, "x-ratelimit-remaining")) > 0, headers
    finally:
        github.reset_rate_limits()

    # An injected primary rate limit answers a request without a token as GitHub would: 60 an hour, the address named.
    standins.fail(r"/pulls$", host="api.github.com", rate_limit="primary", times=1)
    status, headers, body = github.rest("GET", path)
    assert status == 403 and header(headers, "x-ratelimit-limit") == "60" and header(headers, "x-ratelimit-remaining") == "0", headers
    assert body["message"].startswith(f"API rate limit exceeded for {address}. (But here's the good news"), body
    github.reset_rate_limits()


def test_injected_failures_answer_as_each_service_would(github, cloudflare, gh_token, standins):
    # GitHub's shape: its error body and its rate-limit headers, with requests left.
    standins.fail(r"^/user$", status=502, host="api.github.com", times=1)
    status, headers, body = github.rest("GET", "/user", gh_token)
    assert status == 502 and body["message"] == "Bad Gateway" and body["status"] == "502" and body["documentation_url"], body
    assert int(header(headers, "x-ratelimit-remaining")) > 0, headers
    # GitHub's primary rate limit (403, none left, the reset time) and its secondary one (retry-after).
    standins.fail(r"^/user$", host="api.github.com", rate_limit="primary", times=1)
    status, headers, body = github.rest("GET", "/user", gh_token)
    assert status == 403 and header(headers, "x-ratelimit-remaining") == "0" and int(header(headers, "x-ratelimit-reset")) > time.time()
    assert body["message"].startswith("API rate limit exceeded for user ID "), body
    standins.fail(r"^/user$", host="api.github.com", rate_limit="secondary", times=1)
    status, headers, body = github.rest("GET", "/user", gh_token)
    assert status == 403 and header(headers, "retry-after") == "60" and "secondary rate limit" in body["message"], (headers, body)
    assert github.rest("GET", "/user", gh_token)[0] == 200

    # Cloudflare's shape: its envelope with an error code; its rate limit is a 429 with error 971.
    token = cloudflare.token()
    standins.fail(r"/user/tokens/verify$", status=500, host="api.cloudflare.com", times=1)
    status, _, body = cloudflare.rest("GET", "user/tokens/verify", token)
    assert status == 500 and body["success"] is False and body["result"] is None and body["errors"][0]["code"], body
    standins.fail(r"/user/tokens/verify$", host="api.cloudflare.com", rate_limit="primary", times=1)
    status, headers, body = cloudflare.rest("GET", "user/tokens/verify", token)
    assert status == 429 and body["errors"][0]["code"] == 971 and header(headers, "Retry-After"), body


def test_refused_and_failed_calls_still_count_for_their_repo(github, gh_token, standins, engine, uniq):
    """Every call about a repo is found by its repo, however it was answered: an injected failure, GitHub's 401 for a
    revoked token, a git request refused. The scenarios about failures count on it (api_calls, wait_pulls_listed,
    wait_watcher_passes)."""
    repo = github.create_repo(uniq("rig"))
    private = github.create_repo(uniq("prv"), private=True)
    mark = standins.seq()

    # The list of pull requests, answered by an injected failure: still a listing of the repo's pull requests.
    standins.fail(rf"^/repos/{re.escape(repo.full_name)}/pulls$", status=502, host="api.github.com", times=1)
    assert github.rest("GET", f"/repos/{repo.full_name}/pulls?state=open", gh_token)[0] == 502
    # These are the runner's own calls: by default the fixtures return only Gitberth's, so they're asked for here.
    assert repo.api_calls(mark) == []
    listed = repo.wait_pulls_listed(mark, timeout=5, by_gitberth=False)
    assert listed["status"] == 502 and listed["injected"], listed
    # A revoked token's call, refused (401) before GitHub looks at the repo; and an expired one's, the same.
    revoked = github.token()
    github.change_token(revoked, revoked=True)
    assert github.rest("GET", f"/repos/{repo.full_name}/hooks", revoked)[0] == 401
    expired = github.token()
    github.change_token(expired, expires="2020-01-01 00:00:00 UTC")
    assert github.rest("GET", f"/repos/{repo.full_name}/hooks", expired)[0] == 401
    calls = repo.api_calls(mark, by_gitberth=False)
    assert [(c["method"], c["status"]) for c in calls] == [("GET", 502), ("GET", 401), ("GET", 401)], calls

    # git, run where Gitberth runs it: a look with ls-remote refused at its first request (an injected failure, or
    # GitHub asking for credentials for a private repo) is a watcher pass that failed; it gets no further.
    def ls_remote(url):
        return engine.exec(config.GITBERTH_CONTAINER, "git", "-c", "credential.helper=", "ls-remote", url, check=False,
                           env={"GIT_TERMINAL_PROMPT": "0"})

    standins.fail(rf"^/{re.escape(repo.full_name)}(\.git)?/info/refs$", status=502, host="github.com", times=1)
    ls_remote(repo.url)
    failed = repo.wait_watcher_passes(1, since=mark, timeout=10)
    assert failed["status"] == 502 and failed["injected"] and failed["git"]["service"] == "git-upload-pack", failed
    ls_remote(repo.url)
    passed = repo.wait_watcher_passes(2, since=mark, timeout=10)
    assert passed["git"]["ls_remote"] and passed["status"] == 200, passed
    assert len(repo.ls_remotes(mark)) == 1, "a refused look counted as one that got as far as ls-refs"
    ls_remote(private.url)
    refused = private.wait_watcher_passes(1, since=mark, timeout=10)
    assert refused["status"] == 401 and not refused["injected"], refused


def test_calls_without_a_stand_in_route_are_marked(github, cloudflare, gh_token, standins, uniq):
    """A call no stand-in route answers gets what GitHub or Cloudflare answer an unknown one (404, or Cloudflare's 7003)
    and is marked: the scenarios fail on Gitberth's (conftest.py), as Gitberth would read that answer as a missing
    permission or as not set up. These are the runner's own, which don't count."""
    repo = github.create_repo(uniq("rig"))
    token = cloudflare.token()
    mark = standins.seq()
    status, _, body = github.rest("GET", f"/repos/{repo.full_name}/no-such-endpoint", gh_token)
    assert status == 404 and body["message"] == "Not Found", body
    status, _, body = cloudflare.rest("GET", "accounts/no-such-account/no-such-endpoint", token)
    assert status == 404 and body["errors"][0]["code"] == 7003, body
    marked = standins.calls(mark, unrouted=True)
    assert [(c["host"], c["status"]) for c in marked] == [("api.github.com", 404), ("api.cloudflare.com", 404)], marked
    assert marked[0]["repo"] == repo.full_name.lower()
    # A route that exists, answering 404 for a repo that doesn't, isn't one.
    assert github.rest("GET", f"/repos/{repo.owner}/no-such-repo/pulls", gh_token)[0] == 404
    assert len(standins.calls(mark, unrouted=True)) == 2


def test_access_permissions_are_cloudflares_two_groups(cloudflare):
    account = cloudflare.state()["account_id"]
    # A token that may manage Access applications can't read the Zero Trust organization: Cloudflare keeps that under
    # "Access: Organizations, Identity Providers, and Groups".
    apps_only = cloudflare.token(cloudflare.APPS_ONLY)
    status, _, body = cloudflare.rest("GET", f"accounts/{account}/access/organizations", apps_only)
    assert status == 403 and body["errors"][0]["code"] == 10000, body
    assert cloudflare.rest("GET", f"accounts/{account}/access/apps", apps_only)[0] == 200
    # The token Settings asks for has both groups, and reads it (404 here: no team is set up).
    for token in (cloudflare.settings_token(), cloudflare.token()):
        assert cloudflare.rest("GET", f"accounts/{account}/access/organizations", token)[0] == 404
        assert cloudflare.rest("GET", f"accounts/{account}/access/apps", token)[0] == 200
    apps_only_org = cloudflare.token(["tunnel", "dns", "zone", "access_org"])
    assert cloudflare.rest("GET", f"accounts/{account}/access/apps", apps_only_org)[0] == 403


def test_deliveries_go_where_their_url_leads(github, gh_token, cloudflare, engine, uniq, standins):
    repo = github.create_repo(uniq("rig"))
    zone = f"{uniq('rig')}.test"
    host = f"hooks.{zone}"
    secret = secrets.token_hex(16)
    # Another service's hook with the same path on another host, made first.
    foreign = repo.add_foreign_hook(f"https://ci.{zone}/hooks/github", secret="the-other-services-secret")
    status, _, hook = github.rest("POST", f"/repos/{repo.full_name}/hooks", gh_token, {
        "name": "web", "active": True, "events": ["push", "pull_request", "delete"],
        "config": {"url": f"https://{host}/hooks/github", "content_type": "json", "secret": secret, "insecure_ssl": "0"}})
    assert status == 201, hook
    assert hook["config"]["secret"] == "********", "GitHub never shows a hook's secret"

    # Nothing sends the host anywhere yet: GitHub can't resolve it, for either hook.
    mark = standins.seq()
    message = "Grüße aus Köln: café ✓ 🚀"
    pushed = repo.commit({"marker.txt": "pushed"}, message=message)
    records = until(lambda: (lambda d: d if len(d) == 2 else None)(repo.deliveries(mark, event="push")), 30, "both hooks' push deliveries")
    record = next(r for r in records if r["hook_id"] == hook["id"])
    # GitHub's delivery: its headers, its payload, to the hook's host. The body is UTF-8 with unicode as it is (GitHub
    # doesn't escape it), and that's what the hook's secret signs.
    body = json.dumps(record["payload"], separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    assert record["payload"]["head_commit"]["message"] == message, record["payload"]["head_commit"]
    assert record["body_size"] == len(body), "the delivery's body isn't the payload in UTF-8, as GitHub sends it"
    assert record["headers"]["X-Hub-Signature-256"] == "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()
    assert record["headers"]["X-GitHub-Event"] == "push" and record["headers"]["Content-Type"] == "application/json"
    assert record["host"] == host and record["via_tunnel"]
    assert record["payload"]["after"] == pushed["sha"] and record["payload"]["repository"]["full_name"] == repo.full_name
    assert record["status"] is None and "resolve" in record["error"], record
    assert all(r["status"] is None for r in records), records

    # Routed the way Gitberth routes its webhook host: a proxied CNAME to the tunnel, and in the tunnel only /hooks/github
    # (and deploy hooks' /hooks/deploy/<token>) to Gitberth's published port (cloudflared is on the host's network), the
    # rest of the host a 404.
    tunnel = cloudflare.state()["tunnel_id"]
    cloudflare.zone(zone)
    cloudflare.add_dns_record(zone, "CNAME", host, f"{tunnel}.cfargotunnel.com", proxied=True)
    before = cloudflare.tunnel()["config"]
    try:
        routed = cloudflare.set_tunnel_config({"ingress": [
            {"hostname": host, "path": "^/hooks/(github|deploy/[A-Za-z0-9_-]{20,100})$", "service": "http://localhost:8080"},
            {"hostname": host, "service": "http_status:404"},
            {"service": "http_status:404"}]})["version"]
        cloudflare.wait_runs(routed)
        mark = standins.seq()
        repo.commit({"marker.txt": "pushed again"})
        records = until(lambda: (lambda d: d if len(d) == 2 else None)(repo.deliveries(mark, event="push")), 30, "both push deliveries")
        record = next(r for r in records if r["hook_id"] == hook["id"])
        # Gitberth's /hooks/github answered it through the tunnel. Instant deploys are off, so Gitberth has no secret and
        # refuses every signature, with a bare 401.
        assert record["error"] is None and record["status"] == 401 and record["response_body"] == "", record
        assert record["route"]["cloudflared"]["rule"] == "1" and record["route"]["cloudflared"]["service"] == "http://localhost:8080", record["route"]
        # The other service's host has no record: its delivery still goes nowhere.
        assert next(r for r in records if r["hook_id"] == foreign["id"])["status"] is None

        # Any other path of the host gets the tunnel's own 404.
        other = repo.send_delivery(url=f"https://{host}/hooks/github/extra")
        assert other["status"] == 404 and other["route"]["cloudflared"]["rule"] == "2", other
        # A name two levels below the zone fails GitHub's certificate check: Cloudflare's free certificate doesn't cover it.
        cloudflare.add_dns_record(zone, "CNAME", f"deep.{host}", f"{tunnel}.cfargotunnel.com", proxied=True)
        deep = repo.send_delivery(url=f"https://deep.{host}/hooks/github")
        assert deep["status"] is None and "SSL certificate problem" in deep["error"], deep
        # Without hook_id, the control API sends through Gitberth's hook (made through the API), not the other one.
        assert repo.send_delivery()["hook_id"] == hook["id"]
        # Straight to Gitberth's container, for checks of its endpoint alone.
        direct = repo.send_delivery(via_tunnel=False, cf_connecting_ip=None)
        assert direct["status"] == 401 and direct["route"] is None and "CF-Connecting-IP" not in direct["headers"], direct
        # One sent through no hook at all (url and secret only) is JSON, as Gitberth's hooks are; form data when asked.
        loose = github.create_repo(uniq("rig"))
        sent = loose.send_delivery(url=f"https://{host}/hooks/github", secret="any", via_tunnel=False)
        assert sent["hook_id"] is None and sent["headers"]["Content-Type"] == "application/json", sent
        form = loose.send_delivery(url=f"https://{host}/hooks/github", secret="any", via_tunnel=False, content_type="form")
        assert form["headers"]["Content-Type"] == "application/x-www-form-urlencoded", form

        # A configuration cloudflared refuses (a service with a path) never runs: it says why and keeps the one it runs.
        since = time.time() - 1
        refused = cloudflare.set_tunnel_config({"ingress": [{"hostname": host, "service": "http://localhost:8080/"},
                                                            {"service": "http_status:404"}]})["version"]
        mentions = re.compile(rf"\bversion={refused}\b")
        log = until(lambda: (lambda t: t if mentions.search(t) else None)(engine.logs("cloudflare-tunnel", since=since)), 15,
                    "the fake cloudflared to refuse the configuration", seen=lambda: engine.logs("cloudflare-tunnel", tail=5))
        line = next(x for x in log.splitlines() if mentions.search(x))
        assert " ERR Failed to deserialize new configuration " in line and "don't support proxying to a different path" in line, line
        # cloudflared logs to stderr, as the real one does: a reader of stdout alone would never see it.
        out, err = engine.logs("cloudflare-tunnel", since=since, streams=True)
        assert mentions.search(err) and not mentions.search(out), (out[-500:], err[-500:])
        assert cloudflare.connector()["connector"]["version"] == routed
        still = repo.send_delivery()
        assert still["status"] == 401 and still["route"]["cloudflared"]["version"] == str(routed), still

        # A rule behind Access: cloudflared refuses a request without the visitor's Access token (403).
        guarded = cloudflare.set_tunnel_config({"ingress": [
            {"hostname": host, "service": "http://localhost:8080",
             "originRequest": {"access": {"required": True, "teamName": "gbe2e", "audTag": ["0" * 64]}}},
            {"service": "http_status:404"}]})["version"]
        cloudflare.wait_runs(guarded)
        blocked = repo.send_delivery()
        assert blocked["status"] == 403 and "Access" in blocked["route"]["cloudflared"]["note"], blocked
    finally:
        cloudflare.set_tunnel_config(before)


class SlowOrigin:
    """An origin in the runner that answers a POST with 202 after `seconds`, for a tunnel rule to send deliveries to."""

    def __init__(self, seconds):
        origin = self
        self.seconds = seconds

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def do_POST(self):
                self.rfile.read(int(self.headers.get("Content-Length") or 0))
                time.sleep(origin.seconds)
                try:
                    self.send_response(202)
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                except OSError:
                    pass

            def log_message(self, *args):
                pass

        self.server = ThreadingHTTPServer(("0.0.0.0", 0), Handler)
        self.server.daemon_threads = True
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        # The runner's address on the rig's network, which the fake cloudflared (on the host's) reaches.
        self.url = f"http://{socket.gethostbyname(socket.gethostname())}:{self.server.server_address[1]}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()


def test_a_delivery_gets_githubs_ten_seconds(github, cloudflare, uniq):
    """GitHub waits 10 seconds for a delivery's answer, then hangs up and records it as failed ("timed out"), whatever the
    origin answers later. An answer in time is recorded as it came."""
    repo = github.create_repo(uniq("rig"))
    zone = f"{uniq('slow')}.test"
    host = f"hooks.{zone}"
    cloudflare.zone(zone)
    cloudflare.add_dns_record(zone, "CNAME", host, f"{cloudflare.state()['tunnel_id']}.cfargotunnel.com", proxied=True)
    origin = SlowOrigin(13)
    before = cloudflare.tunnel()["config"]
    try:
        version = cloudflare.set_tunnel_config({"ingress": [{"hostname": host, "service": origin.url},
                                                            {"service": "http_status:404"}]})["version"]
        cloudflare.wait_runs(version)
        started = time.monotonic()
        late = repo.send_delivery(url=f"https://{host}/hooks/github", secret="any")
        took = time.monotonic() - started
        assert late["status"] is None and late["error"] == "timed out", late
        assert 9.5 <= took < 12.5, f"GitHub hung up after {took:.1f} s, not 10 s"
        origin.seconds = 1
        quick = repo.send_delivery(url=f"https://{host}/hooks/github", secret="any")
        assert quick["status"] == 202 and quick["error"] is None, quick
    finally:
        cloudflare.set_tunnel_config(before)
        origin.close()


def test_gitberth_finds_the_fake_cloudflared(page, new_page, gitberth, cloudflare, standins, scenario):
    state = cloudflare.state()
    token = cloudflare.token()
    switch_name = "Manage app domains through Cloudflare"
    gitberth.open(page, "/settings")
    domains = page.locator("#domains")
    try:
        domains.get_by_role("switch", name=switch_name).click()
        gitberth.snackbar(page, "Domains turned on")
        # TunnelLocator found the fake cloudflared, and read the tunnel from its token.
        found = f"Tunnel {state['tunnel_id'][:8]} from the cloudflare-tunnel container"
        until(lambda: found in domains.inner_text(), 30, "the Domains card to name the tunnel", seen=domains.inner_text)

        domains.locator("#cf-token").fill(token)
        domains.get_by_role("button", name="Save token").click()
        said = gitberth.snackbar(page, "Token saved|Cloudflare")
        assert said.startswith("Token saved"), said
        # Gitberth checked the token and read the tunnel's routes from the Cloudflare stand-in.
        paths = [c["path"] for c in standins.calls(scenario.seq, host="api.cloudflare.com")]
        assert "/client/v4/user/tokens/verify" in paths, paths
        assert f"/client/v4/accounts/{state['account_id']}/cfd_tunnel/{state['tunnel_id']}/configurations" in paths, paths
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"
    finally:
        # Back to what the other scenarios start from, whatever happened: domains off, no Cloudflare token.
        restore = new_page()
        gitberth.open(restore, "/settings")
        section = restore.locator("#domains")
        switch = section.get_by_role("switch", name=switch_name)
        if switch.get_attribute("aria-checked") == "true":
            switch.click()
            gitberth.snackbar(restore, "Domains turned off")
        if section.get_by_role("button", name="Remove").count() > 0:
            section.get_by_role("button", name="Remove").click()
            gitberth.snackbar(restore, "Cloudflare token removed")
    assert gitberth.db.setting("domains.enabled") in (None, "false")
    assert gitberth.db.setting("cloudflare.tunnelId") is None


def test_fake_cloudflared_off_the_hosts_network(github, cloudflare, engine, uniq):
    """Off the host's network the fake cloudflared reaches what a real one there would, and nothing through the rig's own
    network: on Gitberth's shared network ("gitberth") it reaches Gitberth by its name, as Gitberth's own route for it
    says; on Docker's default bridge it's on neither. It reaches the stand-in (Cloudflare, for a real one) on its own
    network, which the stand-in joins. Back on the host's network afterwards, as the rig keeps it."""
    repo = github.create_repo(uniq("rig"))
    zone = f"{uniq('net')}.test"
    host = f"hooks.{zone}"
    cloudflare.zone(zone)
    cloudflare.add_dns_record(zone, "CNAME", host, f"{cloudflare.state()['tunnel_id']}.cfargotunnel.com", proxied=True)
    token = cloudflare.cloudflared_token()
    before = cloudflare.tunnel()["config"]
    # Gitberth joined its network at startup: only then does that name lead to it.
    assert "gitberth" in engine.networks(config.GITBERTH_CONTAINER)
    try:
        version = cloudflare.set_tunnel_config({"ingress": [{"hostname": host, "service": "http://gitberth:8080"},
                                                            {"service": "http_status:404"}]})["version"]
        engine.recreate_fake_cloudflared(token, "gitberth")
        cloudflare.wait_runs(version, network="gitberth")
        assert engine.networks("cloudflare-tunnel") == ["gitberth"]
        assert engine.networks(STANDIN) == ["gbe2e", "gitberth"]
        reached = repo.send_delivery(url=f"https://{host}/hooks/github", secret="any")
        # Gitberth's /hooks/github answered (instant deploys are off, so it refuses every signature).
        assert reached["status"] == 401 and reached["route"]["cloudflared"]["service"] == "http://gitberth:8080", reached

        engine.recreate_fake_cloudflared(token, "bridge")
        cloudflare.wait_runs(version, network="bridge")
        assert engine.networks("cloudflare-tunnel") == ["bridge"]
        assert engine.networks(STANDIN) == ["bridge", "gbe2e"]
    finally:
        engine.recreate_fake_cloudflared(token, "host")
        cloudflare.set_tunnel_config(before)
        cloudflare.wait_runs(network="host")
    assert engine.networks(STANDIN) == ["gbe2e"]
    assert engine.networks("cloudflare-tunnel") == ["host"]
