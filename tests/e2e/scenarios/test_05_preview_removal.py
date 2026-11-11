"""Scenario 5: removing previews.

- Remove, from the parent's Previews tab (a preview's menu) or from the preview's own page, asks first in a dialog that
  says what goes (its container, images and volumes, and its domain, which leaves the tunnel) and that a new commit
  makes it again; Cancel keeps it. Confirmed, it takes the preview's container, image, volumes (gb-<preview-slug>-…) and
  domain (its tunnel route and DNS record) and its rows, and the parent's Activity says so. Production is untouched: the
  same container, image and volumes (same ids, its data still there), deploys, and domain.
- A preview removed by hand doesn't come back on the watcher's later passes with the same commit; a new commit on its
  branch or pull request brings it back, under the same name, with fresh volumes and its domain again.
- Deleting a branch removes its preview, and only its; the parent's Activity says the branch was deleted.
- Turning previews off asks first, saying how many go with their containers, images, volumes and domains (Cancel keeps
  everything), then removes them all; the Previews tab says previews are off, and new commits make none.
- Deleting the parent removes its previews, with their containers, images, volumes and domains, and none is made or
  deployed again: not right after, nor on the watcher's next look, also when the deletion spans one of its looks
  (Cloudflare slow to take the change to the tunnel, so removing the first preview's domain takes a while).

Domains are on (with a Cloudflare token and a zone of the tunnel's account) for the scenarios that give the parent a
domain, and off again, without the token, once they're done. The previews' domains aren't protected here: protection is
scenario 8's.
"""
import contextlib
import json
import re
import time
from types import SimpleNamespace
from urllib.parse import urlsplit

import pytest
from playwright.sync_api import TimeoutError as PlaywrightTimeout

from e2e.engine import docker
from e2e.net import call, get
from e2e.wait import until
from test_02_pull_request_previews import (activity_texts, fetched, leftovers, new_app, open_pull, own_apps, preview_row, previews_card,
                                           text_of, unique, wait_deployed)
from test_04_preview_limit import answer_dialog, any_snackbar, choose_from_menu, menu_item, read_dialog

DOMAINS_SWITCH = "Manage app domains through Cloudflare"
PREVIEW_DATA = b"written into the preview's own volume"
PRODUCTION_DATA = b"written into production's volume"


# ---- domains ----

@pytest.fixture(scope="module")
def domains(gitberth, cloudflare):
    """Domains on in Settings, with a Cloudflare token Gitberth checked, and a zone of the tunnel's account for the apps'
    domains. Off again, and the token removed, once the module's scenarios (and so their apps) are done."""
    zone = f"{unique('rmz')}.test"
    cloudflare.zone(zone)
    page = gitberth.new_page()
    try:
        gitberth.open(page, "/settings")
        section = page.locator("#domains")
        switch = section.get_by_role("switch", name=DOMAINS_SWITCH)
        if switch.get_attribute("aria-checked") != "true":
            switch.click()
            gitberth.snackbar(page, "Domains turned on")
        section.locator("#cf-token").fill(cloudflare.token())
        section.get_by_role("button", name=re.compile("^(Save|Replace) token$")).click()
        said = gitberth.snackbar(page, "Token saved|Cloudflare")
        assert said.startswith("Token saved"), said
        page.context.close()
        yield SimpleNamespace(zone=zone)
    finally:
        restore = gitberth.new_page()
        try:
            gitberth.open(restore, "/settings")
            section = restore.locator("#domains")
            switch = section.get_by_role("switch", name=DOMAINS_SWITCH)
            if switch.get_attribute("aria-checked") == "true":
                switch.click()
                gitberth.snackbar(restore, "Domains turned off")
            remove = section.get_by_role("button", name="Remove", exact=True)
            with contextlib.suppress(PlaywrightTimeout):
                remove.wait_for(timeout=10_000)
            if remove.count() > 0:
                remove.click()
                gitberth.snackbar(restore, "Cloudflare token removed")
        finally:
            restore.context.close()
            with contextlib.suppress(Exception):
                page.context.close()


def answer_via(cloudflare, host):
    """What https://<host>/ answers through Cloudflare's edge and the tunnel: its status, the test app's marker, the error."""
    r = cloudflare.visit(f"https://{host}/")
    body = None
    if r.get("status") == 200:
        with contextlib.suppress(ValueError, TypeError):
            body = json.loads(r["body"])
    return {"status": r.get("status"), "marker": (body or {}).get("marker"), "error": r.get("error"), "note": (r.get("route") or {}).get("note")}


def marker_via(cloudflare, host):
    return answer_via(cloudflare, host)["marker"]


