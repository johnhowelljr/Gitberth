"""Scenario 2: pull request previews, from branches of the same repo.

- Opening a pull request makes <app>-pr-<n>: an app of its own (container, image, LAN address), built from
  refs/pull/<n>/head, told VERCEL_ENV=preview and the pull request's number. Production is untouched.
- The parent's Previews tab lists it: its label (PR #n) linking to the pull request, its title, state, commit and LAN
  address, and its links (Open, Log, its page, the menu). Its own page has the preview banner and a preview's tabs only,
  and its deploy log page says what it is.
- A new commit to the pull request redeploys the same preview.
- Its row on the Previews tab follows its deploys: Failed when its first build fails (nothing to open, its failed
  deploy's log at hand), deploying while a new commit deploys, Ready once it runs, and "Ready; its newest commit didn't
  deploy" while a newer commit's build has failed and the older one still runs.
- Closing the pull request removes its preview, and the parent's Activity says the pull request was closed; merging one
  removes its preview, and says it was merged. Without a webhook, the watcher's regular look at the pull requests (every
  5 minutes) is what notices both.
- After the merge, the branch that stays gets no branch preview until it gets a new commit.
- While GitHub's list of pull requests fails (an injected 502), previews stay: nothing is removed as closed, a preview
  still follows its pull request's new commits (the repo's own refs have them), and the Previews tab says the pull
  requests couldn't be read. Once GitHub answers again, a pull request closed meanwhile loses its preview.

The helpers here (worlds several scenarios share, pull requests, waits, the Previews tab) serve scenario 3 too.
"""
import contextlib
import json
import os
import re
import secrets
import time
from types import SimpleNamespace

import pytest
from playwright.sync_api import TimeoutError as PlaywrightTimeout

from e2e import config
from e2e.gitberth import FINAL
from e2e.net import get, get_json
from e2e.wait import until

# Where the forks of the scenarios' repos live on GitHub.
FORKER = "e2e-forker"
# Without a webhook the watcher asks GitHub for a repo's open pull requests when their heads move (refs/pull/<n>/head in
# ls-remote), and otherwise every 5 minutes (PreviewService.PullsEvery). Closing or merging a pull request moves no head.
PULLS_EVERY = 300


# ---- helpers (scenario 3 uses them too) ----

def unique(prefix):
    """A unique short name, for a world several scenarios share (the uniq fixture is per scenario)."""
    return f"{prefix}-{secrets.token_hex(2)}"


class Made:
    """The apps a shared world made: removed when the world ends."""

    def __init__(self):
        self.apps = []

    def track(self, app):
        self.apps.append(app)
        return app


@contextlib.contextmanager
def own_apps(gitberth, standins, name):
    """For a world several scenarios share (a class-scoped fixture): a signed-in page, and the apps made with it, removed
    with their images and volumes when the world ends (unless E2E_KEEP), as the apps fixture removes a scenario's. When
    setting the world up fails, its page's screenshot is saved in the rig's /e2e/out as <name>-setup.png."""
    page = gitberth.new_page()
    made = Made()
    try:
        try:
            yield page, made
        except Exception:
            with contextlib.suppress(Exception):
                os.makedirs(config.OUT, exist_ok=True)
                page.screenshot(path=os.path.join(config.OUT, f"{name}-setup.png"), full_page=True)
            raise
    finally:
        with contextlib.suppress(Exception):
            standins.clear_failures()
        if not config.KEEP:
            for app in reversed(made.apps):
                try:
                    if gitberth.db.app(app.slug) is not None:
                        gitberth.wait_idle(app.id, timeout=300)
                        gitberth.delete_app(page, app)
                except Exception as e:  # noqa: BLE001 - cleaning up never fails a scenario
                    print(f"[e2e] couldn't remove {app.slug} after its scenarios: {e}")
        page.context.close()


def new_app(page, gitberth, github, track, name, files=None):
    """A repo made from the test app (its main says "production"; with `files` added, {path: text}) and the Git app New
    app makes of it, its first deploy over. Returns (repo, app, production's deploy)."""
    repo = github.create_repo(name, files={"marker.txt": "production", **(files or {})})
    app = track(gitberth.create_git_app(page, repo.url))
    production = gitberth.wait_deploy(app.id, timeout=420)
    assert production["CommitSha"] == repo.branch_sha("main"), production
    return repo, app, production


