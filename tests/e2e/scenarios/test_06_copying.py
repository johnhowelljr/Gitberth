"""Scenario 6: what a preview copies from its parent.

The parent gets, on its Settings tab and applied (a deploy without a build), everything a preview could copy: named
volumes data (the image's VOLUME /data) and test-data, a host folder, a host device, privileged mode, added and dropped
capabilities, an init process, a read-only root, memory, CPU and process limits, shared memory, a stop signal and
timeout, an extra host, a ulimit, a command of its own, an owner's label and a health URL. Previews of pull requests and
branches, forks too. Then a branch named test gets a commit, and a fork opens a pull request.
- Each preview runs with the parent's settings that stay inside its container: its container has the parent's limits,
  shared memory, command, label, init process, dropped capabilities, read-only root, restart policy, stop signal and
  timeout, extra host and ulimit, and its deploys check the parent's health URL. The branch's preview (the repo's own
  code) keeps the added capabilities too.
- No preview gets the parent's host folders, host devices or privileged mode, the repo's own branch included; a fork's
  pull request doesn't get the added capabilities either. Each preview's deploy log lists every item it left out (the
  file and the deploy log page); its page's banner names the host folder it doesn't mount, its Overview lists its own
  volumes and no host folder, and the Previews tab says what previews never get.
- Each preview's named volumes are its own (gb-<preview-slug>-data and -test-data): new Docker volumes, empty, never
  production's.
- The branch test collides with the parent's volumes: as <app>-test, its data would be gb-<app>-test-data, production's
  test-data. Its preview gets another name and shares no volume with production, both ways.
- The other way round (another app): a preview of the branch test runs as <app>-test, its data in gb-<app>-test-data;
  then the parent can't add a volume test-data, which would be that name. Saving says why, and nothing changes.
"""
import contextlib
import re
from types import SimpleNamespace

import pytest
from playwright.sync_api import TimeoutError as PlaywrightTimeout

from e2e.wait import until
from test_02_pull_request_previews import (FORKER, log_lines, new_app, open_pull, own_apps, preview_row, previews_card, runtime_extras,
                                           text_of, unique, wait_deployed, wait_finished)
from test_19_declared_volumes import (add_row, apply_bar, apply_settings, container_fact, log_texts, read_file, settings_volume_rows,
                                      volume_mounts, volume_rows, wait_final, wait_for_plan, write_file)

COMMAND = ["python3", "-u", "/app/server.py", "copied-from-the-parent"]
LABEL = ("e2e.copied", "from-the-parent")
# A host device the rig's engine has and Docker doesn't give a container by default, at the same path inside: in an
# engine in a user namespace (the rig's, in an LXC container) runc bind-mounts a device from its path in the container,
# so one mapped elsewhere (/dev/zero:/dev/e2e-zero) can't be made at all.
DEVICE = ("/dev/tty1", "/dev/tty1")
MEMORY = 256 * 1024 * 1024
NANO_CPUS = 500_000_000
PIDS = 200
SHM = 128 * 1024 * 1024
STOP = ("SIGINT", 5)
EXTRA_HOST = "printer.lan:192.168.50.20"
ULIMIT = ("nofile", 1024, 2048)
HEALTH_URL = "/health"
PRODUCTION_DATA = "written by production"
PREVIEW_DATA = "written by a preview"

BIND_LEFT_OUT = "Not mounted, because a preview never writes into production's folders: {folder} at /host-files."
DEVICE_LEFT_OUT = f"Not given the host's device, because a preview never takes production's: {DEVICE[0]}."
PRIVILEGED_LEFT_OUT = "Runs without privileged mode, because it would give the preview every device of the host, and a preview never takes production's."
FORK_LEFT_OUT = "Runs without added capabilities (NET_ADMIN), because the pull request comes from a fork and its code mustn't reach the host."
TAB_FOOT = ("Host folders aren't mounted, and host devices and privileged mode aren't passed in, so a preview never writes into "
            "production's files or takes its devices.")
FORKS_FOOT = "A pull request from a fork also runs without added capabilities, security options or GPUs."


def caps(names):
    """Capabilities by their names, as Docker may keep them (NET_ADMIN or CAP_NET_ADMIN)."""
    return sorted(c.upper().removeprefix("CAP_") for c in names or [])


