"""Scenario 42: release and scheduled commands (B2).

- An uploaded app (nginx with a VOLUME for what its release writes), made through the API, gets a release command on
  its Settings tab. One that fails (exit 3) stops the next upload's deploy: it ends Failed, its log has the command's
  output and says the running version stays, and the app still serves the version before. One that works runs in a
  one-off container of the new version before it takes over: it writes a marker into the app's volume, which the new
  version then serves, and no one-off container is left behind.
- Its Scheduled commands card: a command added through the card's dialog runs with Run now, and the card shows what it
  printed. One that fails, every minute, runs on its schedule: the card shows Failed (7), the webhook destination gets
  job.failed, and the app's Activity has it.
"""
import re

from e2e.net import get
from e2e.standin import Notify
from e2e.wait import until
from test_21_notifications import add_destination, remove_all
from test_29_mcp import make_token
from test_46_uploads import flat, upload_api, zip_bytes
from e2e.gitberth import App

DOCKERFILE = """FROM nginx:alpine
COPY index.html /usr/share/nginx/html/index.html
VOLUME /usr/share/nginx/html/rel
EXPOSE 80
"""


def site(name, version):
    """The app's folder, zipped: a Dockerfile on nginx and its page, which says its version."""
    return zip_bytes([(f"{name}/Dockerfile", DOCKERFILE), (f"{name}/index.html", f"<!doctype html><title>{name}</title><h1>{name} {version}</h1>")])


def set_release(gitberth, page, app, command):
    """The app's Settings tab › Release command, filled in and saved as someone does it."""
    gitberth.open(page, f"/apps/{app.key}/settings")
    section = page.locator("#sec-release")
    section.wait_for()
    section.locator("#set-release").fill(command)
    section.get_by_role("button", name="Save release command").click()
    gitberth.snackbar(page, r"^Saved")
    until(lambda: (gitberth.db.app_by_id(app.id) or {}).get("ReleaseCommand") == command, 15, "the release command to be saved",
          seen=lambda: (gitberth.db.app_by_id(app.id) or {}).get("ReleaseCommand"))


def add_command(gitberth, page, name, command, every_minute=False):
    """The Overview's Scheduled commands card › Add, its dialog filled in: daily at 03:00 unless every minute."""
    card = page.locator("#gb-jobs")
    card.wait_for()
    card.get_by_role("button", name="Add", exact=True).click()
    dialog = page.get_by_role("dialog")
    dialog.wait_for()
    dialog.locator("#job-name").fill(name)
    dialog.locator("#job-command").fill(command)
    if every_minute:
        dialog.locator(".gb-seg[aria-label='How often it runs'] .gb-seg-btn", has_text="Every N minutes").click()
        dialog.locator("#job-every").fill("1")
        until(lambda: "every minute" in flat(dialog.locator("#job-when").inner_text()), 10, "the dialog to say it runs every minute",
              seen=lambda: dialog.locator("#job-when").inner_text())
    dialog.get_by_role("button", name="Add", exact=True).click()
    said = flat(gitberth.snackbar(page, rf"^Added {re.escape(name)}"))
    dialog.wait_for(state="detached")
    return said


def runs(gitberth, app_id, name):
    """A command's runs, newest first."""
    return gitberth.db.query("SELECT r.* FROM CommandRuns r JOIN ScheduledCommands c ON c.Id = r.CommandId WHERE c.AppId = ? AND c.Name = ? ORDER BY r.Id DESC",
                             app_id, name)


