"""Scenario 38: Railpack, Gitberth's second builder (src/Gitberth/Railpack).

One app serves the four tests: New app makes it from a repo of apps/railpack-shell (a start.sh that serves its marker.txt
with Perl), a kind of project Gitberth doesn't know, so with Build on Detect automatically Railpack builds it. They run
in this order, each on what the one before left:
- New app shows Railpack's card (How it will run: Shell script with Railpack, its ticks, the note that Gitberth didn't
  know it, Railpack's plan, port 8080, and no switch to turn Railpack off), and Create and deploy builds it with Railpack
  in gitberth-buildkit: the image is gitberth/<slug>:<sha7> with Gitberth's labels (no -railpack tag left), the
  container gets PORT=8080 and answers on its LAN address, the deploy log says why Railpack built it, and get_app says
  how. Railpack plans in Gitberth's own network namespace, where github.com is the stand-in: the stand-ins get no call
  they have no route for, so it fetched nothing there (its tools come with Gitberth's image).
- Settings › Source offers three ways to build, says what was detected and that Railpack builds it. Railpack chosen is
  saved as a change the next build takes (a Git app's settings apply without the change dialog: the audit log has the
  change's line, Build: Detect automatically → Railpack, and Gitberth says the next deploy builds it), and the app's page
  says Saved, not live yet, with Rebuild and deploy. Then Detect automatically again. Settings › System has Railpack's
  version. Nothing deploys. It runs before the next test, whose build clears what it leaves pending.
- A RAILPACK_ variable is always build-time (its Build time switch on, and disabled), and never secret (its Secret switch
  off, and disabled). RAILPACK_BUILD_CMD=getent hosts
  <slug>, then Deploy without the build cache: every step runs again, the build runs the command, and the command finds
  the app's running container by its name on Gitberth's network (the address it prints is the container's). The
  variable is removed again, saved for the next deploy.
- A commit with no start.sh, which neither Gitberth nor Railpack can build, deploys by itself (Deploy on push) and fails
  with RailpackText.NeitherCanBuild, before any build, and the app keeps running and answering.

The first run on a fresh rig volume pulls BuildKit and Railpack's images once; gitberth-buildkit and its cache then stay
in the rig engine's volume (scenario 36's Clear build cache removes them, so 36 runs before this one in a full run).
"""
import json
import re
from types import SimpleNamespace
from urllib.parse import quote

import pytest
from playwright.sync_api import TimeoutError as PlaywrightTimeout

from e2e import audit
from e2e.github_reports import wait_deployment
from e2e.gitberth import FINAL, App
from e2e.standin import RUNNER_AGENT
from e2e.wait import until
from test_02_pull_request_previews import own_apps, text_of, unique, wait_finished
from test_04_preview_limit import text_of_said
from test_09_scoped_variables import ALL, add_variable, click_apply, env_row, open_environment, saved_variables, wait_bar
from test_19_declared_volumes import apply_bar, log_texts
from test_29_mcp import make_token, tool

TEMPLATE = "railpack-shell"
MARKER = "railpack v1"
RAILPACK = "0.43.0"
SUMMARY = "Shell script with Railpack"
PORT = "8080"
NETWORK = "gitberth"

# Gitberth's words (src/Gitberth/Railpack/RailpackText.cs).
FALLBACK = "Gitberth doesn't know this kind of project, so Railpack builds it"
PLANNING = f"Planning the build with Railpack {RAILPACK}"
FOUND = f"Railpack found {SUMMARY}"
FINISHING = "Adding Gitberth's labels to the image"
REACHABLE = "Reachable by name while building"
WITHOUT_CACHE = "without the build cache: every step runs again"
NEITHER_CAN_BUILD = ("Neither Gitberth nor Railpack could work out how to build this project. Add a Dockerfile to the repo, or set a "
                     "custom Dockerfile in the app's settings.")
