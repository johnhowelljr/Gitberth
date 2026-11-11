"""Scenario 19: declared volumes.

The test app's image declares VOLUME /data.
- Its first deploy keeps that in a named volume of the app's own, gb-<slug>-data: saved in its settings (the Settings
  tab's Volumes section lists it), mounted at /data, said in the deploy log (the file and its page), and listed on its
  Overview.
- A file written there survives redeploys (a new commit, then Deploy by hand), each in a new container. No deploy makes
  an anonymous volume, and the settings keep the one volume.
- A preview gets its own (gb-<preview-slug>-data): its own row and Docker volume, mounted at /data, without production's
  data, and production doesn't see what it writes. Removing the preview (Previews tab › Remove) removes its volume;
  production's stays, with its data.
- A declared VOLUME the app keeps in a tmpfs instead (Settings › Volumes: the volume taken out, Temporary added at /data)
  gets no named volume: its deploys, without a build (Apply) and with one (a new commit), succeed with the tmpfs as the
  one mount at /data, and make no anonymous volume.
- A preview made before that keeps its own volume at /data, with its data: its next deploy (Previews tab › Redeploy) has
  exactly one mount there, no tmpfs, and its deploy log (the file and its page) says 'No tmpfs at /data: the preview
  keeps its volume ... there'.

The helpers here (mounts, files in the test app, the Settings tab's Volumes section and apply bar, the Overview's
Container card, deploy logs) serve scenarios 6 and 7 too.
"""
import re
from types import SimpleNamespace

import pytest
from playwright.sync_api import TimeoutError as PlaywrightTimeout

from e2e.engine import docker
from e2e.gitberth import FINAL
from e2e.net import call, get
from e2e.wait import until
from test_02_pull_request_previews import (log_lines, new_app, own_apps, preview_row, runtime_extras, text_of, unique, wait_deployed,
                                           wait_finished)
from test_04_preview_limit import choose_from_menu, remove_from_previews_tab

KEPT = b"written into production's /data"
PREVIEW_DATA = b"written into the preview's own /data"


# ---- helpers (scenarios 6 and 7 use them too) ----

def declares_line(volume):
    """What a deploy log says when it keeps an image's VOLUME /data in a named volume (DeployPipeline)."""
    return (f"The image declares VOLUME /data: it's kept in the volume {volume}, so its data stays from one deploy to the next "
            f"(Settings › Volumes).")


def no_tmpfs_line(volume, path="/data"):
    """What a preview's deploy log says when its parent has a tmpfs where the preview keeps a volume (DeployPipeline)."""
    return f"No tmpfs at {path}: the preview keeps its volume {volume} there, and Docker mounts one thing at a path."


def log_texts(gitberth, deployment_id):
    """A deploy log's lines as its file has them, without their time (HH:MM:SS)."""
    return [line[9:] if re.match(r"^\d\d:\d\d:\d\d ", line) else line for line in gitberth.deploy_log(deployment_id).splitlines()]


def wait_final(gitberth, deployment_id, timeout=300):
    """Waits for a deploy (by its id) to be over, however it ended; returns its row."""
    return until(lambda: (lambda d: d if d and d["Status"] in FINAL else None)(gitberth.db.deployment(deployment_id)), timeout,
                 f"deploy #{deployment_id} to finish", interval=2, seen=lambda: gitberth.db.deployment(deployment_id))


def check_succeeded(gitberth, deploy):
    """Fails with the deploy's error and the end of its log unless it succeeded."""
    assert deploy and deploy["Status"] == "Succeeded", (
        f"Deploy #{deploy['Id']} ({deploy['Trigger']}) ended {deploy['Status']}: {deploy['Error']}\n"
        f"--- the end of its log ---\n{gitberth.deploy_log(deploy['Id'], tail=40)}")


def is_anonymous(name):
    return bool(name) and len(name) == 64 and all(c in "0123456789abcdef" for c in name)


