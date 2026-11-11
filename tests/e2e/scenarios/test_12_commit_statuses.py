"""Scenario 12: commit statuses.

- A deploy marks its commit on GitHub: pending once its build starts (while the deploy still runs), linking to its
  deploy log, then success with the app's address, under "Gitberth: <app>" (the app's name, not its repo's), written
  with the saved token. A new commit gets a pair of its own.
- A build that fails, and a new version that fails its checks (rolled back), end in failure linking to that deploy's
  log, which opens its page. Nothing GitHub receives carries the deploy's error, the build's or the app's output, a
  variable's value or a secret, though the failed build printed the secret.
- A deploy cancelled while it builds (not the commit's fault) ends as an error, linking to its log.
- A restart (the app page's, and one on a schedule), a settings change and a rollback post none: the scheduled
  restart, the settings change and the rollback are deploys without a build. (The scheduled one waits about 6
  minutes: its shortest schedule is 5.)
- Turning commit statuses off in Settings › GitHub stops them; turned on again, the next deploy reports.
- A branch's preview reports under "Gitberth preview: <parent>", linking to its own address, and gets no comment (it
  has no pull request).
- The success link is the app's main domain once it has one; before that, its LAN address.
- Settings › GitHub shows both switches, what each needs from the token, and that both need the token.
"""
import contextlib
import json
import re
import secrets

import pytest

from e2e.github_reports import (COMMENTS, STATUSES, comment_writes, context, leaks, set_github_switch, set_variables,
                                status_writes, statuses, wait_deployment, wait_status)
from e2e.wait import stays, until

# A build that prints the app's secret on its way to failing. The repo's own Dockerfile is used as it is, and declares
# the build-time variable, so docker build hands it over.
BUILD_FAILS = """FROM python:3.12-alpine
WORKDIR /app
COPY . .
ARG E2E_API_TOKEN
RUN echo calling the release API with $E2E_API_TOKEN && echo release API: deploy key rejected && exit 7
EXPOSE 8080
CMD ["python3", "-u", "/app/server.py"]
"""

# A version that builds, then prints the secret and exits as it starts: it fails its checks, and the old one runs on.
STARTS_AND_CRASHES = """FROM python:3.12-alpine
WORKDIR /app
COPY . .
EXPOSE 8080
CMD ["sh", "-c", "echo connecting with $E2E_API_TOKEN; echo release API: listener crashed; exit 3"]
"""

# A build that takes its time, so it can be cancelled while it runs.
SLOW_BUILD = """FROM python:3.12-alpine
WORKDIR /app
COPY . .
RUN echo building slowly && sleep 300
EXPOSE 8080
CMD ["python3", "-u", "/app/server.py"]
"""


def bare(url):
    return url.split("://", 1)[1]


def test_settings_github_shows_the_switches_and_what_each_needs(page, gitberth, baseline):
    gitberth.open(page, "/settings")
    section = page.locator("#github-integration")
    section.wait_for()
    text = section.inner_text()
    assert "GitHub integration" in text and "Deploys reported on GitHub, and deploys that start the moment you push." in text, text
    # Each switch is on unless turned off, and says what it does and what it needs from the token.
    for title, sub in (
        (STATUSES, "Each deploy marks its commit on GitHub: pending while it builds, then live (with the app's address) or failed "
                   "(with a link to the deploy log). Needs Commit statuses: Read and write."),
        (COMMENTS, "A preview of a pull request keeps one comment on it up to date: its state, address, commit and time. "
                   "Needs Pull requests: Read and write."),
    ):
        switch = section.get_by_role("switch", name=title, exact=True)
        assert switch.get_attribute("aria-checked") == "true", f"{title} isn't on"
        row = section.locator(".gb-switchrow").filter(has=page.get_by_role("switch", name=title, exact=True))
        assert row.locator(".gb-switchrow-t").inner_text().strip() == title
        assert row.locator(".gb-switchrow-s").inner_text().strip() == sub
    # With a token saved, nothing says one is missing.
    assert "Both need the GitHub token above" not in text
    # GitHub access names the same permissions for the token, and links to the section.
    access = page.locator("#github")
    named = [t.strip() for t in access.locator(".gb-help strong").all_inner_texts()]
    for permission in ("Commit statuses: Read and write", "Pull requests: Read and write", "Webhooks: Read and write"):
        assert permission in named, named
    assert access.locator(".gb-help a", has_text="GitHub integration").get_attribute("href") == "settings#github-integration"

    # Without a token, the section says both need one.
    try:
        access.get_by_role("button", name="Remove", exact=True).click()
        gitberth.snackbar(page, "Token removed")
        until(lambda: "Both need the GitHub token above; without one, GitHub hears nothing from Gitberth." in section.inner_text(), 15,
              "the switches to say they need the token", seen=section.inner_text)
    finally:
        said = gitberth.save_github_token(page, baseline["token"])
    assert said.startswith("Token saved"), said
    assert "Both need the GitHub token above" not in page.locator("#github-integration").inner_text()
    assert not gitberth.errors(page), gitberth.errors(page)