def test_a_release_command_runs_before_each_new_version_and_scheduled_commands_run_and_tell(page, gitberth, apps, uniq, standins, engine):
    name = uniq("rel")
    tag = uniq("m")
    token, _ = make_token(gitberth, page, uniq("rlt"), "Admin")

    # ---- The app, made from an upload through the API, without a release command yet.
    status, answer = upload_api(gitberth, token, name, f"{name}.zip", site(name, "v1"), wait=True)
    assert status == 200 and answer["created"] is True, (status, answer)
    app = apps.track(App(answer["id"], gitberth.db.app_by_id(answer["id"])["Slug"], name))
    assert answer["deploy"]["status"] == "Succeeded", answer["deploy"]
    url = gitberth.lan_url(app.id)
    until(lambda: f"{name} v1" in get(url + "/")[1], 60, f"{url} to serve v1", seen=lambda: get(url + "/"))

    # ---- A release command that fails stops the new version: the deploy fails and v1 serves on.
    set_release(gitberth, page, app, f"echo migrating-{tag}; exit 3")
    status, answer = upload_api(gitberth, token, app.key, "v2.zip", site(name, "v2"), wait=True)
    assert status == 200 and answer["deploy"]["status"] == "Failed", (status, answer)
    failed = gitberth.db.deployment(answer["deploy"]["deployment_id"])
    assert failed["Error"] == f"The release command failed (exit code 3), so {name} keeps running its current version. Its output is in the log above.", failed
    log = gitberth.deploy_log(failed["Id"])
    assert "Running the release command in the new version, before it takes over" in log and f"migrating-{tag}" in log, log[-2000:]
    assert f"{name} v1" in get(url + "/")[1]

    # ---- One that works writes a marker into the app's volume before the new version takes over, which then serves it.
    set_release(gitberth, page, app, f"echo released-{tag} > /usr/share/nginx/html/rel/marker.txt && echo wrote the marker")
    status, answer = upload_api(gitberth, token, app.key, "v3.zip", site(name, "v3"), wait=True)
    assert status == 200 and answer["deploy"]["status"] == "Succeeded", (status, answer)
    log = gitberth.deploy_log(answer["deploy"]["deployment_id"])
    assert "wrote the marker" in log and "The release command finished (exit code 0)." in log, log[-2000:]
    until(lambda: f"{name} v3" in get(url + "/")[1], 60, f"{url} to serve v3", seen=lambda: get(url + "/"))
    assert get(url + "/rel/marker.txt")[1].strip() == f"released-{tag}"
    assert engine.containers(label="gitberth.helper=command") == [], engine.containers(label="gitberth.helper=command")

    # ---- Scheduled commands: one run now from the card, its output shown there.
    gitberth.open(page, f"/apps/{app.key}")
    said = add_command(gitberth, page, "Report", f"echo report-{tag} && ls /usr/share/nginx/html/rel")
    assert said.startswith("Added Report: it runs daily at 03:00 ("), said
    row = page.locator(".gb-jobrow[data-job='Report']")
    row.get_by_role("button", name="Run now").click()
    output = page.locator("pre.gb-output[id^='job-output-']")
    until(lambda: f"report-{tag}" in output.inner_text() and "marker.txt" in output.inner_text(), 60, "Run now's output on the card",
          seen=lambda: output.all_inner_texts())
    until(lambda: "Succeeded" in flat(row.inner_text()), 30, "the card to say Report succeeded", seen=lambda: flat(row.inner_text()))
    [run] = runs(gitberth, app.id, "Report")
    assert (run["Status"], run["ExitCode"], run["Trigger"]) == ("Succeeded", 0, "RunNow"), run

    # ---- One that fails, every minute: it runs on its schedule, and a webhook destination is told.
    notify = Notify(standins)
    try:
        add_destination(gitberth, page, "Webhook", {"Name": "Automations", "URL": Notify.WEBHOOK, "Bearer token": "e2e-webhook-token"})
        since = standins.seq()
        gitberth.open(page, f"/apps/{app.key}")
        add_command(gitberth, page, "Boom", f"echo boom-{tag}; exit 7", every_minute=True)
        hook = notify.wait(since, "notify.e2e.test", check=lambda calls: any((c.get("body") or {}).get("event") == "job.failed" for c in calls),
                           what="job.failed at the webhook", timeout=150)
        body = next(c["body"] for c in hook if (c.get("body") or {}).get("event") == "job.failed")
        assert (body["level"], body["app"], body["appId"], body["title"]) == ("critical", name, app.id, f"Scheduled command Boom of {name} failed"), body
        failed_run = runs(gitberth, app.id, "Boom")[-1]
        assert (failed_run["Status"], failed_run["ExitCode"], failed_run["Trigger"]) == ("Failed", 7, "Schedule"), failed_run
        assert f"boom-{tag}" in (failed_run["Output"] or ""), failed_run
        boom = page.locator(".gb-jobrow[data-job='Boom']")
        until(lambda: "Failed (7)" in flat(boom.inner_text()), 30, "the card to say Boom failed", seen=lambda: flat(boom.inner_text()))
        activity = [a["Text"] for a in gitberth.db.activity(app.id) if a["Kind"] == "Job"]
        assert any(t.startswith(f"Scheduled command Boom of {name} failed") for t in activity), activity
        assert not gitberth.errors(page), gitberth.errors(page)
    finally:
        remove_all(gitberth)