def add_domain(page, gitberth, cloudflare, app, hostname):
    """The app's Network tab, Domains card: adds the domain, and waits for Gitberth's tunnel route and DNS record for it."""
    gitberth.open(page, f"/apps/{app.key}/network")
    card = page.locator("section[aria-labelledby='gb-domains-title']")
    box = card.locator("input[aria-label='New domain']")
    box.wait_for()
    until(box.is_enabled, 30, "the Domains card to take a domain", seen=lambda: text_of(card))
    box.fill(hostname)
    card.get_by_role("button", name="Add domain", exact=True).click()
    card.locator(".gb-drow", has_text=hostname).wait_for(timeout=60_000)
    until(lambda: cloudflare.rules_for(hostname) and cloudflare.dns_records(hostname), 60, f"Gitberth's route and DNS record for {hostname}",
          seen=lambda: {"rules": cloudflare.rules_for(hostname), "dns": cloudflare.dns_records(hostname), "card": text_of(card)})


# ---- what an app has, and what's left of it ----

def volume_created(name):
    """When Docker made the volume (it's another volume once that changes); None when there's no such volume."""
    out = docker("volume", "inspect", name, check=False)
    data = json.loads(out) if out.strip().startswith("[") else []
    return data[0].get("CreatedAt") if data else None


def resources(gitberth, engine, row):
    """What Gitberth and the engine have of an app (a preview, or a parent): its container, image, images, volumes,
    domains and deploys."""
    slug = row["Slug"]
    c = engine.inspect(f"gb-{slug}") or {}
    return SimpleNamespace(
        id=row["Id"], slug=slug, container=c.get("Id"), started=(c.get("State") or {}).get("StartedAt"), image=c.get("Image"),
        images=engine.images(f"gitberth/{slug}"), volumes=engine.volumes(prefix=f"gb-{slug}-"),
        mounts=sorted(m.get("Name") for m in c.get("Mounts") or [] if m.get("Type") == "volume" and m.get("Name")),
        domains=[d["Hostname"] for d in gitberth.db.domains(row["Id"])],
        deployments=[d["Id"] for d in gitberth.db.deployments(row["Id"])])


def what_is_left(gitberth, engine, cloudflare, res):
    """Everything of the app that's still there: in the engine (its container, images, volumes, the image it ran, the
    volumes it mounted), in Cloudflare (its domains' tunnel rules and DNS records), and in Gitberth (its rows, a domain
    removal still to finish)."""
    left = leftovers(engine, res.slug)
    left += [f"the volume {v} it mounted" for v in res.mounts if engine.volume_exists(v) and f"the volume {v}" not in left]
    if res.image and engine.image_exists(res.image):
        left.append(f"the image it ran ({res.image[:19]})")
    for host in res.domains:
        if cloudflare.rules_for(host):
            left.append(f"the tunnel's rules for {host}: {cloudflare.rules_for(host)}")
        if cloudflare.dns_records(host):
            left.append(f"the DNS records of {host}: {cloudflare.dns_records(host)}")
        if gitberth.db.query("SELECT Hostname FROM DomainRemovals WHERE Hostname = ?", host):
            left.append(f"a removal of {host} still to finish")
    if gitberth.db.app_by_id(res.id) is not None:
        left.append("its app row")
    for table in ("Previews", "Deployments", "Ports", "Volumes", "Domains"):
        rows = gitberth.db.query(f"SELECT AppId FROM {table} WHERE AppId = ?", res.id)
        if rows:
            left.append(f"{len(rows)} row(s) in {table}")
    return left


def production_state(gitberth, engine, cloudflare, app, host=None):
    """Production as the engine, Gitberth and Cloudflare have it: its container (id, start), image (id and tags), volumes
    (each with when Docker made it), deploys, ports, and its domain's route and DNS record."""
    c = engine.inspect(app.container)
    assert c is not None, f"production's container {app.container} is gone"
    row = gitberth.db.app(app.slug)
    volumes = sorted(m["Name"] for m in c.get("Mounts") or [] if m.get("Type") == "volume" and m.get("Name"))
    state = {"container": c["Id"], "started": c["State"]["StartedAt"], "running": c["State"]["Running"], "image": c["Image"],
             "image ref": c["Config"]["Image"], "images": engine.images(app.image_repository),
             "volumes": {v: volume_created(v) for v in volumes}, "last deploy": gitberth.db.last_deployment_id(app.id),
             "commit": row and row["CurrentCommit"],
             "ports": [(p["ContainerPort"], p["HostPort"], p["IsWeb"]) for p in gitberth.db.ports(app.id)]}
    if host is not None:
        state["domain"] = {"rules": cloudflare.rules_for(host), "dns": sorted(r["id"] for r in cloudflare.dns_records(host)),
                           "rows": [(d["Hostname"], d["Service"]) for d in gitberth.db.domains(app.id)]}
    return state


def changes(before, after):
    return {k: {"before": before.get(k), "after": after.get(k)} for k in sorted(set(before) | set(after)) if before.get(k) != after.get(k)}