def test_a_deploy_marks_its_commit_pending_then_live(page, gitberth, github, apps, uniq, scenario, baseline):
    repo = github.create_repo(uniq("cs"), files={"marker.txt": "statuses v1"})
    first = repo.branch_sha("main")
    # Named apart from its repo, so the context is seen to be the app's own name.
    app = apps.track(gitberth.create_git_app(page, repo.url, name=f"Board {repo.name}"))
    assert app.name == f"Board {repo.name}", app
    ctx = context(app.name)
    ui = gitberth.db.setting("ui.address")
    assert ui == gitberth.url("/"), f"Gitberth's address for its own links is {ui!r}"

    # Pending as soon as the build starts, while the deploy still runs, linking to that deploy's log.
    pending = wait_status(repo, first, ctx, "pending", timeout=240)
    running = gitberth.db.one("SELECT * FROM Deployments WHERE AppId = ? ORDER BY Id DESC LIMIT 1", app.id)
    assert running["Status"] in ("Cloning", "Building", "Starting"), f"pending came only once the deploy was over: {running}"
    assert [s["state"] for s in statuses(repo, first, ctx)] == ["pending"]
    assert pending["description"] == "Building and deploying on Gitberth", pending
    assert pending["target_url"] == f"{ui}apps/{app.id}/deployments/{running['Id']}", pending

    # Success once it runs, with the app's LAN address (it has no domain), which answers.
    deploy = gitberth.wait_deploy(app.id, timeout=420)
    assert deploy["Id"] == running["Id"] and deploy["CommitSha"] == first, deploy
    success = wait_status(repo, first, ctx, "success", after=pending["id"], timeout=60)
    url = gitberth.lan_url(app.id)
    assert success["description"] == f"Deploy is live at {bare(url)}", success
    assert success["target_url"] == url + "/", success
    assert gitberth.wait_answers(success["target_url"].rstrip("/"), "/")["marker"] == "statuses v1"
    # Exactly those two on the commit, under the app's context, written with the saved token.
    assert [(s["context"], s["state"]) for s in repo.statuses(sha=first)] == [(ctx, "pending"), (ctx, "success")]
    writes = status_writes(repo, scenario.seq)
    assert [(w["status"], w["token"] == baseline["token"]) for w in writes] == [(201, True), (201, True)], writes
    assert {s["creator"]["login"] for s in (pending, success)} == {"e2e-bot"}

    # The pending status's link opens that deploy's page, with its log.
    page.goto(pending["target_url"])
    gitberth.wait_ready(page)
    assert gitberth.path_of(page) == f"/apps/{app.id}/deployments/{deploy['Id']}"
    until(lambda: f"Deploy #{deploy['Id']}" in page.title(), 15, "the deploy's own page", seen=page.title)
    log = page.locator(".gb-terminal-body")
    until(lambda: "Deploy succeeded" in log.inner_text(), 30, "the deploy log on its page", seen=log.inner_text)

    # A new commit gets a pair of its own; the first commit's stay as they were.
    second = repo.commit({"marker.txt": "statuses v2"}, message="Second version")["sha"]
    pending2 = wait_status(repo, second, ctx, "pending", timeout=120)
    two = gitberth.wait_deploy(app.id, after=deploy["Id"], timeout=300)
    assert two["CommitSha"] == second and two["Trigger"] == "Poll", two
    assert pending2["target_url"] == f"{ui}apps/{app.id}/deployments/{two['Id']}", pending2
    success2 = wait_status(repo, second, ctx, "success", after=pending2["id"], timeout=60)
    assert (success2["description"], success2["target_url"]) == (success["description"], success["target_url"]), success2
    assert [s["state"] for s in repo.statuses(sha=first)] == ["pending", "success"]
    assert not gitberth.errors(page), gitberth.errors(page)