PLANS_THE_BUILD = f"Railpack {RAILPACK} plans the build: no Dockerfile needed"
PLAN_TITLE = "Railpack's plan"
FALLBACK_NOTE = ("Gitberth doesn't build this kind of project itself, so Railpack, Railway's open-source builder, makes the image. "
                 "It plans the build again on every deploy.")
SWITCH = "Build with Railpack"

# The Settings tab's.
BUILD_OPTIONS = ["Detect automatically", "Railpack", "Custom Dockerfile"]
BUILT_BY_RAILPACK = f"Railpack {RAILPACK} builds it, from the repository's railpack.json and the app's RAILPACK_ variables."
NEXT_BUILD = "Saved. The next deploy builds it with the change."
NEEDS_A_BUILD = "Some saved changes only take effect after a new build."

BUILD_CMD = "RAILPACK_BUILD_CMD"


# ---- New app ----

def read_card(page, gitberth):
    """New app's card for the repo (How it will run), once the analysis, Railpack's plan with it, is over: its title, its
    ticks, the note under them, Railpack's plan (whether its title shows, and the text of its block), whether it offers
    the switch Build with Railpack, and the container port the Ports table offers."""
    try:
        page.locator("#git-name").wait_for(timeout=180_000)
    except PlaywrightTimeout:
        raise AssertionError(f"New app didn't get to the settings: {gitberth.banners(page)}") from None
    card = page.locator("section.gb-card").filter(has=page.locator(".gb-h3", has_text="How it will run"))
    card.wait_for()
    note = card.locator("#git-railpack-note")
    block = card.locator(".gb-code").filter(has=page.locator(".gb-code-title", has_text="railpack prepare"))
    port = page.get_by_label("Container port, row 1", exact=True)
    return SimpleNamespace(
        title=text_of(card.locator(".gb-h3").first),
        ticks=[" ".join(t.split()) for t in card.locator(".gb-keep").all_inner_texts()],
        note=text_of(note) if note.count() else None,
        plan_title=card.locator(".gb-label", has_text=PLAN_TITLE).count() > 0,
        plan=block.locator("pre").inner_text() if block.count() else "",
        switch=card.get_by_role("switch", name=SWITCH).count() > 0,
        port=port.input_value() if port.count() else None,
        banners=gitberth.banners(page))


def create_and_deploy(page, gitberth):
    """New app's Create and deploy, with everything as it's offered. Returns the App once its page opens."""
    page.get_by_role("button", name="Create and deploy").click()
    try:
        page.wait_for_url(re.compile(r"/apps/gb-[a-z0-9-]+$"), timeout=120_000)
    except PlaywrightTimeout:
        raise AssertionError(f"Create and deploy didn't open the new app's page: {gitberth.banners(page)}") from None
    slug = gitberth.path_of(page).rsplit("/", 1)[-1][len("gb-"):]
    row = until(lambda: gitberth.db.app(slug), 30, f"the app {slug} in Gitberth's database")
    return App(row["Id"], row["Slug"], row["Name"])


# ---- the app's tabs ----

def save_for_next_deploy(page, gitberth, what):
    """The apply bar's Save for next deploy, once it has worked out what applying does: saved, and nothing deploys now.
    Returns what Gitberth said."""
    wait_bar(page, lambda line: "Working out" not in line, f"the apply bar to say what {what} does")
    button = apply_bar(page).get_by_role("button", name="Save for next deploy")
    until(lambda: button.count() > 0 and button.is_enabled(), 30, "Save for next deploy to be offered", seen=lambda: text_of(apply_bar(page)))
    button.click()
    return text_of_said(gitberth.snackbar(page, "Saved|[Cc]ouldn't"))


def remove_variable(page, gitberth, app, key):
    """The Environment tab: the variable's row removed, saved for the next deploy. Returns what Gitberth said."""
    open_environment(page, gitberth, app)
    env_row(page, key, ALL).get_by_role("button", name=f"Remove {key}", exact=True).click()
    return save_for_next_deploy(page, gitberth, f"removing {key}")