def what_runs(inspect):
    """What a container runs with, of what a preview may copy: {setting: value}."""
    host, config = inspect["HostConfig"], inspect["Config"]
    return {
        "memory": host.get("Memory"), "cpus": host.get("NanoCpus"), "init": bool(host.get("Init")), "read-only root": host.get("ReadonlyRootfs"),
        "dropped capabilities": caps(host.get("CapDrop")), "added capabilities": caps(host.get("CapAdd")), "privileged": host.get("Privileged"),
        "devices": [(d.get("PathOnHost"), d.get("PathInContainer")) for d in host.get("Devices") or []],
        "host folders": sorted((m.get("Source"), m.get("Destination")) for m in inspect.get("Mounts") or [] if m.get("Type") == "bind"),
        "command": config.get("Cmd"), "label": (config.get("Labels") or {}).get(LABEL[0]), "restart": (host.get("RestartPolicy") or {}).get("Name"),
        "process limit": host.get("PidsLimit"), "shared memory": host.get("ShmSize"), "stop": (config.get("StopSignal"), config.get("StopTimeout")),
        "extra hosts": host.get("ExtraHosts") or [], "ulimits": [(u.get("Name"), u.get("Soft"), u.get("Hard")) for u in host.get("Ulimits") or []],
    }


# Settings that stay inside the container: a preview gets them all.
INSIDE = ("memory", "cpus", "process limit", "shared memory", "init", "read-only root", "dropped capabilities", "command", "label", "restart",
          "stop", "extra hosts", "ulimits")