def test_a_failed_deploy_links_to_its_log_and_github_gets_no_error_or_secret(page, gitberth, github, standins, apps, uniq, scenario):
    secret = "e2e-made-up-" + secrets.token_hex(12)
    motto = "plain-value-" + secrets.token_hex(6)
    repo = github.create_repo(uniq("cf"), files={"marker.txt": "failures v1"})
    first = repo.branch_sha("main")
    app = apps.track(gitberth.create_git_app(page, repo.url))
    gitberth.wait_deploy(app.id, timeout=420)
    ctx = context(app.name)
    live = wait_status(repo, first, ctx, "success", timeout=60)
    ui = gitberth.db.setting("ui.address")
    url = gitberth.lan_url(app.id)

    # The app's own variables, set in Gitberth (GitHub never had them): a secret the build gets too, and a plain one.
    applied = wait_deployment(gitberth, set_variables(gitberth, page, app, [
        {"name": "E2E_API_TOKEN", "value": secret, "build_time": True},
        {"name": "E2E_PUBLIC_MOTTO", "value": motto}]))
    saved = {v["Key"]: v["BuildTime"] for v in gitberth.db.env_vars(app.id)}
    assert saved == {"E2E_API_TOKEN": 1, "E2E_PUBLIC_MOTTO": 0}, gitberth.db.env_vars(app.id)

    # ---- A build that fails, having printed the secret ----
    broken = repo.commit({"Dockerfile": BUILD_FAILS}, message="Call the release API while building")["sha"]
    pending = wait_status(repo, broken, ctx, "pending", timeout=120)
    failed = gitberth.wait_deploy(app.id, after=applied["Id"], statuses=("Failed",), timeout=300)
    assert failed["CommitSha"] == broken and failed["Trigger"] == "Poll", failed
    failure = wait_status(repo, broken, ctx, "failure", after=pending["id"], timeout=60)

    # ---- A version that builds, prints the secret as it starts, and exits: rolled back ----
    crashing = repo.commit({"Dockerfile": STARTS_AND_CRASHES}, message="Connect to the release API on start")["sha"]
    pending2 = wait_status(repo, crashing, ctx, "pending", timeout=120)
    rolled = gitberth.wait_deploy(app.id, after=failed["Id"], statuses=("RolledBack",), timeout=300)
    assert rolled["CommitSha"] == crashing, rolled
    failure2 = wait_status(repo, crashing, ctx, "failure", after=pending2["id"], timeout=60)

    # Each failure links to its deploy's log (the pending before it did too), and says nothing of why.
    log_url = f"{ui}apps/{app.id}/deployments/{failed['Id']}"
    assert (pending["target_url"], failure["target_url"]) == (log_url, log_url), (pending, failure)
    assert failure["description"] == "Deploy failed: see the deploy log on Gitberth", failure
    rolled_url = f"{ui}apps/{app.id}/deployments/{rolled['Id']}"
    assert (pending2["target_url"], failure2["target_url"]) == (rolled_url, rolled_url), (pending2, failure2)
    assert failure2["description"] == "The new version didn't pass its checks; the previous one still runs", failure2
    # Both did print the secret, as their logs show (masked there); and the previous version still answers.
    assert "calling the release API with" in gitberth.deploy_log(failed["Id"]), gitberth.deploy_log(failed["Id"], tail=40)
    assert "connecting with" in gitberth.deploy_log(rolled["Id"]), gitberth.deploy_log(rolled["Id"], tail=40)
    assert gitberth.wait_answers(url, "/")["marker"] == "failures v1"

    # Nothing GitHub received carries the secret, the plain value, either deploy's error, or the build's or app's output;
    # each status says only what Gitberth always says.
    found = leaks(standins, scenario.seq, [secret, motto, failed["Error"], rolled["Error"], "deploy key rejected", "listener crashed",
                                           "exit code", "exited"])
    assert not found, f"Gitberth sent GitHub: {found}"
    said = {"Building and deploying on Gitberth", f"Deploy is live at {bare(url)}", "Deploy failed: see the deploy log on Gitberth",
            "The new version didn't pass its checks; the previous one still runs"}
    assert {s["description"] for s in repo.statuses()} <= said, [s["description"] for s in repo.statuses()]
    assert live["target_url"] == url + "/"

    # The failure's link opens that deploy's page: it failed, says why, and shows its log (with the secret masked).
    page.goto(failure["target_url"])
    gitberth.wait_ready(page)
    banner = page.locator(".gb-banner", has_text="The deploy failed")
    banner.wait_for()
    assert failed["Error"] in banner.inner_text(), banner.inner_text()
    terminal = page.locator(".gb-terminal-body")
    until(lambda: "calling the release API with" in terminal.inner_text(), 30, "the build's output on the deploy's page", seen=terminal.inner_text)
    page.goto(failure2["target_url"])
    gitberth.wait_ready(page)
    page.locator(".gb-banner", has_text="Rolled back: the old version kept running").wait_for()
    # The secret the build printed shows nowhere: not in either log, nor on the pages GitHub's links open.
    assert secret not in gitberth.deploy_log(failed["Id"]) and secret not in gitberth.deploy_log(rolled["Id"])
    assert secret not in page.content()


