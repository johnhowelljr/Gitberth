"""Scenario 24: the audit log.

Every change to the system is recorded in /data/audit.db with who made it, from where, and how it ended:

- Sign-ins, good and failed, with the browser's address. Through the UI, a new app, a settings change, a secret variable,
  the deploys they start, a stop and a start, a deploy hook made and deleted: each one entry, by admin through the UI from
  the browser's address, against the app, with its outcome. A deploy is told against the one who asked for it.
- The secret variable's value is nowhere: not in the log's file, not on the page, not in its exports.
- Docker changes made outside Gitberth (a docker stop and start of the app's container, a docker volume create, a docker
  run, through the rig's engine) are recorded as Docker's, "outside Gitberth"; Gitberth's own never are: its deploys
  (with and without a build), a Git app's settings change, a recreate through the change engine, a stop and a start, the
  removal of a container and the deletion of an app, and a registry app's update (the pull of its tag's new image from a
  registry in the rig, the recreate) and the roll back of it.
- The page (/audit) shows it all, filters it (the app's link from its Activity tab, the outside-Gitberth area), opens an
  entry with what changed, exports the view as CSV and JSON Lines, and checks the chain of hashes. Settings has its
  section, and changing it is recorded too.
"""
import json
import re
import secrets
import time

from e2e.audit import AUDIT, last_id, no_network_change_outside, since
from e2e.engine import DockerError, docker
from e2e.github_reports import set_variables
from e2e.wait import until
from test_23_deploy_hooks import create_hook, hook_row

IP =re.compile(r"^\d{1,3}(\.\d{1,3}){3}$|:")
# The rig's own containers, which it changes itself around a run (it restarts its fake cloudflared after a reset, and
# starts its runner): outside Gitberth, and rightly recorded so.
RIG = re.compile(r"^(gbe2e|cloudflare-tunnel$)")


def flat(text):
    return " ".join((text or "").split())


def wait_entry(first, action, what, where="1=1", *params, timeout=60):
    """Waits for an entry with this action after entry `first` (the writer stores entries a moment after they're made)."""
    return until(lambda: next(iter(since(first, f"action = ? AND {where}", action, *params)), None), timeout,
                 f"the audit log to record {what} ({action})",
                 seen=lambda: [(e["id"], e["action"], e["actor"], e["summary"]) for e in since(first)][-8:])


def by_person(entry):
    """Made by admin through the UI, from the browser's address."""
    assert (entry["actor_kind"], entry["actor"], entry["via"]) == ("User", "admin", "UI"), entry
    assert entry["ip"] and IP.search(entry["ip"]), entry
    return entry


def outside_but_not_the_rigs(first):
    """The changes recorded as made outside Gitberth after entry `first`, but for the rig's own."""
    return [(e["id"], e["action"], e["container"], e["target"], e["summary"]) for e in since(first, "actor_kind = 'Docker'")
            if not RIG.match(e["container"] or e["target"] or "")]


def raw_files():
    """The log's file as it lies on disk, with its write-ahead log."""
    out = b""
    for suffix in ("", "-wal"):
        try:
            with open(AUDIT.path + suffix, "rb") as f:
                out += f.read()
        except OSError:
            pass
    return out