def deploy_without_cache(page, gitberth, app):
    """The app page's More menu › Deploy without the build cache: returns the deployment it started."""
    gitberth.open(page, f"/apps/{app.key}")
    page.locator(".gb-hero-actions button[aria-label='More actions']").click()
    entry = page.locator(".mud-menu-item", has_text="Deploy without the build cache")
    entry.wait_for()
    entry.click()
    page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"))
    return int(gitberth.path_of(page).rsplit("/", 1)[-1])


def build_choice(page):
    """Settings › Source's Build: its options as it shows them, and the one chosen."""
    group = page.locator("#sec-source").get_by_role("radiogroup", name="Build", exact=True)
    group.wait_for()
    options = [text_of(o) for o in group.get_by_role("radio").all()]
    chosen = [text_of(o) for o in group.get_by_role("radio").all() if o.get_attribute("aria-checked") == "true"]
    return group, options, chosen


def choose_build(page, gitberth, app, option):
    """Settings › Source: Build set to the option, applied. Returns (the apply bar's line, whether it offered Save for next
    deploy, what Gitberth said)."""
    gitberth.open(page, f"/apps/{app.key}/settings")
    group, _, _ = build_choice(page)
    group.get_by_role("radio", name=option, exact=True).click()
    _, line = wait_bar(page, lambda line: "Working out" not in line, f"the apply bar to say what Build: {option} does")
    later = apply_bar(page).get_by_role("button", name="Save for next deploy").count() > 0
    click_apply(page)
    return line, later, text_of_said(gitberth.snackbar(page, "Saved|[Cc]ouldn't"))


# ---- what a deploy left ----

def log_of(gitberth, deployment_id):
    """A deploy log's lines without their time, trimmed, and a step's without its ==> mark."""
    return [t[4:] if t.startswith("==> ") else t for t in (x.strip() for x in log_texts(gitberth, deployment_id))]


def built(texts):
    """The lines of a deploy log that say something was built: BuildKit's steps (#1 …), Gitberth's Building … line, or a
    buildx build."""
    return [t for t in texts if re.match(r"#\d+ ", t) or t.startswith("Building gitberth/") or "buildx build" in t]


def getent_step(texts, slug):
    """The build step that ran `getent hosts <slug>` (RAILPACK_BUILD_CMD), as BuildKit's log names it: its #, the address
    it printed for the slug (None when it printed none), and whether it came from the build cache. None when the log has
    no such step."""
    title = re.compile(rf"#(?P<n>\d+) (\[[^\]]*\] )?getent hosts {re.escape(slug)}$")
    for t in texts:
        m = title.search(t)
        if m:
            n = m.group("n")
            answer = re.compile(rf"#{n} [\d.]+ (\S+)\s+{re.escape(slug)}(\s|$)")
            printed = next((a.group(1) for a in (answer.search(x) for x in texts) if a), None)
            cached = any(re.search(rf"(^|\s)#{n} CACHED$", x) for x in texts)
            return n, printed, cached
    return None


def addresses(engine, container):
    """The container's addresses on Gitberth's network (IPv4, and IPv6 when it has one)."""
    endpoint = (((engine.inspect(container) or {}).get("NetworkSettings") or {}).get("Networks") or {}).get(NETWORK) or {}
    return {a for a in (endpoint.get("IPAddress"), endpoint.get("GlobalIPv6Address")) if a}


def marker_of(gitberth, url):
    return (gitberth.wait_answers(url, "/") or {}).get("marker")


def ended_well(gitberth, d, what):
    assert d["Status"] == "Succeeded", (f"{what} (deploy #{d['Id']}, {d['Trigger']}) ended {d['Status']}: {d['Error']}\n"
                                        f"--- the end of its log ---\n{gitberth.deploy_log(d['Id'], tail=40)}")


# ---- the app the tests share ----