def mounts_at(inspect, path):
    """Everything a container has mounted at the path, as [(type, the volume's name or the host folder)]: its mounts, and
    its tmpfs mounts, which `--tmpfs` keeps in HostConfig.Tmpfs rather than in Mounts."""
    path = path.rstrip("/")
    found = [(m.get("Type"), m.get("Name") or m.get("Source")) for m in (inspect or {}).get("Mounts") or []
             if (m.get("Destination") or "").rstrip("/") == path]
    found += [("tmpfs", options) for target, options in (((inspect or {}).get("HostConfig") or {}).get("Tmpfs") or {}).items()
              if target.rstrip("/") == path]
    return found


def mounts(inspect):
    """A container's mounts as {path in the container: [(type, the volume's name or the host folder)]}, tmpfs ones too."""
    out = {}
    for m in (inspect or {}).get("Mounts") or []:
        out.setdefault((m.get("Destination") or "").rstrip("/"), []).append((m.get("Type"), m.get("Name") or m.get("Source")))
    for target, options in (((inspect or {}).get("HostConfig") or {}).get("Tmpfs") or {}).items():
        out.setdefault(target.rstrip("/"), []).append(("tmpfs", options))
    return out


def volume_mounts(inspect):
    """A container's named volumes as {path in the container: the volume's name}."""
    return {(m.get("Destination") or "").rstrip("/"): m.get("Name") for m in (inspect or {}).get("Mounts") or [] if m.get("Type") == "volume"}


def anonymous_mounts(inspect):
    return [m.get("Name") for m in (inspect or {}).get("Mounts") or [] if m.get("Type") == "volume" and is_anonymous(m.get("Name"))]


def new_anonymous(engine, before):
    """The anonymous volumes the engine has now that it didn't have before, each with the containers that use it."""
    return {v: docker("ps", "-a", "--filter", f"volume={v}", "--format", "{{.Names}}", check=False).split()
            for v in sorted(set(engine.anonymous_volumes()) - set(before))}


def volume_rows(gitberth, app_id):
    """The app's Volumes rows as (source, target, read-only)."""
    return [(v["Source"], v["Target"], bool(v["ReadOnly"])) for v in gitberth.db.volumes(app_id)]


def write_file(url, path, data):
    """Writes a file through the test app (POST /file)."""
    status, _, text = call("POST", f"{url}/file?path={path}", data)
    assert status == 200, f"writing {path} through {url} answered {status}: {text}"


def read_file(url, path):
    """(status, text) of the test app's GET /file: 404 when there's no such file."""
    return get(f"{url}/file?path={path}")


# The Settings tab

def settings_volume_rows(page):
    """The Settings tab's Volumes section as it lists the mounts: [(the volume's name, the host folder, or "Temporary, in
    memory"; the path in the container; its tag: "volume", "host folder" or None)]. Empty when it says there are none."""
    section = page.locator("#sec-volumes")
    section.wait_for()
    rows = section.locator(".gb-envrow:not(.gb-envrow--head)")
    out = []
    for i in range(rows.count()):
        row = rows.nth(i)
        key = row.locator(".gb-envkey")
        source = key.locator("input")
        tag = key.locator(".gb-default-tag")
        out.append((source.input_value() if source.count() else text_of(key),
                    row.locator("input[aria-label='Path in the container']").input_value(),
                    text_of(tag) if tag.count() else None))
    return out


def volume_row(page, source):
    """The Settings tab's Volumes row of the volume (or host folder) with this name."""
    rows = page.locator("#sec-volumes .gb-envrow:not(.gb-envrow--head)")
    rows.first.wait_for()
    for i in range(rows.count()):
        field = rows.nth(i).locator(".gb-envkey input")
        if field.count() and field.input_value() == source:
            return rows.nth(i)
    raise AssertionError(f"the Settings tab's Volumes section has no row for {source}: {settings_volume_rows(page)}")