def give_the_parent_everything(page, gitberth, app, folder):
    """The app's Settings tab: everything a preview could copy, then Apply. Returns ((the apply bar's title, its line), the
    deploy that makes it live)."""
    gitberth.open(page, f"/apps/{app.key}/settings")
    volumes = page.locator("#sec-volumes")
    volumes.wait_for()
    page.locator("#set-health-url").fill(HEALTH_URL)
    page.locator("#set-cmd").fill(" ".join(COMMAND))
    # A second named volume, and a host folder.
    row = add_row(volumes, "Named volume")
    row.locator("input[aria-label='Volume name']").fill("test-data")
    row.locator("input[aria-label='Path in the container']").fill("/test-data")
    row = add_row(volumes, "Host folder")
    row.locator("input[aria-label='Host folder']").fill(folder)
    row.locator("input[aria-label='Path in the container']").fill("/host-files")
    # Limits.
    resources = page.locator("#sec-resources")
    resources.locator("select[aria-label='Unit']").select_option("MiB")
    resources.locator("input[aria-label='Memory limit']").fill(str(MEMORY // (1024 * 1024)))
    resources.locator("input[aria-label='CPU limit']").fill("0.5")
    resources.locator("input[aria-label='Process limit']").fill(str(PIDS))
    resources.locator("input[aria-label='Shared memory']").fill(str(SHM // (1024 * 1024)))
    # An owner's label.
    labels = page.locator("#sec-labels")
    row = add_row(labels, "Add label")
    row.locator("input[aria-label='Label']").fill(LABEL[0])
    row.locator(".gb-envval input").fill(LABEL[1])
    # Advanced: init, privileged, a read-only root, capabilities, a device.
    advanced = page.locator("#sec-advanced")
    for name in ("Init process", "Privileged", "Read-only root"):
        switch = advanced.get_by_role("switch", name=name)
        assert switch.get_attribute("aria-checked") == "false", f"{name} is on already"
        switch.click()
        until(lambda: switch.get_attribute("aria-checked") == "true", 15, f"{name} to be on", seen=lambda: switch.get_attribute("aria-checked"))
    advanced.locator("input[aria-label='Capabilities added']").fill("NET_ADMIN")
    advanced.locator("input[aria-label='Capabilities dropped']").fill("MKNOD")
    advanced.locator("textarea[aria-label='Devices']").fill(f"{DEVICE[0]}:{DEVICE[1]}")
    # And more that stays inside the container: how it's stopped, extra hosts, a ulimit.
    advanced.locator("input[aria-label='Stop signal']").fill(STOP[0])
    advanced.locator("input[aria-label='Stop timeout']").fill(str(STOP[1]))
    advanced.locator("textarea[aria-label='Extra hosts']").fill(EXTRA_HOST)
    advanced.locator("textarea[aria-label='Ulimits']").fill(f"{ULIMIT[0]}={ULIMIT[1]}:{ULIMIT[2]}")
    return apply_settings(page, gitberth, app)


def close_snackbars(page):
    """Closes the snackbars shown (each with its close button, the mouse moved off them first), and waits for them to go."""
    shown = page.locator(".mud-snackbar")
    page.mouse.move(0, 0)
    for _ in range(shown.count()):
        with contextlib.suppress(PlaywrightTimeout):
            shown.first.locator("button").last.click(timeout=5_000)
    until(lambda: shown.count() == 0, 20, "the snackbars to close", seen=shown.all_inner_texts)


def overview(page, gitberth, key):
    """The Overview's Container card: its Volumes, Limits and Command."""
    gitberth.open(page, f"/apps/{key}")
    return {label: container_fact(page, label) for label in ("Volumes", "Limits", "Command")}


@pytest.fixture(scope="class")
def copied(gitberth, github, engine, standins):
    """The parent with everything (applied, and running with it), previews of pull requests and branches with forks on,
    then a commit on a new branch test and a fork's pull request: both previewed and deployed. Production writes a file
    into each of its volumes and its host folder."""
    with own_apps(gitberth, standins, "copying") as (page, made):
        repo, app, first = new_app(page, gitberth, github, made.track, unique("cpy"))
        folder = f"/tmp/gbe2e-{app.slug}-files"
        bar, applied_id = give_the_parent_everything(page, gitberth, app, folder)
        applied = wait_final(gitberth, applied_id)

        mark = standins.seq()
        said = gitberth.set_previews(page, app, mode="Pull requests and branches", forks=True)
        assert said.startswith("Saved"), said
        repo.wait_pulls_listed(since=mark, timeout=90)
        branch_sha = repo.commit({"marker.txt": "the branch test"}, branch="test", message="Work on test")["sha"]
        fork = repo.fork(FORKER)
        fork_n, fork_sha = open_pull(repo, "from-fork", "From a fork", marker="from a fork", fork=fork)
        branch = gitberth.wait_preview(app.id, branch="test", timeout=90)
        forked = gitberth.wait_preview(app.id, pr=fork_n, timeout=90)
        previews = {}
        for key, row, sha in (("branch", branch, branch_sha), ("fork", forked, fork_sha)):
            deploy = wait_finished(gitberth, row["Id"], sha)
            previews[key] = SimpleNamespace(row=row, deploy=deploy, container=f"gb-{row['Slug']}", label="test" if key == "branch" else f"PR #{fork_n}")

        url = gitberth.lan_url(app.id)
        wrote = {}
        for path in ("/data/production.txt", "/test-data/production.txt", "/host-files/production.txt"):
            try:
                write_file(url, path, PRODUCTION_DATA.encode())
                wrote[path] = True
            except AssertionError as e:
                wrote[path] = str(e)
        yield SimpleNamespace(repo=repo, app=app, folder=folder, bar=bar, applied=applied, previews=previews, fork_n=fork_n, url=url, wrote=wrote)


def preview_deployed(w, gitberth, key):
    p = w.previews[key]
    assert p.deploy["Status"] == "Succeeded", (f"{p.label}'s preview's deploy #{p.deploy['Id']} ended {p.deploy['Status']}: {p.deploy['Error']}\n"
                                               f"--- the end of its log ---\n{gitberth.deploy_log(p.deploy['Id'], tail=40)}")
    return p


class TestWhatAPreviewCopies:
    def test_production_runs_with_everything(self, copied, engine, gitberth):
        w = copied
        title, line = w.bar
        assert re.fullmatch(r"\d+ unsaved changes", title) and "Applies with a deploy without a build" in line, w.bar
        assert w.applied["Status"] == "Succeeded" and w.applied["Trigger"] == "ConfigChange", \
            f"applying the parent's settings: {w.applied}\n{gitberth.deploy_log(w.applied['Id'], tail=40)}"
        runs = what_runs(engine.inspect(w.app.container))
        expected = {"memory": MEMORY, "cpus": NANO_CPUS, "process limit": PIDS, "shared memory": SHM, "init": True, "read-only root": True,
                    "dropped capabilities": ["MKNOD"], "added capabilities": ["NET_ADMIN"], "privileged": True, "devices": [DEVICE],
                    "host folders": [(w.folder, "/host-files")], "command": COMMAND, "label": LABEL[1], "stop": STOP, "ulimits": [ULIMIT]}
        assert {k: runs[k] for k in expected} == expected, f"production runs with {runs}"
        # Docker keeps an extra host as host:ip or host=ip.
        assert [h.replace("=", ":") for h in runs["extra hosts"]] == [EXTRA_HOST], runs["extra hosts"]
        assert engine.exec(w.app.container, "sh", "-c", f"test -e {DEVICE[1]} && echo there || echo absent").strip() == "there", \
            f"production doesn't have the device {DEVICE[1]}"
        assert gitberth.db.app(w.app.slug)["HealthCheckPath"] == HEALTH_URL
        assert sorted(volume_rows(gitberth, w.app.id)) == sorted([("data", "/data", False), ("test-data", "/test-data", False), (w.folder, "/host-files", False)])
        assert all(v is True for v in w.wrote.values()), f"production couldn't write into its volumes and host folder: {w.wrote}"

    @pytest.mark.parametrize("key", ["branch", "fork"])
    def test_a_preview_gets_the_settings_that_stay_in_its_container(self, copied, engine, gitberth, key):
        w = copied
        p = preview_deployed(w, gitberth, key)
        production, preview = what_runs(engine.inspect(w.app.container)), what_runs(engine.inspect(p.container))
        assert {k: preview[k] for k in INSIDE} == {k: production[k] for k in INSIDE}, \
            f"{p.label}'s preview runs with {preview}, production with {production}"
        assert gitberth.db.app(p.row["Slug"])["HealthCheckPath"] == HEALTH_URL
        texts = log_texts(gitberth, p.deploy["Id"])
        checked = [x for x in texts if re.fullmatch(rf"==> Checking http://\S+{re.escape(HEALTH_URL)}", x)]
        assert checked and "Health check OK (200)" in texts, \
            f"{p.label}'s preview's deploy didn't check the health URL {HEALTH_URL}:\n" + "\n".join(texts[-30:])

    def test_the_repos_own_branch_keeps_the_added_capabilities(self, copied, engine, gitberth):
        w = copied
        p = preview_deployed(w, gitberth, "branch")
        assert what_runs(engine.inspect(p.container))["added capabilities"] == ["NET_ADMIN"]
        assert not [x for x in log_texts(gitberth, p.deploy["Id"]) if "added capabilities" in x], "the branch's preview left out the added capabilities"

    @pytest.mark.parametrize("key", ["branch", "fork"])
    def test_no_preview_gets_host_folders_devices_or_privileged_mode(self, copied, engine, gitberth, key):
        w = copied
        p = preview_deployed(w, gitberth, key)
        runs = what_runs(engine.inspect(p.container))
        assert (runs["privileged"], runs["devices"], runs["host folders"]) == (False, [], []), \
            f"{p.label}'s preview runs with privileged={runs['privileged']}, devices {runs['devices']}, host folders {runs['host folders']}"
        # Nothing of them inside it either.
        assert read_file(gitberth.lan_url(p.row["Id"]), "/host-files/production.txt")[0] == 404, "the preview reads production's host folder"
        assert engine.exec(p.container, "sh", "-c", f"test -e {DEVICE[1]} && echo there || echo absent").strip() == "absent", \
            f"the preview has the device {DEVICE[1]}"
        # Its settings, copied from the parent's, say the same.
        row = gitberth.db.app(p.row["Slug"])
        extras = runtime_extras(row)
        assert not extras.get("privileged") and not extras.get("devices"), f"{p.label}'s preview's settings: {extras}"
        assert [v for v in volume_rows(gitberth, p.row["Id"]) if v[0].startswith("/")] == [], volume_rows(gitberth, p.row["Id"])

    def test_a_forks_preview_doesnt_get_the_added_capabilities_either(self, copied, engine, gitberth):
        w = copied
        p = preview_deployed(w, gitberth, "fork")
        assert what_runs(engine.inspect(p.container))["added capabilities"] == []
        assert not runtime_extras(gitberth.db.app(p.row["Slug"])).get("capAdd")

    @pytest.mark.parametrize("key", ["branch", "fork"])
    def test_its_deploy_log_lists_each_item_left_out(self, copied, page, gitberth, key):
        w = copied
        p = w.previews[key]
        wanted = [BIND_LEFT_OUT.format(folder=w.folder), DEVICE_LEFT_OUT, PRIVILEGED_LEFT_OUT] + ([FORK_LEFT_OUT] if key == "fork" else [])
        texts = log_texts(gitberth, p.deploy["Id"])
        step = f"==> A preview of {w.app.name} ({p.label}): its settings are {w.app.name}'s, and so are its variables for previews"
        assert step in texts, "\n".join(texts[:20])
        missing = [x for x in wanted if x not in texts]
        assert not missing, f"{p.label}'s preview's deploy log doesn't list {missing}; it says:\n" + "\n".join(texts[texts.index(step):][:12])
        # Right under what the preview is, on the deploy log page too.
        after = texts[texts.index(step) + 1:texts.index(step) + 1 + len(wanted)]
        assert sorted(after) == sorted(wanted), after
        gitberth.open(page, f"/apps/{p.row['Id']}/deployments/{p.deploy['Id']}")
        until(lambda: all(x in log_lines(page) for x in wanted), 30, "the deploy log page to list what the preview left out",
              seen=lambda: log_lines(page)[:16])
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    @pytest.mark.parametrize("key", ["branch", "fork"])
    def test_its_page_says_the_host_folder_isnt_mounted(self, copied, page, gitberth, key):
        w = copied
        p = preview_deployed(w, gitberth, key)
        gitberth.open(page, f"/apps/{p.container}")
        banner = page.locator(".gb-banner", has_text="Preview of")
        banner.wait_for()
        assert text_of(banner.locator(".gb-banner-title")) == f"Preview of {w.app.name} for {'the branch test' if key == 'branch' else p.label}"
        assert text_of(banner.locator(".gb-banner-sub")).startswith(
            f"Its settings come from {w.app.name}, and so do its variables for previews. {w.app.name}'s host folder ({w.folder}) isn't mounted: "
            "a preview never writes into production's files."), text_of(banner.locator(".gb-banner-sub"))
        assert gitberth.tabs(page) == ["Overview", "Network", "Logs", "Activity"]
        # Its Overview: its own volumes and no host folder, and the parent's limits and command.
        mine, parents = overview(page, gitberth, p.container), overview(page, gitberth, w.app.key)
        slug = p.row["Slug"]
        assert sorted(mine["Volumes"]) == sorted([f"gb-{slug}-data /data volume", f"gb-{slug}-test-data /test-data volume"]), mine["Volumes"]
        assert any("host folder" in v for v in parents["Volumes"]), parents["Volumes"]
        assert (mine["Limits"], mine["Command"]) == (parents["Limits"], parents["Command"]), {"preview": mine, "production": parents}
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_the_previews_tab_says_what_previews_never_get(self, copied, page, gitberth):
        w = copied
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        for label in ("test", f"PR #{w.fork_n}"):
            pill = preview_row(page, label).locator(".gb-pill2")
            pill.wait_for()
            until(lambda: text_of(pill) == "Ready", 60, f"{label}'s row to say Ready", seen=lambda: text_of(pill))
        foot = text_of(previews_card(page).locator(".gb-card-foot"))
        assert TAB_FOOT in foot and FORKS_FOOT in foot, foot
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    @pytest.mark.parametrize("key", ["branch", "fork"])
    def test_its_named_volumes_are_its_own(self, copied, engine, gitberth, key):
        w = copied
        p = preview_deployed(w, gitberth, key)
        slug = p.row["Slug"]
        own = {"/data": f"gb-{slug}-data", "/test-data": f"gb-{slug}-test-data"}
        assert volume_mounts(engine.inspect(p.container)) == own, volume_mounts(engine.inspect(p.container))
        assert sorted(volume_rows(gitberth, p.row["Id"])) == [("data", "/data", False), ("test-data", "/test-data", False)]
        assert all(engine.volume_exists(v) for v in own.values()), engine.volumes(prefix=f"gb-{slug}-")
        production = volume_mounts(engine.inspect(w.app.container))
        assert production == {"/data": f"gb-{w.app.slug}-data", "/test-data": f"gb-{w.app.slug}-test-data"}, production
        assert not set(own.values()) & set(production.values()), f"the preview shares production's volumes: {own} {production}"
        # Empty when it started: none of production's files.
        url = gitberth.lan_url(p.row["Id"])
        for path in ("/data/production.txt", "/test-data/production.txt"):
            assert read_file(url, path)[0] == 404, f"{p.label}'s preview reads production's {path}"

    # Last: it writes into the branch's preview.
    def test_the_branch_test_gets_another_name_and_never_shares_productions_volume(self, copied, engine, gitberth):
        w = copied
        p = preview_deployed(w, gitberth, "branch")
        production_test_data = f"gb-{w.app.slug}-test-data"
        assert p.row["Slug"] != f"{w.app.slug}-test", f"the branch test's preview is {p.row['Slug']}: its data would be {production_test_data}"
        assert p.row["Name"] == f"{w.app.name} · test" and p.row["PreviewBranch"] == "test", p.row
        mounted = volume_mounts(engine.inspect(p.container))
        assert production_test_data not in mounted.values(), f"the branch test's preview mounts production's test-data: {mounted}"
        # Both ways: what each writes, the other never reads.
        url = gitberth.lan_url(p.row["Id"])
        write_file(url, "/data/preview.txt", PREVIEW_DATA.encode())
        write_file(url, "/test-data/preview.txt", PREVIEW_DATA.encode())
        for path in ("/data/preview.txt", "/test-data/preview.txt"):
            assert read_file(w.url, path)[0] == 404, f"production reads the preview's {path}"
        assert read_file(w.url, "/test-data/production.txt") == (200, PRODUCTION_DATA), "production's test-data lost its file"
        assert read_file(url, "/data/production.txt")[0] == 404 and read_file(url, "/test-data/production.txt")[0] == 404


# ---- the other way round: the preview first, then the parent's volume ----

def test_a_parent_volume_named_like_a_previews_is_refused(page, gitberth, github, engine, apps, uniq, standins):
    repo, app, _ = new_app(page, gitberth, github, apps.track, uniq("clb"))
    mark = standins.seq()
    said = gitberth.set_previews(page, app, mode="Pull requests and branches")
    assert said.startswith("Saved"), said
    repo.wait_pulls_listed(since=mark, timeout=90)
    sha = repo.commit({"marker.txt": "the branch test"}, branch="test", message="Work on test")["sha"]
    preview = gitberth.wait_preview(app.id, branch="test", timeout=90)
    wait_deployed(gitberth, preview["Id"], sha)
    # With only data, nothing collides: the preview is <app>-test, its data gb-<app>-test-data.
    assert preview["Slug"] == f"{app.slug}-test", preview["Slug"]
    taken = f"gb-{app.slug}-test-data"
    assert volume_mounts(engine.inspect(f"gb-{preview['Slug']}")) == {"/data": taken}
    production_before = engine.inspect(app.container)
    rows_before = volume_rows(gitberth, app.id)
    deploys_before = gitberth.db.last_deployment_id(app.id)

    # The parent adds a volume test-data: gb-<app>-test-data, the preview's. Saving it is refused, and says why.
    gitberth.open(page, f"/apps/{app.key}/settings")
    volumes = page.locator("#sec-volumes")
    volumes.wait_for()
    row = add_row(volumes, "Named volume")
    row.locator("input[aria-label='Volume name']").fill("test-data")
    row.locator("input[aria-label='Path in the container']").fill("/test-data")
    wait_for_plan(page)
    save = apply_bar(page).get_by_role("button", name="Save for next deploy")
    until(save.is_enabled, 30, "Save for next deploy to be offered", seen=lambda: text_of(apply_bar(page)))
    save.click()
    said = gitberth.snackbar(page, "would be")
    wanted = f"The volume test-data would be {taken}, which is also the preview {app.name} · test's. Give it another name, or remove that preview first."
    assert re.sub(r"\s+", " ", said).strip() == wanted, said
    # Nothing was saved: the parent's volumes, its container and the preview's volume are as they were; the edit waits.
    assert volume_rows(gitberth, app.id) == rows_before, volume_rows(gitberth, app.id)
    assert gitberth.db.last_deployment_id(app.id) == deploys_before, "refusing the volume deployed production"
    assert not gitberth.db.app(app.slug)["ConfigChangePending"], "the refused volume is pending for the next deploy"
    now = engine.inspect(app.container)
    assert now["Id"] == production_before["Id"] and taken not in volume_mounts(now).values(), volume_mounts(now)
    assert volume_mounts(engine.inspect(f"gb-{preview['Slug']}")) == {"/data": taken}
    assert text_of(apply_bar(page).locator(".gb-applybar-title")) == "1 unsaved change"
    assert ("test-data", "/test-data", "volume") in settings_volume_rows(page)
    # The snackbar sits over the apply bar's buttons (and stays while the mouse is on it): closed with its ✕.
    close_snackbars(page)
    apply_bar(page).get_by_role("button", name="Discard").click()
    until(lambda: apply_bar(page).count() == 0, 15, "Discard to drop the edit")
    assert settings_volume_rows(page) == [(f"gb-{app.slug}-data", "/data", "volume")], settings_volume_rows(page)
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"