def keep_production_data(gitberth, app):
    """Writes a file into production's /data volume, to see it's still there after."""
    status, _, text = call("POST", f"{gitberth.lan_url(app.id)}/file?path=/data/production.txt", PRODUCTION_DATA)
    assert status == 200, text


def check_production_untouched(gitberth, engine, cloudflare, app, before, host=None):
    after = production_state(gitberth, engine, cloudflare, app, host)
    assert before["volumes"], f"production had no volume to compare: {before}"
    assert after == before, f"production changed: {changes(before, after)}"
    url = gitberth.lan_url(app.id)
    assert get(f"{url}/file?path=/data/production.txt") == (200, PRODUCTION_DATA.decode()), "production's data is gone"
    assert gitberth.wait_answers(url, "/")["marker"] == "production"
    if host is not None:
        until(lambda: marker_via(cloudflare, host) == "production", 30, f"{host} to answer with production",
              seen=lambda: answer_via(cloudflare, host))


def with_two_previews(page, gitberth, repo, app, cloudflare, engine):
    """Previews of pull requests and branches turned on, their domains not protected: a pull request from the branch
    pr-work opened before, and a new branch feature pushed after (so a new commit). Both previewed and deployed, each
    answering on its own domain, with a file in its own /data volume. Returns ({label: preview}, the pull request's
    number)."""
    pull_n, pull_sha = open_pull(repo, "pr-work", "Work in a pull request", marker="pull request work")
    said = gitberth.set_previews(page, app, mode="Pull requests and branches", protect=False)
    assert said.startswith("Saved"), said
    pull = gitberth.wait_preview(app.id, pr=pull_n, timeout=90)
    # The look that made it noted the branches there were: a branch pushed now has a new commit, and gets a preview.
    feature_sha = repo.commit({"marker.txt": "feature work"}, branch="feature", message="Work on a feature")["sha"]
    feature = gitberth.wait_preview(app.id, branch="feature", timeout=90)
    made = {}
    for label, row, sha, marker, branch in ((f"PR #{pull_n}", pull, pull_sha, "pull request work", "pr-work"),
                                             ("feature", feature, feature_sha, "feature work", "feature")):
        wait_deployed(gitberth, row["Id"], sha)
        host = until(lambda: [d["Hostname"] for d in gitberth.db.domains(row["Id"])], 60, f"{label}'s preview to have a domain",
                     seen=lambda: gitberth.db.domains(row["Id"]))[0]
        until(lambda: marker_via(cloudflare, host) == marker, 90, f"{host} to answer with {label}'s preview",
              seen=lambda: {"answer": answer_via(cloudflare, host), "rules": cloudflare.rules_for(host), "dns": cloudflare.dns_records(host)})
        status, _, text = call("POST", f"{gitberth.lan_url(row['Id'])}/file?path=/data/preview.txt", PREVIEW_DATA)
        assert status == 200, text
        made[label] = SimpleNamespace(label=label, row=row, sha=sha, marker=marker, branch=branch, host=host,
                                      kind="pull request" if branch == "pr-work" else "branch")
    for p in made.values():
        p.res = resources(gitberth, engine, p.row)
    return made, pull_n


def activity_sub(page, gitberth, app, wanted):
    """The parent's Activity tab: the entry with this title, and what kind of entry it says it is."""
    gitberth.open(page, f"/apps/{app.key}/activity")
    entry = page.locator(".gb-tl li").filter(has=page.locator(".gb-tl-title", has_text=wanted))
    entry.first.wait_for(timeout=20_000)
    return text_of(entry.first.locator(".gb-tl-sub"))


def remove_dialog_text(p):
    """What the Remove dialog says for a preview with a domain."""
    return (f"Remove the preview of {p.label}?",
            f"Its container, images and volumes go, and {p.host} leaves the tunnel. A new commit on the {p.kind} makes it again.",
            ["Cancel", "Remove"])


# ---- removed by hand: from the Previews tab, and from its own page ----

