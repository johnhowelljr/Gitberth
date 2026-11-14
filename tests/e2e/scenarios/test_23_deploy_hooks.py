"""Scenario 23: deploy hooks.

A deploy hook is a secret URL that deploys an app when something calls it: a CMS whose content changed, a scheduled job,
CI. A Git app's hook builds its branch's newest commit again.

- An app's Settings tab has a Deploy hooks section. Create hook takes a name, lists the hook with its URL on the LAN
  (with a copy button) and when it was last used, and shows a curl line to try it. Without a host name for GitHub's
  webhooks it says how to reach hooks from the internet.
- A POST to the hook's LAN URL is answered 201 with the deploy's number, and builds the branch's newest commit again, the
  one that runs already. The deploy's trigger is the hook, by name, on its page, the Activity tab and the app page; the
  call itself is a line on the app's Activity, and the hook's Last used.
- A wrong token is a bare 404, anything but POST a bare 405. Delete asks first, and the deleted hook's URL is a 404.
- Through the tunnel: the host name of GitHub's webhooks takes deploy hooks too, by its one path rule. A tunnel with the
  rule from before deploy hooks (/hooks/github only) has it rewritten where it stands, never taken for someone else's
  or written twice. The section shows the hook's URL from the internet, which deploys the app through Cloudflare and the
  tunnel; other addresses under /hooks/deploy are the tunnel's 404, and a wrong token is Gitberth's.
"""
import hashlib
import json
import re
import secrets
from urllib.parse import urlsplit

from e2e.net import call
from e2e.wait import until
# Settings › Domains on with a Cloudflare token (scenario 12's), and instant deploys' host name with the tunnel's rules for
# it (scenario 14's), shared rather than copied.
from test_12_commit_statuses import domains_turned_on
from test_14_instant_deploys import COPY_SPY, NOT_FOUND, OWN_SERVICE, PATH_RULE, TURNED_ON, instant_deploys, notes, webhook_rules

SECTION = "#sec-hooks"
OLD_PATH = "^/hooks/github$"
TOKEN = re.compile(r"/hooks/deploy/([A-Za-z0-9_-]{43})$")


def flat(text):
    return " ".join((text or "").split())


def header(headers, name):
    return next((v for k, v in (headers or {}).items() if k.lower() == name.lower()), None)


def open_section(gitberth, page, app):
    gitberth.open(page, f"/apps/{app.key}/settings")
    section = page.locator(SECTION)
    section.wait_for()
    return section


def create_hook(gitberth, page, app, name):
    """Settings › Deploy hooks › Create hook, with this name. Returns the section."""
    section = open_section(gitberth, page, app)
    section.locator("#hook-name").fill(name)
    button = section.get_by_role("button", name="Create hook")
    until(button.is_enabled, 10, "Create hook to take the name")
    button.click()
    said = flat(gitberth.snackbar(page, re.escape(f"Deploy hook {name} is ready.")))
    assert said == f"Deploy hook {name} is ready.", said
    return section


def hook_row(section, name):
    return section.locator(".gb-hook").filter(has=section.page.locator("strong", has_text=re.compile(f"^{re.escape(name)}$")))


def url_shown(row, term):
    """The URL the hook's row shows under a term (On your network, From the internet)."""
    dd = row.locator(f"xpath=.//dl[contains(@class, 'gb-kv')]/dt[normalize-space(.)='{term}']/following-sibling::dd[1]")
    return flat(dd.locator("code").inner_text())


def post(url, body=None, method="POST", headers=None):
    """(status, headers, text) of a call to the hook's URL, as a CMS makes it."""
    return call(method, url, body=body, headers=headers)


def hook_rows(gitberth, app_id):
    return gitberth.db.query("SELECT * FROM DeployHooks WHERE AppId = ? ORDER BY Id", app_id)