def open_pull(repo, branch, title, marker=None, fork=None, files=None):
    """A new branch with a commit of its own (in the fork, when one is given: its marker.txt, and any other files), and a
    pull request from it into the repo's main. Returns (its number, its head commit)."""
    source = fork or repo
    sha = source.commit({"marker.txt": marker or title, **(files or {})}, branch=branch, message=f"{title}: first commit")["sha"]
    pull = repo.open_pull(f"{fork.owner}:{branch}" if fork else branch, title=title)
    assert pull["head"]["sha"] == sha, pull["head"]
    return pull["number"], sha


def wait_finished(gitberth, app_id, sha, after=0, timeout=300):
    """Waits for a deploy of the app (after deployment `after`) that fetched `sha` to be over, however it ended."""
    def done():
        rows = gitberth.db.query("SELECT * FROM Deployments WHERE AppId = ? AND Id > ? AND CommitSha = ? ORDER BY Id DESC",
                                 app_id, after, sha)
        return next((d for d in rows if d["Status"] in FINAL), None)

    return until(done, timeout, f"a deploy of app {app_id} at {sha[:7]} to finish", interval=2,
                 seen=lambda: [{k: x[k] for k in ("Id", "Trigger", "Status", "CommitSha", "Error")} for x in gitberth.db.deployments(app_id)[:4]])


def wait_deployed(gitberth, app_id, sha, after=0, timeout=300):
    """Waits for a deploy of the app (after deployment `after`) that built `sha` to be over, and checks it succeeded:
    otherwise it fails with the deploy's error and the end of its log. Returns the deploy."""
    d = wait_finished(gitberth, app_id, sha, after, timeout)
    if d["Status"] != "Succeeded":
        raise AssertionError(f"Deploy #{d['Id']} of app {app_id} at {sha[:7]} ended {d['Status']}: {d['Error']}\n"
                             f"--- the end of its log ---\n{gitberth.deploy_log(d['Id'], tail=40)}")
    return d


def fetched(repo, since, ref):
    """What Gitberth's git asked the repo for after the marker: (whether an ls-refs named `ref`, the commits its fetches
    wanted, a summary of its requests for failure messages)."""
    git = [c.get("git") or {} for c in repo.git_calls(since)]
    asked = any(g.get("command") == "ls-refs" and ref in (g.get("ref_prefixes") or []) for g in git)
    wanted = {w for g in git if g.get("command") == "fetch" for w in (g.get("wants") or [])}
    shown = [(g.get("command"), g.get("ref_prefixes"), [w[:7] for w in g.get("wants") or []]) for g in git]
    return asked, wanted, shown


def leftovers(engine, slug):
    """What the engine still has of the app with this slug: its container, images and own named volumes."""
    left = [f"the container gb-{slug}"] if engine.exists(f"gb-{slug}") else []
    left += [f"the image {i}" for i in engine.images(f"gitberth/{slug}")]
    left += [f"the volume {v}" for v in engine.volumes(prefix=f"gb-{slug}-")]
    return left


def activity_texts(gitberth, app):
    """The parent's Activity entries, oldest first."""
    return [a["Text"] for a in gitberth.db.activity(app.id)]


def branch_preview(gitberth, parent_id, branch):
    """The parent's preview of the branch itself (not a pull request from it), or None."""
    return next((p for p in gitberth.db.previews(parent_id) if p["Kind"] == "Branch" and p["PreviewBranch"] == branch), None)


def runtime_extras(row):
    """An app row's settings beyond the classic ones (its RuntimeExtras column, camelCase JSON), or {}."""
    return json.loads(row["RuntimeExtras"]) if row and row["RuntimeExtras"] else {}


def log_lines(page):
    """The lines a deploy log page shows (each line's text, without its number and time)."""
    return [t.strip() for t in page.locator(".gb-terminal-body .gb-ln-text").all_inner_texts()]


def preview_row(page, label):
    """The parent's Previews tab's row of a preview, by its label: "PR #3", or a branch."""
    return page.locator(".gb-drow").filter(has=page.locator(".gb-drow-host a.gb-mono", has_text=re.compile(rf"^{re.escape(label)}$")))


def previews_card(page):
    """The Previews tab's card (its summary is its head's quiet line, .gb-card-sub)."""
    return page.locator("section.gb-card").filter(has=page.locator(".gb-card-title", has_text="Previews"))


def text_of(locator):
    return re.sub(r"\s+", " ", locator.inner_text()).strip()


# ---- opening a pull request, and a new commit to it ----

