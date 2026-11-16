"""Scenario 29: AI agents (MCP) and the HTTP API, with API tokens.

- Settings › AI agents and API makes a token: name, access, expiry. It's shown once, with ready-to-paste setup for Claude
  Code (and the other clients); the list shows its name, access and first characters, never the token again. Only its
  SHA-256 is kept.
- An MCP client talks JSON-RPC to /mcp (stateless Streamable HTTP): initialize, tools/list (only the tools the token's
  access allows), tools/call. list_apps lists the test app; deploy_app with wait deploys it and says how it ended, and
  the audit log tells the deploy (queued and succeeded) against the token, by name, over MCP.
- A Read token is refused deploy_app with a plain sentence that says why. A revoked token (its name not plain ASCII) gets
  401 with a fixed, plain-ASCII Bearer challenge, as does Basic; the body says why. A request with an Origin header (a
  browser page's) gets 403. The signed-in cookie gets 401 on /mcp.
- The same deploy through POST /api/v1/tools/deploy_app, recorded against the token over the API; /api/v1/openapi.json
  lists the tools.
- deploy_app with commit builds one commit of the app's own branch (checked against the branch's history); a commit on
  another branch fails at the fetch with "That commit isn't on main."; anything but a SHA, or a ref, is a 400.
"""
import hashlib
import json
import re

from e2e import audit
from e2e.net import call
from e2e.wait import until

SECTION = "#agents"


def flat(text):
    return " ".join((text or "").split())


def make_token(gitberth, page, name, access):
    """Settings › AI agents and API › Create a token. Returns (token, setup text shown)."""
    gitberth.open(page, "/settings#agents")
    section = page.locator(SECTION)
    section.wait_for()
    section.locator("#token-name").fill(name)
    section.locator(".gb-seg[aria-label='Access'] .gb-seg-btn", has_text=access).click()
    section.locator(".gb-seg[aria-label='Expires'] .gb-seg-btn", has_text="90 days").click()
    button = section.get_by_role("button", name="Create token")
    until(button.is_enabled, 10, "Create token to take the name")
    button.click()
    said = flat(gitberth.snackbar(page, re.escape(f"Token {name} is ready.")))
    assert said.startswith(f"Token {name} is ready."), said
    token = flat(section.locator(".gb-agent-token code").inner_text())
    assert re.fullmatch(r"gbt_[A-Za-z0-9_-]{43}", token), token
    setup = flat(section.locator(".gb-agent-setup .gb-code pre").inner_text())
    return token, setup


def rpc(gitberth, token, method, params=None, rid=1, headers=None, timeout=60):
    """One JSON-RPC request to /mcp, as an MCP client sends it: (status, the JSON answer or the text)."""
    body = {"jsonrpc": "2.0", "id": rid, "method": method}
    if params is not None:
        body["params"] = params
    hdrs = {"Accept": "application/json, text/event-stream", "MCP-Protocol-Version": "2025-11-25"}
    if token:
        hdrs["Authorization"] = f"Bearer {token}"
    hdrs.update(headers or {})
    status, h, text = call("POST", gitberth.url("/mcp"), body=body, headers=hdrs, timeout=timeout)
    if status != 200:
        return status, text
    kind = next((v for k, v in h.items() if k.lower() == "content-type"), "")
    if "text/event-stream" in kind:
        text = [line[5:].strip() for line in text.splitlines() if line.startswith("data:") and line[5:].strip()][-1]
    return status, json.loads(text)


def tool(gitberth, token, name, args=None, timeout=60):
    """tools/call: (is_error, the result's JSON or its text)."""
    status, answer = rpc(gitberth, token, "tools/call", {"name": name, "arguments": args or {}}, rid=7, timeout=timeout)
    assert status == 200, (status, answer)
    result = answer["result"]
    text = result["content"][0]["text"]
    if result.get("isError"):
        return True, text
    return False, json.loads(text)