def test_a_cancelled_deploy_ends_its_status_as_an_error(page, gitberth, github, apps, uniq):
    """Not the commit's fault: a deploy cancelled while it builds ends its pending status as an error, not a failure."""
    repo = github.create_repo(uniq("cc"), files={"marker.txt": "cancel v1"})
    first = repo.branch_sha("main")
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    ctx = context(app.name)
    wait_status(repo, first, ctx, "success", timeout=60)
    ui = gitberth.db.setting("ui.address")

    slow = repo.commit({"Dockerfile": SLOW_BUILD}, message="A slow build")["sha"]
    pending = wait_status(repo, slow, ctx, "pending", timeout=120)
    running = gitberth.db.one("SELECT * FROM Deployments WHERE AppId = ? ORDER BY Id DESC LIMIT 1", app.id)
    assert running["CommitSha"] == slow and running["Id"] > one["Id"], running
    # The deploy's page (the pending status's link): Cancel, once its build is under way.
    page.goto(pending["target_url"])
    gitberth.wait_ready(page)
    terminal = page.locator(".gb-terminal-body")
    until(lambda: "building slowly" in terminal.inner_text(), 120, "the slow build to be under way", seen=terminal.inner_text)
    page.get_by_role("button", name="Cancel", exact=True).click()
    gitberth.snackbar(page, "Cancelling the deploy")
    cancelled = gitberth.wait_deploy(app.id, after=one["Id"], statuses=("Cancelled",), timeout=120)
    assert cancelled["Id"] == running["Id"], cancelled

    error = wait_status(repo, slow, ctx, "error", after=pending["id"], timeout=60)
    assert (error["description"], error["target_url"]) == ("Deploy cancelled", f"{ui}apps/{app.id}/deployments/{cancelled['Id']}"), error
    assert [s["state"] for s in statuses(repo, slow, ctx)] == ["pending", "error"]


