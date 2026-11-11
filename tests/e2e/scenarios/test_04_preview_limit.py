"""Scenario 4: the limit on running previews.

- "At most running" on the Settings tab's Previews section: saved as 2, it shows 2 again with its help; a value outside
  1 to 20 is refused as it's typed ("Between 1 and 20.", Save previews off), a good one shows what it was, and Undo puts
  the saved one back without saving anything.
- With 2 previews running, a third pull request's preview is made but waits: no deploy, no container, no image, its code
  never fetched, through several watcher passes. Its row on the Previews tab says "Waiting: 2 previews run already",
  with nothing to open and no log, and the card counts 2 of 2 running.
- A preview that runs keeps its place: a new commit to it deploys while the third still waits.
- Removing one of the running previews (its menu's Remove, confirmed) lets the third start on the watcher's next look:
  it deploys, runs and answers, and never more than 2 run at once. The removed one doesn't come back.

The helpers here (the Previews tab's menu and its Remove, the confirmation dialogs, the snackbar) serve scenario 5 too.
"""
import re
from types import SimpleNamespace

import pytest

from e2e.gitberth import FINAL
from e2e.wait import until
from test_02_pull_request_previews import (activity_texts, fetched, leftovers, new_app, open_pull, own_apps, preview_row, previews_card,
                                           text_of, unique, wait_deployed)

LIMIT_HELP = "More wait, and start when one is removed."
LIMIT_ERROR = "Between 1 and 20."


# ---- helpers (scenario 5 uses them too) ----

def read_dialog(page):
    """The confirmation dialog that's open: (its title, its text, its buttons' names in alphabetical order)."""
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    title = text_of(dialog.locator(".mud-dialog-title"))
    body = text_of(dialog.locator(".mud-dialog-content"))
    buttons = sorted(text_of(b) for b in dialog.locator(".mud-dialog-actions button").all())
    return title, body, buttons


def answer_dialog(page, button):
    """Clicks the dialog's button (exactly that name) and waits for the dialog to close."""
    dialog = page.locator(".mud-dialog")
    dialog.get_by_role("button", name=button, exact=True).click()
    dialog.wait_for(state="detached")


def menu_item(page, item):
    """The item of the open menu (the one shown) whose text starts with `item`."""
    return page.locator(".mud-menu-item:visible").filter(has_text=re.compile(rf"^\s*{re.escape(item)}")).first


def choose_from_menu(page, label, item):
    """The Previews tab's menu of a preview (its "More for <label>" button): clicks the item that starts with `item`."""
    row = preview_row(page, label)
    row.wait_for()
    row.locator(f"button[aria-label='More for {label}']").click()
    entry = menu_item(page, item)
    entry.wait_for()
    entry.click()


def any_snackbar(page, gitberth, timeout=120_000):
    """The first snackbar's text, whatever it says (what an action reported, success or not)."""
    return text_of_said(gitberth.snackbar(page, ".", timeout=timeout))


def text_of_said(text):
    return re.sub(r"\s+", " ", text or "").strip()


def remove_from_previews_tab(page, gitberth, app, label, cancel_first=False):
    """The parent's Previews tab: the preview's menu › Remove…, confirmed (after a Cancel first, when asked). Returns
    {"dialog": (title, text, buttons), "cancelled": the dialog Cancel answered, or None, "said": the snackbar's text}."""
    gitberth.open(page, f"/apps/{app.key}/previews")
    cancelled = None
    if cancel_first:
        choose_from_menu(page, label, "Remove")
        cancelled = read_dialog(page)
        answer_dialog(page, "Cancel")
    choose_from_menu(page, label, "Remove")
    seen = read_dialog(page)
    answer_dialog(page, "Remove")
    return {"dialog": seen, "cancelled": cancelled, "said": any_snackbar(page, gitberth)}