def add_row(section, button):
    """Clicks one of a Settings section's add buttons (Named volume, Host folder, Temporary (tmpfs), Add label) and returns
    the row it added, once the page shows it."""
    added = section.locator(".gb-envrow--new")
    before = added.count()
    section.get_by_role("button", name=button, exact=True).click()
    until(lambda: added.count() == before + 1, 15, f"a new row after {button}", seen=lambda: text_of(section))
    return added.nth(before)


def apply_bar(page):
    return page.locator(".gb-applybar")


def wait_for_plan(page):
    """Waits for the apply bar to have worked out what applying does; returns (its title, its line)."""
    bar = apply_bar(page)
    bar.wait_for()
    sub = bar.locator(".gb-applybar-sub")
    until(lambda: sub.count() and text_of(sub) and "Working out" not in text_of(sub), 30, "the apply bar to say what applying does",
          seen=lambda: text_of(bar))
    return text_of(bar.locator(".gb-applybar-title")), text_of(sub)


def apply_settings(page, gitberth, app):
    """The Settings tab's apply bar: Apply. A Git app's settings are saved, and a deploy without a build makes them live:
    the page goes to it. Returns ((the bar's title, its line) before it was clicked, the deploy's id)."""
    said = wait_for_plan(page)
    button = apply_bar(page).get_by_role("button", name=re.compile(r"^(Apply|Review and apply)$"))
    until(button.is_enabled, 30, "Apply to be offered", seen=lambda: text_of(apply_bar(page)))
    button.click()
    try:
        page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"), timeout=60_000)
    except PlaywrightTimeout:
        raise AssertionError(f"applying {app.slug}'s settings started no deploy; Gitberth said: "
                             f"{page.locator('.mud-snackbar').all_inner_texts() or gitberth.banners(page)}") from None
    return said, int(gitberth.path_of(page).rsplit("/", 1)[-1])


# The Overview

def container_card(page):
    """The Overview's Container card."""
    return page.locator("section.gb-card").filter(has=page.locator(".gb-card-title", has_text=re.compile(r"^\s*Container\s*$")))


def container_fact(page, label):
    """One entry of the Overview's Container card (Volumes, Limits, Command, ...): its parts, each as its text."""
    entry = container_card(page).locator("dl.gb-kv > dt", has_text=re.compile(rf"^\s*{re.escape(label)}\s*$")).locator("xpath=following-sibling::dd[1]")
    entry.wait_for(timeout=30_000)
    parts = entry.locator(":scope > span")
    return [text_of(parts.nth(i)) for i in range(parts.count())] or [text_of(entry)]


def overview_volumes(page, gitberth, key):
    """The Overview's Container card's Volumes, each as "<volume or folder> <path> <tag>"."""
    gitberth.open(page, f"/apps/{key}")
    return container_fact(page, "Volumes")


def snapshot(gitberth, engine, app, url, anonymous_before, deploy=None):
    """Production as a stage of a scenario left it: its deploy, container, volumes rows, the file kept in /data, and the
    anonymous volumes the engine got since the scenario began."""
    return SimpleNamespace(deploy=deploy, inspect=engine.inspect(app.container), rows=volume_rows(gitberth, app.id),
                           kept=read_file(url, "/data/kept.txt"), anonymous=new_anonymous(engine, anonymous_before))


# ---- the declared volume, redeploys, and a preview's own ----