@pytest.fixture(scope="class")
def by_hand(gitberth, github, engine, cloudflare, standins, domains):
    """An app with a domain, previews of pull requests and branches, and two previews, each with its own domain: the
    branch's is removed from the Previews tab's menu (after a Cancel), the pull request's from its own page."""
    with own_apps(gitberth, standins, "removed-by-hand") as (page, made):
        repo, app, production = new_app(page, gitberth, github, made.track, unique("rbh"))
        main_host = f"{app.slug}.{domains.zone}"
        add_domain(page, gitberth, cloudflare, app, main_host)
        until(lambda: marker_via(cloudflare, main_host) == "production", 60, f"{main_host} to answer with production",
              seen=lambda: answer_via(cloudflare, main_host))
        previews, pull_n = with_two_previews(page, gitberth, repo, app, cloudflare, engine)
        keep_production_data(gitberth, app)
        before = production_state(gitberth, engine, cloudflare, app, main_host)
        feature, pull = previews["feature"], previews[f"PR #{pull_n}"]

        # The branch's preview, from the Previews tab's menu: Cancel first, then Remove.
        gitberth.open(page, f"/apps/{app.key}/previews")
        choose_from_menu(page, "feature", "Remove")
        cancelled = read_dialog(page)
        answer_dialog(page, "Cancel")
        kept = {"row": gitberth.preview(app.id, branch="feature") is not None, "running": engine.running(f"gb-{feature.res.slug}"),
                "listed": preview_row(page, "feature").count() == 1}
        choose_from_menu(page, "feature", "Remove")
        tab_dialog = read_dialog(page)
        answer_dialog(page, "Remove")
        tab_said = any_snackbar(page, gitberth)
        try:
            preview_row(page, "feature").wait_for(state="detached", timeout=20_000)
            tab_unlisted = True
        except PlaywrightTimeout:
            tab_unlisted = False

        # The pull request's preview, from its own page's menu.
        gitberth.open(page, f"/apps/gb-{pull.res.slug}")
        page.locator(".gb-banner", has_text="Preview of").wait_for()
        page.locator(".gb-hero-actions button[aria-label='More actions']").click()
        entry = menu_item(page, "Remove")
        entry.wait_for()
        entry.click()
        own_dialog = read_dialog(page)
        answer_dialog(page, "Remove")
        own_said = any_snackbar(page, gitberth)
        with contextlib.suppress(PlaywrightTimeout):
            page.wait_for_url(re.compile(rf"/apps/{re.escape(app.key)}/previews$"), timeout=20_000)
        own_landed = gitberth.path_of(page)
        removed = standins.seq()
        yield SimpleNamespace(repo=repo, app=app, production=production, main_host=main_host, previews=previews, pull_n=pull_n,
                              feature=feature, pull=pull, before=before, cancelled=cancelled, kept=kept, tab_dialog=tab_dialog,
                              tab_said=tab_said, tab_unlisted=tab_unlisted, own_dialog=own_dialog, own_said=own_said,
                              own_landed=own_landed, removed=removed)