def running_previews(engine, app):
    """The app's preview containers that run (gb-<app>-…, without a deploy's temporary ones)."""
    return sorted(c["Names"] for c in engine.containers(name=f"gb-{app.slug}-", all=False)
                  if c["Names"].startswith(f"gb-{app.slug}-") and "_" not in c["Names"] and c.get("State") == "running")


def preview_state(gitberth, engine, preview):
    """What Gitberth and the engine have of a preview: its deploys, image, container."""
    row = gitberth.db.app_by_id(preview["Id"])
    return {"deploys": [(d["Id"], d["Status"]) for d in gitberth.db.deployments(preview["Id"])],
            "image": row and row["CurrentImage"], "commit": row and row["CurrentCommit"],
            "container": engine.exists(f"gb-{preview['Slug']}"), "images": engine.images(f"gitberth/{preview['Slug']}")}


# ---- three pull requests, at most two running ----

@pytest.fixture(scope="class")
def limited(gitberth, github, engine, standins):
    """An app with previews of pull requests on and at most 2 running: two pull requests opened before, both previewed
    and running; then a third opened, whose preview is made."""
    with own_apps(gitberth, standins, "limit") as (page, made):
        repo, app, production = new_app(page, gitberth, github, made.track, unique("lim"))
        opened = {}
        for branch, title, marker in (("one", "First change", "first"), ("two", "Second change", "second")):
            n, sha = open_pull(repo, branch, title, marker=marker)
            opened[n] = (branch, sha)
        said = gitberth.set_previews(page, app, mode="Pull requests", limit=2)
        assert said.startswith("Saved"), said
        row = gitberth.db.app(app.slug)
        assert (row["PreviewMode"], row["PreviewLimit"]) == ("PullRequests", 2), {k: row[k] for k in ("PreviewMode", "PreviewLimit")}
        running = {}
        for n, (branch, sha) in opened.items():
            preview = gitberth.wait_preview(app.id, pr=n, timeout=90)
            running[n] = SimpleNamespace(row=preview, branch=branch, deploy=wait_deployed(gitberth, preview["Id"], sha))
        mark = standins.seq()
        third_n, third_sha = open_pull(repo, "three", "Third change", marker="third")
        third = gitberth.wait_preview(app.id, pr=third_n, timeout=90)
        yield SimpleNamespace(repo=repo, app=app, production=production, running=running, third_n=third_n, third_sha=third_sha,
                              third=third, mark=mark)