@pytest.fixture(scope="class")
def declared(gitberth, github, engine, standins):
    """An app made from the test app (its first deploy over), a file written into its /data, then two redeploys: a new
    commit, and Deploy by hand. Then previews of pull requests and branches, a branch pushed, its preview deployed, a file
    written into its /data, and the preview removed from the Previews tab."""
    with own_apps(gitberth, standins, "declared-volumes") as (page, made):
        anonymous_before = engine.anonymous_volumes()
        repo, app, first = new_app(page, gitberth, github, made.track, unique("dvol"))
        url = gitberth.lan_url(app.id)
        states = {"first": snapshot(gitberth, engine, app, url, anonymous_before, first)}
        write_file(url, "/data/kept.txt", KEPT)

        # A new commit on main: the watcher deploys it.
        sha = repo.commit({"marker.txt": "production, again"}, message="A new commit on main")["sha"]
        states["commit"] = snapshot(gitberth, engine, app, url, anonymous_before, wait_finished(gitberth, app.id, sha, after=first["Id"]))
        # Deploy by hand.
        manual = gitberth.deploy(page, app)
        states["manual"] = snapshot(gitberth, engine, app, url, anonymous_before, wait_final(gitberth, manual))

        # A preview of a branch pushed once previews are on.
        mark = standins.seq()
        said = gitberth.set_previews(page, app, mode="Pull requests and branches")
        assert said.startswith("Saved"), said
        repo.wait_pulls_listed(since=mark, timeout=90)
        feature_sha = repo.commit({"marker.txt": "feature"}, branch="feature", message="Work on a feature")["sha"]
        preview = gitberth.wait_preview(app.id, branch="feature", timeout=90)
        preview_deploy = wait_deployed(gitberth, preview["Id"], feature_sha)
        preview_url = gitberth.lan_url(preview["Id"])
        p = SimpleNamespace(row=preview, deploy=preview_deploy, inspect=engine.inspect(f"gb-{preview['Slug']}"),
                            rows=volume_rows(gitberth, preview["Id"]), kept=read_file(preview_url, "/data/kept.txt"),
                            volumes=engine.volumes(prefix=f"gb-{preview['Slug']}-"))
        write_file(preview_url, "/data/preview.txt", PREVIEW_DATA)
        p.production_sees = read_file(url, "/data/preview.txt")
        production_before_removal = engine.inspect(app.container)

        # Removed from the Previews tab.
        removal = remove_from_previews_tab(page, gitberth, app, "feature")
        yield SimpleNamespace(repo=repo, app=app, volume=f"gb-{app.slug}-data", url=url, states=states, preview=p, removal=removal,
                              production_before_removal=production_before_removal, anonymous_before=anonymous_before)


class TestDeclaredVolume:
    def test_the_first_deploy_keeps_it_in_a_named_volume_of_the_apps_own(self, declared, gitberth):
        w = declared
        first = w.states["first"]
        assert first.rows == [("data", "/data", False)], f"the app's settings don't keep /data in its own volume data: {first.rows}"
        assert mounts_at(first.inspect, "/data") == [("volume", w.volume)], f"its container has at /data: {mounts_at(first.inspect, '/data')}"
        assert not anonymous_mounts(first.inspect), f"its container mounts anonymous volumes: {anonymous_mounts(first.inspect)}"
        assert not first.anonymous, f"its first deploy made anonymous volumes (with the containers using them): {first.anonymous}"
        assert declares_line(w.volume) in log_texts(gitberth, first.deploy["Id"]), \
            f"the first deploy's log doesn't say where VOLUME /data is kept:\n{gitberth.deploy_log(first.deploy['Id'])[:4000]}"

    def test_its_settings_overview_and_deploy_log_page_show_it(self, declared, page, gitberth):
        w = declared
        # Settings › Volumes: the volume by its name, at /data.
        gitberth.open(page, f"/apps/{w.app.key}/settings")
        rows = settings_volume_rows(page)
        assert rows == [(w.volume, "/data", "volume")], f"the Settings tab's Volumes section lists {rows}"
        # The Overview's Container card.
        listed = overview_volumes(page, gitberth, w.app.key)
        assert listed == [f"{w.volume} /data volume"], f"the Overview lists the volumes {listed}"
        # The first deploy's page says where VOLUME /data is kept.
        gitberth.open(page, f"/apps/{w.app.id}/deployments/{w.states['first'].deploy['Id']}")
        until(lambda: declares_line(w.volume) in log_lines(page), 30, "the deploy log page to say where VOLUME /data is kept",
              seen=lambda: [x for x in log_lines(page) if "VOLUME" in x] or log_lines(page)[-10:])
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    @pytest.mark.parametrize("stage", ["commit", "manual"])
    def test_a_file_written_there_survives_a_redeploy(self, declared, gitberth, stage):
        w = declared
        state, before = w.states[stage], w.states["first" if stage == "commit" else "commit"]
        check_succeeded(gitberth, state.deploy)
        assert state.deploy["Trigger"] == ("Poll" if stage == "commit" else "Manual"), state.deploy
        assert state.inspect["Id"] != before.inspect["Id"], "the redeploy didn't make a new container"
        assert mounts_at(state.inspect, "/data") == [("volume", w.volume)], f"after the redeploy, /data is {mounts_at(state.inspect, '/data')}"
        assert state.kept == (200, KEPT.decode()), f"the file written into /data before the redeploy reads {state.kept}"
        assert state.rows == [("data", "/data", False)], f"after the redeploy the app's volumes are {state.rows}"
        # It was kept already: the redeploy didn't name it again.
        assert declares_line(w.volume) not in log_texts(gitberth, state.deploy["Id"]), "a redeploy kept VOLUME /data in a volume again"

    def test_no_anonymous_volumes_pile_up(self, declared):
        w = declared
        for stage, state in w.states.items():
            assert not anonymous_mounts(state.inspect), f"after the {stage} deploy its container mounts anonymous volumes: {anonymous_mounts(state.inspect)}"
            assert not state.anonymous, f"by the {stage} deploy the engine had new anonymous volumes (with the containers using them): {state.anonymous}"


