"""Scenario 7: cron jobs run for production only.

The test app's vercel.json has a cron job, /cron every minute; the app writes each hit to /data/cron.log, with the
caller's User-Agent, and logs the request.
- Production runs it on its schedule, as Vercel does (a GET with vercel-cron/1.0), minute after minute. Its Overview's
  Scheduled jobs card shows the job, its schedule and its last run; Run now runs it at once, and says what it answered.
- A preview of a pull request is built with the same job (its image has the same cron label), but never runs it: through
  two of production's scheduled runs, and production's Run now, the preview's /cron is never called and it has no
  /data/cron.log. Its Overview's card says previews don't run these jobs, and its Run now is off (a click does nothing).
"""
import json
import time
from types import SimpleNamespace

import pytest

from e2e.wait import stays, until
from test_02_pull_request_previews import new_app, open_pull, own_apps, text_of, unique, wait_deployed
from test_04_preview_limit import text_of_said
from test_19_declared_volumes import read_file

CRON_AGENT = "vercel-cron/1.0"
PREVIEW_PROBLEM = "Previews don't run these jobs: only production does, as on Vercel."
JOBS = [{"path": "/cron", "schedule": "* * * * *"}]


def cron_hits(url):
    """The hits /data/cron.log has (the test app writes one per GET /cron): [(when, User-Agent)]; [] without the file."""
    status, text = read_file(url, "/data/cron.log")
    if status != 200:
        return []
    hits = []
    for line in text.splitlines():
        when, _, agent = line.partition(" ")
        if when.isdigit():
            hits.append((int(when), agent))
    return hits


def cron_requests(engine, container):
    """The GET /cron requests the test app logged (its own log lines)."""
    return [line for line in engine.logs(container).splitlines() if "GET /cron" in line]


def jobs_card(page):
    """The Overview's Scheduled jobs card."""
    return page.locator("section.gb-card").filter(has=page.locator(".gb-card-title", has_text="Scheduled jobs"))


def job_row(page, path="/cron"):
    return jobs_card(page).locator(".gb-feed-row").filter(has=page.locator(".gb-mono", has_text=path))


