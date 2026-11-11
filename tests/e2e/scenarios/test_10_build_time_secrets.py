"""Scenario 10: build-time secrets, per scope.

The app is built from a custom Dockerfile with Gitberth's build-args line (the test app's own Dockerfile, whose build
step writes what it saw into /build: plain values for E2E_PUBLIC_*, every other one as a SHA-256), with Vercel's
variables off, so a build gets only the app's own values. E2E_BUILD_SECRET is a build-time secret with a value for every
deploy, one for production and one for previews; E2E_PUBLIC_STAGE a plain build-time value for every deploy. The values
are made up afresh on every run.
- Production's image is built with the Production value, a pull request's preview's with the Preview value, never the
  one for every deploy. The secret is mounted into the build step (RUN --mount=type=secret), never declared as an ARG.
- Changing the production value and deploying the same commit again (Saved, not live yet › Rebuild and deploy) builds it
  with the new value: the step that saw the secret runs again. Deploying once more with nothing changed (Deploy) takes
  that step from the build cache, with the same GITBERTH_SECRETS.
- Changing the Preview value doesn't make production's build pending (the apply bar says it doesn't restart, and no
  banner says a build is needed), and the preview's next build (Previews tab › Redeploy) takes it.
- Promote of the preview's commit builds it again with production's value: the step that saw the secret isn't taken from
  the preview's build (it ran again, after the preview's, and wrote production's value).
- The GITBERTH_SECRETS build argument that keys that step to the secrets is a keyed hash: 64 hex characters, not the value
  nor a plain hash of it, the same for the same values and different for others; the build step saw exactly it.
- No secret value appears in an image's history, labels or docker inspect, in a deploy log (its file or its page), in
  anything GitHub was sent (every call, the commit statuses, the pull request's comment), in Gitberth's own log, or in
  the app's and the preview's Activity.

What makes the cache checks mean something: the repo's .dockerignore leaves out .git (as most repos' do), which differs
from one clone to the next, so two builds of one commit copy the same files, and with Vercel's variables off a build
gets no values but the app's own (none about its commit, branch or pull request). Two builds of one commit then differ,
for BuildKit's cache, only in GITBERTH_SECRETS and in the containers the build is given by name (--add-host, every
container on Gitberth's network). The checks compare those (the names the deploy log lists, and the containers with
their addresses just before the build started), and the Deploy with nothing changed shows the cache is used when they and
the secrets are the same. So a step that runs again does so because of GITBERTH_SECRETS.
"""
import contextlib
import hashlib
import json
import re
import secrets
import time
from types import SimpleNamespace

import pytest
from playwright.sync_api import TimeoutError as PlaywrightTimeout

from e2e.engine import docker
from e2e.standin import RUNNER_AGENT
from e2e.wait import until
from test_02_pull_request_previews import TESTAPP_DOCKERFILE, log_lines, new_app, open_pull, own_apps, text_of, unique, wait_finished
from test_04_preview_limit import answer_dialog, choose_from_menu, read_dialog, text_of_said
from test_09_scoped_variables import (ALL, PREVIEW, PREVIEWS_ONLY, PRODUCTION, PRODUCTION_TAKES, add_variable, click_apply, env_row, env_rows,
                                      open_environment, production_now, shown_rows, wait_bar, watch_production)
from test_19_declared_volumes import apply_bar, log_texts, wait_final

SECRET = "E2E_BUILD_SECRET"
STAGE = ("E2E_PUBLIC_STAGE", "every-deploy-stage")
MOUNTED = f"RUN --mount=type=secret,id={SECRET},env={SECRET} python3 /app/build_info.py > /app/build.json"
STAMP = re.compile(r"\bGITBERTH_SECRETS=(\S*)")
GITHUB_HOSTS = ("github.com", "api.github.com", "codeload.github.com", "uploads.github.com")
# Gitberth's network, the one whose containers a build is given by name.
NETWORK = "gitberth"
# The repo leaves its .git out of the build, as most repos do: it differs from one clone to the next.
DOCKERIGNORE = {".dockerignore": ".git\n"}


def made_up(what):
    """A value for the secret, made up for this run (never a real one)."""
    return f"e2e-{what}-{secrets.token_hex(8)}"


def sha256(text):
    return hashlib.sha256(text.encode()).hexdigest()


# ---- what a build left ----