class TestAPreviewsDeclaredVolume:
    def test_a_preview_gets_its_own(self, declared):
        w = declared
        p = w.preview
        own = f"gb-{p.row['Slug']}-data"
        assert p.rows == [("data", "/data", False)], f"the preview's volumes are {p.rows}"
        assert mounts_at(p.inspect, "/data") == [("volume", own)], f"the preview has at /data: {mounts_at(p.inspect, '/data')}"
        assert p.volumes == [own], f"the engine has the preview's volumes {p.volumes}"
        assert own != w.volume and not anonymous_mounts(p.inspect), (own, anonymous_mounts(p.inspect))
        # Its own: without production's file, and what it writes isn't production's.
        assert p.kept[0] == 404, f"the preview reads production's file from its /data: {p.kept}"
        assert p.production_sees[0] == 404, f"production reads the preview's file: {p.production_sees}"

    def test_it_goes_with_the_preview_and_productions_stays(self, declared, engine, gitberth):
        w = declared
        own = f"gb-{w.preview.row['Slug']}-data"
        assert w.removal["said"] == "Removed the preview of feature.", w.removal
        assert gitberth.preview(w.app.id, branch="feature") is None
        until(lambda: not engine.volume_exists(own), 30, f"the preview's volume {own} to go", seen=lambda: engine.volumes(prefix=f"gb-{w.app.slug}"))
        assert engine.volume_exists(w.volume), f"removing the preview removed production's volume {w.volume}"
        now = engine.inspect(w.app.container)
        assert now and now["Id"] == w.production_before_removal["Id"] and now["State"]["Running"], "removing the preview touched production's container"
        assert mounts_at(now, "/data") == [("volume", w.volume)]
        assert read_file(w.url, "/data/kept.txt") == (200, KEPT.decode()), "production's data is gone"
        assert not new_anonymous(engine, w.anonymous_before), new_anonymous(engine, w.anonymous_before)


# ---- a declared volume kept in a tmpfs, and a preview made before that ----