@pytest.fixture(scope="class")
def opened(gitberth, github, standins):
    """An app with previews of pull requests on, then a pull request opened from a branch of its repo: its preview made
    and deployed."""
    with own_apps(gitberth, standins, "pr-opened") as (page, made):
        repo, app, production = new_app(page, gitberth, github, made.track, unique("pro"))
        said = gitberth.set_previews(page, app, mode="Pull requests")
        assert said.startswith("Saved"), said
        assert gitberth.db.app(app.slug)["PreviewMode"] == "PullRequests"
        mark = standins.seq()
        number, sha = open_pull(repo, "feature", "Add a feature", marker="feature one")
        preview = gitberth.wait_preview(app.id, pr=number, timeout=90)
        deploy = wait_deployed(gitberth, preview["Id"], sha)
        yield SimpleNamespace(repo=repo, app=app, production=production, mark=mark, number=number, sha=sha, preview=preview, deploy=deploy)


class TestOpenedPullRequest:
    def test_it_gets_a_preview_built_from_its_pull_ref(self, opened, gitberth, engine):
        w = opened
        p = w.preview
        n = w.number
        assert p["Slug"] == f"{w.app.slug}-pr-{n}", p["Slug"]
        assert p["Name"] == f"{w.app.name} · PR #{n}", p["Name"]
        assert (p["Kind"], p["PullRequest"], p["Ref"], p["PreviewBranch"]) == ("PullRequest", n, f"refs/pull/{n}/head", "feature"), p
        assert (p["PreviewTitle"], p["HeadSha"], p["FromFork"]) == ("Add a feature", w.sha, 0), p
        # An app of its own under its parent, which the watcher's per-app loop leaves to the previews' logic.
        assert p["ParentAppId"] == w.app.id and p["AutoDeploy"] == 0, p
        assert w.deploy["Trigger"] == "Poll", w.deploy
        assert f"Preview PR #{n} added: Add a feature." in activity_texts(gitberth, w.app), activity_texts(gitberth, w.app)

        # Built from the pull request's ref: Gitberth's git asked the repo for refs/pull/<n>/head, and fetched its commit.
        asked, wanted, shown = fetched(w.repo, w.mark, f"refs/pull/{n}/head")
        assert asked and w.sha in wanted, f"Gitberth didn't fetch refs/pull/{n}/head ({w.sha[:7]}); its git requests: {shown}"

        # Its own container, from its own image repository, at that commit.
        container = f"gb-{p['Slug']}"
        assert engine.running(container), engine.inspect(container)
        assert engine.image_of(container) == f"gitberth/{p['Slug']}:{w.sha[:7]}"

        # It answers on its own LAN address, as a preview of the pull request.
        url = gitberth.lan_url(p["Id"])
        production_url = gitberth.lan_url(w.app.id)
        assert url != production_url
        assert gitberth.wait_answers(url, "/")["marker"] == "feature one"
        env = gitberth.wait_answers(url, "/env")
        expected = {"VERCEL_ENV": "preview", "VERCEL_TARGET_ENV": "preview", "VERCEL_GIT_PULL_REQUEST_ID": str(n),
                    "VERCEL_GIT_COMMIT_REF": "feature", "VERCEL_GIT_COMMIT_SHA": w.sha, "VERCEL_GIT_COMMIT_MESSAGE": "Add a feature: first commit",
                    "VERCEL_GIT_REPO_OWNER": w.repo.owner, "VERCEL_GIT_REPO_SLUG": w.repo.name, "VERCEL_GIT_PROVIDER": "github"}
        assert {k: env.get(k) for k in expected} == expected

        # Production is untouched: same deploy, still production of main.
        production_env = gitberth.wait_answers(production_url, "/env")
        assert production_env.get("VERCEL_ENV") == "production" and production_env.get("VERCEL_GIT_COMMIT_SHA") == w.production["CommitSha"]
        assert "VERCEL_GIT_PULL_REQUEST_ID" not in production_env
        assert gitberth.db.last_deployment_id(w.app.id) == w.production["Id"], "production was deployed again"

    def test_the_previews_tab_and_its_own_page_show_it(self, opened, page, gitberth):
        w = opened
        n = w.number
        label = f"PR #{n}"
        preview_id = w.preview["Id"]
        container = f"gb-{w.preview['Slug']}"
        commit = gitberth.db.app_by_id(preview_id)["CurrentCommit"]
        deployment = gitberth.db.last_deployment_id(preview_id)
        lan = gitberth.lan_url(preview_id)
        pull_url = f"{w.repo.url}/pull/{n}"

        # The parent's Previews tab: its label linking to the pull request, its title, Ready, its commit, its LAN address.
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        row = preview_row(page, label)
        row.wait_for()
        pill = row.locator(".gb-pill2")
        until(lambda: text_of(pill) == "Ready", 60, f"{label}'s row to say Ready", seen=lambda: text_of(pill))
        link = row.locator(".gb-drow-host a.gb-mono")
        assert (link.get_attribute("href"), link.get_attribute("target")) == (pull_url, "_blank")
        assert text_of(row.locator(".gb-drow-host .gb-ellipsis")) == "Add a feature"
        assert row.locator(".gb-tag", has_text="Fork").count() == 0, "a pull request from the repo's own branch is tagged as a fork"
        sub = text_of(row.locator(".gb-drow-sub").first)
        assert commit[:7] in sub and "last commit" in sub and "removed in 7 days without a new commit" in sub, sub
        lan_link = row.locator("a[title='On the LAN']")
        assert (lan_link.get_attribute("href"), text_of(lan_link)) == (lan, lan.split("://", 1)[1])
        # Open goes to its LAN address (it has no domain); Log to its newest deploy; Its page to its own page.
        assert row.get_by_role("link", name=f"Open {label}").get_attribute("href") == lan
        assert row.get_by_role("link", name=f"Log of {label}").get_attribute("href") == f"apps/{preview_id}/deployments/{deployment}"
        assert row.get_by_role("link", name="Its page").get_attribute("href") == f"apps/{container}"
        card = previews_card(page)
        assert text_of(card.locator(".gb-card-sub")) == "Pull requests · 1 of 3 running"
        foot = text_of(card.locator(".gb-card-foot"))
        assert f"Each preview is an app of its own, built with {w.app.name}'s settings and its variables for previews" in foot, foot
        assert "from a fork" not in foot, f"with forks off, the Previews tab talks about forks: {foot}"
        # Its Settings button goes to the Settings tab's Previews section.
        card.get_by_role("link", name="Settings").click()
        page.wait_for_url(re.compile(rf"/apps/{re.escape(w.app.key)}/settings#sec-previews$"))
        page.locator("#sec-previews").wait_for()
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        row = preview_row(page, label)
        row.wait_for()

        # Its page, by its link: the banner says what it previews, and it has only a preview's tabs.
        row.get_by_role("link", name="Its page").click()
        page.wait_for_url(re.compile(rf"/apps/{re.escape(container)}$"))
        banner = page.locator(".gb-banner", has_text="Preview of")
        banner.wait_for()
        assert text_of(banner.locator(".gb-banner-title")) == f"Preview of {w.app.name} for {label}"
        assert f"Its settings come from {w.app.name}, and so do its variables for previews." in text_of(banner.locator(".gb-banner-sub"))
        assert banner.get_by_role("link", name="Pull request").get_attribute("href") == pull_url
        assert page.locator(".gb-crumbs").get_by_role("link", name=w.app.name, exact=True).get_attribute("href") == f"apps/{w.app.key}/previews"
        assert gitberth.tabs(page) == ["Overview", "Network", "Logs", "Activity"]

        # Back to the parent's previews by the banner's button, then the preview's deploy log by its Log button.
        banner.get_by_role("link", name="All previews").click()
        page.wait_for_url(re.compile(rf"/apps/{re.escape(w.app.key)}/previews$"))
        row = preview_row(page, label)
        row.wait_for()
        row.get_by_role("link", name=f"Log of {label}").click()
        page.wait_for_url(re.compile(rf"/apps/{preview_id}/deployments/{deployment}$"))
        # A step's line shows without its "==> " (LogLine), as the deploy log page marks it as a step.
        step = f"A preview of {w.app.name} ({label}): its settings are {w.app.name}'s, and so are its variables for previews"
        until(lambda: step in log_lines(page), 30, "the deploy log to say what the preview is", seen=lambda: log_lines(page)[:12])
        assert f"==> {step}" in gitberth.deploy_log(deployment), "the deploy log file doesn't say what the preview is"
        assert page.locator(".gb-hero-meta .gb-tag", has_text="New commit").count() == 1
        assert commit[:7] in text_of(page.locator(".gb-hero-meta"))
        live = page.locator(".gb-banner", has_text="Live")
        live.wait_for()
        assert lan.split("://", 1)[1] in text_of(live), text_of(live)

        # Its menu on the Previews tab: Redeploy, Promote to production and Remove.
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        row = preview_row(page, label)
        row.wait_for()
        row.locator(f"button[aria-label='More for {label}']").click()
        for item in ("Redeploy", "Promote to production", "Remove"):
            page.locator(".mud-menu-item", has_text=item).first.wait_for()
        page.keyboard.press("Escape")
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_a_new_commit_to_it_redeploys_the_preview(self, opened, gitberth, engine, standins):
        w = opened
        before = gitberth.preview(w.app.id, pr=w.number)
        last = gitberth.db.last_deployment_id(before["Id"])
        mark = standins.seq()
        sha = w.repo.commit({"marker.txt": "feature two"}, branch="feature", message="More on the feature")["sha"]
        # The pull request's head moved: Gitberth asks GitHub again, and the same preview deploys the new commit.
        deploy = wait_deployed(gitberth, before["Id"], sha, after=last, timeout=240)
        assert deploy["Trigger"] == "Poll", deploy
        after = gitberth.preview(w.app.id, pr=w.number)
        assert (after["Id"], after["Slug"]) == (before["Id"], before["Slug"]), "the new commit made another preview"
        assert after["HeadSha"] == sha and after["CurrentCommit"] == sha, after
        assert after["LastActivityAt"] > before["LastActivityAt"], "its expiry didn't start again with the new commit"
        assert [p["Id"] for p in gitberth.db.previews(w.app.id)] == [before["Id"]]
        asked, wanted, shown = fetched(w.repo, mark, f"refs/pull/{w.number}/head")
        assert asked and sha in wanted, f"Gitberth didn't fetch refs/pull/{w.number}/head ({sha[:7]}); its git requests: {shown}"

        container = f"gb-{after['Slug']}"
        assert engine.image_of(container) == f"gitberth/{after['Slug']}:{sha[:7]}"
        url = gitberth.lan_url(after["Id"])
        until(lambda: (get_json(url + "/") or {}).get("marker") == "feature two", 60, "the preview to answer with the new commit", seen=lambda: get(url + "/"))
        env = gitberth.wait_answers(url, "/env")
        assert (env.get("VERCEL_GIT_COMMIT_SHA"), env.get("VERCEL_GIT_PULL_REQUEST_ID"), env.get("VERCEL_ENV")) == (sha, str(w.number), "preview")
        assert gitberth.db.last_deployment_id(w.app.id) == w.production["Id"], "production was deployed again"