class TestRemovedByHand:
    def test_the_dialogs_say_what_goes_and_cancel_keeps_it(self, by_hand):
        w = by_hand
        # From the Previews tab: what goes, the domain by name, and how it comes back. Cancel kept everything.
        assert w.cancelled == remove_dialog_text(w.feature), w.cancelled
        assert w.kept == {"row": True, "running": True, "listed": True}, f"Cancel didn't keep the preview: {w.kept}"
        assert w.tab_dialog == remove_dialog_text(w.feature), w.tab_dialog
        assert w.tab_said == "Removed the preview of feature.", w.tab_said
        assert w.tab_unlisted, "the Previews tab still lists the removed preview"
        # From its own page: the same, for a pull request, and back to the parent's previews.
        assert w.own_dialog == remove_dialog_text(w.pull), w.own_dialog
        assert w.own_said == f"Removed the preview of PR #{w.pull_n}.", w.own_said
        assert w.own_landed == f"/apps/{w.app.key}/previews", f"its page went to {w.own_landed}, not the parent's previews"

    def test_its_container_image_volumes_and_domain_go(self, by_hand, page, gitberth, engine, cloudflare):
        w = by_hand
        for p in (w.feature, w.pull):
            res = p.res
            # What it had: its own container, image, volume and domain.
            assert res.container and res.images and res.volumes and res.domains == [p.host], vars(res)
            assert all(v.startswith(f"gb-{res.slug}-") for v in res.mounts), f"{p.label}'s preview mounted another's volumes: {res.mounts}"
            until(lambda: not what_is_left(gitberth, engine, cloudflare, res), 30, f"everything of {p.label}'s preview to go",
                  seen=lambda: what_is_left(gitberth, engine, cloudflare, res))
            gone = answer_via(cloudflare, p.host)
            assert gone["status"] is None and "Couldn't resolve host" in (gone["error"] or ""), f"{p.host} still leads somewhere: {gone}"
        assert gitberth.db.previews(w.app.id) == []
        texts = activity_texts(gitberth, w.app)
        for wanted in ("Preview feature removed.", f"Preview PR #{w.pull_n} removed."):
            assert wanted in texts, f"the parent's Activity doesn't say {wanted!r}: {texts}"
        assert activity_sub(page, gitberth, w.app, "Preview feature removed.") == "Removed"
        # The Previews tab has none left.
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        empty = previews_card(page).locator(".gb-empty", has_text="No previews yet")
        empty.wait_for()
        assert ("Open a pull request, or push a new commit to a branch other than main, and its preview shows up here within a minute."
                in text_of(empty)), text_of(empty)
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_production_is_untouched(self, by_hand, gitberth, engine, cloudflare):
        w = by_hand
        check_production_untouched(gitberth, engine, cloudflare, w.app, w.before, w.main_host)

    def test_it_doesnt_come_back_with_the_same_commit(self, by_hand, gitberth, engine):
        w = by_hand
        w.repo.wait_watcher_passes(3, since=w.removed, timeout=120)
        assert gitberth.db.previews(w.app.id) == [], [(p["Slug"], p["PreviewBranch"], p["PullRequest"]) for p in gitberth.db.previews(w.app.id)]
        for p in (w.feature, w.pull):
            assert gitberth.db.app(p.res.slug) is None and not engine.exists(f"gb-{p.res.slug}"), f"{p.label}'s preview came back"
            asked, wanted, shown = fetched(w.repo, w.removed, p.row["Ref"])
            assert not asked and p.sha not in wanted, f"{p.label}'s code was fetched again after its removal: {shown}"
            assert activity_texts(gitberth, w.app).count(f"Preview {p.label} added" + (": Work in a pull request." if p.kind == "pull request" else ".")) == 1
        # Its branch and pull request are still there, at the same commits.
        assert w.repo.branch_sha("feature") == w.feature.sha and w.repo.branch_sha("pr-work") == w.pull.sha

    def test_a_new_commit_brings_each_back(self, by_hand, page, gitberth, engine, cloudflare):
        w = by_hand
        shas = {"feature": w.repo.commit({"marker.txt": "feature, again"}, branch="feature", message="More on the feature")["sha"],
                f"PR #{w.pull_n}": w.repo.commit({"marker.txt": "pull request work, again"}, branch="pr-work", message="More in the pull request")["sha"]}
        markers = {"feature": "feature, again", f"PR #{w.pull_n}": "pull request work, again"}
        for p in (w.feature, w.pull):
            back = (gitberth.wait_preview(w.app.id, branch="feature", timeout=90) if p.kind == "branch"
                    else gitberth.wait_preview(w.app.id, pr=w.pull_n, timeout=90))
            assert back["Slug"] == p.res.slug, f"{p.label}'s preview came back as {back['Slug']}, not {p.res.slug}"
            deploy = wait_deployed(gitberth, back["Id"], shas[p.label])
            assert deploy["CommitSha"] == shas[p.label]
            # Its domain again, routed, answering with the new commit.
            until(lambda: [d["Hostname"] for d in gitberth.db.domains(back["Id"])] == [p.host], 60, f"{p.label}'s preview to have {p.host} again",
                  seen=lambda: gitberth.db.domains(back["Id"]))
            until(lambda: marker_via(cloudflare, p.host) == markers[p.label], 90, f"{p.host} to answer with the new commit",
                  seen=lambda: answer_via(cloudflare, p.host))
            # Fresh volumes: what the removed one wrote is gone.
            url = gitberth.lan_url(back["Id"])
            assert gitberth.wait_answers(url, "/")["marker"] == markers[p.label]
            assert get(f"{url}/file?path=/data/preview.txt")[0] == 404, "the preview came back with the removed one's volume"
            assert engine.volumes(prefix=f"gb-{back['Slug']}-"), "the preview came back without a volume of its own"
        texts = activity_texts(gitberth, w.app)
        assert texts.count("Preview feature added.") == 2 and texts.count(f"Preview PR #{w.pull_n} added: Work in a pull request.") == 2, texts
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        for label in ("feature", f"PR #{w.pull_n}"):
            pill = preview_row(page, label).locator(".gb-pill2")
            pill.wait_for()
            until(lambda: text_of(pill) == "Ready", 60, f"{label}'s row to say Ready", seen=lambda: text_of(pill))
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


# ---- deleting a branch ----

@pytest.fixture(scope="class")
def branch_deleted(gitberth, github, engine, standins):
    """An app with previews of pull requests and branches, and two branch previews; then one branch is deleted on GitHub,
    and the watcher is given time to notice. Whether its preview went in that time is for the scenarios to check."""
    with own_apps(gitberth, standins, "branch-deleted") as (page, made):
        repo, app, production = new_app(page, gitberth, github, made.track, unique("rmb"))
        mark = standins.seq()
        said = gitberth.set_previews(page, app, mode="Pull requests and branches")
        assert said.startswith("Saved"), said
        # The first look with previews on notes the branches there are: branches pushed after it get previews.
        repo.wait_pulls_listed(since=mark, timeout=90)
        shas = {b: repo.commit({"marker.txt": b}, branch=b, message=f"Work on {b}")["sha"] for b in ("doomed", "stays")}
        previews = {}
        for branch, sha in shas.items():
            p = gitberth.wait_preview(app.id, branch=branch, timeout=90)
            wait_deployed(gitberth, p["Id"], sha)
            previews[branch] = p
        keep_production_data(gitberth, app)
        before = production_state(gitberth, engine, None, app)
        doomed = resources(gitberth, engine, previews["doomed"])
        stays = resources(gitberth, engine, previews["stays"])
        started = time.monotonic()
        repo.delete_branch("doomed")
        try:
            gitberth.wait_no_preview(app.id, branch="doomed", timeout=90)
            waited = None
        except AssertionError as e:
            waited = str(e)
        yield SimpleNamespace(repo=repo, app=app, previews=previews, doomed=doomed, stays=stays, before=before, waited=waited,
                              took=time.monotonic() - started)