@pytest.fixture(scope="class")
def in_memory(gitberth, github, engine, standins):
    """An app with previews of pull requests and branches, and a branch's preview deployed (its own /data volume, with a
    file in it). Then the app's Settings tab: its /data volume taken out, a Temporary (tmpfs) one added at /data, and
    Apply (a deploy without a build); then a new commit on main (a deploy with one). Then the preview's Redeploy, from
    the Previews tab."""
    with own_apps(gitberth, standins, "volume-in-memory") as (page, made):
        anonymous_before = engine.anonymous_volumes()
        repo, app, first = new_app(page, gitberth, github, made.track, unique("tmv"))
        mark = standins.seq()
        said = gitberth.set_previews(page, app, mode="Pull requests and branches")
        assert said.startswith("Saved"), said
        repo.wait_pulls_listed(since=mark, timeout=90)
        feature_sha = repo.commit({"marker.txt": "feature"}, branch="feature", message="Work on a feature")["sha"]
        preview = gitberth.wait_preview(app.id, branch="feature", timeout=90)
        preview_first = wait_deployed(gitberth, preview["Id"], feature_sha)
        preview_url = gitberth.lan_url(preview["Id"])
        write_file(preview_url, "/data/preview.txt", PREVIEW_DATA)
        preview_before = SimpleNamespace(inspect=engine.inspect(f"gb-{preview['Slug']}"), rows=volume_rows(gitberth, preview["Id"]))

        # Settings › Volumes: /data's volume out, a tmpfs in, and Apply.
        gitberth.open(page, f"/apps/{app.key}/settings")
        row = volume_row(page, f"gb-{app.slug}-data")
        row.get_by_role("button", name="Remove this volume").click()
        until(lambda: "gb-envrow--gone" in (row.get_attribute("class") or ""), 15, "the volume's row to be struck through",
              seen=lambda: row.get_attribute("class"))
        tmpfs = add_row(page.locator("#sec-volumes"), "Temporary (tmpfs)")
        tmpfs.locator("input[aria-label='Path in the container']").fill("/data")
        bar, applied_id = apply_settings(page, gitberth, app)
        applied = wait_final(gitberth, applied_id)
        after_apply = SimpleNamespace(deploy=applied, inspect=engine.inspect(app.container), rows=volume_rows(gitberth, app.id),
                                      extras=runtime_extras(gitberth.db.app(app.slug)), anonymous=new_anonymous(engine, anonymous_before))
        gitberth.open(page, f"/apps/{app.key}/settings")
        settings_after = settings_volume_rows(page)

        # A new commit on main: a deploy with a build.
        main_sha = repo.commit({"marker.txt": "production, in memory"}, message="A new commit on main")["sha"]
        built = wait_finished(gitberth, app.id, main_sha, after=applied_id)
        after_build = SimpleNamespace(deploy=built, inspect=engine.inspect(app.container), rows=volume_rows(gitberth, app.id),
                                      anonymous=new_anonymous(engine, anonymous_before))

        # The preview's next deploy: Redeploy on the Previews tab.
        gitberth.open(page, f"/apps/{app.key}/previews")
        choose_from_menu(page, "feature", "Redeploy")
        page.wait_for_url(re.compile(rf"/apps/{preview['Id']}/deployments/\d+$"), timeout=60_000)
        redeployed = wait_final(gitberth, int(gitberth.path_of(page).rsplit("/", 1)[-1]))
        preview_after = SimpleNamespace(deploy=redeployed, inspect=engine.inspect(f"gb-{preview['Slug']}"), rows=volume_rows(gitberth, preview["Id"]),
                                        extras=runtime_extras(gitberth.db.app(preview["Slug"])), kept=read_file(preview_url, "/data/preview.txt"),
                                        anonymous=new_anonymous(engine, anonymous_before))
        yield SimpleNamespace(repo=repo, app=app, first=first, bar=bar, after_apply=after_apply, settings_after=settings_after,
                              after_build=after_build, preview=preview, preview_volume=f"gb-{preview['Slug']}-data", preview_first=preview_first,
                              preview_before=preview_before, preview_after=preview_after)