# ---- the Previews tab's states ----

# The test app's Dockerfile (the template the scenarios' repos start from), whose build step a commit can break.
TESTAPP_DOCKERFILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "apps", "testapp", "Dockerfile")
BUILD_STEP = "RUN python3 /app/build_info.py > /app/build.json"
# What the Previews tab says while a preview deploys (PreviewStates: Building).
DEPLOYING = ("Queued…", "Fetching the code…", "Building…", "Starting…")


def test_the_previews_tab_follows_a_pull_requests_deploys(page, gitberth, github, apps, uniq):
    """The states a pull request's preview shows on the Previews tab: Failed when its first build fails (nothing to
    open, its failed deploy's log at hand), deploying while a new commit deploys, Ready once it runs, and Ready with a
    note while a newer commit's build has failed and the older one still runs."""
    with open(TESTAPP_DOCKERFILE, encoding="utf-8") as f:
        dockerfile = f.read()
    assert BUILD_STEP in dockerfile, f"the test app's Dockerfile has no {BUILD_STEP!r} for this scenario to break"
    broken = dockerfile.replace(BUILD_STEP, BUILD_STEP + " && false")
    repo, app, _ = new_app(page, gitberth, github, apps.track, uniq("prs"))
    said = gitberth.set_previews(page, app, mode="Pull requests")
    assert said.startswith("Saved"), said
    n, sha = open_pull(repo, "shaky", "Breaks the build", marker="broken", files={"Dockerfile": broken})
    label = f"PR #{n}"
    preview = gitberth.wait_preview(app.id, pr=n, timeout=90)
    failed = wait_finished(gitberth, preview["Id"], sha)
    assert failed["Status"] == "Failed", failed

    # Failed: nothing runs, so there's nothing to open; its Log is the failed deploy's, whose page says it failed.
    gitberth.open(page, f"/apps/{app.key}/previews")
    row = preview_row(page, label)
    pill = row.locator(".gb-pill2")
    pill.wait_for()
    until(lambda: text_of(pill) == "Failed", 60, f"{label}'s row to say Failed", seen=lambda: text_of(pill))
    assert "gb-tone-critical" in pill.get_attribute("class"), pill.get_attribute("class")
    assert row.get_by_role("link", name=f"Open {label}").count() == 0, "a preview that doesn't run offers Open"
    row.get_by_role("link", name=f"Log of {label}").click()
    page.wait_for_url(re.compile(rf"/apps/{preview['Id']}/deployments/{failed['Id']}$"))
    page.locator(".gb-banner", has_text="The deploy failed").wait_for()

    # A commit that fixes it: the row says it's deploying while it does, then Ready, at that commit.
    gitberth.open(page, f"/apps/{app.key}/previews")
    pill.wait_for()
    fixed = repo.commit({"Dockerfile": dockerfile, "marker.txt": "fixed"}, branch="shaky", message="Fixes the build")["sha"]
    until(lambda: text_of(pill) in DEPLOYING, 90, f"{label}'s row to say it's deploying", interval=0.5, seen=lambda: text_of(pill))
    assert "gb-tone-accent" in pill.get_attribute("class"), pill.get_attribute("class")
    wait_deployed(gitberth, preview["Id"], fixed, after=failed["Id"])
    until(lambda: text_of(pill) == "Ready", 60, f"{label}'s row to say Ready", seen=lambda: text_of(pill))
    assert "gb-tone-good" in pill.get_attribute("class") and fixed[:7] in text_of(row), text_of(row)
    lan = gitberth.lan_url(preview["Id"])
    assert row.get_by_role("link", name=f"Open {label}").get_attribute("href") == lan

    # A newer commit that breaks it again: the older one keeps running, and the row says the newest didn't deploy.
    again = repo.commit({"Dockerfile": broken, "marker.txt": "broken again"}, branch="shaky", message="Breaks it again")["sha"]
    assert wait_finished(gitberth, preview["Id"], again)["Status"] == "Failed"
    until(lambda: text_of(pill) == "Ready; its newest commit didn't deploy", 60, f"{label}'s row to say its newest commit didn't deploy",
          seen=lambda: text_of(pill))
    assert fixed[:7] in text_of(row) and again[:7] not in text_of(row), text_of(row)
    assert row.get_by_role("link", name=f"Open {label}").get_attribute("href") == lan
    assert gitberth.wait_answers(lan, "/")["marker"] == "fixed"
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