class TestDeletingABranch:
    def test_its_preview_goes_as_the_branch_was_deleted(self, branch_deleted, gitberth, engine, cloudflare):
        w = branch_deleted
        assert w.waited is None, f"the deleted branch's preview is still there {w.took:.0f} s later: {w.waited}"
        texts = activity_texts(gitberth, w.app)
        assert "Preview doomed removed: the branch was deleted." in texts, texts
        res = w.doomed
        assert res.container and res.images and res.volumes, vars(res)
        until(lambda: not what_is_left(gitberth, engine, cloudflare, res), 30, "everything of the deleted branch's preview to go",
              seen=lambda: what_is_left(gitberth, engine, cloudflare, res))

    def test_the_other_branchs_preview_and_production_are_untouched(self, branch_deleted, gitberth, engine):
        w = branch_deleted
        stays = gitberth.preview(w.app.id, branch="stays")
        assert stays is not None and stays["Id"] == w.stays.id, "the other branch's preview went too"
        c = engine.inspect(f"gb-{w.stays.slug}")
        assert c and (c["Id"], c["State"]["StartedAt"], c["State"]["Running"]) == (w.stays.container, w.stays.started, True), \
            f"the other branch's preview was touched: {(c or {}).get('State')}"
        assert gitberth.wait_answers(gitberth.lan_url(w.stays.id), "/")["marker"] == "stays"
        check_production_untouched(gitberth, engine, None, w.app, w.before)

    def test_the_previews_tab_and_activity_show_it(self, branch_deleted, page, gitberth):
        w = branch_deleted
        assert activity_sub(page, gitberth, w.app, "Preview doomed removed: the branch was deleted.") == "Removed"
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        pill = preview_row(page, "stays").locator(".gb-pill2")
        pill.wait_for()
        until(lambda: text_of(pill) == "Ready", 60, "the other branch's row to say Ready", seen=lambda: text_of(pill))
        assert preview_row(page, "doomed").count() == 0, "the Previews tab still lists the deleted branch"
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


# ---- turning previews off ----

@pytest.fixture(scope="class")
def turned_off(gitberth, github, engine, cloudflare, standins, domains):
    """An app with a domain and two previews (each with its own domain); then the Settings tab's Previews section set to
    Off and saved: the dialog answered with Cancel first, then with Turn off and remove."""
    with own_apps(gitberth, standins, "previews-off") as (page, made):
        repo, app, production = new_app(page, gitberth, github, made.track, unique("rmo"))
        main_host = f"{app.slug}.{domains.zone}"
        add_domain(page, gitberth, cloudflare, app, main_host)
        previews, pull_n = with_two_previews(page, gitberth, repo, app, cloudflare, engine)
        keep_production_data(gitberth, app)
        before = production_state(gitberth, engine, cloudflare, app, main_host)

        gitberth.open(page, f"/apps/{app.key}/settings")
        section = page.locator("#sec-previews")
        section.wait_for()
        section.get_by_role("radio", name="Off", exact=True).click()
        until(lambda: section.locator("#set-preview-limit").count() == 0, 15, "the section to leave out what Off doesn't need",
              seen=lambda: text_of(section))
        off_section = text_of(section)
        section.get_by_role("button", name="Save previews").click()
        cancelled = read_dialog(page)
        answer_dialog(page, "Cancel")
        after_cancel = {"mode": gitberth.db.app(app.slug)["PreviewMode"], "previews": len(gitberth.db.previews(app.id)),
                        "running": all(engine.running(f"gb-{p.res.slug}") for p in previews.values())}
        section.get_by_role("button", name="Save previews").click()
        confirmed = read_dialog(page)
        answer_dialog(page, "Turn off and remove")
        said = any_snackbar(page, gitberth)
        yield SimpleNamespace(repo=repo, app=app, main_host=main_host, previews=previews, pull_n=pull_n, before=before,
                              off_section=off_section, cancelled=cancelled, after_cancel=after_cancel, confirmed=confirmed, said=said)