class TestADeclaredVolumeKeptInATmpfs:
    def test_the_settings_keep_it_in_memory(self, in_memory):
        w = in_memory
        title, line = w.bar
        assert title == "1 unsaved change" and "Applies with a deploy without a build" in line, w.bar
        assert w.after_apply.rows == [], f"the app still has volumes: {w.after_apply.rows}"
        assert [t.get("target") for t in w.after_apply.extras.get("tmpfs") or []] == ["/data"], w.after_apply.extras
        assert w.settings_after == [("Temporary, in memory", "/data", None)], f"the Settings tab's Volumes section lists {w.settings_after}"

    @pytest.mark.parametrize("stage", ["after_apply", "after_build"])
    def test_its_deploys_succeed_with_the_tmpfs_alone_at_data(self, in_memory, gitberth, stage):
        w = in_memory
        state = getattr(w, stage)
        check_succeeded(gitberth, state.deploy)
        assert state.deploy["Trigger"] == ("ConfigChange" if stage == "after_apply" else "Poll"), state.deploy
        assert [kind for kind, _ in mounts_at(state.inspect, "/data")] == ["tmpfs"], \
            f"production's container has at /data: {mounts_at(state.inspect, '/data')} (all its mounts: {mounts(state.inspect)})"
        assert state.rows == [], f"a deploy gave the app a volume again: {state.rows}"
        assert not [x for x in log_texts(gitberth, state.deploy["Id"]) if "declares VOLUME" in x], "a deploy kept VOLUME /data in a named volume"
        assert not anonymous_mounts(state.inspect) and not state.anonymous, f"anonymous volumes: {anonymous_mounts(state.inspect)} {state.anonymous}"


class TestAPreviewKeepsItsVolumeUnderItsParentsTmpfs:
    def test_its_next_deploy_has_its_own_volume_alone_at_data(self, in_memory, gitberth):
        w = in_memory
        before, after = w.preview_before, w.preview_after
        assert mounts_at(before.inspect, "/data") == [("volume", w.preview_volume)], mounts_at(before.inspect, "/data")
        check_succeeded(gitberth, after.deploy)
        assert after.inspect["Id"] != before.inspect["Id"], "the preview's redeploy didn't make a new container"
        assert mounts_at(after.inspect, "/data") == [("volume", w.preview_volume)], \
            f"the preview's container has at /data: {mounts_at(after.inspect, '/data')} (all its mounts: {mounts(after.inspect)})"
        assert after.rows == [("data", "/data", False)], f"the preview's volumes are {after.rows}"
        assert not [t for t in after.extras.get("tmpfs") or [] if (t.get("target") or "").rstrip("/") == "/data"], \
            f"the preview's settings have a tmpfs at /data: {after.extras}"
        assert after.kept == (200, PREVIEW_DATA.decode()), f"the preview's data in its volume reads {after.kept}"
        assert not anonymous_mounts(after.inspect) and not after.anonymous, f"anonymous volumes: {anonymous_mounts(after.inspect)} {after.anonymous}"

    def test_its_deploy_log_says_why(self, in_memory, page, gitberth):
        w = in_memory
        wanted = no_tmpfs_line(w.preview_volume)
        texts = log_texts(gitberth, w.preview_after.deploy["Id"])
        assert wanted in texts, f"the preview's deploy log doesn't say {wanted!r}:\n" + "\n".join(texts[:40])
        assert wanted not in log_texts(gitberth, w.preview_first["Id"]), "the preview's first deploy talked about a tmpfs its parent didn't have yet"
        gitberth.open(page, f"/apps/{w.preview['Id']}/deployments/{w.preview_after.deploy['Id']}")
        until(lambda: wanted in log_lines(page), 30, "the deploy log page to say the preview keeps its volume", seen=lambda: log_lines(page)[:20])
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_the_previews_tab_still_lists_it_ready(self, in_memory, page, gitberth):
        w = in_memory
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        pill = preview_row(page, "feature").locator(".gb-pill2")
        pill.wait_for()
        until(lambda: text_of(pill) == "Ready", 60, "the preview's row to say Ready", seen=lambda: text_of(pill))
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"