# ---- closing one pull request and merging another ----

@pytest.fixture(scope="class")
def ended(gitberth, github, engine, standins):
    """Two pull requests from branches of the repo, open when previews of pull requests and branches are turned on, both
    previewed; then one is closed and the other merged (its branch kept) on GitHub, and the watcher is given its regular
    look at the pull requests to notice. Whether both previews went in that time is for the scenarios to check."""
    with own_apps(gitberth, standins, "pr-ended") as (page, made):
        repo, app, production = new_app(page, gitberth, github, made.track, unique("pre"))
        closed_n, closed_sha = open_pull(repo, "feat-a", "To be closed", marker="to be closed")
        merged_n, merged_sha = open_pull(repo, "feat-b", "To be merged", marker="to be merged")
        said = gitberth.set_previews(page, app, mode="Pull requests and branches")
        assert said.startswith("Saved"), said
        previews = {}
        for n, sha in ((closed_n, closed_sha), (merged_n, merged_sha)):
            p = gitberth.wait_preview(app.id, pr=n, timeout=90)
            wait_deployed(gitberth, p["Id"], sha)
            previews[n] = SimpleNamespace(row=p, volumes=engine.volumes(prefix=f"gb-{p['Slug']}-"), images=engine.images(f"gitberth/{p['Slug']}"))
        # While their pull requests preview them, the branches have no preview of their own.
        branch_previews = [p["Slug"] for p in gitberth.db.previews(app.id) if p["Kind"] == "Branch"]

        mark = standins.seq()
        started = time.monotonic()
        repo.close_pull(closed_n)
        merge_sha = repo.merge_pull(merged_n)["sha"]
        try:
            until(lambda: all(gitberth.preview(app.id, pr=n) is None for n in previews), PULLS_EVERY + 120,
                  "both pull requests' previews to go", interval=3,
                  seen=lambda: [(p["Slug"], p["PullRequest"]) for p in gitberth.db.previews(app.id)])
            waited = None
        except AssertionError as e:
            waited = str(e)
        yield SimpleNamespace(repo=repo, app=app, production=production, closed_n=closed_n, merged_n=merged_n, merge_sha=merge_sha,
                              previews=previews, branch_previews=branch_previews, mark=mark, took=time.monotonic() - started, waited=waited)