def image_facts(image_id):
    """What anyone with the image can read: docker inspect's text, its labels and environment, and its history (each
    layer's command, in full)."""
    inspect = docker("image", "inspect", image_id)
    config = json.loads(inspect)[0].get("Config") or {}
    history = [json.loads(line).get("CreatedBy") or "" for line in docker("history", "--no-trunc", "--format", "{{json .}}", image_id).splitlines()
               if line.strip()]
    return SimpleNamespace(id=image_id, inspect=inspect, labels=config.get("Labels") or {}, env=config.get("Env") or [], history=history)


def build_run(history):
    """The history's line for the step that ran build_info.py, with the build arguments BuildKit recorded for it."""
    return next((h for h in history if "build_info.py" in h), None)


def stamp_of(history):
    """The GITBERTH_SECRETS value recorded with that step, or None."""
    m = STAMP.search(build_run(history) or "")
    return m.group(1) if m else None


def build_step(texts):
    """The step of a deploy's build that ran build_info.py, as BuildKit's log names it: (its #, whether it came from the
    build cache), or None when the log has no such step."""
    for t in texts:
        m = re.match(r"#(\d+) \[[^\]]*\] RUN .*build_info\.py", t.strip())
        if m:
            return m.group(1), any(x.strip() == f"#{m.group(1)} CACHED" for x in texts)
    return None


def reachable(texts):
    """The build's 'Reachable by name while building' line: the names of the containers it was given (--add-host)."""
    return next((t.strip() for t in texts if t.strip().startswith("Reachable by name while building")), "(no names given)")


def network_now():
    """The containers running on Gitberth's network now, as (name, address), sorted: what a build started now is given
    (each by name, with its address). Taken just before a build is started."""
    out = docker("network", "inspect", NETWORK, "--format", "{{json .Containers}}", check=False).strip()
    # No such network yet: no containers on it.
    containers = (json.loads(out) if out.startswith(("{", "null")) else None) or {}
    return sorted((c.get("Name"), (c.get("IPv4Address") or "").split("/")[0]) for c in containers.values())


def quiet_network(timeout=180):
    """Waits a while (not forever) for Gitberth's network to have no app's container: what an earlier scenario's apps
    left there goes first (deleting an app can leave one of its previews for a few watcher passes), so this scenario's
    builds are given only its own app's containers. The checks that need it compare what each build was given anyway."""
    with contextlib.suppress(AssertionError):
        until(lambda: not [n for n, _ in network_now() if n.startswith("gb-")], timeout, "Gitberth's network to have no app's container",
              interval=2, seen=network_now)


def built(gitberth, engine, app_id, container, deploy, hosts):
    """What a deploy left: its row and log, and when it worked, the image its container runs (by id: a later build of the
    same commit takes the tag) and what the app says its build saw (/build) and it runs with (/env); with the containers
    on Gitberth's network just before it started (hosts)."""
    s = SimpleNamespace(deploy=deploy, log=log_texts(gitberth, deploy["Id"]), hosts=hosts, image=None, ref=None, build=None, env=None)
    if deploy["Status"] == "Succeeded":
        url = gitberth.lan_url(app_id)
        s.build = gitberth.wait_answers(url, "/build")
        s.env = gitberth.wait_answers(url, "/env")
        s.ref = engine.image_of(container)
        s.image = image_facts(engine.inspect(container)["Image"])
    return s


def given_the_same(s, earlier):
    """Whether two builds were given the same containers: the names their logs list, and those containers' addresses."""
    return (reachable(s.log), s.hosts) == (reachable(earlier.log), earlier.hosts)


def given(s):
    return f"{reachable(s.log)}; on the network: {s.hosts}"


def check_built(gitberth, s, what):
    d = s.deploy
    assert d["Status"] == "Succeeded", (f"{what} (deploy #{d['Id']}, {d['Trigger']}) ended {d['Status']}: {d['Error']}\n"
                                        f"--- the end of its log ---\n{gitberth.deploy_log(d['Id'], tail=40)}")


def saw(s):
    """What the build step saw of the secret: the SHA-256 of its value, or None when it didn't see it."""
    return (s.build or {}).get("sha256", {}).get(SECRET)


def ran_again(s, earlier, what):
    """The build step of `s` ran (it isn't from the cache), and the only thing that can have made it is GITBERTH_SECRETS:
    the build was given the same containers as `earlier`'s, a build of the same commit."""
    assert given_the_same(s, earlier), (f"{what} wasn't given the same containers as the build it's compared with ({given(earlier)}; then "
                                        f"{given(s)}), so this can't tell what made its step run")
    step = build_step(s.log)
    assert step, f"{what}'s log has no step that ran build_info.py:\n" + "\n".join(s.log[-40:])
    assert not step[1], f"{what}'s build step came from the cache (#{step[0]} CACHED): it didn't run with the new secret"