def test_recreates_restarts_and_settings_changes_post_no_status(page, gitberth, github, engine, standins, apps, uniq):
    repo = github.create_repo(uniq("rc"), files={"marker.txt": "recreate v1"})
    first = repo.branch_sha("main")
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    ctx = context(app.name)
    wait_status(repo, first, ctx, "success", timeout=60)
    second = repo.commit({"marker.txt": "recreate v2"}, message="Second version")["sha"]
    two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=300)
    assert two["CommitSha"] == second, two
    wait_status(repo, second, ctx, "success", timeout=60)
    before = {sha: statuses(repo, sha) for sha in (first, second)}
    mark = standins.seq()

    # A restart (the app page's Restart): the same container starts again, and no deploy runs.
    started = engine.inspect(app.container)["State"]["StartedAt"]
    gitberth.open(page, f"/apps/{app.key}")
    page.locator(".gb-hero-actions").get_by_role("button", name=f"Restart {app.name}", exact=True).click()
    # It asks first only when something else would notice.
    page.locator(".mud-dialog, .mud-snackbar").first.wait_for()
    if page.locator(".mud-dialog").count() > 0:
        gitberth.confirm(page, "Restart")
    gitberth.snackbar(page, f"Restarted {re.escape(app.name)}")
    until(lambda: engine.inspect(app.container)["State"]["StartedAt"] != started, 30, "the container to start again",
          seen=lambda: engine.inspect(app.container)["State"])
    assert gitberth.db.last_deployment_id(app.id) == two["Id"], "a restart ran a deploy"

    # A settings change: a variable added on the Environment tab, made live by a deploy without a build.
    note = "note-" + secrets.token_hex(4)
    applied = wait_deployment(gitberth, set_variables(gitberth, page, app, [{"name": "E2E_PUBLIC_NOTE", "value": note}]))
    assert (applied["Trigger"], applied["CommitSha"]) == ("ConfigChange", second), applied
    assert engine.env(app.container).get("E2E_PUBLIC_NOTE") == note

    # A rollback (Activity › Roll back to this): the first build's image again.
    gitberth.open(page, f"/apps/{app.key}/activity")
    button = page.locator(".gb-row", has_text=first[:7]).get_by_role("button", name="Roll back to this").first
    button.wait_for()
    button.click()
    gitberth.confirm(page, "Roll back")
    page.wait_for_url(re.compile(r"/deployments/\d+$"))
    back = gitberth.wait_deploy(app.id, after=applied["Id"], timeout=300)
    assert (back["Trigger"], back["CommitSha"]) == ("Rollback", first), back
    assert engine.image_of(app.container) == f"{app.image_repository}:{first[:7]}"

    # None of them told GitHub anything: no status was written, and both commits keep theirs as they were.
    stays(lambda: status_writes(repo, mark) == [], 5, "no commit status after a restart, a settings change and a rollback")
    assert {sha: statuses(repo, sha) for sha in (first, second)} == before


@pytest.mark.slow
def test_a_scheduled_restart_posts_no_status(page, gitberth, github, standins, apps, uniq):
    """A restart on a schedule recreates the app through a deploy without a build (trigger Restart). The shortest schedule
    is every 5 minutes, counted from when the container last started, so this waits about 6 minutes."""
    repo = github.create_repo(uniq("sr"), files={"marker.txt": "scheduled v1"})
    sha = repo.branch_sha("main")
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    ctx = context(app.name)
    wait_status(repo, sha, ctx, "success", timeout=60)
    before = statuses(repo, sha)
    mark = standins.seq()

    # Settings › Restart: every 5 minutes. Saving it is a settings change that restarts nothing.
    gitberth.open(page, f"/apps/{app.key}/settings")
    section = page.locator("#sec-restart")
    section.get_by_role("switch", name="Off", exact=True).click()
    section.get_by_label("How many").fill("5")
    section.get_by_label("Unit").select_option("Minutes")
    bar = page.get_by_role("region", name="Unsaved changes")
    until(lambda: "nothing restarts" in bar.inner_text(), 30, "the apply bar to say nothing restarts", seen=bar.inner_text)
    bar.get_by_role("button", name=re.compile(r"^(Review and apply|Apply)$")).click()
    gitberth.snackbar(page, "^Saved")
    assert gitberth.db.last_deployment_id(app.id) == one["Id"], "saving a schedule ran a deploy"

    # The scheduler's restart: a deploy without a build of the same commit, once the container has run 5 minutes.
    restarted = until(lambda: gitberth.db.one("SELECT * FROM Deployments WHERE AppId = ? AND Trigger = 'Restart' AND Status IN "
                                              "('Succeeded', 'Failed', 'RolledBack', 'Cancelled')", app.id),
                      480, "the scheduled restart", interval=5, seen=lambda: gitberth.db.deployments(app.id)[:3])
    assert (restarted["Status"], restarted["CommitSha"]) == ("Succeeded", sha), restarted
    stays(lambda: status_writes(repo, mark) == [], 5, "no commit status after a scheduled restart")
    assert statuses(repo, sha) == before