def check_removed(w, n, reason, other, page, gitberth, engine):
    p = w.previews[n].row
    assert gitberth.preview(w.app.id, pr=n) is None, \
        f"PR #{n}'s preview is still there {w.took:.0f} s after the pull request was {reason.rsplit(' ', 1)[-1]} on GitHub. {w.waited}"
    assert gitberth.db.app(p["Slug"]) is None, f"{p['Slug']}'s app row is still there"
    # Its container, images and own volumes went with it.
    assert w.previews[n].images, f"PR #{n}'s preview had no image to remove: {vars(w.previews[n])}"
    left = leftovers(engine, p["Slug"])
    assert not left, f"removing PR #{n}'s preview left {left} (it had {w.previews[n].images + w.previews[n].volumes})"
    # The parent's Activity says why: GitHub was asked which it was.
    texts = activity_texts(gitberth, w.app)
    wanted = f"Preview PR #{n} removed: {reason}."
    assert wanted in texts, f"the parent's Activity doesn't say {wanted!r}; it says {[t for t in texts if f'PR #{n}' in t]}"
    assert f"Preview PR #{n} removed: {other}." not in texts, texts
    # And its Activity tab shows it, as a removal.
    gitberth.open(page, f"/apps/{w.app.key}/activity")
    entry = page.locator(".gb-tl li").filter(has=page.locator(".gb-tl-title", has_text=wanted))
    entry.first.wait_for()
    assert text_of(entry.first.locator(".gb-tl-sub")) == "Removed"
    # The Previews tab doesn't list it any more.
    gitberth.open(page, f"/apps/{w.app.key}/previews")
    previews_card(page).locator(".gb-drow, .gb-empty").first.wait_for()
    assert preview_row(page, f"PR #{n}").count() == 0, f"the Previews tab still lists PR #{n}"
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