# ---- the Settings and Environment tabs ----

def use_custom_dockerfile(page, gitberth, app, dockerfile):
    """Settings › Source: Build is the custom Dockerfile given, and Vercel's variables are off; applied. Returns (the
    apply bar's title and line, what Gitberth said)."""
    gitberth.open(page, f"/apps/{app.key}/settings")
    source = page.locator("#sec-source")
    source.wait_for()
    source.get_by_role("radio", name="Custom Dockerfile").click()
    box = page.locator("#set-dockerfile")
    box.wait_for()
    box.fill(dockerfile)
    switch = source.get_by_role("switch", name="Vercel's variables")
    if switch.get_attribute("aria-checked") == "true":
        switch.click()
    until(lambda: switch.get_attribute("aria-checked") == "false", 15, "Vercel's variables to be off", seen=lambda: switch.get_attribute("aria-checked"))
    bar = wait_bar(page, lambda line: "Working out" not in line, "the apply bar to say what applying does")
    click_apply(page)
    return bar, text_of_said(gitberth.snackbar(page, "Saved|[Cc]ouldn't|empty"))


def apply_and_follow(page, gitberth):
    """The apply bar's Apply, for a change production takes: the page goes to the deploy without a build that applies it,
    which is waited for. Returns that deploy."""
    click_apply(page)
    page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"), timeout=60_000)
    return wait_final(gitberth, int(gitberth.path_of(page).rsplit("/", 1)[-1]))


def rebuild_from_banner(page, gitberth, app):
    """The app's page: the banner that says saved settings aren't live yet, and its Rebuild and deploy. Returns (the
    banner's line, whether it offered Apply now, the deploy it started, waited for)."""
    gitberth.open(page, f"/apps/{app.key}")
    banner = page.locator(".gb-banner", has_text="Saved, not live yet")
    banner.wait_for(timeout=30_000)
    line = text_of(banner.locator(".gb-banner-sub"))
    apply_now = banner.get_by_role("button", name="Apply now").count() > 0
    banner.get_by_role("button", name="Rebuild and deploy").click()
    page.wait_for_url(re.compile(rf"/apps/{app.id}/deployments/\d+$"), timeout=60_000)
    return line, apply_now, wait_final(gitberth, int(gitberth.path_of(page).rsplit("/", 1)[-1]))


# ---- the world, step by step ----

@pytest.fixture(scope="module")
def world(gitberth, github, engine, standins):
    """An app made from the test app (its repo with a .dockerignore leaving .git out); on its Settings tab the test app's
    Dockerfile as a custom one and Vercel's variables off; on its Environment tab the secret in every scope and the plain
    value, build-time, applied (a deploy without a build); then Rebuild and deploy from the banner: production's first
    build with them."""
    with open(TESTAPP_DOCKERFILE, encoding="utf-8") as f:
        dockerfile = f.read()
    assert "# gitberth:build-args\nRUN python3 /app/build_info.py" in dockerfile, "the test app's Dockerfile has no build-args line before its build step"
    values = SimpleNamespace(all=made_up("all"), production=made_up("production"), preview=made_up("preview"),
                             production_2=made_up("production-2"), preview_2=made_up("preview-2"))
    quiet_network()
    t0, mark = time.time(), standins.seq()
    with own_apps(gitberth, standins, "build-secrets") as (page, made):
        repo, app, first = new_app(page, gitberth, github, made.track, unique("bts"), files=DOCKERIGNORE)
        settings_bar, settings_said = use_custom_dockerfile(page, gitberth, app, dockerfile)
        row = gitberth.db.app(app.slug)
        settings_saved = (row["DockerfileMode"], row["CustomDockerfile"] == dockerfile, row["VercelVariables"])

        open_environment(page, gitberth, app)
        for scope, value in ((ALL, values.all), (PRODUCTION, values.production), (PREVIEW, values.preview)):
            add_variable(page, SECRET, value, scope, build=True)
        add_variable(page, STAGE[0], STAGE[1], ALL, build=True)
        drafted = [(r.key, r.scope, r.tags, r.value_type) for r in env_rows(page) if r.key.startswith("E2E_")]
        wait_bar(page, lambda line: line.startswith(PRODUCTION_TAKES), "the apply bar to say production takes the variables")
        recreated = apply_and_follow(page, gitberth)
        hosts = network_now()
        banner_line, banner_apply_now, d1 = rebuild_from_banner(page, gitberth, app)
        yield SimpleNamespace(page=page, repo=repo, app=app, first=first, values=values, t0=t0, mark=mark, dockerfile=dockerfile,
                              settings_bar=settings_bar, settings_said=settings_said, settings_saved=settings_saved, drafted=drafted,
                              recreated=recreated, banner_line=banner_line, banner_apply_now=banner_apply_now,
                              d1=built(gitberth, engine, app.id, app.container, d1, hosts))


