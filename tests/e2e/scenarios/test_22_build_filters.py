"""Scenario 22: build filters, commits that ask not to be deployed, and builds without the cache (1.2).

- Build filters are set on the app's Settings tab (Source), and saving them restarts and rebuilds nothing.
- A push that changes only files the filters leave out isn't deployed: its deploy ends Skipped with why, the app keeps
  running its commit, GitHub gets a success status for the skipped commit (never a pending one), and the watcher doesn't
  start it again. The deploy's page says the running version stays.
- A push that changes a file the app builds from deploys, compared with the commit that runs.
- A commit whose message says [skip ci] isn't deployed either; Deploy by hand still builds it.
- Deploy without the build cache (the app page's More menu) builds every step again, and its page and log say so.
- Settings › Deploys shows how big the build cache is, and asks before clearing it.
"""
import re

from e2e.github_reports import context, statuses, wait_deployment, wait_status
from e2e.net import get, get_json
from e2e.wait import until


def marker_of(url):
    return (get_json(url.rstrip("/") + "/") or {}).get("marker")


def set_filters(gitberth, page, app, watch=None, ignored=None):
    """The Settings tab's Source section: the build filters typed in, then Apply. Returns the snackbar's text."""
    gitberth.open(page, f"/apps/{app.key}/settings")
    section = page.locator("#sec-source")
    section.wait_for()
    if watch is not None:
        section.locator("#set-watch").fill(watch)
    if ignored is not None:
        section.locator("#set-ignored").fill(ignored)
    bar = page.locator(".gb-applybar")
    bar.wait_for()
    # Once its plan is worked out, the bar says what applying does: nothing that runs changes.
    until(lambda: "nothing restarts" in bar.inner_text(), 30, "the apply bar to say nothing restarts", seen=bar.inner_text)
    bar.get_by_role("button", name=re.compile(r"^(Apply|Review and apply)$")).click()
    return gitberth.snackbar(page, "^Saved")


def test_a_push_the_filters_leave_out_is_skipped_and_one_they_watch_deploys(page, gitberth, github, engine, apps, uniq, standins):
    repo = github.create_repo(uniq("bf"), files={"marker.txt": "filters v1"})
    first = repo.branch_sha("main")
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    url = gitberth.lan_url(app.id)
    until(lambda: marker_of(url) == "filters v1", 60, "the first version to answer", seen=lambda: get(url))
    ctx = context(app.name)

    # Saved at once, with nothing pending and no deploy: the filters only decide which commits deploy by themselves.
    said = set_filters(gitberth, page, app, ignored="# The docs don't deploy\ndocs/\n*.md")
    assert said == "Saved.", said
    row = gitberth.db.app(app.slug)
    assert (row["IgnoredPaths"], row["WatchPaths"]) == ("# The docs don't deploy\ndocs/\n*.md", None), row
    assert (row["ConfigChangePending"], row["RebuildPending"]) == (0, 0), row
    assert gitberth.db.last_deployment_id(app.id) == one["Id"]

    # A docs-only push: skipped, and the app keeps running its commit.
    docs = repo.commit({"docs/guide.md": "# Guide\n", "README.md": "# Readme\n"}, message="Write the docs")["sha"]
    skipped = gitberth.wait_deploy(app.id, after=one["Id"], statuses=("Skipped",), timeout=300)
    assert (skipped["CommitSha"], skipped["CommitMessage"], skipped["Error"]) == (docs, "Write the docs", None), skipped
    assert skipped["Trigger"] in ("Poll", "Webhook"), skipped
    assert skipped["SkipReason"] == "Only files the build filters leave out changed: README.md, docs/guide.md", skipped
    row = gitberth.db.app(app.slug)
    assert (row["CurrentCommit"], row["LastAttemptedCommit"]) == (first, docs), row
    assert engine.image_of(app.container) == f"{app.image_repository}:{first[:7]}"
    assert marker_of(url) == "filters v1"
    log = gitberth.deploy_log(skipped["Id"])
    assert "==> Skipped: only files the build filters leave out changed: README.md, docs/guide.md." in log, log
    assert f"Nothing was built, and {app.name} keeps running {first[:7]}." in log, log

    # GitHub: a success for the skipped commit, linking to its log, and never a pending one.
    ui = gitberth.db.setting("ui.address")
    success = wait_status(repo, docs, ctx, "success", timeout=60)
    assert success["description"] == "Skipped: nothing the app builds from changed", success
    assert success["target_url"] == f"{ui}apps/{app.id}/deployments/{skipped['Id']}", success
    assert [s["state"] for s in statuses(repo, docs, ctx)] == ["success"]

    # The watcher leaves the skipped commit alone: no loop.
    repo.wait_watcher_passes(2, since=standins.seq())
    assert gitberth.db.last_deployment_id(app.id) == skipped["Id"], gitberth.db.deployments(app.id)[:2]

    # Its page says why, and that the running version stays; the app's page still shows the deploy that runs as its last.
    gitberth.open(page, f"/apps/{app.id}/deployments/{skipped['Id']}")
    banner = page.locator(".gb-banner", has_text="Skipped: the running version stays")
    banner.wait_for()
    assert "README.md, docs/guide.md" in banner.inner_text(), banner.inner_text()
    assert page.locator(".gb-hero-meta .gb-pill", has_text="Skipped").is_visible()
    gitberth.open(page, f"/apps/{app.key}")
    hero = page.locator(".gb-hero")
    until(lambda: f"#{one['Id']}" in hero.inner_text(), 30, "the app page's last deploy", seen=hero.inner_text)

    # A push that changes the app: compared with the commit that runs, so the docs changed since count too, and it deploys.
    code = repo.commit({"marker.txt": "filters v2"}, message="Ship v2")["sha"]
    two = gitberth.wait_deploy(app.id, after=skipped["Id"], timeout=300)
    assert two["CommitSha"] == code, two
    until(lambda: marker_of(url) == "filters v2", 60, "the new version to answer", seen=lambda: get(url))
    assert f"The build filters let it through: marker.txt changed since {first[:7]}, with 2 more files." in gitberth.deploy_log(two["Id"])
    wait_status(repo, code, ctx, "success", timeout=60)
    assert [s["state"] for s in statuses(repo, code, ctx)] == ["pending", "success"]
    assert not gitberth.errors(page), gitberth.errors(page)