def test_changes_are_recorded_with_who_made_them_and_nothing_secret(page, new_page, gitberth, github, engine, apps, uniq):
    first = last_id()
    secret = "e2e-audit-" + secrets.token_hex(12)

    # ---- Sign-ins: a wrong password, then the right one, each with the browser's address.
    visitor = new_page(signed_in=False)
    assert not gitberth.attempt_sign_in(visitor, password="not-it-" + secrets.token_hex(4)), "a wrong password signed in"
    failed = wait_entry(first, "auth.signin.failed", "the failed sign-in")
    assert (failed["actor"], failed["outcome"]) == ("admin", "Failed") and IP.search(failed["ip"] or ""), failed
    assert "wrong password" in failed["summary"], failed
    gitberth.sign_in(visitor)
    signed = wait_entry(first, "auth.signin.succeeded", "the sign-in")
    assert (signed["actor_kind"], signed["actor"], signed["outcome"]) == ("User", "admin", "Succeeded") and signed["ip"], signed

    # ---- A new app: created and deployed, both by admin.
    repo = github.create_repo(uniq("au"), files={"marker.txt": "audit v1"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    deploy = gitberth.wait_deploy(app.id, timeout=420)
    created = by_person(wait_entry(first, "app.created", "the new app"))
    assert (created["app_id"], created["container"]) == (app.id, app.container), created
    assert created["summary"] == f"Created {app.name} from github.com/{repo.owner}/{repo.name} (main).", created
    queued = by_person(wait_entry(first, "app.deploy.queued", "the first deploy queued", "correlation = ?", f"deployment:{deploy['Id']}"))
    assert queued["summary"] == f"Queued the first deploy of {app.name}.", queued
    # The deploy ran on the queue's own time, and is still told against admin, who asked for it.
    done = by_person(wait_entry(first, "app.deploy.succeeded", "the first deploy's success", "correlation = ?", f"deployment:{deploy['Id']}"))
    assert done["summary"].startswith(f"Deploy #{deploy['Id']} of {app.name} succeeded"), done
    assert gitberth.db.deployment(deploy["Id"])["RequestedBy"].startswith("User|UI|"), gitberth.db.deployment(deploy["Id"])

    # ---- A setting: the health URL, saved at once.
    gitberth.open(page, f"/apps/{app.key}/settings")
    page.locator("#set-health-url").fill("/")
    bar = page.get_by_role("region", name="Unsaved changes")
    button = bar.get_by_role("button", name=re.compile(r"^(Apply|Review and apply)$"))
    until(button.is_enabled, 30, "Apply to be offered", seen=bar.inner_text)
    button.click()
    gitberth.snackbar(page, "^Saved")
    changed = by_person(wait_entry(first, "app.settings.changed", "the settings change", "app_id = ?", app.id))
    assert changed["summary"] == f"Changed {app.name}'s settings: health URL.", changed
    assert json.loads(changed["changes"]) == [{"field": "Health URL", "from": "none", "to": "/"}], changed["changes"]

    # ---- A secret variable: its name, never its value; the deploy that applies it is admin's too.
    applied = set_variables(gitberth, page, app, [{"name": "AUDIT_TOKEN", "value": secret}])
    gitberth.wait_deploy(app.id, after=deploy["Id"], timeout=300)
    added = by_person(wait_entry(first, "app.env.added", "the variable", "target = ?", "AUDIT_TOKEN"))
    assert added["summary"] == f"Added the secret variable AUDIT_TOKEN to {app.name} (all deploys).", added
    assert json.loads(added["changes"])[0] == {"field": "Value", "from": None, "to": None}, added["changes"]
    by_person(wait_entry(first, "app.deploy.succeeded", "the deploy without a build", "correlation = ?", f"deployment:{applied}"))
    started = by_person(wait_entry(first, "app.deploy.started", "the deploy's start", "correlation = ?", f"deployment:{applied}"))
    assert started["summary"] == f"Started deploy #{applied} of {app.name} (settings change, {repo.branch_sha('main')[:7]}).", started

    # ---- The repo's variables in GitHub, from the same tab: saved and deleted, each by admin, against the app.
    gitberth.open(page, f"/apps/{app.key}/environment")
    stored = page.locator(".gb-card", has_text="Stored in GitHub")
    stored.get_by_label("Name", exact=True).fill("AUDIT_SITE")
    stored.get_by_label("Value", exact=True).fill("audit-site-1")
    stored.get_by_role("button", name="Save to GitHub").click()
    gitberth.snackbar(page, "^Saved AUDIT_SITE to GitHub")
    saved = by_person(wait_entry(first, "app.env.github.set", "the GitHub variable saved", "target = ?", "AUDIT_SITE"))
    assert (saved["app_id"], saved["container"]) == (app.id, app.container), saved
    assert json.loads(saved["changes"])[:2] == [{"field": "Repo", "from": None, "to": f"{repo.owner}/{repo.name}"},
                                               {"field": "Value", "from": None, "to": "audit-site-1"}], saved["changes"]
    stored.get_by_role("button", name="Delete AUDIT_SITE").click()
    gitberth.confirm(page, "Delete")
    gitberth.snackbar(page, "^Deleted AUDIT_SITE from GitHub")
    removed = by_person(wait_entry(first, "app.env.github.removed", "the GitHub variable deleted", "target = ?", "AUDIT_SITE"))
    assert removed["summary"].startswith(f"Deleted the variable AUDIT_SITE from {repo.owner}/{repo.name}'s variables in GitHub"), removed

    # ---- Stop and start, from the app page.
    gitberth.open(page, f"/apps/{app.key}")
    page.get_by_role("button", name=f"Stop {app.name}", exact=True).click()
    gitberth.confirm(page, "Stop")
    gitberth.snackbar(page, re.escape(f"Stopped {app.name}."))
    by_person(wait_entry(first, "app.stopped", "the stop", "app_id = ?", app.id))
    page.get_by_role("button", name=f"Start {app.name}", exact=True).click()
    gitberth.snackbar(page, re.escape(f"Started {app.name}."))
    started = by_person(wait_entry(first, "app.started", "the start", "app_id = ?", app.id))
    assert started["summary"] == f"Started {app.name}.", started

    # ---- A deploy hook made and deleted.
    section = create_hook(gitberth, page, app, "Audit")
    hook_row(section, "Audit").get_by_role("button", name="Delete the deploy hook Audit").click()
    gitberth.confirm(page, "Delete")
    gitberth.snackbar(page, re.escape("Deploy hook Audit is deleted."))
    made = by_person(wait_entry(first, "app.hook.created", "the hook made", "app_id = ?", app.id))
    gone = by_person(wait_entry(first, "app.hook.deleted", "the hook deleted", "app_id = ?", app.id))
    assert (made["target"], gone["target"]) == ("Audit", "Audit"), (made, gone)

    # ---- Gitberth's own Docker work (a full deploy, a deploy without a build, a settings change, a stop and a start)
    # made no entry as a change outside Gitberth. Past the longest a change takes to be said (a kill waits 12 s).
    time.sleep(15)
    mine = last_id()
    assert outside_but_not_the_rigs(first) == [], outside_but_not_the_rigs(first)

    # ---- Docker changes made outside Gitberth, through the rig's engine.
    volume = uniq("gbe2e-outside")
    try:
        docker("stop", app.container)
        stopped = wait_entry(mine, "docker.container.stopped", "the stop made outside Gitberth", "container = ?", app.container)
        assert (stopped["actor_kind"], stopped["actor"], stopped["via"]) == ("Docker", "outside Gitberth", "Docker"), stopped
        assert (stopped["app_id"], stopped["summary"]) == (app.id, f"Stopped outside Gitberth: {app.container}."), stopped
        docker("start", app.container)
        wait_entry(mine, "docker.container.started", "the start made outside Gitberth", "container = ?", app.container)
        docker("volume", "create", volume)
        made_outside = wait_entry(mine, "docker.volume.created", "the volume made outside Gitberth", "target = ?", volume)
        assert made_outside["summary"] == f"Created the volume {volume} outside Gitberth.", made_outside
    finally:
        docker("volume", "rm", volume, check=False)
    engine.wait_running(app.container)

    # ---- The secret's value is nowhere in the log: its rows, its file, its exports, its page.
    assert not any(secret in json.dumps(e) for e in since(first)), "an entry holds the secret's value"
    assert secret.encode() not in raw_files(), "the log's file holds the secret's value"
    for fmt in ("csv", "jsonl"):
        export = page.request.get(gitberth.url(f"/audit/export?format={fmt}&range=all&app={app.container}"))
        assert export.status == 200, (fmt, export.status)
        body = export.text()
        assert secret not in body, f"the {fmt} export holds the secret's value"
        assert "AUDIT_TOKEN" in body and "app.deploy.succeeded" in body, body[:500]
        assert "attachment" in (export.headers.get("content-disposition") or ""), export.headers
    csv_head = page.request.get(gitberth.url("/audit/export?format=csv&range=all")).text().splitlines()[0]
    assert csv_head == "id,time,actor_kind,actor,via,ip,action,outcome,app_id,app,container,target,summary,changes,correlation,prev_hash,hash", csv_head
    lines = [json.loads(line) for line in page.request.get(gitberth.url(f"/audit/export?format=jsonl&range=all&app={app.container}")).text().splitlines()]
    assert len(lines) >= 12 and {line["container"] for line in lines} == {app.container}, lines[:3]
    # Newest first, and each as the log keeps it.
    assert [line["id"] for line in lines] == sorted((line["id"] for line in lines), reverse=True), [line["id"] for line in lines]
    kept = {e["id"]: e for e in since(first)}
    assert all(line["hash"] == kept[line["id"]]["hash"] for line in lines if line["id"] in kept), lines[:3]
    # Not signed in: no export.
    visitor.context.clear_cookies()
    refused = visitor.request.get(gitberth.url("/audit/export?format=csv&range=all"), max_redirects=0)
    assert refused.status in (302, 401), refused.status

    # ---- The page: the app's Activity links to its changes, newest first, each with who and from where.
    gitberth.open(page, f"/apps/{app.key}/activity")
    page.get_by_role("link", name=f"All changes to {app.name} in the audit log").click()
    page.wait_for_url(re.compile(rf"/audit\?range=all&app={re.escape(app.container)}$"))
    rows = page.locator(".audit-row")
    until(lambda: rows.count() >= 12, 30, "the app's entries on the page", seen=lambda: page.locator("main").inner_text()[:1500])
    text = flat(page.locator(".audit-list").inner_text())
    for said in (f"Created {app.name} from github.com/{repo.owner}/{repo.name} (main).", f"Changed {app.name}'s settings: health URL.",
                 f"Added the secret variable AUDIT_TOKEN to {app.name} (all deploys).", f"Stopped {app.name}.", f"Started {app.name}.",
                 f"Stopped outside Gitberth: {app.container}.", "Made the deploy hook Audit for", "Deleted the deploy hook Audit of"):
        assert said in text, (said, text[:3000])
    assert {"admin", "Docker"} <= set(page.locator(".audit-row .audit-who-name").all_inner_texts()), page.locator(".audit-row .audit-who-name").all_inner_texts()
    assert secret not in page.content(), "the page shows the secret's value"
    meta = flat(page.locator(".gb-hero-meta").inner_text())
    assert re.search(r"\d+ entries in the whole log that match", meta), meta

    # An entry opens with everything about it.
    panel = page.locator("aside.audit-panel")
    rows.filter(has_text=f"Changed {app.name}'s settings: health URL.").first.click()
    panel.wait_for()
    detail = flat(panel.inner_text())
    for said in ("app.settings.changed", "admin", "the UI", "Health URL", "none", "Tamper evidence", changed["hash"], changed["ip"]):
        assert said in detail, (said, detail)
    panel.get_by_role("button", name="Close").click()
    panel.wait_for(state="detached")
    rows.filter(has_text=f"Deploy #{deploy['Id']} of {app.name} succeeded").first.click()
    panel.wait_for()
    link = panel.get_by_role("link", name=re.compile(rf"^Deploy #{deploy['Id']} and its log$"))
    assert link.get_attribute("href") == f"apps/{app.id}/deployments/{deploy['Id']}", link.get_attribute("href")
    panel.get_by_role("button", name="Close").click()
    panel.wait_for(state="detached")

    # Filters, from their menus: the deploys only.
    page.locator("button.gb-filterbtn", has_text="Area").click()
    page.locator(".mud-menu-item").filter(has_text=re.compile(r"^\s*Deploys\s*$")).click()
    page.wait_for_url(re.compile(r"[?&]area=deploys(&|$)"))
    until(lambda: rows.count() >= 4 and all(a.startswith("app.deploy.") for a in page.locator(".audit-row .audit-action").all_inner_texts()), 30,
          "only the app's deploys", seen=lambda: page.locator(".audit-row .audit-action").all_inner_texts())
    # An entry opens from its own link.
    gitberth.open(page, f"/audit?range=all&entry={changed['id']}")
    panel.wait_for()
    assert flat(panel.locator(".audit-panel-title").inner_text()) == changed["summary"], panel.inner_text()
    panel.get_by_role("button", name="Close").click()
    panel.wait_for(state="detached")
    # By who, from the address: admin's changes to the app, and none of Docker's.
    gitberth.open(page, f"/audit?range=all&who=User%3Aadmin&app={app.container}")
    until(lambda: rows.count() >= 10, 30, "admin's changes to the app", seen=lambda: page.locator("main").inner_text()[:800])
    assert set(page.locator(".audit-row .audit-who-name").all_inner_texts()) == {"admin"}, page.locator(".audit-row .audit-who-name").all_inner_texts()

    # Outside Gitberth only.
    gitberth.open(page, "/audit?range=day&area=outside")
    until(lambda: rows.count() >= 3, 30, "the outside changes on the page", seen=lambda: page.locator("main").inner_text()[:800])
    outside = flat(page.locator(".audit-list").inner_text())
    assert f"Created the volume {volume} outside Gitberth." in outside, outside
    assert set(page.locator(".audit-row .audit-who-name").all_inner_texts()) == {"Docker"}, page.locator(".audit-row .audit-who-name").all_inner_texts()

    # Searching, and the integrity check.
    gitberth.open(page, f"/audit?range=all&q={app.name}")
    until(lambda: rows.count() >= 5, 30, "the search's entries", seen=lambda: page.locator("main").inner_text()[:800])
    page.get_by_role("button", name="Check the log's integrity").click()
    banner = page.locator(".gb-banner", has_text="The audit log is intact")
    banner.wait_for(timeout=60_000)
    assert "entries are as they were recorded" in flat(banner.inner_text()), banner.inner_text()

    # ---- Settings › Audit log: its section, and its own change recorded.
    gitberth.open(page, "/settings")
    section = page.locator("#audit")
    section.wait_for()
    assert "/data/audit.db" in section.inner_text(), section.inner_text()
    mark = last_id()
    section.locator("#audit-days").fill("400")
    section.locator("#audit-days").blur()
    section.get_by_role("button", name="Save").click()
    gitberth.snackbar(page, "keeps entries for 400 days")
    saved = by_person(wait_entry(mark, "settings.audit.changed", "the audit settings"))
    assert json.loads(saved["changes"]) == [{"field": "Keep entries for", "from": "365 days", "to": "400 days"}], saved["changes"]
    section.locator("#audit-days").fill("365")
    section.locator("#audit-days").blur()
    section.get_by_role("button", name="Save").click()
    gitberth.snackbar(page, "keeps entries for 365 days")

    # ---- Gitberth's change engine and its removals are its own too: a container started outside Gitberth (that start is
    # outside), then recreated with a new variable and removed through Gitberth, and the Git app deleted.
    copy = uniq("au-copy")
    try:
        mark = last_id()
        docker("run", "-d", "--name", copy, "python:3.12-alpine", "python", "-m", "http.server", "8080")
        made = wait_entry(mark, "docker.container.created", "the container made outside Gitberth", "container = ?", copy)
        assert made["summary"] == f"Created and started outside Gitberth: {copy}.", made
        mark = last_id()
        gitberth.open(page, f"/apps/{copy}/environment")
        table = page.get_by_role("table", name="Environment variables")
        table.wait_for()
        names = table.get_by_label("Variable name", exact=True)
        count = names.count()
        page.get_by_role("button", name="Add variable").click()
        until(lambda: names.count() == count + 1, 15, "a new variable's row", seen=table.inner_text)
        names.nth(count).fill("AUDIT_PLAIN")
        table.get_by_label("Value of AUDIT_PLAIN", exact=True).fill("one")
        bar = page.get_by_role("region", name="Unsaved changes")
        apply = bar.get_by_role("button", name=re.compile(r"^(Review and apply|Apply)$"))
        until(apply.is_enabled, 30, "Review and apply to be offered", seen=bar.inner_text)
        apply.click()
        dialog = page.locator(".mud-dialog")
        dialog.get_by_role("button", name=re.compile(r"^Recreate ")).click()
        dialog.get_by_role("button", name="Done").click(timeout=180_000)
        added = by_person(wait_entry(mark, "app.env.added", "the variable added through the change engine", "container = ?", copy))
        assert added["summary"].startswith("Added the variable AUDIT_PLAIN to "), added
        assert json.loads(added["changes"])[0] == {"field": "Value", "from": None, "to": "one"}, added["changes"]
        engine.wait_running(copy)
        assert engine.env(copy).get("AUDIT_PLAIN") == "one", engine.env(copy)

        gitberth.open(page, f"/apps/{copy}/settings")
        page.locator("#sec-remove").get_by_role("button", name=re.compile("^Remove")).click()
        page.locator(".mud-dialog").get_by_role("button", name=re.compile(r"^Remove ")).click()
        page.wait_for_url(re.compile(r"/apps$"), timeout=60_000)
        by_person(wait_entry(mark, "app.deleted", "the container removed through Gitberth", "container = ?", copy))
        engine.wait_gone(copy)
    finally:
        docker("rm", "-f", copy, check=False)
    gitberth.delete_app(page, app)
    deleted = by_person(wait_entry(mark, "app.deleted", "the app deleted", "app_id = ?", app.id))
    assert deleted["summary"].startswith(f"Deleted {app.name}: "), deleted
    # The deploys, the stop and the start through Gitberth, and the deletion: no network join or leave of the app's was outside,
    # however long its container's start or stop took. (The docker stop and start above were outside, and theirs went with them.)
    no_network_change_outside(app.container, first)
    assert outside_but_not_the_rigs(mark) == [], outside_but_not_the_rigs(mark)
    assert not gitberth.errors(page), gitberth.errors(page)


# A registry in the rig's engine (Docker's own registry image), on its port 5000: the engine pulls from localhost without TLS.
REGISTRY = "gbe2e-registry"


def build_image(tag, version):
    """A small web server image whose version is only a label, so each version is an image of its own."""
    docker("build", "-t", tag, "-", input=f'FROM python:3.12-alpine\nLABEL gbe2e.version="{version}"\nCMD ["python3", "-m", "http.server", "8080"]\n',
           timeout=300)
    return docker("image", "inspect", "--format", "{{.Id}}", tag).strip()


def push(tag):
    """Pushes to the rig's registry, which may take a moment to take the first push after it starts."""
    def pushed():
        try:
            docker("push", tag, timeout=180)
            return True
        except DockerError:
            return False

    until(pushed, 90, f"{tag} to be pushed to the rig's registry")


def test_a_registry_update_and_its_roll_back_through_gitberth_are_never_outside_changes(page, gitberth, engine, uniq):
    name = uniq("au-reg")
    image = f"localhost:5000/gbe2e/{name}:stable"
    try:
        # Outside Gitberth, and rightly recorded so: the registry, the image's first version pushed and run, then its next
        # version pushed under the same tag. The host's tag goes back to the version that runs, so Gitberth pulls the new one.
        docker("rm", "-f", REGISTRY, check=False)
        docker("run", "-d", "--name", REGISTRY, "-p", "5000:5000", "registry:2", timeout=300)
        first = build_image(image, "1")
        push(image)
        docker("run", "-d", "--name", name, image)
        engine.wait_running(name)
        build_image(f"gbe2e-{name}-next", "2")
        docker("tag", f"gbe2e-{name}-next", image)
        push(image)
        docker("tag", first, image)
        docker("image", "rm", f"gbe2e-{name}-next")
        wait_entry(0, "docker.container.created", "the registry app's container made outside Gitberth", "container = ?", name)
        time.sleep(5)
        mark = last_id()

        # Through Gitberth, from the app page: the check finds the tag's new image, the update pulls it and recreates the
        # container on it, then the roll back recreates it on the one it ran before.
        gitberth.open(page, f"/apps/{name}")
        page.locator(".gb-hero-actions").get_by_role("button", name="Check for update", exact=True).click()
        update = page.locator(".gb-hero-actions").get_by_role("button", name="Update", exact=True)
        update.wait_for(timeout=60_000)
        update.click()
        dialog = page.locator(".mud-dialog")
        dialog.get_by_role("button", name=re.compile(r"^Update ")).click(timeout=180_000)
        dialog.get_by_role("button", name="Done").click(timeout=180_000)
        dialog.wait_for(state="detached")
        applied = by_person(wait_entry(mark, "app.update.applied", "the update", "container = ?", name, timeout=120))
        assert applied["outcome"] == "Succeeded", applied
        until(lambda: engine.inspect(name)["Image"] != first, 30, "the container to run the new image", seen=lambda: engine.inspect(name)["Image"])

        roll_back = page.get_by_role("button", name=re.compile(r"^Roll back to "))
        until(lambda: roll_back.count() > 0, 60, "Roll back to be offered", seen=lambda: page.locator("main").inner_text()[:1500])
        roll_back.first.click()
        dialog.get_by_role("button", name=re.compile(r"^Roll back ")).click(timeout=60_000)
        dialog.get_by_role("button", name="Done").click(timeout=180_000)
        dialog.wait_for(state="detached")
        by_person(wait_entry(mark, "app.update.rolledback", "the roll back", "container = ?", name, timeout=120))
        until(lambda: engine.inspect(name)["Image"] == first, 30, "the container to run its first image again", seen=lambda: engine.inspect(name)["Image"])

        # Removed through Gitberth too.
        gitberth.open(page, f"/apps/{name}/settings")
        page.locator("#sec-remove").get_by_role("button", name=re.compile("^Remove")).click()
        page.locator(".mud-dialog").get_by_role("button", name=re.compile(r"^Remove ")).click()
        page.wait_for_url(re.compile(r"/apps$"), timeout=60_000)
        by_person(wait_entry(mark, "app.deleted", "the registry app removed through Gitberth", "container = ?", name))
        engine.wait_gone(name)

        # Past the longest a change takes to be said: none of it was outside Gitberth, its pull, image moves and network
        # joins and leaves included.
        no_network_change_outside(name, mark)
        assert outside_but_not_the_rigs(mark) == [], outside_but_not_the_rigs(mark)
        assert not gitberth.errors(page), gitberth.errors(page)
    finally:
        docker("rm", "-f", name, check=False)
        docker("rm", "-f", REGISTRY, check=False)
        for leftover in docker("image", "ls", "--format", "{{.Repository}}:{{.Tag}} {{.ID}}", check=False).splitlines():
            ref, _, image_id = leftover.partition(" ")
            if name in ref:
                docker("image", "rm", "-f", image_id, check=False)