@pytest.fixture(scope="module")
def world(gitberth, github, standins):
    """A repo made from apps/railpack-shell, New app's card for it (read before Create), and the app Create and deploy made,
    its first deploy over however it ended (test 1 says how). mark: the stand-ins' marker from before any of it."""
    with own_apps(gitberth, standins, "railpack") as (page, made):
        mark = standins.seq()
        repo = github.create_repo(unique("rp"), template=TEMPLATE)
        sha = repo.branch_sha("main")
        gitberth.open(page, "/apps/new?url=" + quote(repo.url, safe=""))
        card = read_card(page, gitberth)
        app = made.track(create_and_deploy(page, gitberth))
        # The first deploy on a fresh rig volume pulls BuildKit and Railpack's images.
        first = gitberth.wait_deploy(app.id, statuses=FINAL, timeout=600)
        yield SimpleNamespace(page=page, repo=repo, sha=sha, card=card, app=app, first=first, mark=mark)


def test_a_project_gitberth_doesnt_know_is_built_by_railpack(world, gitberth, engine, standins):
    w = world
    app, sha, first = w.app, w.sha, w.first

    # ---- New app: Railpack's card, because Gitberth didn't know the project (the fallback), so no switch turns it off.
    card = w.card
    assert card.title == f"How it will run: {SUMMARY}", (card.title, card.banners)
    assert "start.sh" in card.ticks and PLANS_THE_BUILD in card.ticks, card.ticks
    assert card.note == FALLBACK_NOTE, card.note
    assert card.plan_title, f"New app has no {PLAN_TITLE} block"
    assert "Detected Shell" in card.plan and "sh start.sh" in card.plan, card.plan
    assert not card.switch, f"the fallback card offers {SWITCH}"
    assert card.port == PORT, card.port

    # ---- Created and deployed: built by Railpack, then finished with Gitberth's labels and PORT.
    ended_well(gitberth, first, "The first deploy")
    assert first["CommitSha"] == sha, first
    ref = f"{app.image_repository}:{sha[:7]}"
    assert engine.image_of(app.container) == ref, engine.image_of(app.container)
    labels = engine.image_labels(ref)
    assert {k: labels.get(k) for k in ("gitberth.builder", "gitberth.railpack", "gitberth.app", "gitberth.port")} == {
        "gitberth.builder": "railpack", "gitberth.railpack": RAILPACK, "gitberth.app": app.slug, "gitberth.port": PORT}, labels
    # Railpack's own image is only a step on the way: its tag goes once the finished image has its labels.
    assert [r for r in engine.images(app.image_repository) if r.endswith("-railpack")] == [], engine.images(app.image_repository)
    assert engine.env(app.container).get("PORT") == PORT, engine.env(app.container)
    url = gitberth.lan_url(app.id)
    assert gitberth.wait_answers(url, "/") == {"marker": MARKER}
    assert gitberth.wait_answers(url, "/env") == {"PORT": PORT}
    texts = log_of(gitberth, first["Id"])
    for line in (FALLBACK, PLANNING, FOUND, FINISHING):
        assert any(t.startswith(line) for t in texts), f"the deploy log has no line {line!r}:\n" + "\n".join(texts[-60:])
    row = gitberth.db.app(app.slug)
    assert (row["DockerfileMode"], row["DetectedKind"], row["DetectedSummary"]) == ("Auto", "Railpack", SUMMARY), row

    # ---- An agent sees how it's built.
    token, _ = make_token(gitberth, w.page, unique("rp-agent"), "Read")
    error, got = tool(gitberth, token, "get_app", {"app": app.key})
    assert not error, got
    assert (got["git"]["build"], got["git"]["detected"]) == ("auto", SUMMARY), got["git"]

    # ---- Railpack planned it twice (New app, the deploy) in Gitberth's network namespace, where github.com is the
    # stand-in: nothing was asked of a host or path the stand-ins don't serve, so it fetched none of its tools there.
    gaps = [c for c in standins.calls(since=w.mark, unrouted=True) if (c.get("headers") or {}).get("User-Agent") != RUNNER_AGENT]
    assert gaps == [], [f"{c['method']} {c['host']}{c['path']}" for c in gaps]