@pytest.fixture(scope="module")
def changed(world, gitberth, engine):
    """The production value changed on the Environment tab and applied, then Rebuild and deploy: the same commit again."""
    w = world
    page = w.page
    open_environment(page, gitberth, w.app)
    env_row(page, SECRET, PRODUCTION).locator(".gb-envval input").fill(w.values.production_2)
    w.change_bar = wait_bar(page, lambda line: line.startswith(PRODUCTION_TAKES), "the apply bar to say production takes the new value")
    w.change_recreated = apply_and_follow(page, gitberth)
    w.change_pending = {k: gitberth.db.app(w.app.slug)[k] for k in ("ConfigChangePending", "RebuildPending")}
    hosts = network_now()
    w.change_banner_line, w.change_banner_apply_now, d2 = rebuild_from_banner(page, gitberth, w.app)
    w.d2 = built(gitberth, engine, w.app.id, w.app.container, d2, hosts)
    return w


@pytest.fixture(scope="module")
def unchanged(changed, gitberth, engine):
    """Then Deploy on the app's page, with nothing changed: the same commit and values again."""
    w = changed
    hosts = network_now()
    w.d2b = built(gitberth, engine, w.app.id, w.app.container, wait_final(gitberth, gitberth.deploy(w.page, w.app)), hosts)
    return w


@pytest.fixture(scope="module")
def previewed(unchanged, gitberth, engine):
    """Previews of pull requests on, and a pull request opened: its preview built."""
    w = unchanged
    said = gitberth.set_previews(w.page, w.app, mode="Pull requests")
    assert said.startswith("Saved"), said
    hosts = network_now()
    w.n, w.sha = open_pull(w.repo, "feature", "Build-time secrets", marker="feature")
    w.preview = gitberth.wait_preview(w.app.id, pr=w.n, timeout=90)
    w.preview_container = f"gb-{w.preview['Slug']}"
    w.p1 = built(gitberth, engine, w.preview["Id"], w.preview_container, wait_finished(gitberth, w.preview["Id"], w.sha), hosts)
    return w


@pytest.fixture(scope="module")
def preview_changed(previewed, gitberth, engine):
    """The Preview value changed on the Environment tab and applied, production watched for a few seconds, then the
    preview's Redeploy from the Previews tab."""
    w = previewed
    page = w.page
    w.production_before = production_now(gitberth, engine, w.app)
    open_environment(page, gitberth, w.app)
    env_row(page, SECRET, PREVIEW).locator(".gb-envval input").fill(w.values.preview_2)
    try:
        w.preview_bar, w.preview_bar_waited = wait_bar(page, PREVIEWS_ONLY, "the apply bar to say production doesn't restart"), None
    except AssertionError as e:
        w.preview_bar, w.preview_bar_waited = None, str(e)
    click_apply(page)
    w.preview_said = text_of_said(gitberth.snackbar(page, "."))
    with contextlib.suppress(PlaywrightTimeout):
        apply_bar(page).wait_for(state="detached", timeout=15_000)
    w.preview_landed = gitberth.path_of(page)
    w.production_after = watch_production(gitberth, engine, w.app, w.production_before)
    gitberth.open(page, f"/apps/{w.app.key}")
    page.locator(".gb-hero").wait_for()
    w.preview_change_banner = page.locator(".gb-banner", has_text="Saved, not live yet").count()

    gitberth.open(page, f"/apps/{w.app.key}/previews")
    hosts = network_now()
    choose_from_menu(page, f"PR #{w.n}", "Redeploy")
    page.wait_for_url(re.compile(rf"/apps/{w.preview['Id']}/deployments/\d+$"), timeout=60_000)
    w.p2 = built(gitberth, engine, w.preview["Id"], w.preview_container, wait_final(gitberth, int(gitberth.path_of(page).rsplit("/", 1)[-1])), hosts)
    return w