def test_an_agent_deploys_an_app_over_mcp_and_the_api_as_its_token(page, gitberth, github, apps, uniq):
    repo = github.create_repo(uniq("mcp"), files={"marker.txt": "mcp v1"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    gitberth.wait_deploy(app.id, timeout=420)

    # ---- Settings: a token, shown once, with the setup for Claude Code.
    name = uniq("agent")
    token, setup = make_token(gitberth, page, name, "Operate")
    assert setup == f'claude mcp add --transport http gitberth {gitberth.base}/mcp --header "Authorization: Bearer {token}"', setup
    [row] = gitberth.db.query("SELECT * FROM ApiTokens WHERE Name = ?", name)
    assert (row["Access"], row["TokenHash"], row["Prefix"], row["RevokedAt"]) == ("Operate", hashlib.sha256(token.encode()).hexdigest(), token[4:12], None), row
    assert row["ExpiresAt"], row
    # Once the page is left, it's never shown again: only its first characters.
    gitberth.open(page, "/apps")
    gitberth.open(page, "/settings#agents")
    section = page.locator(SECTION)
    entry = section.locator(".gb-token").filter(has=page.locator("strong", has_text=re.compile(f"^{re.escape(name)}$")))
    entry.wait_for()
    assert section.locator(".gb-agent-token").count() == 0
    assert token not in section.inner_text()
    assert f"gbt_{token[4:12]}…" in flat(entry.inner_text()), entry.inner_text()
    assert "Operate" in flat(entry.inner_text())
    until(lambda: [e["action"] for e in audit.since(0, "action = 'auth.token.created' AND target = ?", name)] == ["auth.token.created"], 15, "the token's making in the audit log")

    # ---- MCP: initialize, then the tools an Operate token may call.
    status, init = rpc(gitberth, token, "initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "e2e", "version": "1"}})
    assert status == 200, init
    assert init["result"]["serverInfo"]["name"] == "gitberth", init
    status, listed = rpc(gitberth, token, "tools/list", rid=2)
    names = [t["name"] for t in listed["result"]["tools"]]
    assert {"list_apps", "get_app", "deploy_app", "restart_app", "query_audit_log"} <= set(names), names
    assert "delete_app" not in names and "set_env_vars" not in names, names
    deploy = next(t for t in listed["result"]["tools"] if t["name"] == "deploy_app")
    assert deploy["annotations"]["readOnlyHint"] is False and "app" in deploy["inputSchema"]["properties"], deploy

    error, apps_now = tool(gitberth, token, "list_apps")
    assert not error, apps_now
    assert app.key in [a["key"] for a in apps_now["apps"]], apps_now

    # ---- deploy_app with wait: the deploy runs and its outcome comes back; the audit log has it against the token.
    mark = audit.last_id()
    error, done = tool(gitberth, token, "deploy_app", {"app": app.key, "wait": True}, timeout=660)
    assert not error, done
    assert done["status"] == "Succeeded" and done["finished"] is True, done
    assert done["requested_by"] == f"the token {name}", done
    dep = gitberth.db.deployment(done["deployment_id"])
    # Kind|Via|address|name: the deploy remembers who asked, so its outcome is told against them.
    assert dep["RequestedBy"].startswith("ApiToken|Mcp|") and dep["RequestedBy"].endswith("|" + name), dep
    told = until(lambda: (lambda t: t if len(t) >= 2 else None)(audit.since(mark, "action IN ('app.deploy.queued', 'app.deploy.succeeded') AND app_id = ?", app.id)),
                 30, "the deploy queued and succeeded in the audit log")
    assert [(e["action"], e["actor_kind"], e["actor"], e["via"]) for e in told] == [
        ("app.deploy.queued", "ApiToken", name, "Mcp"), ("app.deploy.succeeded", "ApiToken", name, "Mcp")], told
    # The audit log says it to the agent too.
    error, log = tool(gitberth, token, "query_audit_log", {"who": f"ApiToken:{name}", "app": app.key})
    assert not error, log
    assert {"app.deploy.queued", "app.deploy.succeeded"} <= {e["action"] for e in log["entries"]}, log

    # ---- A Read token: refused deploy_app with why; its tools/list has no deploy_app. Its name isn't plain ASCII, and
    # the 401 it gets once revoked still has a header Kestrel sends.
    reader = uniq("reader") + " – café"
    read_token, _ = make_token(gitberth, page, reader, "Read")
    error, why = tool(gitberth, read_token, "deploy_app", {"app": app.key})
    assert error, why
    assert why == (f"The token {reader} has Read access, and deploy_app needs Operate. "
                   "A person can make a token with Operate access in Gitberth's Settings › AI agents and API."), why
    _, listed = rpc(gitberth, read_token, "tools/list", rid=3)
    assert "deploy_app" not in [t["name"] for t in listed["result"]["tools"]]

    # ---- Revoked: 401 with a Bearer challenge.
    gitberth.open(page, "/settings#agents")
    row = page.locator(SECTION).locator(".gb-token").filter(has=page.locator("strong", has_text=re.compile(f"^{re.escape(reader)}$")))
    row.get_by_role("button", name=f"Revoke the token {reader}").click()
    gitberth.confirm(page, "Revoke")
    gitberth.snackbar(page, re.escape(f"Token {reader} is revoked."))
    status, text = rpc(gitberth, read_token, "tools/list", rid=4)
    assert status == 401, (status, text)
    assert json.loads(text)["error"] == f"The token {reader} was revoked.", text
    status, headers, _ = call("POST", gitberth.url("/mcp"), body={"jsonrpc": "2.0", "id": 1, "method": "tools/list"},
                              headers={"Authorization": f"Bearer {read_token}", "Accept": "application/json, text/event-stream"})
    challenge = next(v for k, v in headers.items() if k.lower() == "www-authenticate")
    assert status == 401 and challenge == 'Bearer realm="Gitberth", error="invalid_token", error_description="The access token was revoked"', (status, headers)
    until(lambda: [e["action"] for e in audit.since(0, "action = 'auth.token.revoked' AND target = ?", reader)] == ["auth.token.revoked"], 15, "the revoke in the audit log")

    # ---- Not a bearer token at all (Basic, as a browser or another tool may send): 401 JSON and a plain Bearer challenge.
    for path in ("/api/v1/tools", "/mcp"):
        status, headers, text = call("POST" if path == "/mcp" else "GET", gitberth.url(path), body={} if path == "/mcp" else None,
                                     headers={"Authorization": "Basic YWRtaW46cGFzc3dvcmQ="})
        assert status == 401, (path, status, text)
        assert "Authorization: Bearer" in json.loads(text)["error"], text
        challenge = next(v for k, v in headers.items() if k.lower() == "www-authenticate")
        assert challenge == 'Bearer realm="Gitberth"' and re.fullmatch(r"[\x20-\x7E]*", challenge), challenge

    # ---- A browser page's request (an Origin header): 403, even with a good token.
    status, text = rpc(gitberth, token, "tools/list", rid=5, headers={"Origin": "http://evil.example"})
    assert status == 403, (status, text)
    assert "Origin" in json.loads(text)["error"], text

    # ---- The signed-in cookie never counts on /mcp.
    cookies = "; ".join(f"{c['name']}={c['value']}" for c in page.context.cookies() if c["name"].startswith("gitberth"))
    assert "gitberth.auth=" in cookies, cookies
    status, text = rpc(gitberth, None, "tools/list", rid=6, headers={"Cookie": cookies})
    assert status == 401, (status, text)

    # ---- The same deploy through the HTTP API, recorded over the API.
    mark = audit.last_id()
    status, _, text = call("POST", gitberth.url("/api/v1/tools/deploy_app"), body={"app": app.key, "wait": True},
                           headers={"Authorization": f"Bearer {token}"}, timeout=660)
    assert status == 200, (status, text)
    done = json.loads(text)
    assert done["status"] == "Succeeded", done
    told = until(lambda: (lambda t: t if len(t) >= 2 else None)(audit.since(mark, "action IN ('app.deploy.queued', 'app.deploy.succeeded') AND app_id = ?", app.id)),
                 30, "the API's deploy queued and succeeded in the audit log")
    assert [(e["action"], e["actor"], e["via"]) for e in told] == [("app.deploy.queued", name, "Api"), ("app.deploy.succeeded", name, "Api")], told
    # A Read token over the API: 401 (revoked), and the tools and the OpenAPI document with a good one.
    status, _, _ = call("POST", gitberth.url("/api/v1/tools/list_apps"), body={}, headers={"Authorization": f"Bearer {read_token}"})
    assert status == 401
    status, _, text = call("GET", gitberth.url("/api/v1/openapi.json"), headers={"Authorization": f"Bearer {token}"})
    assert status == 200, text
    spec = json.loads(text)
    assert spec["openapi"] == "3.1.0" and "/api/v1/tools/deploy_app" in spec["paths"], list(spec["paths"])[:5]
    # Last used: when, and from where.
    used = gitberth.db.query("SELECT LastUsedAt, LastUsedIp FROM ApiTokens WHERE Name = ?", name)[0]
    assert used["LastUsedAt"] and used["LastUsedIp"], used

    # ---- deploy_app with commit: one commit of the app's own branch, and nothing else.
    def api_deploy(args, timeout=60):
        status, _, text = call("POST", gitberth.url("/api/v1/tools/deploy_app"), body={"app": app.key, **args},
                               headers={"Authorization": f"Bearer {token}"}, timeout=timeout)
        return status, json.loads(text)

    # A commit on main that production never ran: the push's own deploy skips it ([skip ci]).
    before = gitberth.db.last_deployment_id(app.id)
    v2 = repo.commit({"marker.txt": "mcp v2"}, message="mcp v2 [skip ci]")["sha"]
    skipped = gitberth.wait_deploy(app.id, after=before, statuses=("Skipped",), timeout=240)
    assert skipped["CommitSha"] == v2, skipped
    status, done = api_deploy({"commit": v2[:10], "wait": True}, timeout=660)
    assert status == 200, done
    assert (done["status"], done["commit"]) == ("Succeeded", v2[:7]), done
    error, found = tool(gitberth, token, "get_deployment", {"deployment_id": done["deployment_id"], "grep": "is on main"})
    assert not error and any(f"Checking that {v2[:10]} is on main" in line for line in found["deployment"]["log_tail"]), found
    # A commit of another branch, which GitHub serves by its SHA too: refused when it's fetched, nothing built.
    side = repo.commit({"marker.txt": "side"}, branch="side", base="main", message="side")["sha"]
    status, done = api_deploy({"commit": side, "wait": True}, timeout=660)
    assert status == 200, done
    assert (done["status"], done["error"]) == ("Failed", "That commit isn't on main."), done
    assert gitberth.db.deployment(done["deployment_id"])["CommitSha"] is None
    # Anything but a SHA, or a ref, is refused before anything is queued.
    queued = gitberth.db.last_deployment_id(app.id)
    status, refused = api_deploy({"commit": "main"})
    assert (status, refused["error"]) == (400, "commit is a commit's SHA: 7 to 40 hex characters, like 1d2948c."), refused
    status, refused = api_deploy({"ref": "refs/heads/side"})
    assert status == 400 and refused["error"].startswith("deploy_app doesn't take ref."), refused
    assert gitberth.db.last_deployment_id(app.id) == queued