def test_build_can_be_set_to_railpack_and_the_change_says_the_next_deploy_builds_it(world, gitberth):
    w = world
    page, app = w.page, w.app
    before = gitberth.db.last_deployment_id(app.id)
    mark = audit.last_id()

    # ---- Settings › Source: three ways to build, what was detected, and who builds it.
    gitberth.open(page, f"/apps/{app.key}/settings")
    _, options, chosen = build_choice(page)
    assert (options, chosen) == (BUILD_OPTIONS, ["Detect automatically"]), (options, chosen)
    source = page.locator("#sec-source")
    assert f"last detected {SUMMARY}" in text_of(source.locator(".gb-ro-note")), text_of(source.locator(".gb-ro-note"))
    note = source.locator("#set-build-railpack")
    assert BUILT_BY_RAILPACK in text_of(note), text_of(note)
    assert note.get_by_role("link", name="How Gitberth runs Railpack").get_attribute("href") == "help/reference/railpack#how-gitberth-runs-it"

    # ---- Railpack: a change the next build takes. A Git app's settings apply without the change dialog, so its line is
    # the audit log's, and Gitberth says the next deploy builds it; the bar offers to save it for the next deploy.
    _, later, said = choose_build(page, gitberth, app, "Railpack")
    assert later, "the apply bar didn't offer Save for next deploy for a change the next build takes"
    assert said == NEXT_BUILD, said
    row = gitberth.db.app(app.slug)
    assert (row["DockerfileMode"], row["RebuildPending"]) == ("Railpack", 1), row
    entries = until(lambda: audit.since(mark, "action = 'app.settings.changed' AND app_id = ?", app.id), 30, "the change in the audit log")
    assert json.loads(entries[-1]["changes"]) == [{"field": "Build", "from": "Detect automatically", "to": "Railpack"}], entries[-1]["changes"]
    gitberth.open(page, f"/apps/{app.key}")
    banner = page.locator(".gb-banner", has_text="Saved, not live yet")
    banner.wait_for(timeout=30_000)
    assert NEEDS_A_BUILD in text_of(banner), text_of(banner)
    assert banner.get_by_role("button", name="Rebuild and deploy").count() == 1, text_of(banner)

    # ---- Detect automatically again.
    _, _, said = choose_build(page, gitberth, app, "Detect automatically")
    assert said == NEXT_BUILD, said
    assert gitberth.db.app(app.slug)["DockerfileMode"] == "Auto"
    entries = until(lambda: (lambda e: e if len(e) >= 2 else None)(audit.since(mark, "action = 'app.settings.changed' AND app_id = ?", app.id)), 30,
                    "the second change in the audit log")
    assert json.loads(entries[-1]["changes"]) == [{"field": "Build", "from": "Railpack", "to": "Detect automatically"}], entries[-1]["changes"]

    # ---- Settings › System: Railpack's version, and where its builds run.
    gitberth.open(page, "/settings")
    cell = page.locator("#system-railpack")
    shown = until(lambda: (lambda t: t if t and t != "Checking…" else None)(text_of(cell)), 30, "Settings › System's Railpack row")
    assert shown.startswith(RAILPACK), shown

    # None of it deployed anything.
    assert gitberth.db.last_deployment_id(app.id) == before, gitberth.db.deployments(app.id)[:2]