def test_a_hook_made_in_settings_deploys_the_app_on_a_post(page, gitberth, github, apps, uniq):
    repo = github.create_repo(uniq("dh"), files={"marker.txt": "hook v1"})
    sha = repo.branch_sha("main")
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)

    # ---- The section, before there's a hook.
    section = open_section(gitberth, page, app)
    text = flat(section.inner_text())
    assert "Secret URLs that deploy it when something calls them: a CMS whose content changed, a scheduled job, CI." in text, text
    assert "No deploy hooks" in text, text
    assert ("These URLs work on your network. To call them from the internet (a CMS in the cloud, GitHub Actions), give GitHub's "
            "webhooks a host name in Settings › GitHub integration, which needs domains: deploy hooks go through it too.") in text, text
    assert section.get_by_role("link", name="Settings › GitHub integration").get_attribute("href") == "settings#github-integration"
    assert section.get_by_role("button", name="Create hook").is_disabled(), "Create hook works without a name"

    # ---- Create hook: listed with its LAN URL, never used yet, and a curl line to try it.
    section = create_hook(gitberth, page, app, "Contentful")
    row = hook_row(section, "Contentful")
    row.wait_for()
    url = url_shown(row, "On your network")
    assert url.startswith(gitberth.base + "/hooks/deploy/") and TOKEN.search(url), url
    token = TOKEN.search(url).group(1)
    assert "Never used" in flat(row.inner_text()), row.inner_text()
    assert row.locator("dt", has_text="From the internet").count() == 0, row.inner_text()
    assert flat(section.locator(".gb-code pre").inner_text()) == f"curl -X POST '{url}'"
    # Its copy button copies the URL.
    page.evaluate(COPY_SPY)
    row.get_by_role("button", name="Copy Contentful's URL on your network").click()
    gitberth.snackbar(page, "Copied to the clipboard")
    assert page.evaluate("() => window.__gbCopied") == [url]
    # Kept by its SHA-256, and encrypted, never as it is.
    [kept] = hook_rows(gitberth, app.id)
    assert (kept["Name"], kept["TokenHash"], kept["LastUsedAt"]) == ("Contentful", hashlib.sha256(token.encode()).hexdigest(), None), kept
    assert token not in kept["ProtectedToken"]

    # ---- A POST, as a CMS sends it (a body that means nothing to Gitberth): 201 with the deploy's number.
    status, headers, body = post(url, body={"sys": {"type": "Entry", "id": "hello"}})
    assert status == 201, (status, body)
    assert (header(headers, "Content-Type") or "").startswith("application/json"), headers
    answer = json.loads(body)
    assert (answer["app"], answer["status"]) == (app.name, "queued"), answer
    two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=420)
    # A full build of the branch's newest commit: the one that runs already, built again.
    assert (two["Id"], two["Trigger"], two["HookName"], two["CommitSha"]) == (answer["deployment"], "Hook", "Contentful", sha), two
    assert f"Building {app.image_repository}:{sha[:7]}" in gitberth.deploy_log(two["Id"]), gitberth.deploy_log(two["Id"], tail=30)
    assert hook_rows(gitberth, app.id)[0]["LastUsedAt"] is not None

    # ---- What the pages say: the hook, by its name.
    gitberth.open(page, f"/apps/{app.id}/deployments/{two['Id']}")
    meta = page.locator(".gb-hero-meta")
    meta.wait_for()
    tags = [flat(t) for t in meta.locator(".gb-tag").all_inner_texts()]
    assert 'Deploy hook "Contentful"' in tags, tags

    gitberth.open(page, f"/apps/{app.key}/activity")
    entry = page.locator("ol.gb-tl > li").filter(has_text=f'Deploy #{two["Id"]} (Deploy hook "Contentful") succeeded')
    entry.wait_for()
    assert 'Deploy hook "Contentful" · took' in flat(entry.inner_text()), entry.inner_text()
    page.locator("ol.gb-tl > li").filter(has_text=f'Deploy hook "Contentful" started deploy #{two["Id"]}').wait_for()
    assert [a["Text"] for a in gitberth.db.activity(app.id) if a["Text"].startswith("Deploy hook")] == [f'Deploy hook "Contentful" started deploy #{two["Id"]}']

    gitberth.open(page, f"/apps/{app.key}")
    hero = page.locator(".gb-hero")
    until(lambda: f"#{two['Id']}" in hero.inner_text() and '· deploy hook "Contentful"' in flat(hero.inner_text()), 30,
          "the app page's last deploy to be the hook's", seen=hero.inner_text)

    section = open_section(gitberth, page, app)
    until(lambda: "Last used just now" in flat(hook_row(section, "Contentful").inner_text()) or "min ago" in hook_row(section, "Contentful").inner_text(),
          10, "the hook's Last used", seen=lambda: hook_row(section, "Contentful").inner_text())

    # ---- A wrong token: a bare 404, never a page; anything but POST: a bare 405.
    wrong = f"{gitberth.base}/hooks/deploy/{secrets.token_urlsafe(32)}"
    status, headers, body = post(wrong)
    assert (status, body) == (404, ""), (status, body)
    status, headers, body = post(url, method="GET")
    assert (status, body, header(headers, "Allow")) == (405, "", "POST"), (status, headers, body)
    assert gitberth.db.last_deployment_id(app.id) == two["Id"], "a refused call deployed"

    # ---- Delete asks first; then the URL is unknown.
    hook_row(section, "Contentful").get_by_role("button", name="Delete the deploy hook Contentful").click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    assert "Whatever calls its URL gets 404 from then on" in flat(dialog.inner_text()), dialog.inner_text()
    gitberth.confirm(page, "Delete")
    gitberth.snackbar(page, re.escape("Deploy hook Contentful is deleted."))
    until(lambda: "No deploy hooks" in section.inner_text(), 10, "the hook to go from the list", seen=section.inner_text)
    assert hook_rows(gitberth, app.id) == []
    status, _, body = post(url)
    assert (status, body) == (404, ""), (status, body)
    assert gitberth.db.last_deployment_id(app.id) == two["Id"]
    assert not gitberth.errors(page), gitberth.errors(page)


