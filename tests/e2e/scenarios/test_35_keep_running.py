"""Scenario 35: keeping apps running (the keep-running check, src/Gitberth/Keeper; the rig looks every 3 s, keepEvery=3).

- Settings has its section, Keep apps running, in the list on the left with its tip and its switch on, and Settings ›
  Notifications has its switch, Gitberth brings an app back, on unless someone turned it off.
- A crash its restart policy (Never) doesn't start again is brought back: the container runs again, its Activity and the
  audit log say so (Gitberth, through the keep-running check), and a webhook hears app.brought-back.
- An app stopped in Gitberth stays stopped.
- A container removed by hand comes back by a deploy without a build (Brought back).
- A container stopped by hand (docker stop) stays stopped, and its page says why.

The give-up after 4 tries is unit-only (AppKeeperTests): its back-off is minutes long.
"""
import re
from types import SimpleNamespace

import pytest

from e2e.audit import last_id, since
from e2e.engine import docker
from e2e.standin import Notify
from e2e.wait import stays, until
from test_02_pull_request_previews import new_app, own_apps, unique
from test_21_notifications import add_destination, crash, remove_all


def test_settings_has_the_keep_apps_running_section_and_its_notification_switch(page, gitberth):
    gitberth.open(page, "/settings")
    link = page.locator(".gb-toc a[href='settings#keep']")
    link.wait_for()
    assert link.inner_text().strip() == "Keep apps running", link.inner_text()
    assert link.get_attribute("data-tip") == "settings.keep"
    section = page.locator("section#keep")
    section.wait_for()
    assert section.locator("#keep-t").inner_text().strip() == "Keep apps running"
    keep = section.get_by_role("switch", name="Bring apps back by themselves", exact=True)
    keep.wait_for()
    assert keep.get_attribute("aria-checked") == "true", "Bring apps back by themselves isn't on"
    assert re.search(r"(Nothing brought back|Brought back .+) in the last 24 hours", section.inner_text()), section.inner_text()

    switch = page.locator("#notifications").get_by_role("switch", name="Gitberth brings an app back", exact=True)
    switch.wait_for()
    assert switch.get_attribute("aria-checked") == "true", "Gitberth brings an app back isn't on"
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


@pytest.fixture(scope="module")
def kept(gitberth, github, standins):
    """One test app, deployed, and a webhook destination for the module (removed after it)."""
    with own_apps(gitberth, standins, "keep-running") as (page, made):
        repo, app, first = new_app(page, gitberth, github, made.track, unique("kr"))
        add_destination(gitberth, page, "Webhook", {"Name": "Keep", "URL": Notify.WEBHOOK})
        try:
            yield SimpleNamespace(page=page, repo=repo, app=app, first=first, notify=Notify(standins))
        finally:
            remove_all(gitberth)


def started_at(engine, container):
    return (engine.inspect(container) or {}).get("State", {}).get("StartedAt")


def activity(gitberth, app, kind):
    return [a for a in gitberth.db.activity(app.id) if a["Kind"] == kind]


def test_a_crash_its_restart_policy_leaves_is_brought_back(kept, gitberth, engine, standins):
    app = kept.app
    # Never: Docker leaves it down after a crash.
    docker("update", "--restart=no", app.container)
    try:
        mark, heard, before = last_id(), standins.seq(), started_at(engine, app.container)
        crash(gitberth, app, code=3)
        until(lambda: engine.running(app.container) and started_at(engine, app.container) != before, 60, "Gitberth to start it again",
              seen=lambda: (engine.inspect(app.container) or {}).get("State"))

        tried = until(lambda: activity(gitberth, app, "BroughtBack"), 30, "its Activity to say it was brought back")[-1]
        assert tried["Text"] == "Gitberth started it again: it crashed (exit code 3), and its restart policy (no) doesn't start it again. Try 1 of 4.", tried
        entry = until(lambda: since(mark, "action = 'app.recovered' AND app_id = ?", app.id), 30, "the audit log's app.recovered")[0]
        assert (entry["actor_kind"], entry["via"], entry["outcome"]) == ("Gitberth", "KeepRunning", "Succeeded"), entry
        calls = kept.notify.wait(heard, "notify.e2e.test", lambda c: any((x.get("body") or {}).get("event") == "app.brought-back" for x in c),
                                 "the app.brought-back webhook", timeout=60)
        body = next(x["body"] for x in calls if (x.get("body") or {}).get("event") == "app.brought-back")
        assert (body["title"], body["container"]) == (f"{app.name} is running again", app.container), body
    finally:
        docker("update", "--restart=unless-stopped", app.container)


def test_an_app_stopped_in_gitberth_stays_stopped(kept, gitberth, engine):
    app, page = kept.app, kept.page
    gitberth.open(page, f"/apps/{app.key}")
    page.get_by_role("button", name=f"Stop {app.name}", exact=True).click()
    gitberth.confirm(page, "Stop")
    gitberth.snackbar(page, re.escape(f"Stopped {app.name}."))
    stays(lambda: not engine.running(app.container), 10, "it stays stopped")
    assert page.locator(".gb-banner").filter(has_text="Gitberth brings it back").count() == 0, gitberth.banners(page)

    page.get_by_role("button", name=f"Start {app.name}", exact=True).click()
    gitberth.snackbar(page, re.escape(f"Started {app.name}."))
    engine.wait_running(app.container, timeout=60)


def test_a_container_removed_by_hand_comes_back(kept, gitberth, engine):
    app = kept.app
    image = gitberth.db.app_by_id(app.id)["CurrentImage"]
    before = gitberth.db.last_deployment_id(app.id)
    docker("rm", "-f", app.container)

    engine.wait_running(app.container, timeout=120)
    back = gitberth.wait_deploy(app.id, after=before, timeout=120)
    # A deploy without a build, from the image it ran.
    assert (back["Trigger"], back["ImageTag"]) == ("Recover", image), back
    gitberth.wait_answers(gitberth.lan_url(app.id), "/")


def test_a_container_stopped_by_hand_stays_stopped(kept, gitberth, engine):
    app, page = kept.app, kept.page
    docker("stop", app.container)
    try:
        stays(lambda: not engine.running(app.container), 15, "it stays stopped")
        note = until(lambda: [a for a in activity(gitberth, app, "Event") if "Gitberth leaves it stopped" in a["Text"]], 15,
                     "its Activity to say Gitberth leaves it stopped")[-1]
        assert note["Text"].endswith("so Gitberth leaves it stopped. Start it to bring it back."), note
        gitberth.open(page, f"/apps/{app.key}")
        page.locator(".gb-banner").filter(has_text="It stopped without a crash").wait_for(timeout=30_000)
    finally:
        docker("start", app.container)
    engine.wait_running(app.container, timeout=60)