class TestTurningPreviewsOff:
    def test_it_asks_first_and_cancel_keeps_them(self, turned_off):
        w = turned_off
        assert "No previews: only production runs." in w.off_section, w.off_section
        wanted = ("Turn previews off?", "Its 2 previews are removed now, with their containers, images, volumes and domains.",
                  ["Cancel", "Turn off and remove"])
        assert w.cancelled == wanted, w.cancelled
        assert w.after_cancel == {"mode": "PullRequestsAndBranches", "previews": 2, "running": True}, f"Cancel didn't keep them: {w.after_cancel}"
        assert w.confirmed == wanted, w.confirmed
        assert w.said == "Previews are off.", w.said

    def test_every_preview_goes_with_its_container_image_volumes_and_domain(self, turned_off, gitberth, engine, cloudflare):
        w = turned_off
        assert gitberth.db.app(w.app.slug)["PreviewMode"] == "Off"
        assert gitberth.db.previews(w.app.id) == [], [p["Slug"] for p in gitberth.db.previews(w.app.id)]
        texts = activity_texts(gitberth, w.app)
        for p in w.previews.values():
            assert p.res.container and p.res.images and p.res.volumes and p.res.domains == [p.host], vars(p.res)
            until(lambda: not what_is_left(gitberth, engine, cloudflare, p.res), 30, f"everything of {p.label}'s preview to go",
                  seen=lambda: what_is_left(gitberth, engine, cloudflare, p.res))
            assert f"Preview {p.label} removed: previews were turned off." in texts, texts

    def test_production_is_untouched(self, turned_off, gitberth, engine, cloudflare):
        w = turned_off
        check_production_untouched(gitberth, engine, cloudflare, w.app, w.before, w.main_host)

    def test_the_previews_tab_says_theyre_off_and_new_commits_make_none(self, turned_off, page, gitberth, engine, standins):
        w = turned_off
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        empty = page.locator(".gb-empty", has_text="Previews are off")
        empty.wait_for()
        assert (f"Turned on, every pull request (and, if you like, every branch) of {w.repo.full_name} gets an app of its own"
                in text_of(empty)), text_of(empty)
        empty.get_by_role("link", name="Turn previews on").click()
        page.wait_for_url(re.compile(rf"/apps/{re.escape(w.app.key)}/settings#sec-previews$"))
        section = page.locator("#sec-previews")
        section.wait_for()
        assert section.get_by_role("radio", name="Off", exact=True).get_attribute("aria-checked") == "true", text_of(section)
        # New commits on the branch and the pull request make no preview.
        mark = standins.seq()
        w.repo.commit({"marker.txt": "feature, later"}, branch="feature", message="Later on the feature")
        w.repo.commit({"marker.txt": "pull request, later"}, branch="pr-work", message="Later in the pull request")
        w.repo.wait_watcher_passes(2, since=mark, timeout=90)
        assert gitberth.db.previews(w.app.id) == [], [p["Slug"] for p in gitberth.db.previews(w.app.id)]
        assert not [n for n in engine.container_names(prefix=f"gb-{w.app.slug}-")], engine.container_names(prefix=f"gb-{w.app.slug}-")
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


# ---- deleting the parent ----

# How long after a deletion the scenarios watch for a preview of the deleted app to turn up: more than a watcher pass.
AFTER_DELETION = 15


def previews_of(gitberth, parent_id):
    """The apps that preview the parent, as Gitberth's database has them: [(id, slug)]."""
    return sorted((a["Id"], a["Slug"]) for a in gitberth.db.query("SELECT Id, Slug FROM Apps WHERE ParentAppId = ?", parent_id))


def delete_from_settings(page, gitberth, standins, app, slow_tunnel=False):
    """Settings › Remove › Delete app, with its images and named volumes (as gitberth.delete_app does it), waiting as long
    as the deletion takes. With slow_tunnel, Cloudflare answers the next change to the tunnel's configuration 15 seconds
    late (and takes it), as it does when it's slow: the removal of the first preview's domain then spans a watcher pass.
    Returns (when the deletion started, the stand-ins' marker then, how long it took)."""
    gitberth.open(page, f"/apps/{app.key}/settings")
    page.locator("#sec-remove").get_by_role("button", name=re.compile("^Remove")).click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    dialog.get_by_text("Also delete its named volumes").click()
    if slow_tunnel:
        standins.fail(r"/cfd_tunnel/[^/]+/configurations$", host="api.cloudflare.com", method="PUT", delay_ms=15_000, times=1)
    mark, started = standins.seq(), time.time()
    dialog.get_by_role("button", name="Delete app").click()
    page.wait_for_url(lambda u: urlsplit(u).path == "/apps", timeout=180_000)
    return started, mark, time.time() - started