class TestClosedAndMergedPullRequests:
    def test_closing_it_removes_its_preview_as_closed(self, ended, page, gitberth, engine):
        check_removed(ended, ended.closed_n, "the pull request was closed", "the pull request was merged", page, gitberth, engine)

    def test_merging_it_removes_its_preview_as_merged(self, ended, page, gitberth, engine):
        check_removed(ended, ended.merged_n, "the pull request was merged", "the pull request was closed", page, gitberth, engine)

    def test_the_branch_kept_after_the_merge_gets_a_preview_only_with_a_new_commit(self, ended, gitberth, standins):
        w = ended
        assert w.branch_previews == [], f"branches got previews of their own while their pull requests previewed them: {w.branch_previews}"
        # Two more looks after the pull requests' previews went: the merged pull request's branch (and the closed one's)
        # gets no preview of its own, as it has no commit since previews were turned on.
        w.repo.wait_watcher_passes(2, since=standins.seq(), timeout=90)
        assert branch_preview(gitberth, w.app.id, "feat-b") is None, "the merged pull request's branch got a preview without a new commit"
        assert branch_preview(gitberth, w.app.id, "feat-a") is None, "the closed pull request's branch got a preview without a new commit"
        assert gitberth.db.previews(w.app.id) == [], [(p["Slug"], p["Kind"]) for p in gitberth.db.previews(w.app.id)]

        # A new commit on it makes its branch preview.
        sha = w.repo.commit({"marker.txt": "after the merge"}, branch="feat-b", message="More after the merge")["sha"]
        preview = until(lambda: branch_preview(gitberth, w.app.id, "feat-b"), 90, "a branch preview of feat-b",
                        seen=lambda: [(p["Slug"], p["Kind"]) for p in gitberth.db.previews(w.app.id)])
        assert (preview["Slug"], preview["Ref"], preview["PullRequest"]) == (f"{w.app.slug}-feat-b", "refs/heads/feat-b", None), preview
        wait_deployed(gitberth, preview["Id"], sha)
        assert gitberth.wait_answers(gitberth.lan_url(preview["Id"]), "/")["marker"] == "after the merge"
        # The closed pull request's branch still has none, and neither pull request is previewed again.
        assert branch_preview(gitberth, w.app.id, "feat-a") is None
        assert gitberth.preview(w.app.id, pr=w.merged_n) is None and gitberth.preview(w.app.id, pr=w.closed_n) is None


# ---- GitHub's list of pull requests failing ----