@pytest.fixture(scope="class")
def crons(gitberth, github, engine, standins):
    """An app made from the test app, previews of pull requests on, and a pull request opened: its preview deployed. Then
    production's scheduler is given two of its runs (whole minutes) while the preview runs."""
    with own_apps(gitberth, standins, "crons") as (page, made):
        repo, app, production = new_app(page, gitberth, github, made.track, unique("crn"))
        said = gitberth.set_previews(page, app, mode="Pull requests")
        assert said.startswith("Saved"), said
        n, sha = open_pull(repo, "cron-work", "Work near the crons", marker="cron work")
        preview = gitberth.wait_preview(app.id, pr=n, timeout=90)
        deploy = wait_deployed(gitberth, preview["Id"], sha)
        up = int(time.time())
        url, preview_url = gitberth.lan_url(app.id), gitberth.lan_url(preview["Id"])
        preview_container = f"gb-{preview['Slug']}"
        # Two of production's scheduled runs, in whole minutes after the preview started.
        try:
            until(lambda: len({when // 60 for when, _ in cron_hits(url) if when > up}) >= 2, 200, "two of production's scheduled cron runs",
                  interval=3, seen=lambda: cron_hits(url))
            waited = None
        except AssertionError as e:
            waited = str(e)
        yield SimpleNamespace(repo=repo, app=app, production=production, n=n, preview=preview, deploy=deploy, up=up, url=url,
                              preview_url=preview_url, preview_container=preview_container, waited=waited,
                              runs=[h for h in cron_hits(url) if h[0] > up], preview_hits=read_file(preview_url, "/data/cron.log"),
                              preview_requests=cron_requests(engine, preview_container))


class TestCronJobs:
    def test_production_runs_it_on_its_schedule(self, crons, engine):
        w = crons
        assert w.waited is None, f"production's cron didn't run twice in the minutes after the preview started: {w.waited}"
        assert all(agent == CRON_AGENT for _, agent in w.runs), f"production's /cron was called by {w.runs}"
        assert len({when // 60 for when, _ in w.runs}) >= 2, w.runs
        # Its image carries the job (from vercel.json), and the app logged the scheduler's requests.
        image = engine.image_of(w.app.container)
        assert json.loads(engine.image_labels(image).get("gitberth.crons") or "[]") == JOBS, engine.image_labels(image)
        assert len(cron_requests(engine, w.app.container)) >= 2, "production's container logged no requests for /cron"

    def test_a_preview_never_runs_it(self, crons, engine):
        w = crons
        # The preview has the job too: its image was built from the same vercel.json.
        image = engine.image_of(w.preview_container)
        assert image == f"gitberth/{w.preview['Slug']}:{w.deploy['CommitSha'][:7]}", image
        assert json.loads(engine.image_labels(image).get("gitberth.crons") or "[]") == JOBS, engine.image_labels(image)
        # Production ran it at least twice while the preview ran, but the preview was never called.
        assert w.waited is None and w.runs, w.waited
        assert engine.running(w.preview_container), engine.inspect(w.preview_container)
        assert w.preview_hits[0] == 404, f"the preview's /data/cron.log exists: {w.preview_hits}"
        assert not w.preview_requests, f"the preview's /cron was called: {w.preview_requests}"

    def test_productions_scheduled_jobs_card_and_run_now(self, crons, page, gitberth):
        w = crons
        gitberth.open(page, f"/apps/{w.app.key}")
        card = jobs_card(page)
        card.wait_for()
        assert "from vercel.json, in UTC" in text_of(card.locator(".gb-card-sub"))
        row = job_row(page)
        row.wait_for()
        assert "* * * * *" in text_of(row), text_of(row)
        assert card.locator(".gb-ro-note").count() == 0, f"production's jobs card says there's a problem: {text_of(card)}"
        pill = row.locator(".gb-pill")
        until(lambda: text_of(pill) == "200", 70, "production's job to show its last run's 200", seen=lambda: text_of(row))
        assert "Last run" in text_of(row), text_of(row)
        button = row.get_by_role("button", name="Run now")
        assert button.is_enabled(), "production's Run now is off"

        # Run now, away from the minute's scheduled run, so the hit it makes is its own.
        until(lambda: 5 <= time.time() % 60 <= 45, 30, "a moment between two of the schedule's runs", interval=0.5)
        before, clicked = len(cron_hits(w.url)), time.time()
        button.click()
        said = text_of_said(gitberth.snackbar(page, "/cron (answered|wasn't called|failed|is still running)"))
        assert said.startswith("/cron answered 200 in "), said
        hits = until(lambda: [h for h in cron_hits(w.url)[before:] if clicked - 1 <= h[0] <= time.time() + 1], 15,
                     "Run now's hit in production's cron.log", seen=lambda: cron_hits(w.url)[before:])
        assert all(agent == CRON_AGENT for _, agent in hits), hits
        until(lambda: "Run by hand" in text_of(row), 15, "the card to say the job was run by hand", seen=lambda: text_of(row))
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_the_previews_card_says_previews_dont_run_jobs(self, crons, page, gitberth, engine):
        w = crons
        gitberth.open(page, f"/apps/{w.preview_container}")
        page.locator(".gb-banner", has_text="Preview of").wait_for()
        card = jobs_card(page)
        card.wait_for()
        note = card.locator(".gb-ro-note")
        note.wait_for()
        assert text_of(note) == PREVIEW_PROBLEM, text_of(note)
        row = job_row(page)
        assert "* * * * *" in text_of(row), text_of(row)
        assert text_of(row.locator(".gb-pill")) == "Not run yet", text_of(row)
        assert "Hasn't run since Gitberth started" in text_of(row), text_of(row)
        button = row.get_by_role("button", name="Run now")
        assert button.is_disabled(), "a preview's Run now can be clicked"
        # Clicked anyway, it does nothing: no answer, no hit.
        button.click(force=True)
        stays(lambda: page.locator(".mud-snackbar").count() == 0 and read_file(w.preview_url, "/data/cron.log")[0] == 404, 3,
              "a click on the preview's Run now does nothing")
        # And still, after all of production's runs, the preview was never called.
        assert read_file(w.preview_url, "/data/cron.log")[0] == 404
        assert not cron_requests(engine, w.preview_container), cron_requests(engine, w.preview_container)
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"