@pytest.fixture(scope="module")
def promoted(preview_changed, gitberth, engine):
    """The preview promoted from the Previews tab's menu (its dialog answered Promote): production builds its commit."""
    w = preview_changed
    page = w.page
    gitberth.open(page, f"/apps/{w.app.key}/previews")
    choose_from_menu(page, f"PR #{w.n}", "Promote to production")
    w.promote_dialog = read_dialog(page)
    hosts = network_now()
    answer_dialog(page, "Promote")
    page.wait_for_url(re.compile(rf"/apps/{w.app.id}/deployments/\d+$"), timeout=60_000)
    w.d3 = built(gitberth, engine, w.app.id, w.app.container, wait_final(gitberth, int(gitberth.path_of(page).rsplit("/", 1)[-1])), hosts)
    return w


def builds(w):
    """Every build the scenario made with the secret: (what it was, what it left)."""
    return [("production's first build", w.d1), ("production's build after its value changed", w.d2),
            ("production's Deploy with nothing changed", w.d2b), ("the preview's first build", w.p1),
            ("the preview's build after its value changed", w.p2), ("production's build of the promoted commit", w.d3)]


# ---- the values each build saw ----

class TestBuildTimeSecretsPerScope:
    def test_the_environment_tab_shows_the_secret_in_each_scope(self, world, page, gitberth):
        w = world
        # The Source section took the custom Dockerfile, and Vercel's variables are off.
        assert w.settings_said.startswith("Saved"), w.settings_said
        assert w.settings_saved == ("Custom", True, 0), w.settings_saved
        # Typed in: each row of the secret masked, tagged Secret and Build time, and the scoped ones with their scope.
        rows = {(k, s): (tags, kind) for k, s, tags, kind in w.drafted}
        for scope, tag in ((ALL, None), (PRODUCTION, "Production"), (PREVIEW, "Preview")):
            tags, kind = rows[(SECRET, scope)]
            assert {"Secret", "Build time"} <= set(tags) and kind == "password", (scope, tags, kind)
            assert (tag in tags) if tag else not {"Production", "Preview"} & set(tags), (scope, tags)
        # Saved, and listed again by the tab: the secret in all three scopes, the plain value for every deploy.
        expected = sorted([(SECRET, ALL, w.values.all, None), (SECRET, PRODUCTION, w.values.production_2 if hasattr(w, "d2") else w.values.production, "Production"),
                           (SECRET, PREVIEW, w.values.preview, "Preview"), (STAGE[0], ALL, STAGE[1], None)])
        open_environment(page, gitberth, w.app)
        assert shown_rows(page) == expected, shown_rows(page)
        for r in [r for r in env_rows(page) if r.key == SECRET]:
            assert {"Secret", "Build time"} <= set(r.tags) and r.value_type == "password", (r.scope, r.tags, r.value_type)
        stage = next(r for r in env_rows(page) if r.key == STAGE[0])
        assert "Build time" in stage.tags and "Secret" not in stage.tags and stage.value_type == "text", (stage.tags, stage.value_type)
        saved = sorted((v["Key"], v["Scope"], v["IsSecret"], v["BuildTime"]) for v in gitberth.db.env_vars(w.app.id) if v["Key"].startswith("E2E_"))
        assert saved == sorted([(SECRET, ALL, 1, 1), (SECRET, "Production", 1, 1), (SECRET, "Preview", 1, 1), (STAGE[0], ALL, 0, 1)]), saved
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_production_is_built_with_the_production_value(self, world, gitberth):
        w = world
        assert w.recreated["Status"] == "Succeeded" and w.recreated["Trigger"] == "ConfigChange", w.recreated
        # The banner said a build was still needed, and offered no Apply now.
        assert w.banner_line == "Some saved changes only take effect after a new build." and not w.banner_apply_now, (w.banner_line, w.banner_apply_now)
        s = w.d1
        check_built(gitberth, s, "production's first build with the secret")
        assert s.deploy["Trigger"] == "Manual" and s.deploy["CommitSha"] == w.first["CommitSha"], s.deploy
        assert "==> Using your custom Dockerfile" in [x.strip() for x in s.log], "production wasn't built from its custom Dockerfile"
        assert s.build["marker"] == "production", s.build
        assert saw(s) == sha256(w.values.production), \
            f"production's build saw {saw(s)}: all={sha256(w.values.all)}, production={sha256(w.values.production)}, preview={sha256(w.values.preview)}"
        assert s.build["plain"].get(STAGE[0]) == STAGE[1], s.build["plain"]
        assert not [k for k in s.build["plain"] if k.startswith("VERCEL_")], f"Vercel's variables are off, but the build saw {s.build['plain']}"
        assert (s.env.get(SECRET), s.env.get(STAGE[0])) == (w.values.production, STAGE[1]), "production runs without its own values"

    def test_a_preview_is_built_with_the_preview_value(self, previewed, gitberth):
        w = previewed
        s = w.p1
        check_built(gitberth, s, f"PR #{w.n}'s preview")
        assert s.deploy["CommitSha"] == w.sha and s.build["marker"] == "feature", (s.deploy, s.build)
        assert saw(s) == sha256(w.values.preview), \
            f"the preview's build saw {saw(s)}: all={sha256(w.values.all)}, production={sha256(w.values.production_2)}, preview={sha256(w.values.preview)}"
        assert s.build["plain"].get(STAGE[0]) == STAGE[1], s.build["plain"]
        assert (s.env.get(SECRET), s.env.get(STAGE[0])) == (w.values.preview, STAGE[1]), "the preview runs without its parent's Preview values"
        assert s.ref == f"gitberth/{w.preview['Slug']}:{w.sha[:7]}", s.ref

    def test_the_secret_is_mounted_into_the_build_step_never_an_argument(self, previewed, gitberth):
        w = previewed
        for what, s in (("production's first build", w.d1), ("the preview's first build", w.p1)):
            check_built(gitberth, s, what)
            # The Dockerfile as Gitberth filled it in (the deploy log shows it): the plain value and the stamp are ARGs, the
            # secret is mounted into the step.
            dockerfile = [x.strip()[2:] for x in s.log if x.strip().startswith("| ")]
            for line in (f"ARG {STAGE[0]}", "ARG GITBERTH_SECRETS", MOUNTED):
                assert line in dockerfile, f"{what}'s Dockerfile has no {line!r}: {dockerfile}"
            assert f"ARG {SECRET}" not in dockerfile, f"{what} declares the secret as a build argument"
            # Its history records the step's build arguments: the plain value and the stamp, never the secret.
            run = build_run(s.image.history)
            assert run and f"{STAGE[0]}={STAGE[1]}" in run and "GITBERTH_SECRETS=" in run and f"{SECRET}=" not in run, run

    def test_changing_it_and_building_the_same_commit_again_takes_the_new_value(self, changed, gitberth):
        w = changed
        assert w.change_recreated["Status"] == "Succeeded", w.change_recreated
        assert w.change_pending["RebuildPending"] == 1, f"changing a build-time value left production without a build pending: {w.change_pending}"
        assert w.change_banner_line == "Some saved changes only take effect after a new build." and not w.change_banner_apply_now, w.change_banner_line
        before, after = w.d1, w.d2
        check_built(gitberth, after, "production's build after its value changed")
        assert after.deploy["Trigger"] == "Manual" and after.deploy["CommitSha"] == before.deploy["CommitSha"], (before.deploy, after.deploy)
        assert after.ref == before.ref and after.image.id != before.image.id, (before.ref, after.ref, before.image.id, after.image.id)
        assert saw(after) == sha256(w.values.production_2), f"the same commit built again saw {saw(after)}; the old value is {sha256(w.values.production)}"
        assert after.build["built_at"] > before.build["built_at"], (before.build["built_at"], after.build["built_at"])
        ran_again(after, before, "production's build after its value changed")
        assert after.env.get(SECRET) == w.values.production_2

    def test_deploying_again_with_nothing_changed_takes_the_step_from_the_cache(self, unchanged, gitberth):
        """The cache at work: the same commit, values and containers give the same GITBERTH_SECRETS, and the step isn't run
        again. So when one does run again with all else the same, the secrets are why."""
        w = unchanged
        before, again = w.d2, w.d2b
        check_built(gitberth, again, "production's Deploy with nothing changed")
        assert again.deploy["Trigger"] == "Manual" and again.deploy["CommitSha"] == before.deploy["CommitSha"], (before.deploy, again.deploy)
        assert given_the_same(again, before), f"the two builds weren't given the same containers: {given(before)}; then {given(again)}"
        assert stamp_of(again.image.history) == stamp_of(before.image.history), (stamp_of(before.image.history), stamp_of(again.image.history))
        step = build_step(again.log)
        assert step and step[1], f"the build step ran again with nothing changed: {step}\n" + "\n".join(again.log[-30:])
        # The same layer: what the step wrote is what it wrote the time before.
        assert again.build == before.build, (before.build, again.build)
        assert saw(again) == sha256(w.values.production_2) and again.env.get(SECRET) == w.values.production_2

    def test_a_change_to_the_preview_value_leaves_production_alone(self, preview_changed):
        w = preview_changed
        assert w.preview_bar_waited is None, w.preview_bar_waited
        assert w.preview_bar == ("1 unsaved change", PREVIEWS_ONLY), w.preview_bar
        assert w.preview_said == "Saved.", w.preview_said
        assert w.preview_landed == f"/apps/{w.app.key}/environment", w.preview_landed
        # Neither a restart nor a build is pending for production: its container, deploys and banner are as they were.
        assert w.production_before["RebuildPending"] == 0 and w.production_before["ConfigChangePending"] == 0, w.production_before
        assert w.production_after == w.production_before, f"production changed: {w.production_before} -> {w.production_after}"
        assert w.preview_change_banner == 0, "production's page says saved settings aren't live yet"

    def test_the_previews_next_build_takes_its_new_value(self, preview_changed, gitberth):
        w = preview_changed
        before, after = w.p1, w.p2
        check_built(gitberth, after, "the preview's redeploy")
        assert after.deploy["Trigger"] == "Manual" and after.deploy["CommitSha"] == w.sha, after.deploy
        assert saw(after) == sha256(w.values.preview_2), f"the preview's build saw {saw(after)}; its old value is {sha256(w.values.preview)}"
        assert after.build["built_at"] > before.build["built_at"], (before.build["built_at"], after.build["built_at"])
        # (Its first build had no preview container to be given yet, so the names differ: only that the step ran is checked.)
        step = build_step(after.log)
        assert step and not step[1], f"the build step came from the cache: {step}; {reachable(after.log)}"
        assert after.env.get(SECRET) == w.values.preview_2

    def test_promote_builds_the_previews_commit_again_with_productions_value(self, promoted, gitberth):
        w = promoted
        title, body, buttons = w.promote_dialog
        assert (title, buttons) == (f"Promote PR #{w.n} to production?", ["Cancel", "Promote"]), w.promote_dialog
        assert body == (f"{w.app.name} builds {w.sha[:7]} again, with production's variables, and runs it on its own address. It stays on that "
                        "commit until main gets a new one, or until you deploy it by hand. The preview keeps running."), body
        s = w.d3
        check_built(gitberth, s, "the promote")
        assert s.deploy["Trigger"] == "Promote" and s.deploy["CommitSha"] == w.sha, s.deploy
        assert s.ref == f"{w.app.image_repository}:{w.sha[:7]}" and s.build["marker"] == "feature", (s.ref, s.build)
        assert saw(s) == sha256(w.values.production_2), \
            f"production's build of the preview's commit saw {saw(s)}: production={sha256(w.values.production_2)}, preview={sha256(w.values.preview_2)}"
        # Not the preview's step: it ran again after the preview's last build, given the same names, so for the secrets.
        assert s.build["built_at"] > w.p2.build["built_at"], (w.p2.build["built_at"], s.build["built_at"])
        ran_again(s, w.p2, "production's build of the promoted commit")
        assert s.env.get(SECRET) == w.values.production_2

    def test_the_build_argument_is_a_keyed_hash_that_follows_the_values(self, promoted, gitberth):
        w = promoted
        stamps = {}
        for what, s in builds(w):
            check_built(gitberth, s, what)
            stamp = stamp_of(s.image.history)
            assert stamp and re.fullmatch(r"[0-9a-f]{64}", stamp), f"{what}'s GITBERTH_SECRETS is {stamp!r}: {build_run(s.image.history)}"
            # The build step saw exactly that argument (the test app hashes every value it sees).
            assert s.build["sha256"].get("GITBERTH_SECRETS") == sha256(stamp), f"{what}'s build step saw another GITBERTH_SECRETS"
            stamps[what] = stamp
        # Never the value, a plain hash of it, or a plain hash of what's keyed.
        v = w.values
        plain = {sha256(x) for x in (v.all, v.production, v.preview, v.production_2, v.preview_2)}
        plain |= {sha256(json.dumps([f"{SECRET}={x}"], separators=(",", ":"))) for x in (v.all, v.production, v.preview, v.production_2, v.preview_2)}
        assert not set(stamps.values()) & plain, f"GITBERTH_SECRETS is a plain hash of a value: {stamps}"
        # The same values give the same argument; another value, or another scope's, another.
        d1, d2, d2b, p1, p2, d3 = stamps.values()
        assert d2b == d2 and d3 == d2, f"production's builds with the same value got different arguments: {stamps}"
        assert len({d1, d2, p1, p2}) == 4, f"builds with different values got the same argument: {stamps}"