def test_railpack_settings_reach_the_build_and_it_reaches_containers_by_name(world, gitberth, engine):
    w = world
    page, app = w.page, w.app
    command = f"getent hosts {app.slug}"

    # ---- The Environment tab: a RAILPACK_ variable is build-time, and it stays so; and never secret, since Railpack writes
    # it into its plan and the image.
    open_environment(page, gitberth, app)
    row = add_variable(page, BUILD_CMD, command)
    toggle = row.get_by_role("button", name=f"Build time for {BUILD_CMD}", exact=True)
    until(lambda: toggle.get_attribute("aria-pressed") == "true", 15, f"Build time to be on for {BUILD_CMD}", seen=lambda: toggle.get_attribute("aria-pressed"))
    assert toggle.is_disabled(), f"{BUILD_CMD}'s Build time switch can be turned off"
    assert "Build time" in [text_of(t) for t in row.locator(".gb-tag").all()], text_of(row)
    lock = row.get_by_role("button", name=f"Secret: {BUILD_CMD}", exact=True)
    assert lock.is_disabled() and lock.get_attribute("aria-pressed") == "false", f"{BUILD_CMD}'s Secret switch can be turned on"
    said = save_for_next_deploy(page, gitberth, f"adding {BUILD_CMD}")
    assert said.startswith("Saved."), said
    assert (BUILD_CMD, ALL, 0, 1) in saved_variables(gitberth, app.id), saved_variables(gitberth, app.id)

    # ---- Deploy without the build cache: the build runs the command, which finds the running container by its name.
    running = addresses(engine, app.container)
    assert running, f"{app.container} has no address on Gitberth's network: {engine.networks(app.container)}"
    deployment = deploy_without_cache(page, gitberth, app)
    done = wait_deployment(gitberth, deployment, timeout=420)
    assert done["NoCache"] == 1, done
    texts = log_of(gitberth, deployment)
    shown = "\n".join(texts[-80:])
    assert any(WITHOUT_CACHE in t for t in texts), shown
    step = getent_step(texts, app.slug)
    assert step, f"the build ran no step `{command}`:\n{shown}"
    number, printed, cached = step
    assert not cached, f"the build's step #{number} came from the cache: it didn't run again\n{shown}"
    assert printed in running, f"`{command}` printed {printed}, not {app.container}'s address on {NETWORK} ({sorted(running)})\n{shown}"
    reachable = next((t for t in texts if t.startswith(REACHABLE)), None)
    assert reachable and app.slug in reachable, reachable or shown
    assert marker_of(gitberth, gitberth.lan_url(app.id)) == MARKER

    # ---- Removed again, for the next deploy: with it, Railpack would still find a build step in the next test's commit.
    said = remove_variable(page, gitberth, app, BUILD_CMD)
    assert said.startswith("Saved."), said
    assert BUILD_CMD not in [v[0] for v in saved_variables(gitberth, app.id)], saved_variables(gitberth, app.id)


def test_a_commit_neither_can_build_fails_plainly_and_keeps_the_app_running(world, gitberth, engine):
    w = world
    page, app = w.page, w.app
    # The test before removes its variable at its end; when it stopped earlier, it goes here.
    if BUILD_CMD in [v[0] for v in saved_variables(gitberth, app.id)]:
        remove_variable(page, gitberth, app, BUILD_CMD)
    url = gitberth.lan_url(app.id)
    container = (engine.inspect(app.container) or {}).get("Id")
    image = engine.image_of(app.container)
    before = gitberth.db.last_deployment_id(app.id)

    # Deploy on push is on: the commit deploys by itself.
    sha = w.repo.commit({"start.sh": None, "README.md": "nothing to run"}, message="Remove the start script")["sha"]
    failed = wait_finished(gitberth, app.id, sha, after=before, timeout=300)
    assert (failed["Status"], failed["Error"]) == ("Failed", NEITHER_CAN_BUILD), (
        f"deploy #{failed['Id']} ended {failed['Status']}: {failed['Error']}\n--- the end of its log ---\n{gitberth.deploy_log(failed['Id'], tail=40)}")
    texts = log_of(gitberth, failed["Id"])
    assert any(t.startswith(PLANNING) for t in texts), "\n".join(texts[-40:])
    assert built(texts) == [], built(texts)

    # The running container wasn't touched.
    assert ((engine.inspect(app.container) or {}).get("Id"), engine.image_of(app.container)) == (container, image)
    assert marker_of(gitberth, url) == MARKER