def test_while_githubs_pull_request_list_fails_nothing_is_removed_as_closed(page, gitberth, github, engine, apps, uniq, standins):
    repo, app, _ = new_app(page, gitberth, github, apps.track, uniq("prf"))
    said = gitberth.set_previews(page, app, mode="Pull requests")
    assert said.startswith("Saved"), said
    kept_n, kept_sha = open_pull(repo, "keep-a", "Closed while GitHub fails", marker="kept")
    moving_n, moving_sha = open_pull(repo, "moves-b", "Moves on", marker="moves one")
    kept = gitberth.wait_preview(app.id, pr=kept_n, timeout=90)
    moving = gitberth.wait_preview(app.id, pr=moving_n, timeout=90)
    wait_deployed(gitberth, kept["Id"], kept_sha)
    first = wait_deployed(gitberth, moving["Id"], moving_sha)
    kept_url = gitberth.lan_url(kept["Id"])

    # From now on GitHub answers 502 to the list of the repo's pull requests, and meanwhile someone closes one of them.
    standins.fail(rf"^/repos/{re.escape(repo.full_name)}/pulls$", status=502, host="api.github.com")
    repo.close_pull(kept_n)
    since = time.time()
    mark = standins.seq()
    # The other one gets a new commit: its head moves, so Gitberth asks GitHub again, and gets the 502.
    moving_sha2 = repo.commit({"marker.txt": "moves two"}, branch="moves-b", message="Moves on while GitHub fails")["sha"]
    listed = repo.wait_pulls_listed(mark, timeout=90)
    assert listed["status"] == 502 and listed["injected"], listed
    # That preview still follows its pull request, from the repo's own refs.
    second = wait_deployed(gitberth, moving["Id"], moving_sha2, after=first["Id"], timeout=240)
    repo.wait_watcher_passes(2, since=listed["seq"], timeout=90)

    # Nothing was removed as closed: GitHub couldn't say which pull requests are open.
    assert gitberth.preview(app.id, pr=kept_n) is not None, "the pull request list failing removed a preview"
    assert not [t for t in activity_texts(gitberth, app) if " removed" in t], activity_texts(gitberth, app)
    assert engine.running(f"gb-{kept['Slug']}")
    assert (get_json(kept_url + "/") or {}).get("marker") == "kept"
    assert gitberth.preview(app.id, pr=kept_n)["HeadSha"] == kept_sha
    failed = repo.api_calls(mark, path=r"/pulls$", method="GET")
    assert failed and all(c["status"] == 502 for c in failed), [(c["path"], c["status"]) for c in failed]
    problem = f"GitHub answered 502 when asked for {repo.full_name}'s pull requests."
    log = gitberth.logs(since=int(since) - 1)
    assert f"Can't list the pull requests of {repo.full_name}: {problem}" in log, "Gitberth's log doesn't say why the pull requests weren't read"

    # The Previews tab says the pull requests couldn't be read, and still lists both previews.
    gitberth.open(page, f"/apps/{app.key}/previews")
    banner = page.locator(".gb-banner", has_text="Pull requests couldn't be read")
    banner.wait_for(timeout=20_000)
    assert text_of(banner.locator(".gb-banner-sub")) == problem
    for n in (kept_n, moving_n):
        preview_row(page, f"PR #{n}").wait_for()
    assert moving_sha2[:7] in text_of(preview_row(page, f"PR #{moving_n}"))

    # GitHub answers again: on the next look (the other pull request's head moves again), the closed one's preview goes.
    standins.clear_failures()
    mark = standins.seq()
    moving_sha3 = repo.commit({"marker.txt": "moves three"}, branch="moves-b", message="GitHub answers again")["sha"]
    until(lambda: [c for c in repo.api_calls(mark, path=r"/pulls$", method="GET") if c["status"] == 200], 90,
          "Gitberth to read the pull requests again", seen=lambda: [(c["path"], c["status"]) for c in repo.api_calls(mark)])
    gitberth.wait_no_preview(app.id, pr=kept_n, timeout=60)
    assert f"Preview PR #{kept_n} removed: the pull request was closed." in activity_texts(gitberth, app), activity_texts(gitberth, app)
    assert not leftovers(engine, kept["Slug"]), leftovers(engine, kept["Slug"])
    wait_deployed(gitberth, moving["Id"], moving_sha3, after=second["Id"], timeout=240)
    # And the Previews tab no longer says they couldn't be read.
    try:
        banner.wait_for(state="detached", timeout=30_000)
    except PlaywrightTimeout:
        raise AssertionError(f"the Previews tab still says the pull requests couldn't be read: {text_of(banner)}") from None
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"