def test_turning_commit_statuses_off_stops_them_and_on_resumes(page, new_page, gitberth, github, standins, apps, uniq):
    repo = github.create_repo(uniq("so"), files={"marker.txt": "switch v1"})
    first = repo.branch_sha("main")
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    ctx = context(app.name)
    wait_status(repo, first, ctx, "success", timeout=60)
    try:
        # Off: Settings › GitHub says so, keeps it, and shows it after a reload.
        said = set_github_switch(gitberth, page, STATUSES, on=False)
        assert said and said.startswith("Deploys don't set commit statuses any more."), said
        assert gitberth.db.setting("github.statuses") == "false"
        page.reload()
        gitberth.wait_ready(page)
        assert page.locator("#github-integration").get_by_role("switch", name=STATUSES, exact=True).get_attribute("aria-checked") == "false"

        # A deploy while they're off marks nothing, then or after it.
        mark = standins.seq()
        second = repo.commit({"marker.txt": "switch v2"}, message="Deployed while statuses are off")["sha"]
        two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=300)
        assert two["CommitSha"] == second, two
        stays(lambda: status_writes(repo, mark) == [], 6, "no commit status while they're off")

        # On again: the next deploy reports, pending then success.
        said = set_github_switch(gitberth, page, STATUSES, on=True)
        assert said and said.startswith("Deploys set commit statuses on GitHub again."), said
        assert gitberth.db.setting("github.statuses") is None
        third = repo.commit({"marker.txt": "switch v3"}, message="Deployed with statuses on again")["sha"]
        pending = wait_status(repo, third, ctx, "pending", timeout=120)
        three = gitberth.wait_deploy(app.id, after=two["Id"], timeout=300)
        assert three["CommitSha"] == third, three
        wait_status(repo, third, ctx, "success", after=pending["id"], timeout=60)
        # The commit deployed while they were off still has none: nothing is made up for it later.
        assert statuses(repo, second) == []
    finally:
        set_github_switch(gitberth, new_page(), STATUSES, on=True)


def test_a_branch_preview_reports_under_its_parents_preview_context(page, gitberth, github, standins, apps, uniq):
    repo = github.create_repo(uniq("bs"), files={"marker.txt": "production"})
    repo.create_branch("feature")
    app = apps.track(gitberth.create_git_app(page, repo.url, name=f"Site {repo.name}"))
    gitberth.wait_deploy(app.id, timeout=420)
    before = standins.seq()
    assert "Saved" in gitberth.set_previews(page, app, mode="Pull requests and branches")
    listed = repo.wait_pulls_listed(since=before, timeout=90)
    # Two more passes, so the branch is known as it was before previews: only a new commit on it makes its preview.
    repo.wait_watcher_passes(2, since=listed["seq"], timeout=90)

    mark = standins.seq()
    sha = repo.commit({"marker.txt": "branch work"}, branch="feature", message="Work on a branch")["sha"]
    preview = gitberth.wait_preview(app.id, branch="feature", timeout=120)
    assert preview["Kind"] == "Branch" and preview["PullRequest"] is None, preview
    ctx = context(app.name, preview=True)
    pending = wait_status(repo, sha, ctx, "pending", timeout=240)
    deploy = gitberth.wait_deploy(preview["Id"], timeout=420)
    assert deploy["CommitSha"] == sha, deploy
    success = wait_status(repo, sha, ctx, "success", after=pending["id"], timeout=60)

    # Under the parent's name ("Site …"), not the preview's ("Site … · feature"); only that context, as production didn't
    # deploy the commit.
    assert ctx == f"Gitberth preview: {app.name}" and preview["Name"] != app.name
    assert [(s["context"], s["state"]) for s in repo.statuses(sha=sha)] == [(ctx, "pending"), (ctx, "success")]
    ui = gitberth.db.setting("ui.address")
    assert pending["target_url"] == f"{ui}apps/{preview['Id']}/deployments/{deploy['Id']}", pending
    # Its success links to the preview's own address, which answers with the branch's version.
    url = gitberth.lan_url(preview["Id"])
    assert url != gitberth.lan_url(app.id)
    assert (success["description"], success["target_url"]) == (f"Preview is live at {bare(url)}", url + "/"), success
    assert gitberth.wait_answers(url, "/")["marker"] == "branch work"

    # A branch has no pull request: no comment, nor any try at one.
    stays(lambda: comment_writes(repo, mark) == [], 3, "no comment for a branch's preview")
    assert repo.comments() == []
    assert gitberth.preview(app.id, branch="feature")["CommentId"] is None


