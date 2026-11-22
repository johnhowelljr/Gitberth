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
- Gitberth finding the fake cloudflared from its token, and checking a Cloudflare token with the stand-in;
- the GitLab stand-in (TestGitLabStandIn), as the contract has Gitberth use GitLab: its answer shapes and paging, tokens'
  scopes and users' roles, merge requests from branches and forks, commit statuses' state machine, notes, hooks and
  CI/CD variables, git over HTTPS with oauth2:<token> (a private project, a merge request's head), Push Hook and Merge
  Request Hook deliveries going where their URL leads, its 429s, and calls recorded without a token in the log;
- the Docker Hub stand-in and registry.e2e.test (TestDockerHubStandIn): Docker Hub's search in its full and basic shapes
  and order, its tag pages newest push first with their platforms, a registry's token flow, Link paging, manifests and
  config, a private repository, and answers on demand (a 429, a 500, a dropped connection).
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
from e2e.gitlab import basic_auth, quote
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


# ---- the GitLab stand-in ----

class RecordingOrigin:
    """An origin in the runner that keeps every request it gets ({method, path, headers, body}) and answers 200, after
    `delay` seconds; a tunnel rule sends deliveries to it."""

    def __init__(self, delay=0):
        origin = self
        self.delay = delay
        self.received = []

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def do_POST(self):
                body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
                origin.received.append({"method": self.command, "path": self.path, "headers": dict(self.headers.items()), "body": body})
                time.sleep(origin.delay)
                try:
                    self.send_response(200)
                    self.send_header("Content-Length", "2")
                    self.end_headers()
                    self.wfile.write(b"ok")
                except OSError:
                    pass

            def log_message(self, *args):
                pass

        self.server = ThreadingHTTPServer(("0.0.0.0", 0), Handler)
        self.server.daemon_threads = True
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.url = f"http://{socket.gethostbyname(socket.gethostname())}:{self.server.server_address[1]}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()


def fresh_minute(margin=6):
    """Waits for a new minute when this one has fewer than `margin` seconds left: GitLab's rate limits count per minute."""
    left = 60 - time.time() % 60
    if left < margin:
        time.sleep(left + 0.2)


class TestGitLabStandIn:
    """The GitLab stand-in (https://gitlab.e2e.test, and https://gitlab.com) as the contract has Gitberth use GitLab. No
    Gitberth feature is needed: these are the runner's calls, and git run in Gitberth's container."""

    def test_answer_shapes_and_paging(self, gitlab, gl_token, uniq):
        """GitLab's answers to what Gitberth reads: the token's user and the token, a project (by URL-encoded path and by
        ID; nested groups), a branch's tip, branches paged with X-Next-Page, and compare."""
        group = uniq("glr")
        project = gitlab.create_project(f"{group}/team/app", visibility="private", files={"README.md": "hello"})
        enc = quote(project.path)
        status, _, user = gitlab.rest("GET", "/user", gl_token)
        assert status == 200 and user["username"] == "e2e-bot" and user["bot"] is False, user
        status, _, me = gitlab.rest("GET", "/personal_access_tokens/self", gl_token)
        assert status == 200 and me["scopes"] == ["api"] and me["active"] is True and me["expires_at"] is None, me
        assert me["user_id"] == user["id"] and me["revoked"] is False, me

        status, _, p = gitlab.rest("GET", f"/projects/{enc}", gl_token)
        assert status == 200, p
        assert (p["path_with_namespace"], p["default_branch"], p["visibility"], p["archived"]) == (project.path, "main", "private", False), p
        assert p["http_url_to_repo"] == f"https://gitlab.e2e.test/{project.path}.git" and p["web_url"] == f"https://gitlab.e2e.test/{project.path}"
        assert p["namespace"]["full_path"] == f"{group}/team" and p["namespace"]["kind"] == "group", p["namespace"]
        assert p["permissions"]["group_access"]["access_level"] == 50, p["permissions"]
        assert gitlab.rest("GET", f"/projects/{p['id']}", gl_token)[2]["path_with_namespace"] == project.path
        # Asked through gitlab.com, the same GitLab names gitlab.com.
        status, _, there = gitlab.rest("GET", f"/projects/{p['id']}", gl_token, host="gitlab.com")
        assert status == 200 and there["web_url"] == f"https://gitlab.com/{project.path}", there
        # A path that isn't URL-encoded is no route of GitLab's.
        status, _, body = gitlab.rest("GET", f"/projects/{project.path}", gl_token)
        assert status == 404 and body == {"error": "404 Not Found"}, body

        sha = project.commit({"marker.txt": "second"}, message="Second\n\nwith a body")["sha"]
        status, _, branch = gitlab.rest("GET", f"/projects/{enc}/repository/branches/main", gl_token)
        assert status == 200 and branch["default"] is True and branch["commit"]["id"] == sha, branch
        assert branch["commit"]["message"] == "Second\n\nwith a body" and branch["commit"]["title"] == "Second", branch["commit"]
        assert branch["commit"]["author_name"] == "e2e-bot" and branch["commit"]["committed_date"], branch["commit"]
        project.create_branch("feature/x")
        status, _, named = gitlab.rest("GET", f"/projects/{enc}/repository/branches/{quote('feature/x')}", gl_token)
        assert status == 200 and named["name"] == "feature/x" and named["commit"]["id"] == sha, named
        assert gitlab.rest("GET", f"/projects/{enc}/repository/branches/nope", gl_token)[2] == {"message": "404 Branch Not Found"}

        for name in ("b0", "b1", "b2"):
            project.create_branch(name)
        status, headers, first = gitlab.rest("GET", f"/projects/{enc}/repository/branches?per_page=2", gl_token)
        assert status == 200 and [b["name"] for b in first] == ["b0", "b1"], first
        paging = {k: header(headers, k) for k in ("X-Page", "X-Per-Page", "X-Next-Page", "X-Prev-Page", "X-Total", "X-Total-Pages")}
        assert paging == {"X-Page": "1", "X-Per-Page": "2", "X-Next-Page": "2", "X-Prev-Page": "", "X-Total": "5", "X-Total-Pages": "3"}, paging
        assert 'rel="next"' in header(headers, "Link") and "page=2" in header(headers, "Link"), headers
        status, headers, last = gitlab.rest("GET", f"/projects/{enc}/repository/branches?per_page=2&page=3", gl_token)
        assert [b["name"] for b in last] == ["main"] and header(headers, "X-Next-Page") == "" and header(headers, "X-Prev-Page") == "2", headers

        after = project.commit({"marker.txt": "third", "new.txt": "n", "README.md": None}, message="Third")["sha"]
        status, _, cmp = gitlab.rest("GET", f"/projects/{enc}/repository/compare?from={sha}&to={after}&straight=true", gl_token)
        assert status == 200 and cmp["compare_timeout"] is False and cmp["compare_same_ref"] is False, cmp
        changed = sorted((d["old_path"], d["new_path"], d["new_file"], d["deleted_file"]) for d in cmp["diffs"])
        assert changed == [("README.md", "README.md", False, True), ("marker.txt", "marker.txt", False, False), ("new.txt", "new.txt", True, False)], changed
        assert [c["id"] for c in cmp["commits"]] == [after] and cmp["commit"]["id"] == after, cmp["commits"]
        assert gitlab.rest("GET", f"/projects/{enc}/repository/compare?from={'0' * 40}&to={after}", gl_token)[2] == {"message": "404 Ref Not Found"}

    def test_tokens_scopes_and_roles(self, gitlab, gl_token, uniq):
        """GitLab's 401s (a token it doesn't know, an expired one, a revoked one), its 403 for a missing scope naming the
        scopes the endpoint takes, 404 for a private project the user isn't in, 403 for a role too low; project access
        tokens as bots; /personal_access_tokens/self's 404s."""
        private = gitlab.create_project(f"{uniq('glr')}/{uniq('app')}", visibility="private")
        public = gitlab.create_project(f"{uniq('glr')}/{uniq('app')}")
        penc, uenc = quote(private.path), quote(public.path)
        sha = private.branch_sha()

        status, _, body = gitlab.rest("GET", "/user", "not-a-token")
        assert status == 401 and body == {"message": "401 Unauthorized"}, body
        expired = gitlab.token(expires_at="2020-01-01")
        status, headers, body = gitlab.rest("GET", "/user", expired)
        assert status == 401 and body["error"] == "invalid_token" and "expired" in body["error_description"], body
        assert "invalid_token" in header(headers, "WWW-Authenticate"), headers
        revoked = gitlab.token()
        gitlab.change_token(revoked, revoked=True)
        status, _, body = gitlab.rest("GET", "/user", revoked)
        assert status == 401 and "revoked" in body["error_description"], body
        # A token GitLab refuses is refused where none is needed, too.
        assert gitlab.rest("GET", f"/projects/{uenc}", "not-a-token")[0] == 401
        assert gitlab.rest("GET", f"/projects/{uenc}")[0] == 200

        clone_only = gitlab.token(scopes=["read_repository"])
        status, _, body = gitlab.rest("GET", f"/projects/{penc}", clone_only)
        assert status == 403 and body["error"] == "insufficient_scope" and body["scope"] == "api read_api", body
        status, _, body = gitlab.rest("GET", "/user", clone_only)
        assert status == 403 and body["scope"] == "read_user api read_api", body
        reader = gitlab.token(scopes=["read_api", "read_repository"])
        assert gitlab.rest("GET", f"/projects/{penc}", reader)[0] == 200
        status, _, body = gitlab.rest("POST", f"/projects/{penc}/statuses/{sha}", reader, {"state": "success", "name": "x"})
        assert status == 403 and body["error"] == "insufficient_scope", body
        # Every way GitLab takes a token: PRIVATE-TOKEN, Authorization: Bearer, ?private_token=.
        for how in ("header", "bearer", "query"):
            assert gitlab.rest("GET", f"/projects/{penc}", gl_token, how=how)[0] == 200, how

        outsider = gitlab.token(username="e2e-outsider")
        status, _, body = gitlab.rest("GET", f"/projects/{penc}", outsider)
        assert status == 404 and body == {"message": "404 Project Not Found"}, body
        private.add_member("e2e-outsider", "reporter")
        assert gitlab.rest("GET", f"/projects/{penc}/merge_requests?state=opened", outsider)[0] == 200
        status, _, body = gitlab.rest("POST", f"/projects/{penc}/statuses/{sha}", outsider, {"state": "running", "name": "x"})
        assert status == 403 and body == {"message": "403 Forbidden"}, body
        private.add_member("e2e-outsider", "developer")
        assert gitlab.rest("POST", f"/projects/{penc}/statuses/{sha}", outsider, {"state": "running", "name": "x"})[0] == 201
        assert gitlab.rest("GET", f"/projects/{penc}/hooks", outsider)[0] == 403
        assert gitlab.rest("GET", f"/projects/{penc}/variables", outsider)[0] == 403
        # Writing to a public project takes a token.
        status, _, body = gitlab.rest("POST", f"/projects/{uenc}/statuses/{public.branch_sha()}", None, {"state": "success"})
        assert status == 401 and body == {"message": "401 Unauthorized"}, body

        bot = gitlab.token(kind="project", project=private.path, scopes=["api"])
        status, _, me = gitlab.rest("GET", "/user", bot)
        assert status == 200 and me["bot"] is True and me["username"].startswith(f"project_{private.id}_bot_"), me
        assert gitlab.rest("GET", f"/projects/{penc}/hooks", bot)[0] == 200
        assert gitlab.rest("GET", "/personal_access_tokens/self", bot)[2]["scopes"] == ["api"]
        other = gitlab.create_project(f"{uniq('glr')}/{uniq('app')}", visibility="private")
        assert gitlab.rest("GET", f"/projects/{quote(other.path)}", bot)[0] == 404

        oauth = gitlab.token(kind="oauth", scopes=["api"])
        assert gitlab.rest("GET", "/user", oauth)[0] == 200
        assert gitlab.rest("GET", "/personal_access_tokens/self", oauth)[0] == 404
        try:
            gitlab.options(self_endpoint=False)
            assert gitlab.rest("GET", "/personal_access_tokens/self", gl_token)[2] == {"error": "404 Not Found"}
        finally:
            gitlab.reset_options()

    def test_merge_requests_from_branches_and_forks(self, gitlab, gl_token, uniq):
        """Merge requests as Gitberth lists and reads them: from a branch and from a fork, with refs/merge-requests/<iid>/head
        in the target project following the source branch; draft, ready, merged, closed, reopened."""
        project = gitlab.create_project(f"{uniq('glr')}/{uniq('app')}")
        enc = quote(project.path)
        project.create_branch("feature")
        first = project.commit({"marker.txt": "feature"}, branch="feature")["sha"]
        mr = project.open_mr("feature", title="A feature", draft=True)
        n = mr["iid"]
        assert (mr["draft"], mr["title"], mr["sha"], mr["state"]) == (True, "Draft: A feature", first, "opened"), mr
        assert project.mr_refs()[f"refs/merge-requests/{n}/head"] == first
        second = project.commit({"marker.txt": "feature, again"}, branch="feature")["sha"]
        assert project.mr_refs()[f"refs/merge-requests/{n}/head"] == second
        assert any(x["system"] and x["body"].startswith("added 1 commit") for x in project.notes(mr=n)), project.notes(mr=n)

        fork = project.fork()
        fork.create_branch("from-fork")
        forked = fork.commit({"marker.txt": "from a fork"}, branch="from-fork")["sha"]
        m = project.open_mr("from-fork", source=fork, title="From a fork")["iid"]
        assert project.mr_refs()[f"refs/merge-requests/{m}/head"] == forked

        status, headers, mrs = gitlab.rest("GET", f"/projects/{enc}/merge_requests?state=opened&per_page=100", gl_token)
        assert status == 200 and header(headers, "X-Next-Page") == "" and header(headers, "X-Total") == "2", headers
        by_iid = {x["iid"]: x for x in mrs}
        assert by_iid[n]["source_project_id"] == by_iid[n]["target_project_id"] == project.id and by_iid[n]["sha"] == second, by_iid[n]
        assert by_iid[n]["draft"] is True and by_iid[n]["author"]["username"] == "e2e-bot", by_iid[n]
        assert by_iid[n]["web_url"] == f"https://gitlab.e2e.test/{project.path}/-/merge_requests/{n}", by_iid[n]["web_url"]
        assert (by_iid[m]["source_project_id"], by_iid[m]["target_project_id"], by_iid[m]["sha"]) == (fork.id, project.id, forked), by_iid[m]
        assert (by_iid[m]["source_branch"], by_iid[m]["target_branch"], by_iid[m]["author"]["username"]) == ("from-fork", "main", "e2e-forker")

        project.update_mr(n, draft=False)
        merged = project.merge_mr(n)["sha"]
        assert project.branch_sha("main") == merged
        project.close_mr(m)
        status, _, one = gitlab.rest("GET", f"/projects/{enc}/merge_requests/{n}", gl_token)
        assert (one["state"], one["merge_commit_sha"], one["draft"], one["title"]) == ("merged", merged, False, "A feature"), one
        assert gitlab.rest("GET", f"/projects/{enc}/merge_requests/{m}", gl_token)[2]["state"] == "closed"
        assert gitlab.rest("GET", f"/projects/{enc}/merge_requests?state=opened", gl_token)[2] == []
        project.reopen_mr(m)
        assert [x["iid"] for x in gitlab.rest("GET", f"/projects/{enc}/merge_requests?state=opened", gl_token)[2]] == [m]
        assert gitlab.rest("GET", f"/projects/{enc}/merge_requests/999", gl_token)[2] == {"message": "404 Not found"}
        assert gitlab.rest("GET", f"/projects/{enc}/merge_requests?state=open", gl_token)[2] == {"error": "state does not have a valid value"}

    def test_statuses_notes_hooks_and_variables(self, gitlab, gl_token, uniq):
        """What Gitberth writes: commit statuses (GitLab moves one status of a name along its state machine and refuses a
        move it doesn't make), the merge request's note (newest first among GitLab's system notes; only its author edits
        it), project hooks (their token never shown, and lost when the URL changes without it), CI/CD variables per
        environment scope (409 when a key alone names more than one)."""
        project = gitlab.create_project(f"{uniq('glr')}/{uniq('app')}")
        enc = quote(project.path)
        sha = project.branch_sha()

        def post(body):
            return gitlab.rest("POST", f"/projects/{enc}/statuses/{sha}", gl_token, body)

        status, _, s1 = post({"state": "pending", "name": "Gitberth: app", "ref": "main", "target_url": "https://gitberth.example/d/1",
                              "description": "Deploying"})
        assert status == 201 and (s1["status"], s1["name"], s1["sha"], s1["ref"]) == ("pending", "Gitberth: app", sha, "main"), s1
        status, _, s2 = post({"state": "running", "name": "Gitberth: app", "ref": "main"})
        assert status == 201 and s2["id"] == s1["id"] and s2["status"] == "running" and s2["target_url"] == "https://gitberth.example/d/1", s2
        status, _, body = post({"state": "running", "name": "Gitberth: app", "ref": "main"})
        assert status == 400 and body["message"].startswith("Cannot transition status via :run from :running"), body
        status, _, s3 = post({"state": "success", "name": "Gitberth: app", "ref": "main"})
        assert status == 201 and s3["id"] == s1["id"] and s3["status"] == "success" and s3["finished_at"], s3
        status, _, s4 = post({"state": "pending", "name": "Gitberth: app", "ref": "main"})
        assert status == 201 and s4["id"] > s1["id"] > 2 ** 31, (s1, s4)
        assert post({"state": "done"})[2] == {"error": "state does not have a valid value"}
        assert post({"state": "success", "description": "x" * 256})[2] == {"message": {"description": ["is too long (maximum is 255 characters)"]}}
        assert gitlab.rest("POST", f"/projects/{enc}/statuses/{'1' * 40}", gl_token, {"state": "success"})[2] == {"message": "404 Commit Not Found"}
        listed = gitlab.rest("GET", f"/projects/{enc}/repository/commits/{sha}/statuses", gl_token)[2]
        assert [(s["id"], s["status"]) for s in listed] == [(s4["id"], "pending")], listed
        assert [s["status"] for s in project.statuses(sha=sha)] == ["success", "pending"]

        project.create_branch("feature")
        project.commit({"marker.txt": "feature"}, branch="feature")
        n = project.open_mr("feature")["iid"]
        notes = f"/projects/{enc}/merge_requests/{n}/notes"
        status, _, note = gitlab.rest("POST", notes, gl_token, {"body": "Preview: building"})
        assert status == 201 and note["id"] > 2 ** 31 and note["system"] is False and note["noteable_iid"] == n, note
        assert note["author"]["username"] == "e2e-bot" and note["noteable_type"] == "MergeRequest", note
        status, _, edited = gitlab.rest("PUT", f"{notes}/{note['id']}", gl_token, {"body": "Preview: ready"})
        assert status == 200 and edited["body"] == "Preview: ready", edited
        project.add_note(n, "Looks good")
        project.commit({"marker.txt": "feature, again"}, branch="feature")
        listed = gitlab.rest("GET", f"{notes}?per_page=100", gl_token)[2]
        assert [x["id"] for x in listed] == sorted((x["id"] for x in listed), reverse=True), listed
        assert {(x["body"].split("\n")[0], x["system"]) for x in listed} >= {("Preview: ready", False), ("Looks good", False), ("added 1 commit", True)}, listed
        theirs = next(x for x in listed if x["body"] == "Looks good")
        assert gitlab.rest("PUT", f"{notes}/{theirs['id']}", gl_token, {"body": "mine now"})[2] == {"message": "403 Forbidden"}
        assert gitlab.rest("POST", notes, gl_token, {})[2] == {"error": "body is missing"}

        hooks = f"/projects/{enc}/hooks"
        token = secrets.token_hex(12)
        status, _, hook = gitlab.rest("POST", hooks, gl_token, {"url": "https://hooks.example.test/hooks/gitlab", "token": token, "push_events": True,
                                                                "merge_requests_events": True, "enable_ssl_verification": True})
        assert status == 201 and hook["push_events"] and hook["merge_requests_events"] and not hook["tag_push_events"], hook
        assert "token" not in hook and token not in json.dumps(hook), hook
        assert [h["id"] for h in gitlab.rest("GET", hooks, gl_token)[2]] == [hook["id"]]
        kept = project.hooks()[0]
        assert (kept["token"], kept["made_by"], kept["instance"]) == (token, "api", "gitlab.e2e.test"), kept
        status, _, moved = gitlab.rest("PUT", f"{hooks}/{hook['id']}", gl_token, {"url": "https://hooks2.example.test/hooks/gitlab"})
        assert status == 200 and moved["url"] == "https://hooks2.example.test/hooks/gitlab" and moved["merge_requests_events"], moved
        assert project.hooks()[0]["token"] is None, "GitLab drops a hook's token when its URL changes without one"
        gitlab.rest("PUT", f"{hooks}/{hook['id']}", gl_token, {"url": "https://hooks2.example.test/hooks/gitlab", "token": token})
        assert project.hooks()[0]["token"] == token
        status, _, body = gitlab.rest("POST", hooks, gl_token, {"url": "not a url"})
        assert status == 422 and body == {"error": "Invalid url given"}, body
        # GitLab doesn't call the local network from a webhook: a LAN address is refused the same way.
        for local in ("http://192.168.70.10:8080/hooks/gitlab", "http://localhost:8080/hooks/gitlab", "http://gitberth:8080/hooks/gitlab"):
            assert gitlab.rest("POST", hooks, gl_token, {"url": local})[0] == 422, local
        assert gitlab.rest("DELETE", f"{hooks}/{hook['id']}", gl_token)[0] == 204
        assert gitlab.rest("GET", hooks, gl_token)[2] == []

        variables = f"/projects/{enc}/variables"
        assert gitlab.rest("POST", variables, gl_token, {"key": "DB_URL", "value": "postgres://prod", "environment_scope": "production"})[0] == 201
        status, _, v = gitlab.rest("POST", variables, gl_token, {"key": "DB_URL", "value": "postgres://review", "environment_scope": "review/*"})
        assert status == 201 and (v["environment_scope"], v["masked"], v["variable_type"]) == ("review/*", False, "env_var"), v
        status, _, body = gitlab.rest("GET", f"{variables}/DB_URL", gl_token)
        assert status == 409 and "filter[environment_scope]" in body["message"], body
        scoped = f"{variables}/DB_URL?filter%5Benvironment_scope%5D=production"
        assert gitlab.rest("GET", scoped, gl_token)[2]["value"] == "postgres://prod"
        assert gitlab.rest("PUT", scoped, gl_token, {"value": "postgres://prod2"})[2]["value"] == "postgres://prod2"
        status, _, body = gitlab.rest("POST", variables, gl_token, {"key": "DB_URL", "value": "x", "environment_scope": "production"})
        assert status == 400 and body == {"message": {"key": ["(DB_URL) has already been taken"]}}, body
        assert gitlab.rest("POST", variables, gl_token, {"key": "SHORT", "value": "abc", "masked": True})[2] == {"message": {"value": ["is invalid"]}}
        assert gitlab.rest("POST", variables, gl_token, {"key": "BAD-KEY", "value": "x"})[0] == 400
        project.set_variable("FROM_UI", "1")
        status, headers, page = gitlab.rest("GET", f"{variables}?per_page=2", gl_token)
        assert len(page) == 2 and header(headers, "X-Next-Page") == "2" and header(headers, "X-Total") == "3", headers
        assert gitlab.rest("DELETE", f"{variables}/DB_URL?filter%5Benvironment_scope%5D=review%2F%2A", gl_token)[0] == 204
        assert sorted((v["key"], v["environment_scope"]) for v in project.variables()) == [("DB_URL", "production"), ("FROM_UI", "*")]

    def test_git_clones_a_private_project_and_a_merge_requests_head(self, gitlab, gl_token, engine, standins, uniq):
        """git, run where Gitberth runs it, with the token as oauth2:<token> in an http.extraHeader: a private project
        clones, and a fork's merge request head is fetched from the target project. Without credentials GitLab asks
        for them (401), a token without a repository scope gets 403, a user who isn't in the project 404."""
        project = gitlab.create_project(f"{uniq('glr')}/team/{uniq('app')}", visibility="private", files={"marker.txt": "a private one"})
        fork = project.fork()
        fork.create_branch("from-fork")
        forked = fork.commit({"marker.txt": "from a fork"}, branch="from-fork")["sha"]
        iid = project.open_mr("from-fork", source=fork)["iid"]

        def sh(script, token=None):
            env = {"GIT_TERMINAL_PROMPT": "0", "URL": project.clone_url, "IID": str(iid)}
            if token:
                env["GL_AUTH"] = f"Authorization: {basic_auth(token)}"
            return engine.exec(config.GITBERTH_CONTAINER, "sh", "-c", script + "\necho rc=$?", env=env)

        git = 'git -c credential.helper= -c "http.extraHeader=$GL_AUTH"'
        mark = standins.seq()
        out = sh(f'd=$(mktemp -d)\n{git} clone -q "$URL" "$d/r" 2>&1 && cat "$d/r/marker.txt" && echo && '
                 f'{git} -C "$d/r" fetch -q origin "refs/merge-requests/$IID/head" 2>&1 && git -C "$d/r" rev-parse FETCH_HEAD\n'
                 'rc=$?; rm -rf "$d"; (exit $rc)', token=gl_token)
        lines = out.strip().splitlines()
        assert lines[-1] == "rc=0" and "a private one" in lines and forked in lines, out
        calls = project.git_calls(mark)
        assert calls and all(c["token"] == gl_token and c["status"] == 200 for c in calls), [(c["method"], c["path"], c["status"]) for c in calls]
        assert f"refs/merge-requests/{iid}/head" in [r for c in calls for r in (c["git"].get("ref_prefixes") or [])], [c["git"] for c in calls]
        assert forked in [w for c in calls for w in (c["git"].get("wants") or [])], [c["git"] for c in calls]
        # ls-remote lists the merge request's head, as GitLab advertises it: the watcher sees a merge request move.
        listed = sh(f'{git} ls-remote "$URL"', token=gl_token)
        assert f"{forked}\trefs/merge-requests/{iid}/head" in listed.splitlines(), listed

        mark = standins.seq()
        sh('git -c credential.helper= ls-remote "$URL"')
        anonymous = project.wait_watcher_passes(1, since=mark, timeout=10)
        assert anonymous["status"] == 401 and anonymous["token"] is None, anonymous
        sh(f'{git} ls-remote "$URL"', token=gitlab.token(scopes=["read_api"]))
        assert project.wait_watcher_passes(2, since=mark, timeout=10)["status"] == 403
        sh(f'{git} ls-remote "$URL"', token=gitlab.token(username="e2e-stranger", scopes=["read_repository"]))
        assert project.wait_watcher_passes(3, since=mark, timeout=10)["status"] == 404
        sh(f'{git} ls-remote "$URL"', token="not-a-token")
        assert project.wait_watcher_passes(4, since=mark, timeout=10)["status"] == 401
        assert project.ls_remotes(mark) == [], "a refused look got as far as ls-refs"

    def test_deliveries_go_where_their_url_leads(self, gitlab, gl_token, cloudflare, standins, uniq):
        """GitLab's Push Hook and Merge Request Hook, as sent to Gitberth's hook: X-Gitlab-Event, X-Gitlab-Token,
        X-Gitlab-Event-UUID and the JSON body (Rails' encoding), through DNS and the tunnel; nowhere without DNS; every
        merge request action in order; a branch deleted; a token changed or left out; straight to Gitberth; GitLab's
        patience."""
        project = gitlab.create_project(f"{uniq('glr')}/{uniq('app')}")
        zone = f"{uniq('glh')}.test"
        host = f"hooks.{zone}"
        token = secrets.token_hex(16)
        status, _, hook = gitlab.rest("POST", f"/projects/{quote(project.path)}/hooks", gl_token, {
            "url": f"https://{host}/hooks/gitlab", "token": token, "push_events": True, "merge_requests_events": True,
            "enable_ssl_verification": True})
        assert status == 201, hook
        project.add_foreign_hook(f"https://ci.{zone}/hook", token=secrets.token_hex(8))

        mark = standins.seq()
        project.commit({"marker.txt": "pushed"})
        records = project.deliveries(mark, event="push")
        assert len(records) == 2 and all(r["status"] is None and "resolve" in r["error"] for r in records), records

        origin = RecordingOrigin()
        cloudflare.zone(zone)
        cloudflare.add_dns_record(zone, "CNAME", host, f"{cloudflare.state()['tunnel_id']}.cfargotunnel.com", proxied=True)
        before = cloudflare.tunnel()["config"]
        try:
            version = cloudflare.set_tunnel_config({"ingress": [{"hostname": host, "service": origin.url}, {"service": "http_status:404"}]})["version"]
            cloudflare.wait_runs(version)
            mark = standins.seq()
            message = "Grüße: café ✓ <&>"
            pushed = project.commit({"marker.txt": "routed", "docs/new.md": "new"}, message=message)
            record = next(d for d in project.deliveries(mark, event="push") if d["hook_id"] == hook["id"])
            assert record["status"] == 200 and record["error"] is None and record["route"]["cloudflared"]["rule"] == "1", record
            got = origin.received[-1]
            assert got["path"] == "/hooks/gitlab", got["path"]
            assert header(got["headers"], "X-Gitlab-Event") == "Push Hook" and header(got["headers"], "X-Gitlab-Token") == token, got["headers"]
            assert header(got["headers"], "Content-Type") == "application/json", got["headers"]
            assert header(got["headers"], "X-Gitlab-Event-UUID") == record["event_uuid"] and header(got["headers"], "X-Gitlab-Instance") == "https://gitlab.e2e.test"
            body = json.loads(got["body"])
            assert (body["object_kind"], body["ref"], body["before"], body["after"], body["checkout_sha"]) == \
                ("push", "refs/heads/main", pushed["before"], pushed["sha"], pushed["sha"]), body
            assert body["project"]["path_with_namespace"] == project.path and body["project"]["id"] == body["project_id"] == project.id, body["project"]
            assert body["total_commits_count"] == 1 and len(body["commits"]) == 1, body["commits"]
            c = body["commits"][0]
            assert (c["id"], c["message"], c["added"], c["modified"], c["removed"]) == (pushed["sha"], message, ["docs/new.md"], ["marker.txt"], []), c
            # Rails' JSON: unicode as it is, <, > and & escaped.
            assert "café ✓".encode() in got["body"] and b"\\u003c\\u0026\\u003e" in got["body"], got["body"][:400]
            # Resent, as GitLab's Resend Request: the same body, Idempotency-Key and event UUID.
            again = project.resend_delivery(record["id"])
            assert again["status"] == 200 and again["id"] == record["id"] and again["event_uuid"] == record["event_uuid"], again
            assert origin.received[-1]["body"] == got["body"] and header(origin.received[-1]["headers"], "Idempotency-Key") == record["id"]

            mark = standins.seq()
            created = project.create_branch("feature")
            iid = project.open_mr("feature", title="Feature")["iid"]
            moved = project.commit({"marker.txt": "on feature"}, branch="feature")["sha"]
            project.approve_mr(iid)
            project.close_mr(iid)
            project.reopen_mr(iid)
            project.merge_mr(iid, remove_source_branch=True)
            mrs = [d for d in project.deliveries(mark, event="merge_request") if d["hook_id"] == hook["id"]]
            assert [d["action"] for d in mrs] == ["open", "update", "approved", "close", "reopen", "merge"], [d["action"] for d in mrs]
            assert all(d["status"] == 200 and d["headers"]["X-Gitlab-Event"] == "Merge Request Hook" for d in mrs), mrs
            update = mrs[1]["payload"]["object_attributes"]
            assert update["oldrev"] == created["sha"] and update["last_commit"]["id"] == moved, update
            assert "oldrev" not in mrs[0]["payload"]["object_attributes"]
            last = mrs[-1]["payload"]["object_attributes"]
            assert (last["state"], last["iid"], last["source_branch"], last["target_branch"], last["source_project_id"], last["target_project_id"]) == \
                ("merged", iid, "feature", "main", project.id, project.id), last
            pushes = [d["payload"] for d in project.deliveries(mark, event="push") if d["hook_id"] == hook["id"]]
            new = next(p for p in pushes if p["ref"] == "refs/heads/feature" and p["before"] == "0" * 40)
            assert new["commits"] == [] and new["total_commits_count"] == 0, new
            gone = next(p for p in pushes if p["ref"] == "refs/heads/feature" and p["after"] == "0" * 40)
            assert gone["checkout_sha"] is None and gone["commits"] == [], gone

            assert project.send_delivery(token="bad")["status"] == 200
            assert header(origin.received[-1]["headers"], "X-Gitlab-Token") == "not-the-hook-token"
            assert project.send_delivery(token="none")["status"] == 200
            assert header(origin.received[-1]["headers"], "X-Gitlab-Token") is None
            assert project.send_delivery(event="merge_request", mr=iid, action="update")["hook_id"] == hook["id"]
            # Straight to Gitberth's container, for checks of its endpoint alone (its /hooks/gitlab comes with the product).
            direct = project.send_delivery(via_tunnel=False, cf_connecting_ip=None)
            assert direct["status"] is not None and direct["route"] is None and "CF-Connecting-IP" not in direct["headers"], direct

            origin.delay = 3
            gitlab.options(webhook_timeout=1)
            started = time.monotonic()
            late = project.send_delivery()
            assert late["status"] is None and late["error"] == "timed out" and time.monotonic() - started < 2.5, late
        finally:
            gitlab.reset_options()
            cloudflare.set_tunnel_config(before)
            origin.close()

    def test_rate_limits_and_a_429_on_demand(self, gitlab, gl_token, uniq):
        """GitLab's RateLimit-* headers on every answer; its 429 on demand (Retry later, Retry-After, RateLimit-*); and
        its limits per minute, for each address without a token and each user with one."""
        project = gitlab.create_project(f"{uniq('glr')}/{uniq('app')}")
        path = f"/projects/{quote(project.path)}"
        status, headers, _ = gitlab.rest("GET", path, gl_token)
        assert status == 200 and header(headers, "RateLimit-Limit") == "2000" and int(header(headers, "RateLimit-Remaining")) < 2000, headers

        gitlab.throttle(path=r"/merge_requests$", retry_after=7)
        status, headers, body = gitlab.rest("GET", f"{path}/merge_requests?state=opened", gl_token)
        assert status == 429 and body == "Retry later\n" and header(headers, "Retry-After") == "7", (status, headers, body)
        assert header(headers, "RateLimit-Remaining") == "0" and header(headers, "RateLimit-Name") == "throttle_authenticated_api", headers
        assert abs(int(header(headers, "RateLimit-Reset")) - (time.time() + 7)) < 3 and header(headers, "RateLimit-ResetTime").endswith(" GMT"), headers
        assert gitlab.rest("GET", f"{path}/merge_requests?state=opened", gl_token)[0] == 200

        try:
            gitlab.options(unauthenticated_limit=3)
            fresh_minute()
            gitlab.reset_rate_limits()
            seen = [gitlab.rest("GET", path)[:2] for _ in range(4)]
            assert [s for s, _ in seen] == [200, 200, 200, 429], [(s, h) for s, h in seen]
            assert header(seen[-1][1], "RateLimit-Name") == "throttle_unauthenticated_api" and int(header(seen[-1][1], "Retry-After")) <= 60
            assert gitlab.rest("GET", path, gl_token)[0] == 200, "a user with a token has a limit of its own"
        finally:
            gitlab.reset_options()
            gitlab.reset_rate_limits()

    def test_calls_are_recorded_and_no_token_reaches_the_log(self, gitlab, gl_token, standins, engine, uniq):
        """Every call about a project is found by it, however it was answered (an injected failure, GitLab's 401, by ID
        too), with the token it carried; a call no route answers is marked; a token sent in the query stays off the
        record's query; and no token ever reaches the stand-ins' log."""
        project = gitlab.create_project(f"{uniq('glr')}/{uniq('app')}", visibility="private")
        enc = quote(project.path)
        mark = standins.seq()
        standins.fail(r"/merge_requests$", status=502, host="gitlab.e2e.test", times=1)
        status, _, body = gitlab.rest("GET", f"/projects/{enc}/merge_requests", gl_token)
        assert status == 502 and body == {"message": "502 Bad Gateway"}, body
        assert gitlab.rest("GET", f"/projects/{project.id}/hooks", "not-a-token")[0] == 401
        calls = project.api_calls(mark, by_gitberth=False)
        assert [(c["method"], c["status"], c["token"], c["kind"]) for c in calls] == \
            [("GET", 502, gl_token, "gitlab"), ("GET", 401, "not-a-token", "gitlab")], calls
        assert calls[0]["injected"] and project.api_calls(mark) == [], "the runner's own calls aren't Gitberth's"

        status, _, body = gitlab.rest("GET", f"/projects/{enc}/no-such-endpoint", gl_token)
        assert status == 404 and body == {"error": "404 Not Found"}, body
        marked = standins.calls(mark, unrouted=True)
        assert [(c["host"], c["kind"], c["repo"]) for c in marked] == [("gitlab.e2e.test", "gitlab", project.path.lower())], marked

        gitlab.rest("GET", f"/projects/{enc}", gl_token, how="query")
        last = project.api_calls(mark, by_gitberth=False)[-1]
        assert last["status"] == 200 and last["token"] == gl_token and last["query"]["private_token"] == "[FILTERED]", last

        hook_token = secrets.token_hex(16)
        gitlab.rest("POST", f"/projects/{enc}/hooks", gl_token, {"url": f"https://nowhere.{uniq('gl')}.test/hooks/gitlab", "token": hook_token})
        project.commit({"marker.txt": "a push to a hook that goes nowhere"})
        log = engine.logs(STANDIN)
        assert "gitlab delivery Push Hook" in log, log[-2000:]
        for t in (gl_token, hook_token, "not-a-token"):
            assert t not in log, "a token reached the stand-ins' log"


class TestDockerHubStandIn:
    """The Docker Hub stand-in (hub.docker.com's search and tags API, index.docker.io's basic search) and the registry
    registry.e2e.test, as the contract has Gitberth use them (design 8.3, with D3: Docker Hub's registry stays the real
    one). No Gitberth feature is needed: these are the runner's calls, through the stand-in's own name."""

    def test_search_answers_in_docker_hubs_shapes_and_order(self, dockerhub):
        """v4's fields and badges; the exact name first, then names that start with the query, then by pulls; type=image
        keeps a Hardened Image out; the basic search (v1) marks official images only; Docker Hub's rate-limit headers."""
        status, headers, found = dockerhub.rest("hub.docker.com", "/api/search/v4?query=postg&from=0&size=25&type=image")
        assert status == 200, found
        ids = [r["id"] for r in found["results"]]
        assert ids == ["library/postgres", "cimg/postgres", "circleci/postgres", "kasmweb/postgres"] and found["total"] == 4, ids
        pg = found["results"][0]
        assert (pg["slug"], pg["type"], pg["badge"], pg["pull_count"], pg["publisher"]["name"]) == ("postgres", "image", "official", "1B+", "library"), pg
        assert isinstance(pg["raw_pull_count"], int) and pg["star_count"] > 1000 and pg["updated_at"].endswith("Z") and pg["archived"] is False, pg
        assert {a["name"] for a in pg["architectures"]} >= {"amd64", "arm64"}, pg["architectures"]
        by_id = {r["id"]: r for r in found["results"]}
        assert {by_id[i]["badge"] for i in ("cimg/postgres", "circleci/postgres", "kasmweb/postgres")} == {"verified_publisher"}
        assert [a["name"] for a in by_id["circleci/postgres"]["architectures"]] == ["amd64"]
        assert header(headers, "x-ratelimit-limit") == "180" and int(header(headers, "x-ratelimit-remaining")) > 3, headers

        _, _, ngin = dockerhub.rest("hub.docker.com", "/api/search/v4?query=ngin&from=0&size=25&type=image")
        assert [r["id"] for r in ngin["results"]] == ["library/nginx", "linuxserver/nginx", "arm64v8/nginx", "dtagdevsec/nginx"], ngin
        assert ngin["results"][1]["badge"] == "open_source" and [a["name"] for a in ngin["results"][2]["architectures"]] == ["arm64"]
        _, _, every = dockerhub.rest("hub.docker.com", "/api/search/v4?query=nginx&from=0&size=25")
        assert every["total"] == 5 and "dhi/nginx" in [r["id"] for r in every["results"]], "without type=image, the Hardened Image is found"
        _, _, two = dockerhub.rest("hub.docker.com", "/api/search/v4?query=nginx&from=0&size=2&type=image")
        assert [r["id"] for r in two["results"]] == ["library/nginx", "linuxserver/nginx"] and two["total"] == 4, two

        status, _, basic = dockerhub.rest("index.docker.io", "/v1/search?q=nginx&n=25")
        assert status == 200 and basic["num_results"] == 4 and basic["query"] == "nginx", basic
        assert (basic["results"][0]["name"], basic["results"][0]["is_official"], basic["results"][0]["pull_count"]) == ("nginx", True, 13624900407)
        assert not any(r["is_official"] for r in basic["results"][1:]) and "dhi/nginx" not in [r["name"] for r in basic["results"]], basic

    def test_tags_come_by_page_newest_push_first(self, dockerhub, standins):
        """nginx's 130 tags in two pages of 100: newest push first (so stable's rebuild first, not the newest version);
        aliases share a digest; each tag's platforms, attestations among them; name= as a substring; 404 for an image
        that isn't there; another path is no route."""
        path = "/v2/namespaces/library/repositories/nginx/tags?page={}&page_size=100&ordering=last_updated"
        status, _, one = dockerhub.rest("hub.docker.com", path.format(1))
        assert status == 200 and (one["count"], len(one["results"])) == (130, 100), (one["count"], len(one["results"]))
        assert "page=2" in one["next"] and one["previous"] is None, one["next"]
        pushed = [t["tag_last_pushed"] for t in one["results"]]
        assert pushed == sorted(pushed, reverse=True)
        top = {t["name"] for t in one["results"][:7]}
        assert top == {"stable", "1.30", "1.30.7", "stable-alpine", "stable-alpine3.24", "1.30.7-alpine", "1.30.7-alpine3.24"}, top
        tags = {t["name"]: t for t in one["results"]}
        latest = tags["latest"]
        assert latest["digest"] == tags["1.31.2"]["digest"] != tags["1.31.1"]["digest"] and latest["digest"].startswith("sha256:")
        assert [(i["os"], i["architecture"], i["variant"]) for i in latest["images"]] == \
            [("linux", "amd64", None), ("linux", "arm64", "v8"), ("unknown", "unknown", None), ("unknown", "unknown", None)], latest["images"]
        assert latest["full_size"] == latest["images"][0]["size"] and latest["content_type"] == "image"
        assert [(i["os"], i["architecture"]) for i in tags["1.31.2-perl"]["images"]] == [("linux", "arm64")]
        assert [i["os"] for i in tags["windowsservercore-ltsc2025"]["images"]] == ["windows"]
        assert "1.32.0-rc.1" in tags and any(n.startswith("sha256-") and n.endswith(".sig") for n in tags)

        _, _, two = dockerhub.rest("hub.docker.com", path.format(2))
        assert len(two["results"]) == 30 and two["next"] is None and "page=1" in two["previous"], two["previous"]
        assert {t["name"] for t in two["results"]}.isdisjoint(tags)
        _, _, named = dockerhub.rest("hub.docker.com", "/v2/namespaces/library/repositories/nginx/tags?page=1&page_size=100&name=alpine")
        assert named["count"] == 10 and all("alpine" in t["name"] for t in named["results"]), named["count"]
        status, _, missing = dockerhub.rest("hub.docker.com", "/v2/namespaces/library/repositories/no-such-image/tags?page=1&page_size=100")
        assert (status, missing) == (404, {"message": "object not found", "errinfo": {}})

        # The legacy path is no route of the stand-in (Gitberth uses the documented one); every call is recorded as Docker Hub's.
        mark = standins.seq()
        assert dockerhub.rest("hub.docker.com", "/v2/repositories/library/nginx/tags/?page_size=2")[0] == 404
        assert [(c["host"], c["path"]) for c in standins.calls(mark, unrouted=True)] == [("hub.docker.com", "/v2/repositories/library/nginx/tags/")]
        dockerhub.rest("hub.docker.com", path.format(1))
        assert [(c["kind"], c.get("repo")) for c in dockerhub.calls(mark, by_gitberth=False)] == [("dockerhub", None), ("dockerhub", "library/nginx")]

    def test_the_registry_wants_a_token_and_pages_with_link(self, dockerhub):
        """registry.e2e.test: a 401 naming its token service; an anonymous token for a public repository; tags/list in
        lexical order, 5 to a page, with a relative Link; an image's index, this host's manifest and its config; a private
        repository's tags only with its user's token."""
        reg = "registry.e2e.test"
        status, headers, body = dockerhub.rest(reg, "/v2/team/api/tags/list?n=1000")
        assert status == 401 and body["errors"][0]["code"] == "UNAUTHORIZED", body
        challenge = header(headers, "WWW-Authenticate")
        assert challenge == 'Bearer realm="https://registry.e2e.test/token",service="registry.e2e.test",scope="repository:team/api:pull"', challenge
        status, _, granted = dockerhub.rest(reg, "/token?service=registry.e2e.test&scope=repository:team/api:pull")
        assert status == 200 and granted["token"] == granted["access_token"] and granted["expires_in"] == 300, granted
        token = granted["token"]
        status, headers, first = dockerhub.rest(reg, "/v2/team/api/tags/list?n=1000", token)
        assert status == 200 and first == {"name": "team/api", "tags": ["1", "1.26", "1.26.5", "1.27", "1.27.2"]}, first
        link = header(headers, "Link")
        assert link == '</v2/team/api/tags/list?last=1.27.2&n=1000>; rel="next"', link
        status, headers, rest = dockerhub.rest(reg, link[1:link.index(">")], token)
        assert rest["tags"] == ["1.27.3", "1.28.0-rc1", "latest", "sha-4f9c2e1", "stable"] and header(headers, "Link") is None, (rest, headers)
        assert dockerhub.rest(reg, "/v2/team/api/tags/list?n=5000", token)[0] == 400

        # What New app reads before a pull: the index, this host's manifest, then its config.
        status, headers, index = dockerhub.rest(reg, "/v2/team/api/manifests/1.27", token)
        assert status == 200 and header(headers, "Content-Type") == "application/vnd.oci.image.index.v1+json", headers
        platforms = {(m["platform"]["os"], m["platform"]["architecture"]): m["digest"] for m in index["manifests"]}
        assert set(platforms) == {("linux", "amd64"), ("linux", "arm64")}, platforms
        _, _, manifest = dockerhub.rest(reg, f"/v2/team/api/manifests/{platforms[('linux', 'amd64')]}", token)
        _, _, config = dockerhub.rest(reg, f"/v2/team/api/blobs/{manifest['config']['digest']}", token)
        assert config["architecture"] == "amd64" and config["config"]["ExposedPorts"] == {"8080/tcp": {}}, config
        assert dockerhub.rest(reg, "/v2/team/api/manifests/9.9", token)[2]["errors"][0]["code"] == "MANIFEST_UNKNOWN"
        nothing = dockerhub.rest(reg, "/token?service=registry.e2e.test&scope=repository:team/nothing:pull")[2]["token"]
        assert dockerhub.rest(reg, "/v2/team/nothing/tags/list", nothing)[2]["errors"][0]["code"] == "NAME_UNKNOWN"

        # A private repository: an anonymous token doesn't reach it, its user's does, and a wrong password gets no token.
        user, password = "e2e-reader", "made-up-" + secrets.token_hex(6)
        dockerhub.seed("team/secret", [{"name": "2.1.0"}, {"name": "2.1"}], registry=reg, private=True, user=user, password=password)
        try:
            scope = "/token?service=registry.e2e.test&scope=repository:team/secret:pull"
            anonymous = dockerhub.rest(reg, scope)[2]["token"]
            status, headers, _ = dockerhub.rest(reg, "/v2/team/secret/tags/list", anonymous)
            assert status == 401 and 'error="insufficient_scope"' in header(headers, "WWW-Authenticate"), headers
            signed = dockerhub.rest(reg, scope, basic=(user, password))[2]["token"]
            assert dockerhub.rest(reg, "/v2/team/secret/tags/list", signed)[2]["tags"] == ["2.1", "2.1.0"]
            assert dockerhub.rest(reg, scope, basic=(user, "wrong"))[0] == 401
        finally:
            dockerhub.reset()

    def test_answers_on_demand_and_its_own_reset(self, dockerhub, standins):
        """POST /answer: a 429 with Retry-After and x-ratelimit-reset, the usual answer with low rate-limit headers, a 500,
        a dropped connection; each for as many calls as it says. Its reset puts the default seed back."""
        search = "/api/search/v4?query=redis&from=0&size=25&type=image"
        dockerhub.answer("hub.docker.com", "/api/search/v4", status=429, retry_after=30, reset_in=30)
        status, headers, _ = dockerhub.rest("hub.docker.com", search)
        assert status == 429 and header(headers, "Retry-After") == "30" and header(headers, "x-ratelimit-remaining") == "0", headers
        assert abs(int(header(headers, "x-ratelimit-reset")) - (time.time() + 30)) < 3, headers
        assert dockerhub.rest("hub.docker.com", search)[0] == 200, "times=1: the next call answers as usual"

        dockerhub.answer("hub.docker.com", "/api/search/v4", remaining=2, reset_in=20)
        status, headers, body = dockerhub.rest("hub.docker.com", search)
        assert status == 200 and body["results"][0]["id"] == "library/redis" and header(headers, "x-ratelimit-remaining") == "2", headers

        dockerhub.answer("index.docker.io", "/v1/search", status=500)
        assert dockerhub.rest("index.docker.io", "/v1/search?q=redis&n=25")[0] == 500

        dockerhub.answer("hub.docker.com", "/api/search/v4", close=True)
        mark = standins.seq()
        try:
            dockerhub.rest("hub.docker.com", search)
            raise AssertionError("the connection wasn't dropped")
        except OSError:
            pass  # RemoteDisconnected, a reset connection: what Gitberth gets from a host going away
        assert [c["status"] for c in dockerhub.calls(mark, by_gitberth=False)] == [0]
        assert dockerhub.answers() and all(a["left"] == 0 for a in dockerhub.answers())

        dockerhub.seed("library/redis", [{"name": "9.9.9", "pushed": 0}])
        assert [t["name"] for t in dockerhub.repo("redis")["tags"]] == ["9.9.9"]
        dockerhub.reset()
        assert dockerhub.answers() == [] and "8.2-alpine" in [t["name"] for t in dockerhub.repo("library/redis")["tags"]]