class TestAtMostTwoRunning:
    def test_at_most_running_is_saved_and_checked_as_its_typed(self, limited, page, gitberth):
        w = limited
        gitberth.open(page, f"/apps/{w.app.key}/settings")
        section = page.locator("#sec-previews")
        box = section.locator("#set-preview-limit")
        box.wait_for()
        field = section.locator(".gb-field").filter(has=page.locator("#set-preview-limit"))
        save = section.get_by_role("button", name="Save previews")
        # The saved value, under its label, with its help; nothing to save.
        assert box.input_value() == "2"
        assert text_of(field.locator("label.gb-label")) == "At most running"
        assert text_of(field.locator(".gb-help")) == LIMIT_HELP
        assert save.is_disabled(), "Save previews is offered with nothing changed"

        # Outside 1 to 20: refused as it's typed, and nothing can be saved.
        for bad in ("0", "21"):
            box.fill(bad)
            error = field.locator(".gb-field-error")
            until(lambda: error.count() == 1 and text_of(error) == LIMIT_ERROR, 15, f"the limit field to refuse {bad}",
                  seen=lambda: text_of(field))
            assert error.get_attribute("role") == "alert"
            assert save.is_disabled(), f"Save previews can be clicked with {bad} as the limit"

        # A good value: the field shows what it was, Save previews is offered; Undo puts the saved one back.
        box.fill("3")
        until(lambda: field.locator(".gb-was").count() == 1, 15, "the limit field to show what it was", seen=lambda: text_of(field))
        assert text_of(field.locator(".gb-was")) == "was 2", text_of(field)
        assert field.locator(".gb-changed-mark").count() == 1
        until(save.is_enabled, 15, "Save previews to be offered for a good limit")
        field.get_by_role("button", name="Undo At most running").click()
        until(lambda: box.input_value() == "2", 15, "Undo to put the saved limit back", seen=box.input_value)
        until(save.is_disabled, 15, "Save previews to go back to having nothing to save")
        assert gitberth.db.app(w.app.slug)["PreviewLimit"] == 2, "typing in the field changed the saved limit"
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_a_third_preview_waits_and_isnt_built(self, limited, gitberth, engine):
        w = limited
        third = w.third
        assert third["Slug"] == f"{w.app.slug}-pr-{w.third_n}" and third["HeadSha"] == w.third_sha, third
        # Three more looks after it was opened: still no deploy, no image, no container, and its code was never fetched.
        w.repo.wait_watcher_passes(3, since=w.mark, timeout=120)
        state = preview_state(gitberth, engine, third)
        assert state == {"deploys": [], "image": None, "commit": None, "container": False, "images": []}, \
            f"the third preview didn't wait while 2 ran: {state}"
        asked, wanted, shown = fetched(w.repo, w.mark, f"refs/pull/{w.third_n}/head")
        assert not asked and w.third_sha not in wanted, f"the waiting preview's code was fetched: {shown}"
        # The two that run still do, and they're all that runs.
        expected = sorted(f"gb-{r.row['Slug']}" for r in w.running.values())
        assert running_previews(engine, w.app) == expected, running_previews(engine, w.app)
        assert f"Preview PR #{w.third_n} added: Third change." in activity_texts(gitberth, w.app), activity_texts(gitberth, w.app)

    def test_the_previews_tab_says_it_waits(self, limited, page, gitberth):
        w = limited
        label = f"PR #{w.third_n}"
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        row = preview_row(page, label)
        row.wait_for()
        pill = row.locator(".gb-pill2")
        waiting = "Waiting: 2 previews run already"
        until(lambda: text_of(pill) == waiting, 30, f"{label}'s row to say it waits", seen=lambda: text_of(pill))
        assert "gb-tone-warn" in pill.get_attribute("class"), pill.get_attribute("class")
        assert pill.get_attribute("title") == waiting
        # Nothing runs, and it has no deploy: nothing to open, no log.
        assert row.get_by_role("link", name=f"Open {label}").count() == 0, "a waiting preview offers Open"
        assert row.get_by_role("link", name=f"Log of {label}").count() == 0, "a waiting preview offers a log"
        assert text_of(row.locator(".gb-drow-host .gb-ellipsis")) == "Third change"
        assert w.third_sha[:7] in text_of(row.locator(".gb-drow-sub").first), text_of(row)
        for n in w.running:
            other = preview_row(page, f"PR #{n}").locator(".gb-pill2")
            until(lambda: text_of(other) == "Ready", 30, f"PR #{n}'s row to say Ready", seen=lambda: text_of(other))
        assert text_of(previews_card(page).locator(".gb-card-sub")) == "Pull requests · 2 of 2 running"
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_a_running_preview_still_takes_a_new_commit(self, limited, gitberth, engine, standins):
        w = limited
        n = max(w.running)
        second = w.running[n]
        before = gitberth.db.last_deployment_id(second.row["Id"])
        mark = standins.seq()
        sha = w.repo.commit({"marker.txt": "second, again"}, branch=second.branch, message="More on the second change")["sha"]
        # It has its place: the new commit deploys at once, limit or not.
        deploy = wait_deployed(gitberth, second.row["Id"], sha, after=before, timeout=240)
        assert deploy["Trigger"] == "Poll", deploy
        assert engine.image_of(f"gb-{second.row['Slug']}") == f"gitberth/{second.row['Slug']}:{sha[:7]}"
        # Meanwhile the third still waits.
        w.repo.wait_watcher_passes(1, since=standins.seq(), timeout=60)
        state = preview_state(gitberth, engine, w.third)
        assert state["deploys"] == [] and not state["container"], f"the third preview started while 2 ran: {state}"
        asked, wanted, shown = fetched(w.repo, mark, f"refs/pull/{w.third_n}/head")
        assert not asked and w.third_sha not in wanted, f"the waiting preview's code was fetched: {shown}"

    def test_the_third_starts_when_one_is_removed(self, limited, page, gitberth, engine, standins):
        w = limited
        n = min(w.running)
        first = w.running[n].row
        label = f"PR #{n}"
        third = w.third
        assert preview_state(gitberth, engine, third)["deploys"] == [], "the third preview didn't wait for this"

        # The first one's menu › Remove…, confirmed.
        done = remove_from_previews_tab(page, gitberth, w.app, label)
        assert done["dialog"] == (f"Remove the preview of {label}?",
                                  "Its container, images and volumes go. A new commit on the pull request makes it again.",
                                  ["Cancel", "Remove"]), done["dialog"]
        assert done["said"] == f"Removed the preview of {label}.", done["said"]
        assert gitberth.preview(w.app.id, pr=n) is None, f"{label}'s preview is still there after Remove"
        assert not leftovers(engine, first["Slug"]), leftovers(engine, first["Slug"])
        assert f"Preview {label} removed." in activity_texts(gitberth, w.app), activity_texts(gitberth, w.app)

        # The third starts on the watcher's next look; never more than 2 run meanwhile.
        most = [len(running_previews(engine, w.app))]

        def started():
            most.append(len(running_previews(engine, w.app)))
            d = gitberth.db.one("SELECT * FROM Deployments WHERE AppId = ? ORDER BY Id DESC LIMIT 1", third["Id"])
            return d if d and d["Status"] in FINAL else None

        deploy = until(started, 240, f"PR #{w.third_n}'s waiting preview to deploy", interval=1,
                       seen=lambda: preview_state(gitberth, engine, third))
        assert (deploy["Status"], deploy["CommitSha"], deploy["Trigger"]) == ("Succeeded", w.third_sha, "Poll"), \
            f"{deploy}\n{gitberth.deploy_log(deploy['Id'], tail=30)}"
        assert max(most) <= 2, f"{max(most)} previews ran at once with at most 2 allowed"
        container = f"gb-{third['Slug']}"
        assert engine.running(container) and engine.image_of(container) == f"gitberth/{third['Slug']}:{w.third_sha[:7]}"
        assert gitberth.wait_answers(gitberth.lan_url(third["Id"]), "/")["marker"] == "third"
        asked, wanted, _ = fetched(w.repo, w.mark, f"refs/pull/{w.third_n}/head")
        assert asked and w.third_sha in wanted

        # The removed one doesn't come back for the same commit: two more looks, and still the same two run.
        w.repo.wait_watcher_passes(2, since=standins.seq(), timeout=90)
        assert gitberth.preview(w.app.id, pr=n) is None, f"{label}'s preview came back without a new commit"
        expected = sorted([f"gb-{w.running[max(w.running)].row['Slug']}", container])
        assert running_previews(engine, w.app) == expected, running_previews(engine, w.app)

        # The Previews tab: the third is Ready, the removed one isn't listed, 2 of 2 run.
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        pill = preview_row(page, f"PR #{w.third_n}").locator(".gb-pill2")
        pill.wait_for()
        until(lambda: text_of(pill) == "Ready", 60, f"PR #{w.third_n}'s row to say Ready", seen=lambda: text_of(pill))
        assert preview_row(page, label).count() == 0, f"the Previews tab still lists {label}"
        assert text_of(previews_card(page).locator(".gb-card-sub")) == "Pull requests · 2 of 2 running"
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"
