"""Scenario 44: Gitberth updates itself (Settings › Updates › Gitberth, src/Gitberth/Images/UpdateGitberth*.cs).

- The rig's Gitberth runs gitberth:e2e, which counts as its published image there (GITBERTH_UPDATE_IMAGE=gitberth in
  compose.e2e.yml). The scenario builds gitberth:9.9.1, the same build saying it's 9.9.1 (GITBERTH_TEST_VERSION, which
  only the rig's timings let it say, and the image's version label), and the GitHub stand-in names v9.9.1 Gitberth's
  newest release. Check now shows it with its notes; Update to 9.9.1 pulls (Docker Hub has no such image, so the host's is
  used), copies Gitberth's data, and starts the updater, which swaps the containers: the page comes back by itself on
  9.9.1 and says Updated to 9.9.1. A container of the scenario's own kept running throughout, the choice made before the
  update (Do nothing) is still there, and the copy of the data is kept as gitberth-data-before-<the e2e version>.
- A broken 9.9.2 (it exits as it starts, but runs the updater): Update to 9.9.2 starts it, it stops, and the updater puts
  9.9.1 back with its data as it was; the card says why.
- At the end Gitberth goes back to gitberth:e2e the same way (the updater, run by the scenario), and what the scenario
  made goes: its container, the two images, the copies of the data.

About 3 minutes: three swaps of Gitberth's container.
"""
import json
import os
import secrets
from datetime import datetime, timezone
from types import SimpleNamespace

import pytest

from e2e import config
from e2e.engine import docker
from e2e.net import get
from e2e.wait import until

OWNER, REPO = "johnhowelljr", "Gitberth"
GOOD, BROKEN = "9.9.1", "9.9.2"
ALPINE = "alpine:3.21"
SELF = config.GITBERTH_CONTAINER


def flat(text):
    return " ".join((text or "").split())


def dockerfile(version, broken=False):
    """The rig's Gitberth as a newer version: the same build, its version said by GITBERTH_TEST_VERSION and its label. A
    broken one runs the updater (--self-update) and nothing else: it exits with code 3 as it starts."""
    lines = ["FROM gitberth:e2e", f"ENV GITBERTH_TEST_VERSION={version}", f"LABEL org.opencontainers.image.version={version}"]
    if broken:
        script = ('if [ "$1" = --self-update ]; then exec dotnet Gitberth.dll "$@"; fi; '
                  f'echo "Gitberth {version} refuses to start: a broken build of the rig." >&2; exit 3')
        lines.append("ENTRYPOINT " + json.dumps(["/bin/sh", "-c", script, "sh"]))
    return "\n".join(lines) + "\n"


def version(gitberth):
    """The version /healthz answers with; None while Gitberth doesn't answer."""
    status, text = get(gitberth.url("/healthz"), timeout=3)
    if status != 200:
        return None
    try:
        return json.loads(text)["version"]
    except (ValueError, KeyError):
        return None


def release(standins, tag, notes):
    standins.api("PUT", f"/github/releases/{OWNER}/{REPO}/latest", {"tag": tag, "name": f"Gitberth {tag[1:]}", "body": notes})


def card(gitberth, page):
    """Settings › Updates › Gitberth, once its status has loaded."""
    gitberth.open(page, "/settings#gitberth-update")
    section = page.locator("#gitberth-update")
    section.locator("#gu-current").wait_for(timeout=30_000)
    return section


def check_now(section, wanted):
    section.get_by_role("button", name="Check now").click()
    until(lambda: wanted in flat(section.locator("#gu-latest").inner_text()), 30, f"the card to say {wanted!r}",
          seen=lambda: flat(section.locator("#gu-latest").inner_text()))


def text_of(page, selector):
    try:
        return flat(page.locator(selector).inner_text(timeout=1_000))
    except Exception:  # noqa: BLE001 - the page is loading again, or isn't there yet
        return ""


def updaters(engine):
    return [c["Names"] for c in engine.containers(label="gitberth.helper=update")]


def go_back(engine, gitberth, w):
    """Gitberth back on gitberth:e2e: the updater of that image, run by the scenario with a plan of its own."""
    me = engine.inspect(SELF)
    if me is None or me["Config"]["Image"] == "gitberth:e2e":
        return
    network = me["HostConfig"]["NetworkMode"]
    plan = {
        "container": me["Id"], "name": SELF, "image": "gitberth:e2e", "from": version(gitberth) or GOOD, "to": w.e2e_version,
        "copyVolume": w.copy, "network": network, "port": 8080, "by": "the end-to-end rig",
        "at": datetime.now(timezone.utc).isoformat(),
    }
    os.makedirs(os.path.join(config.GBDATA, "update"), exist_ok=True)
    with open(os.path.join(config.GBDATA, "update", "plan.json"), "w", encoding="utf-8") as f:
        json.dump(plan, f)
    name = f"gbe2e-su-back-{w.tag}"
    docker("rm", "-fv", name, check=False)
    docker("run", "-d", "--name", name, "--label", "gitberth.helper=update", "--user", "0:0", "--network", network,
           "-v", "gitberth_gitberth-data:/data", "-v", f"{w.copy}:/copy", "-v", "/var/run/docker.sock:/var/run/docker.sock",
           "gitberth:e2e", "--self-update", "/data/update/plan.json")
    code = docker("wait", name, timeout=300).strip()
    said = engine.logs(name, tail=20)
    docker("rm", "-fv", name, check=False)
    assert code == "0", f"going back to gitberth:e2e failed ({code}): {said}"
    until(lambda: version(gitberth) == w.e2e_version, 120, "Gitberth to answer as gitberth:e2e again", interval=2)