# ---- no secret value anywhere it doesn't belong ----

def leaks(text, values):
    return [name for name, value in values.items() if value in text]


def secret_values(w):
    v = w.values
    return {"for every deploy": v.all, "production's": v.production, "the preview's": v.preview, "production's second": v.production_2,
            "the preview's second": v.preview_2}


class TestNoSecretValueLeaks:
    def test_not_in_the_images(self, promoted, gitberth, engine):
        w = promoted
        values = secret_values(w)
        for what, s in builds(w):
            check_built(gitberth, s, what)
            history = "\n".join(s.image.history)
            assert not leaks(history, values), f"{what}'s image history has the {leaks(history, values)} value"
            assert not leaks(json.dumps(s.image.labels), values), f"{what}'s image labels have a secret value: {s.image.labels}"
            assert not leaks(s.image.inspect, values), f"docker inspect of {what}'s image has the {leaks(s.image.inspect, values)} value"
            assert not [e for e in s.image.env if e.split("=", 1)[0] in (SECRET, "GITBERTH_SECRETS")], f"{what}'s image keeps {s.image.env}"
        for container in (w.app.container, w.preview_container):
            labels = json.dumps(engine.labels(container))
            assert not leaks(labels, values), f"{container}'s labels have a secret value: {labels}"

    def test_not_in_the_deploy_logs(self, promoted, page, gitberth):
        w = promoted
        values = secret_values(w)
        deploys = [d for d in gitberth.db.deployments(w.app.id) + gitberth.db.deployments(w.preview["Id"]) if d["Id"] > w.first["Id"]]
        assert len(deploys) >= 8, [(d["Id"], d["Trigger"], d["Status"]) for d in deploys]
        for d in deploys:
            log = gitberth.deploy_log(d["Id"])
            assert not leaks(log, values), f"deploy #{d['Id']} ({d['Trigger']})'s log has the {leaks(log, values)} value"
            assert not leaks(d["Error"] or "", values), d
        # And on the deploy log pages, a build of production's and the preview's.
        for app_id, s in ((w.app.id, w.d3), (w.preview["Id"], w.p2)):
            gitberth.open(page, f"/apps/{app_id}/deployments/{s.deploy['Id']}")
            until(lambda: "Deploy succeeded" in log_lines(page), 30, "the deploy log page to show the whole deploy", seen=lambda: log_lines(page)[-6:])
            shown = "\n".join(log_lines(page))
            assert MOUNTED in shown, f"the deploy log page doesn't show the build step that saw the secret: {log_lines(page)[:40]}"
            assert not leaks(shown, values), f"deploy #{s.deploy['Id']}'s log page shows the {leaks(shown, values)} value"
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_not_in_anything_github_was_sent(self, promoted, standins):
        w = promoted
        values = secret_values(w)
        sent = [c for c in standins.calls(since=w.mark) if c["host"] in GITHUB_HOSTS and (c.get("headers") or {}).get("User-Agent") != RUNNER_AGENT]
        statuses, comments = w.repo.statuses(), w.repo.comments()
        # There was something to look at: the deploys' commit statuses and the pull request's comment.
        assert [s for s in statuses if s["context"].startswith("Gitberth: ")] and [s for s in statuses if s["context"].startswith("Gitberth preview: ")], \
            f"GitHub got no commit statuses for production and the preview to check: {[(s['context'], s['state']) for s in statuses]}"
        assert [c for c in comments if c["issue"] == w.n], f"GitHub got no comment on PR #{w.n} to check"
        for c in sent:
            text = json.dumps(c)
            assert not leaks(text, values), f"GitHub was sent the {leaks(text, values)} value: {c['method']} {c['host']}{c['path']}"
        for what, items in (("its commit statuses", statuses), ("its comments", comments)):
            text = json.dumps(items)
            assert not leaks(text, values), f"GitHub keeps the {leaks(text, values)} value in {what}"

    def test_not_in_gitberths_own_log_or_the_activity(self, promoted, gitberth):
        w = promoted
        values = secret_values(w)
        log = gitberth.logs(since=int(w.t0) - 1)
        assert log.strip(), "Gitberth's log is empty"
        assert not leaks(log, values), f"Gitberth's own log has the {leaks(log, values)} value"
        # The Activity of both says what changed, never with a value.
        entries = gitberth.db.activity(w.app.id) + gitberth.db.activity(w.preview["Id"])
        assert entries, "the app has no Activity to check"
        text = json.dumps(entries, default=str)
        assert not leaks(text, values), f"the Activity has the {leaks(text, values)} value"