@contextlib.contextmanager
def deleted_parent(gitberth, github, engine, cloudflare, standins, domains, name, prefix, slow_tunnel=False):
    """An app with a domain and two previews (each with its own domain), then the app deleted from its Settings tab with
    its images and named volumes; what previews it is read right after, and watched for a while longer."""
    with own_apps(gitberth, standins, name) as (page, made):
        repo, app, production = new_app(page, gitberth, github, made.track, unique(prefix))
        main_host = f"{app.slug}.{domains.zone}"
        add_domain(page, gitberth, cloudflare, app, main_host)
        previews, pull_n = with_two_previews(page, gitberth, repo, app, cloudflare, engine)
        parent = resources(gitberth, engine, gitberth.db.app(app.slug))
        had_skips = gitberth.db.setting(f"previews.skip.{app.id}") is not None
        started, mark, took = delete_from_settings(page, gitberth, standins, app, slow_tunnel)
        right_after = previews_of(gitberth, app.id)
        seen = set(right_after)
        end = time.monotonic() + AFTER_DELETION
        while time.monotonic() < end:
            seen |= set(previews_of(gitberth, app.id))
            time.sleep(0.5)
        yield SimpleNamespace(repo=repo, app=app, main_host=main_host, previews=previews, pull_n=pull_n, parent=parent,
                              had_skips=had_skips, started=started, mark=mark, took=took, right_after=right_after, seen=sorted(seen))


def check_its_previews_go(w, gitberth, engine, cloudflare, timeout=30):
    for p in w.previews.values():
        assert p.res.container and p.res.images and p.res.volumes and p.res.domains == [p.host], vars(p.res)
        until(lambda: not what_is_left(gitberth, engine, cloudflare, p.res), timeout, f"everything of {p.label}'s preview to go",
              seen=lambda: what_is_left(gitberth, engine, cloudflare, p.res))


def check_none_made_again(w, gitberth):
    """Once the parent's deletion started, no preview of it is made or deployed: none right after it's done, none in the
    time after, no deploy of one in Gitberth's log, no fetch of their code."""
    log = gitberth.logs(since=int(w.started) - 1)
    made = [x.strip() for x in log.splitlines()
            if f"Deploying preview {w.app.name} · " in x or f"Couldn't make the preview of {w.app.name} " in x]
    fetches = [(c.get("git") or {}).get("wants") for c in w.repo.git_calls(w.mark) if (c.get("git") or {}).get("command") == "fetch"]
    assert not w.right_after and not w.seen and not made and not fetches, (
        f"while {w.app.name} was deleted (it took {w.took:.0f} s), its previews were made again: right after the deletion Gitberth "
        f"had {w.right_after}, and in the {AFTER_DELETION} s after {w.seen}; its log since the deletion started: {made}; fetches of the "
        f"repo since: {fetches}")


class TestDeletingTheParent:
    @pytest.fixture(scope="class")
    def w(self, gitberth, github, engine, cloudflare, standins, domains):
        with deleted_parent(gitberth, github, engine, cloudflare, standins, domains, "parent-deleted", "rmp") as world:
            yield world

    def test_its_previews_go_with_their_containers_images_volumes_and_domains(self, w, gitberth, engine, cloudflare):
        check_its_previews_go(w, gitberth, engine, cloudflare)

    def test_no_preview_of_it_is_made_again(self, w, gitberth):
        check_none_made_again(w, gitberth)

    def test_the_parent_goes_with_its_domain_and_what_it_kept_for_previews(self, w, gitberth, engine, cloudflare):
        res = w.parent
        assert res.container and res.images and res.mounts and res.domains == [w.main_host], vars(res)
        until(lambda: not what_is_left(gitberth, engine, cloudflare, res), 30, "everything of the parent to go",
              seen=lambda: what_is_left(gitberth, engine, cloudflare, res))
        # What it remembered for its previews (the commits not to preview) goes with it.
        assert w.had_skips, "the parent had nothing remembered for its previews to check"
        assert gitberth.db.setting(f"previews.skip.{w.app.id}") is None


class TestDeletingTheParentWhileTheWatcherLooks:
    """The same, with Cloudflare slow to take the first change to the tunnel (15 s, well within Gitberth's 20 s), so the
    deletion surely spans one of the watcher's looks at the repo, as it can whenever deleting the previews takes a while."""

    @pytest.fixture(scope="class")
    def w(self, gitberth, github, engine, cloudflare, standins, domains):
        with deleted_parent(gitberth, github, engine, cloudflare, standins, domains, "parent-deleted-slowly", "rms", slow_tunnel=True) as world:
            yield world

    def test_its_previews_go_with_their_containers_images_volumes_and_domains(self, w, gitberth, engine, cloudflare):
        check_its_previews_go(w, gitberth, engine, cloudflare, timeout=90)

    def test_no_preview_of_it_is_made_again(self, w, gitberth):
        check_none_made_again(w, gitberth)