def test_a_hook_through_the_tunnel_takes_the_webhooks_host_name(page, new_page, gitberth, github, cloudflare, apps, uniq):
    zone = f"{uniq('dt')}.test"
    host = f"hooks.{zone}"
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("dt"), files={"marker.txt": "tunnel v1"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)

    with domains_turned_on(gitberth, new_page, cloudflare):
        # The tunnel as 1.1 left it for this host name: its webhook's rule on /hooks/github alone, with its 404, after
        # another route.
        config = cloudflare.tunnel()["config"]
        before = list(config.get("ingress") or [])
        theirs = {"hostname": f"ci.{zone}", "service": "http://localhost:9"}
        config["ingress"] = [theirs, {"hostname": host, "path": OLD_PATH, "service": OWN_SERVICE}, {"hostname": host, "service": NOT_FOUND}] \
            + [r for r in before if r.get("hostname") != host]
        cloudflare.wait_runs(cloudflare.set_tunnel_config(config)["version"])
        try:
            with instant_deploys(gitberth, new_page, host) as (settings, said):
                # Gitberth's own rule, not someone else's: the name is taken, and the rule gets the new path where it stands.
                assert said == TURNED_ON.format(host), said
                ingress = cloudflare.ingress()
                assert cloudflare.rules_for(host) == webhook_rules(host), ingress
                assert ingress.index(webhook_rules(host)[0]) == 1 and ingress[0] == theirs, ingress
                assert [r for r in ingress if r.get("path") in (OLD_PATH, PATH_RULE)] == [webhook_rules(host)[0]], ingress
                assert f"https://{host}/hooks/deploy/… goes to Gitberth now too, for deploy hooks" in notes(settings), notes(settings)
                cloudflare.wait_runs()

                # ---- The hook's URL from the internet, on that host name.
                section = create_hook(gitberth, page, app, "CI")
                row = hook_row(section, "CI")
                row.wait_for()
                token = TOKEN.search(url_shown(row, "On your network")).group(1)
                public = url_shown(row, "From the internet")
                assert public == f"https://{host}/hooks/deploy/{token}", public
                assert f"From the internet, hooks go through {host}, the host name in Settings › GitHub integration." in flat(section.inner_text())

                # ---- Called through Cloudflare and the tunnel, by the one path rule: it deploys.
                visit = cloudflare.visit(public, method="POST", headers={"Content-Type": "application/json"}, body='{"ref":"main"}')
                cf = (visit.get("route") or {}).get("cloudflared") or {}
                assert visit["status"] == 201, visit
                assert (cf.get("rule_host"), cf.get("rule_path"), cf.get("service")) == (host, PATH_RULE, OWN_SERVICE), visit["route"]
                answer = json.loads(visit["body"])
                two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=420)
                assert (two["Id"], two["Trigger"], two["HookName"]) == (answer["deployment"], "Hook", "CI"), two

                # ---- Anything else under /hooks/deploy is the tunnel's own 404; a wrong token reaches Gitberth, which says 404.
                for path in (f"/hooks/deploy/{token}/x", "/hooks/deploy/short", "/hooks/deploy/", f"/hooks/deploy/{token}.json"):
                    other = cloudflare.visit(f"https://{host}{path}", method="POST")
                    route = (other.get("route") or {}).get("cloudflared") or {}
                    assert (other["status"], other["body"], route.get("rule_host"), route.get("service")) == (404, "", host, NOT_FOUND), (path, other)
                wrong = cloudflare.visit(f"https://{host}/hooks/deploy/{secrets.token_urlsafe(32)}", method="POST")
                route = (wrong.get("route") or {}).get("cloudflared") or {}
                assert (wrong["status"], wrong["body"], route.get("rule_path"), route.get("service")) == (404, "", PATH_RULE, OWN_SERVICE), wrong
                got = cloudflare.visit(public, method="GET")
                assert (got["status"], got["body"]) == (405, ""), got
                assert gitberth.db.last_deployment_id(app.id) == two["Id"], "a refused call deployed"
            # Turned off: the host's rules go, and theirs stay.
            assert cloudflare.rules_for(host) == [] and theirs in cloudflare.ingress(), cloudflare.ingress()
        finally:
            config = cloudflare.tunnel()["config"]
            config["ingress"] = [r for r in config.get("ingress") or [] if r != theirs and r.get("hostname") != host]
            cloudflare.set_tunnel_config(config)
    assert not gitberth.errors(page), gitberth.errors(page)