def test_a_commit_that_says_skip_ci_isnt_deployed_until_deployed_by_hand(page, gitberth, github, apps, uniq):
    repo = github.create_repo(uniq("sk"), files={"marker.txt": "skip v1"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    url = gitberth.lan_url(app.id)
    until(lambda: marker_of(url) == "skip v1", 60, "the first version to answer", seen=lambda: get(url))

    # The marker in the body, in another case: no build filters needed.
    sha = repo.commit({"marker.txt": "skip v2"}, message="Try an idea\n\nNot ready to ship yet. [Skip CI]")["sha"]
    skipped = gitberth.wait_deploy(app.id, after=one["Id"], statuses=("Skipped",), timeout=300)
    assert (skipped["CommitSha"], skipped["CommitMessage"]) == (sha, "Try an idea"), skipped
    assert skipped["SkipReason"] == "The commit message says [Skip CI]", skipped
    success = wait_status(repo, sha, context(app.name), "success", timeout=60)
    assert success["description"] == "Skipped: the commit asked not to be deployed", success
    assert marker_of(url) == "skip v1"

    # Deploy by hand always builds.
    manual = wait_deployment(gitberth, gitberth.deploy(page, app))
    assert (manual["CommitSha"], manual["Trigger"]) == (sha, "Manual"), manual
    until(lambda: marker_of(url) == "skip v2", 60, "the commit to answer once deployed by hand", seen=lambda: get(url))


def test_deploy_without_the_build_cache(page, gitberth, github, apps, uniq):
    repo = github.create_repo(uniq("nc"), files={"marker.txt": "no cache"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)

    gitberth.open(page, f"/apps/{app.key}")
    page.locator(".gb-hero-actions button[aria-label='More actions']").click()
    entry = page.locator(".mud-menu-item", has_text="Deploy without the build cache")
    entry.wait_for()
    entry.click()
    page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"))
    deployment = int(gitberth.path_of(page).rsplit("/", 1)[-1])
    assert deployment > one["Id"]
    done = wait_deployment(gitberth, deployment)
    assert (done["NoCache"], done["Trigger"]) == (1, "Manual"), done
    log = gitberth.deploy_log(deployment)
    assert "without the build cache: every step runs again, and the base images are pulled" in log, log
    # The step that runs the app's own code ran again rather than coming from the cache.
    step = re.search(r"#(\d+) \[[^\]]*\] RUN .*python3 /app/build_info\.py", log)
    assert step, log
    assert not re.search(rf"#{step.group(1)} CACHED", log), log
    gitberth.open(page, f"/apps/{app.id}/deployments/{deployment}")
    page.locator(".gb-hero-meta .gb-tag", has_text="Without the build cache").wait_for()

    # The app's history says so too.
    gitberth.open(page, f"/apps/{app.key}/activity")
    until(lambda: "without the build cache" in page.inner_text("body"), 30, "the Activity tab to say it", seen=lambda: page.inner_text("body"))
    assert not gitberth.errors(page), gitberth.errors(page)


def test_settings_shows_the_build_cache_and_asks_before_clearing_it(page, gitberth):
    gitberth.open(page, "/settings")
    row = page.locator("#build-cache")
    row.wait_for()
    until(lambda: re.search(r"[\d.]+ (B|KiB|MiB|GiB|TiB) on this host", row.inner_text()), 60, "the build cache's size", seen=row.inner_text)
    row.get_by_role("button", name="Clear build cache").click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    text = dialog.inner_text()
    assert "Clear the build cache?" in text and "the next build of each app runs every step again" in text, text
    # Not cleared here: the rig's own images build from the same cache.
    dialog.get_by_role("button", name="Cancel").click()
    dialog.wait_for(state="detached")
    assert page.locator(".mud-snackbar").count() == 0
    assert not gitberth.errors(page), gitberth.errors(page)