@pytest.fixture(scope="module")
def world(gitberth, engine, standins):
    """The newer and the broken version's images, a container of the scenario's own, and Gitberth's version before."""
    tag = secrets.token_hex(2)
    w = SimpleNamespace(tag=tag, name=f"gbe2e-su-{tag}", e2e_version=version(gitberth), copy=None, started=None)
    assert w.e2e_version, "Gitberth doesn't answer /healthz"
    w.copy = f"gitberth-data-before-{w.e2e_version}"
    docker("build", "-q", "-t", f"gitberth:{GOOD}", "-", input=dockerfile(GOOD), timeout=300)
    docker("build", "-q", "-t", f"gitberth:{BROKEN}", "-", input=dockerfile(BROKEN, broken=True), timeout=300)
    if not engine.image_exists(ALPINE):
        docker("pull", "-q", ALPINE, timeout=300)
    docker("run", "-d", "--name", w.name, ALPINE, "sleep", "36000")
    w.started = engine.wait_running(w.name)["State"]["StartedAt"]
    try:
        yield w
    finally:
        try:
            standins.api("DELETE", f"/github/releases/{OWNER}/{REPO}/latest")
        except Exception as e:  # noqa: BLE001 - cleaning up never hides a scenario's own failure
            print(f"[e2e] couldn't clear the stand-in's release after group 44: {e}")
        try:
            go_back(engine, gitberth, w)
        except Exception as e:  # noqa: BLE001
            print(f"[e2e] couldn't put Gitberth back on gitberth:e2e after group 44: {e}")
        for c in [w.name, *updaters(engine), *engine.container_names(prefix="gitberth-before-")]:
            docker("rm", "-fv", c, check=False)
        docker("image", "rm", "-f", f"gitberth:{GOOD}", f"gitberth:{BROKEN}", check=False)
        for v in engine.volumes(prefix="gitberth-data-before-"):
            docker("volume", "rm", "-f", v, check=False)
        # Tell me again, as a fresh Gitberth has it.
        page = gitberth.new_page()
        try:
            card(gitberth, page).get_by_role("radio", name="Tell me").click()
        except Exception as e:  # noqa: BLE001
            print(f"[e2e] couldn't set Settings › Updates › Gitberth back to Tell me after group 44: {e}")
        finally:
            page.context.close()


def test_gitberth_updates_itself_to_a_newer_version(page, gitberth, engine, standins, world):
    release(standins, f"v{GOOD}", "## What's new\n- The rig's newer Gitberth.\n<script>alert(1)</script>")
    section = card(gitberth, page)
    assert f"This Gitberth runs {world.e2e_version}" in flat(section.locator("#gu-current").inner_text())
    # A choice made before the update, which the new version keeps.
    section.get_by_role("radio", name="Do nothing").click()
    check_now(section, f"Gitberth {GOOD} is out")
    notes = section.locator("#gu-notes")
    notes.locator("summary").click()
    assert "The rig's newer Gitberth." in flat(notes.inner_text())
    # Raw HTML in the notes is shown as text, never run.
    assert notes.locator("script").count() == 0 and "<script>alert(1)</script>" in flat(notes.inner_text())

    section.get_by_role("button", name=f"Update to {GOOD}").click()
    until(lambda: "Gitberth is updating; this page comes back by itself." in text_of(page, "#gu-steps"), 120,
          "the steps to say the updater took over", seen=lambda: text_of(page, "#gu-steps"))
    until(lambda: version(gitberth) == GOOD, 240, f"Gitberth to answer as {GOOD}", interval=2)
    # The page comes back by itself, on the new version.
    until(lambda: text_of(page, "#gu-result").startswith(f"Updated to {GOOD}."), 120, "the card to say it's updated",
          seen=lambda: text_of(page, "#gitberth-update"))
    assert f"This Gitberth runs {GOOD}" in text_of(page, "#gu-current")
    assert page.locator("#gitberth-update").get_by_role("radio", name="Do nothing").is_checked()

    assert engine.image_of(SELF) == f"gitberth:{GOOD}"
    assert engine.container_names(prefix="gitberth-before-") == []
    until(lambda: updaters(engine) == [], 60, "the updater to be gone", seen=lambda: updaters(engine))
    assert engine.volume_exists(world.copy), engine.volumes(prefix="gitberth-data-before-")
    # The scenario's container never stopped.
    assert engine.inspect(world.name)["State"]["StartedAt"] == world.started


def test_a_version_that_doesnt_start_is_undone(page, gitberth, engine, standins, world):
    if version(gitberth) != GOOD:
        pytest.skip(f"needs Gitberth on {GOOD} (the scenario before)")
    release(standins, f"v{BROKEN}", "- A broken build.")
    section = card(gitberth, page)
    check_now(section, f"Gitberth {BROKEN} is out")
    section.get_by_role("button", name=f"Update to {BROKEN}").click()

    why = (f"The update to {BROKEN} failed and Gitberth went back to {GOOD}: It stopped right after it started (exit code 3): "
           f"Gitberth {BROKEN} refuses to start: a broken build of the rig.")
    until(lambda: text_of(page, "#gu-result").startswith(why), 240, "the card to say the update was undone",
          seen=lambda: text_of(page, "#gitberth-update"))
    assert version(gitberth) == GOOD
    assert engine.image_of(SELF) == f"gitberth:{GOOD}"
    assert engine.container_names(prefix="gitberth-before-") == []
    until(lambda: updaters(engine) == [], 60, "the updater to be gone", seen=lambda: updaters(engine))
    # Its data as it was: the choice made before is still there, and so is the copy taken before this try.
    assert page.locator("#gitberth-update").get_by_role("radio", name="Do nothing").is_checked()
    assert engine.volume_exists(f"gitberth-data-before-{GOOD}")
    assert engine.inspect(world.name)["State"]["StartedAt"] == world.started
