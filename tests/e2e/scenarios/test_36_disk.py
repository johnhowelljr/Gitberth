"""Scenario 36: Docker's disk (the disk cleanup, src/Gitberth/Disk).

- Settings has its section, Disk, in the list on the left right after Deploys, with its tip, and Settings ›
  Notifications has its switch, Docker's disk gets full, on unless someone turned it off.
- Settings › Disk says how full the disk is and where its two lines are, has the build cache's row and the cleanup's
  switch, and cleans up on request: Clean up now says what it did, and the audit log has it as the person's.
- A deleted app's image that a container held is listed as waiting, and goes at the next cleanup once the container is
  gone.

Nothing can fill the rig's disk, so what a low or almost full disk does is unit-tested (DiskCleanupTests).
"""
import json
import re

from e2e.audit import last_id, since
from e2e.engine import docker
from e2e.wait import until
from test_02_pull_request_previews import new_app, own_apps, unique


def disk_section(gitberth, page):
    gitberth.open(page, "/settings")
    section = page.locator("section#disk")
    section.wait_for()
    # The section reads Docker's disk once it's open.
    until(lambda: "free of" in section.inner_text() or "couldn't be read" in section.inner_text(), 30, "Settings › Disk to read the disk",
          seen=section.inner_text)
    return section


# Clean up now's answers (src/Gitberth/Disk/DiskText.cs): what it did, or why not now: a build, Clear build cache, or a
# run of the cleanup's own (the daily one, or one for a low disk) holds builds off.
ANSWER = r"^(Cleaned up: |Nothing to clean up\.|Docker didn't clean up: |A cleanup is running already\.|.+: clean up once (it|that).s done\.)"
NOT_NOW = re.compile(r"running already|clean up once (it|that).s done")
# The person's own cleanups, not one the cleanup ran by itself meanwhile.
CLEANED = "action = 'settings.disk.cleaned' AND actor_kind = 'User'"


def clean_up_now(gitberth, page):
    """Clicks Clean up now and returns its answer. "Not now" is asked again, as a person would, until it cleans up."""
    said = []

    def ask():
        section = disk_section(gitberth, page)
        section.get_by_role("button", name="Clean up now").click()
        said.append(gitberth.snackbar(page, ANSWER, timeout=120_000))
        return None if NOT_NOW.search(said[-1]) else said[-1]

    return until(ask, 180, "Clean up now to clean up, not to say it can't now", interval=5,
                 seen=lambda: said or "no answer: no snackbar came")


def test_settings_has_the_disk_section_and_its_notification_switch(page, gitberth):
    gitberth.open(page, "/settings")
    links = page.locator(".gb-toc a")
    links.first.wait_for()
    names = [t.strip() for t in links.all_inner_texts()]
    assert "Disk" in names and names.index("Disk") == names.index("Deploys") + 1, names
    link = page.locator(".gb-toc a[href='settings#disk']")
    assert link.get_attribute("data-tip") == "settings.disk"
    section = page.locator("section#disk")
    section.wait_for()
    assert section.locator("#disk-t").inner_text().strip() == "Disk"

    switch = page.locator("#notifications").get_by_role("switch", name="Docker's disk gets full", exact=True)
    switch.wait_for()
    assert switch.get_attribute("aria-checked") == "true", "Docker's disk gets full isn't on"
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


def test_settings_disk_says_how_full_and_cleans_up_on_request(page, gitberth):
    section = disk_section(gitberth, page)
    text = section.inner_text()
    assert re.search(r"[\d.]+ (GB|MB) free of [\d.]+ (GB|MB)", text), text
    assert re.search(r"Gitberth warns under [\d.]+ (GB|MB) free and stops pulling images under [\d.]+ (GB|MB)\.", text), text
    # The build cache's row moved here from Deploys, and Deploys doesn't have it any more.
    section.locator("#build-cache").wait_for()
    assert page.locator("#deploys #build-cache").count() == 0
    switch = section.get_by_role("switch", name="Clean up by itself", exact=True)
    assert switch.get_attribute("aria-checked") == "true", "Clean up by itself isn't on"

    mark = last_id()
    said = clean_up_now(gitberth, page)
    assert said.startswith("Cleaned up: ") or said == "Nothing to clean up.", said
    assert "Today" in disk_section(gitberth, page).locator("#disk-last").inner_text(), "the last cleanup isn't today's"
    if said.startswith("Cleaned up: "):
        entry = until(lambda: since(mark, CLEANED), 30, "the cleanup in the audit log")[0]
        assert (entry["actor_kind"], entry["actor"], entry["via"]) == ("User", "admin", "UI"), entry
    else:
        assert since(mark, CLEANED) == [], "a cleanup that found nothing is in the audit log"
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


def test_a_deleted_apps_image_a_container_held_goes_at_the_next_cleanup(gitberth, github, standins, engine):
    with own_apps(gitberth, standins, "disk-leftover") as (page, made):
        _, app, _ = new_app(page, gitberth, github, made.track, unique("dl"))
        tags = engine.images(app.image_repository)
        assert tags, f"{app.slug} has no image"
        holder = f"gbe2e-hold-{app.slug}"
        # A container made outside Gitberth from the app's image: its deletion can't remove that image.
        docker("run", "-d", "--name", holder, tags[0], "sleep", "600")
        try:
            gitberth.delete_app(page, app, volumes=False)
            gitberth.wait_app(app.slug, gone=True)
            assert tags[0] in engine.images(app.image_repository), engine.images(app.image_repository)
            leftovers = json.loads(gitberth.db.setting("disk.leftovers") or "[]")
            assert {"repo": app.image_repository, "prefix": False} in [{"repo": l["repo"], "prefix": l["prefix"]} for l in leftovers], leftovers

            section = disk_section(gitberth, page)
            row = section.locator(".gb-notirow", has_text=app.slug)
            row.wait_for()
            assert "Waiting to be removed: a container uses it" in row.inner_text(), row.inner_text()
        finally:
            docker("rm", "-f", holder, check=False)

        mark = last_id()
        said = clean_up_now(gitberth, page)
        assert said.startswith("Cleaned up: "), said
        assert engine.images(app.image_repository) == [], engine.images(app.image_repository)
        entry = until(lambda: since(mark, CLEANED), 30, "the cleanup in the audit log")[0]
        assert tags[0] in (entry["changes"] or ""), entry
        assert json.loads(gitberth.db.setting("disk.leftovers") or "[]") == [], gitberth.db.setting("disk.leftovers")
        assert disk_section(gitberth, page).locator(".gb-notirow", has_text=app.slug).count() == 0