@contextlib.contextmanager
def domains_turned_on(gitberth, new_page, cloudflare):
    """Settings › Domains: on, with a Cloudflare token; off again afterwards, the token removed, whatever happened."""
    page = new_page()
    gitberth.open(page, "/settings")
    section = page.locator("#domains")
    name = "Manage app domains through Cloudflare"
    try:
        switch = section.get_by_role("switch", name=name)
        if switch.get_attribute("aria-checked") != "true":
            switch.click()
            gitberth.snackbar(page, "Domains turned on")
        tunnel = cloudflare.state()["tunnel_id"]
        until(lambda: f"Tunnel {tunnel[:8]}" in section.inner_text(), 30, "the Domains card to find the tunnel", seen=section.inner_text)
        section.locator("#cf-token").fill(cloudflare.token())
        section.get_by_role("button", name="Save token").click()
        said = gitberth.snackbar(page, "Token saved|Cloudflare")
        assert said.startswith("Token saved"), said
        yield
    finally:
        restore = new_page()
        gitberth.open(restore, "/settings")
        section = restore.locator("#domains")
        switch = section.get_by_role("switch", name=name)
        if switch.get_attribute("aria-checked") == "true":
            switch.click()
            gitberth.snackbar(restore, "Domains turned off")
        if section.get_by_role("button", name="Remove").count() > 0:
            section.get_by_role("button", name="Remove").click()
            gitberth.snackbar(restore, "Cloudflare token removed")


def add_domain(gitberth, page, app, host):
    """The app's Network tab › Domains: adds the domain, and waits for the card to say it's live."""
    gitberth.open(page, f"/apps/{app.key}/network")
    card = page.locator("section[aria-labelledby='gb-domains-title']")
    box = card.get_by_label("New domain")
    box.wait_for()
    until(lambda: box.is_enabled(), 30, "the Domains card to take a domain", seen=card.inner_text)
    box.fill(host)
    card.get_by_role("button", name=re.compile(r"^Add domain")).click()
    row = card.locator(".gb-drow", has_text=host)
    row.wait_for(timeout=60_000)

    def live():
        pill = row.locator(".gb-pill2")
        if pill.count() and pill.first.inner_text().strip() == "Live":
            return True
        again = card.get_by_role("button", name="Check again")
        if again.is_enabled():
            again.click()
        return False

    until(live, 120, f"{host} to be live on the Domains card", interval=3, seen=card.inner_text)


def test_the_success_link_is_the_main_domain_once_the_app_has_one(page, new_page, gitberth, github, cloudflare, apps, uniq):
    zone = f"{uniq('st')}.test"
    host = f"shop.{zone}"
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("dl"), files={"marker.txt": "domain link"})
    sha = repo.branch_sha("main")
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    ctx = context(app.name)
    # No domain yet: the LAN address.
    lan = wait_status(repo, sha, ctx, "success", timeout=60)
    assert lan["target_url"] == gitberth.lan_url(app.id) + "/", lan

    with domains_turned_on(gitberth, new_page, cloudflare):
        try:
            add_domain(gitberth, page, app, host)
            assert [d["Hostname"] for d in gitberth.db.domains(app.id)] == [host]
            deployment = gitberth.deploy(page, app)
            two = wait_deployment(gitberth, deployment)
            assert two["Id"] > one["Id"] and two["CommitSha"] == sha, two
            success = wait_status(repo, sha, ctx, "success", after=lan["id"], timeout=60)
            # Its main domain now, over https, as visitors reach it.
            assert (success["description"], success["target_url"]) == (f"Deploy is live at {host}", f"https://{host}/"), success
            between = [s for s in statuses(repo, sha, ctx) if lan["id"] < s["id"] < success["id"]]
            assert [s["state"] for s in between] == ["pending"] and between[0]["target_url"].endswith(f"/apps/{app.id}/deployments/{deployment}"), between
            # The link leads to the app, through Cloudflare's edge and the tunnel.
            visit = cloudflare.visit(success["target_url"])
            assert visit["status"] == 200 and json.loads(visit["body"])["marker"] == "domain link", visit
        finally:
            # Removed while domains are on, so its route and DNS record go with it.
            if gitberth.db.app(app.slug) is not None:
                gitberth.wait_idle(app.id, timeout=300)
                gitberth.delete_app(page, app)
