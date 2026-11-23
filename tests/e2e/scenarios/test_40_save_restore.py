"""Scenario 40: Settings › Save and restore, saved on one Gitberth and restored onto a wiped one (PLAN.md M5).

- Settings › Save and restore lists what can be saved, Gitberth's data always ticked, and offers Save backup only for a
  passphrase of 12 characters typed the same twice, saying what's wrong under each field.
- A host with a Git app (a marker in its volume, a secret variable, a Postgres database of its own with a row in it), the
  Postgres server it's on, and an app from an image (redis, with a key saved in its volume) is saved through the page:
  the browser downloads one file, as it's made. The rig's own containers and volumes are unticked (pausing them for their
  copy would pause the runner itself). The file is encrypted: none of the marker, the secret, the row or the apps' names
  is in it.
- The host is wiped as a new machine would be: Gitberth stopped, its data volume emptied, its apps' containers, volumes
  and built images removed; Gitberth starts afresh, with new keys. A commit is pushed meanwhile, so the restore must
  deploy the commit the app ran, not the branch's tip.
- The fresh Gitberth's dashboard offers a restore. The file is uploaded, refused with a wrong passphrase, opened with its
  own (its summary, with the note about signing in), and restored once restore is typed. The Restoring page comes back by
  itself across Gitberth's restart and asks to sign in; the sign-in returns to it, and it ends Restored.
- Everything is back as it was: the same app ids, the marker in the volume, the secret in the app's environment (read
  with the saved keys), the row through the app's own DATABASE_URL, redis's key, the same host port answering, and the
  restore's deploy at the saved commit. The uploaded file is gone, the restore's log holds no secret, and the dashboard
  offers no restore any more.
- Picked items, into a Gitberth with other apps (plan2 P8): an app saved (a Git app with a volume marker, a secret and a
  database row, Deploy on push off) and then deleted with its volumes and its database comes back picked from the
  backup, with its data, the same host port and the saved commit, though the branch moved on; no restart. Then the same
  app restored as a copy <name>2 next to it: both answer, the copy on another port, with the data and a database of
  its own.

About 4 minutes for the whole restore and 4 for the picked ones with the rig's test timings: one Git build before each
save and one per restore, Postgres made once per rig volume. What they seeded is removed at the end; a Postgres server
they found stays.
"""
import os
import re
import secrets
import shutil
import tempfile
from types import SimpleNamespace
from urllib.parse import parse_qs, urlsplit

import pytest
from playwright.sync_api import TimeoutError as PlaywrightTimeout

from e2e import config
from e2e.engine import docker
from e2e.gitberth import App
from e2e.net import call, get, get_json
from e2e.wait import until
from test_09_scoped_variables import PRODUCTION_TAKES, add_variable, open_environment, wait_bar
from test_10_build_time_secrets import apply_and_follow
from test_19_declared_volumes import add_row
from test_27_databases import SERVER, add_database, create_server, remove_server, url_parts, wait_live
from test_29_mcp import make_token, tool

# Test values that look like nothing real (R3): the passphrase, and the secret variable's value.
PASSPHRASE = "tok-9f8e7d6c5b4a"
SECRET_NAME = "E2E_BR_SECRET"
SECRET_VALUE = "s3cr3t-api-key"
REDIS = "redis:8-alpine"
# The file's first bytes: its header is plain, and starts with the format's magic.
MAGIC = b"GBAK\r\n\x1a\n"


def flat(text):
    return " ".join((text or "").split())


def open_section(gitberth, page):
    """Settings › Save and restore, once its list shows Gitberth's data."""
    gitberth.open(page, "/settings#backup")
    section = page.locator("#backup")
    section.wait_for()
    section.locator(".gb-bk-list li[data-item='gitberth']").wait_for(timeout=60_000)
    return section


def save_rows(section):
    """The save list's rows: (id, parent id or None, ticked, can be changed)."""
    return section.locator(".gb-bk-list li[data-item]").evaluate_all(
        """els => els.map(e => { const c = e.querySelector('input[type=checkbox]');
               return [e.dataset.item, e.dataset.parent || null, !!(c && c.checked), !!(c && !c.disabled)]; })""")


def keep_only(section, wanted):
    """Unticks every top-level row but the wanted items (what's under a row follows it); returns the ids left ticked."""
    for item, parent, ticked, free in save_rows(section):
        if parent is None and ticked and free and item not in wanted:
            section.locator(f".gb-bk-list li[data-item='{item}'] input[type=checkbox]").uncheck()
            # Its children are unticked by the page's next render: wait for it, so no click races it (a click on a child
            # the render just unticked would tick it, and its parent, again).
            until(lambda: not any(t for _, p, t, _ in save_rows(section) if p == item), 15, f"what's under {item} to follow it",
                  seen=lambda: [r for r in save_rows(section) if r[1] == item])
    return {item for item, _, ticked, _ in save_rows(section) if ticked}


def restoring_text(page):
    """The Restoring page as it stands, for a failure's message: its title, its steps and the end of its log."""
    try:
        def text(selector):
            return page.locator(selector).first.inner_text() if page.locator(selector).count() else ""
        log = text(".gb-terminal-body").splitlines()[-30:]
        return f"{page.url} | {text('h1.gb-display')} | {flat(text('#restore-steps'))[:800]} | log: {' / '.join(log)}"
    except Exception as e:  # noqa: BLE001 - a page mid-navigation
        return f"{page.url} (couldn't read it: {e})"


def wipe(gitberth):
    """A new machine, as far as Gitberth and its apps go: Gitberth stopped, its data volume emptied, its apps' containers,
    volumes and built images removed (base images stay, for speed); then Gitberth started again, afresh with new keys and
    the rig's admin password."""
    docker("stop", "-t", "30", config.GITBERTH_CONTAINER, timeout=120)
    ids = docker("ps", "-aq", "--filter", "label=gitberth.managed=true").split()
    # Gitberth's own helpers too, never the rig's (those carry gbe2e.part).
    helpers = [line.split()[0] for line in docker("ps", "-a", "--filter", "label=gitberth.helper", "--format", '{{.ID}} {{.Label "gbe2e.part"}}').splitlines()
               if len(line.split()) == 1]
    if ids + helpers:
        docker("rm", "-f", *(ids + helpers), timeout=180)
    images = [r for r in docker("image", "ls", "--format", "{{.Repository}}:{{.Tag}}").split() if r.startswith("gitberth/")]
    if images:
        docker("rmi", "-f", *images, timeout=180)
    volumes = [v for v in docker("volume", "ls", "-q").split() if v.startswith("gb-")]
    if volumes:
        docker("volume", "rm", "-f", *volumes, timeout=180)
    for entry in os.listdir(config.GBDATA):
        path = os.path.join(config.GBDATA, entry)
        if os.path.isdir(path) and not os.path.islink(path):
            shutil.rmtree(path)
        else:
            os.remove(path)
    docker("start", config.GITBERTH_CONTAINER)
    gitberth.wait_up()
    # A fresh Gitberth: keys of its own (the session's cookies don't open it), and no apps.
    gitberth._signed_in = None
    until(lambda: gitberth.db.apps() == [], 60, "the fresh Gitberth to have no apps", seen=lambda: [a["Slug"] for a in gitberth.db.apps()])


def upload_and_open(page, path):
    """Restore from a file: the file uploaded, a wrong passphrase refused (nothing opened), its own passphrase opening
    it. Returns the summary's text."""
    section = page.locator("#backup")
    # A file left there before (an earlier restore's, or a run that stopped) goes first, so this one is the one opened.
    left = section.get_by_role("button", name="Remove the file")
    if left.count():
        left.first.click()
        section.locator(".gb-bk-picker:not([hidden])").wait_for(timeout=30_000)
    page.locator("#gb-restore-file").set_input_files(path)
    uploaded = section.locator("#bk-uploaded")
    until(lambda: uploaded.count() and "is uploaded" in uploaded.inner_text(), 300, "the file to be uploaded",
          seen=lambda: flat(section.inner_text())[-600:])
    box = section.locator("#bk-restore-pass")
    box.fill("not the passphrase")
    section.get_by_role("button", name="Open the file").click()
    refused = section.locator("#bk-restore-pass ~ .gb-field-error, .gb-banner.gb-tone-critical")
    until(lambda: refused.count(), 120, "a wrong passphrase to be refused", seen=lambda: flat(section.inner_text())[-600:])
    assert section.locator("#bk-summary").count() == 0, "a wrong passphrase opened the file"
    box.fill(PASSPHRASE)
    section.get_by_role("button", name="Open the file").click()
    summary = section.locator("#bk-summary")
    until(lambda: summary.count(), 180, "the file's summary", seen=lambda: flat(section.inner_text())[-600:])
    return flat(summary.inner_text())


def through_the_restart(gitberth, engine, page, started, timeout=600):
    """Gitberth restarts on the saved data partway through. The Restoring page comes back by itself and asks to sign in
    (the saved machine's admin password: the rig's here too), and the sign-in returns to it."""
    until(lambda: engine.inspect(config.GITBERTH_CONTAINER)["State"]["StartedAt"] != started, timeout, "Gitberth to restart on the saved data",
          interval=2, seen=lambda: restoring_text(page))
    gitberth.wait_up()
    # Waited for through Playwright: the sync API only takes in the page's navigations while one of its calls runs.
    try:
        page.wait_for_url(lambda u: urlsplit(u).path == "/login", timeout=120_000)
    except PlaywrightTimeout:
        raise AssertionError(f"The Restoring page didn't come back by itself to ask for the sign-in: it's at {page.url}; "
                             f"browser errors: {gitberth.errors(page)[-5:]}") from None
    assert parse_qs(urlsplit(page.url).query).get("ReturnUrl") == ["/restoring"], page.url
    page.fill("#username", "admin")
    page.fill("#password", gitberth.password)
    with page.expect_navigation(timeout=30_000):
        page.click("button[type=submit]")
    assert urlsplit(page.url).path == "/restoring", f"signing in went to {page.url}, not back to the Restoring page"
    gitberth.wait_ready(page)
    # The restored Gitberth has the saved keys: a cookie made before doesn't count.
    gitberth._signed_in = None


def test_settings_offers_save_backup_only_for_a_passphrase_typed_twice(page, gitberth, scenario):
    gitberth.open(page, "/settings")
    assert page.locator(".gb-toc a[href='settings#backup']").inner_text().strip() == "Save and restore"
    section = open_section(gitberth, page)
    mine = section.locator(".gb-bk-list li[data-item='gitberth']")
    assert "Always saved" in flat(mine.inner_text()), mine.inner_text()
    box = mine.locator("input[type=checkbox]")
    assert box.is_checked() and box.is_disabled(), "Gitberth's data can be left out"
    assert "chosen:" in flat(section.locator("#bk-total").inner_text())
    assert section.get_by_role("switch", name="Stop everything while saving").get_attribute("aria-checked") == "false"

    save = section.get_by_role("button", name="Save backup")
    assert save.is_disabled(), "Save backup is offered without a passphrase"
    section.locator("#bk-pass").fill("too short")
    until(lambda: "At least 12 characters: 9 so far." in flat(section.inner_text()), 10, "the passphrase to be called too short")
    section.locator("#bk-pass").fill(PASSPHRASE)
    section.locator("#bk-pass-again").fill(PASSPHRASE + "x")
    until(lambda: "It isn't the same as the passphrase beside it." in flat(section.inner_text()), 10, "the two passphrases to differ")
    assert save.is_disabled(), "Save backup is offered for two passphrases that differ"
    section.locator("#bk-pass-again").fill(PASSPHRASE)
    until(save.is_enabled, 10, "Save backup to be offered for the same passphrase twice")
    assert "never keeps it" in flat(section.inner_text())
    # The restore's half is there, offered or saying why not.
    assert section.locator("#restore-from-file").inner_text().strip().startswith("Restore from a file")
    assert not [e for e in gitberth.errors(page) if e.startswith("page:")], gitberth.errors(page)


def test_a_saved_host_comes_back_on_a_wiped_gitberth(new_page, gitberth, github, engine, apps, uniq, baseline, scenario):
    folder = tempfile.mkdtemp(prefix="gbe2e-backup-")
    page = new_page()
    made_server = not engine.exists(SERVER)
    app = token = redis_key = None
    wiped = restored = False
    try:
        # ---- Seed: a Git app with a volume marker, a secret and a database row; its Postgres server; redis from an image.
        create_server(gitberth, page, engine)
        repo = github.create_repo(uniq("br"), files={"marker.txt": "save and restore v1"})
        app = apps.track(gitberth.create_git_app(page, repo.url))
        first = gitberth.wait_deploy(app.id, timeout=420)
        said = add_database(gitberth, page, app)
        assert said.startswith("Added the database"), said
        second = wait_live(gitberth, app, first["Id"])
        open_environment(page, gitberth, app)
        add_variable(page, SECRET_NAME, SECRET_VALUE)
        wait_bar(page, lambda line: line.startswith(PRODUCTION_TAKES), "the apply bar to say production takes the variable")
        third = apply_and_follow(page, gitberth)
        assert third["Status"] == "Succeeded" and third["Id"] > second["Id"], third
        [secret] = [v for v in gitberth.db.env_vars(app.id) if v["Key"] == SECRET_NAME]
        assert secret["IsSecret"] == 1, secret

        lan = gitberth.lan_url(app.id)
        marker = f"marker-{secrets.token_hex(4)}"
        assert call("POST", f"{lan}/file?path=/data/br-marker.txt", body=marker.encode())[0] == 200
        assert (get_json(f"{lan}/env") or {}).get(SECRET_NAME) == SECRET_VALUE
        url = engine.env(app.container)["DATABASE_URL"]
        row = f"row-{secrets.token_hex(4)}"
        engine.exec(SERVER, "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", url, "-c", f"CREATE TABLE notes (t text); INSERT INTO notes VALUES ('{row}');")

        if not engine.image_exists(REDIS):
            docker("pull", "-q", REDIS, timeout=300)
        token, _ = make_token(gitberth, page, uniq("br-admin"), "Admin")
        error, made = tool(gitberth, token, "create_app", {"source": "image", "image": REDIS, "name": uniq("br-rd"), "wait": True}, timeout=300)
        assert not error, made
        redis_key = made["key"]
        redis_row = until(lambda: gitberth.db.app_by_id(made["id"]), 30, "the redis app in Gitberth's database")
        redis = redis_row["Container"] or f"gb-{redis_row['Slug']}"
        engine.wait_running(redis)
        # redis:8-alpine declares no volume, and a backup carries data in volumes only: a named one at /data, through its
        # Settings tab and the change engine (named gb-…, so the wipe takes it).
        redis_volume = f"gb-{redis_row['Slug']}-data"
        gitberth.open(page, f"/apps/{redis}/settings")
        volumes = page.locator("#sec-volumes")
        volumes.wait_for()
        mount = add_row(volumes, "Named volume")
        mount.locator("input[aria-label='Volume name']").fill(redis_volume)
        mount.locator("input[aria-label='Path in the container']").fill("/data")
        bar = page.get_by_role("region", name="Unsaved changes")
        apply = bar.get_by_role("button", name=re.compile(r"^(Review and apply|Apply)$"))
        until(apply.is_enabled, 30, "Review and apply to be offered", seen=bar.inner_text)
        apply.click()
        dialog = page.locator(".mud-dialog")
        dialog.get_by_role("button", name=re.compile(r"^Recreate ")).click()
        dialog.get_by_role("button", name="Done").click(timeout=180_000)
        until(lambda: any(m.get("Name") == redis_volume and m.get("Destination") == "/data" for m in engine.mounts(redis)), 60,
              "redis's volume at /data", seen=lambda: engine.mounts(redis))
        engine.wait_running(redis)
        kept = f"kept-{secrets.token_hex(4)}"
        assert engine.exec(redis, "redis-cli", "SET", "e2e-br", kept).strip() == "OK"
        assert engine.exec(redis, "redis-cli", "SAVE").strip() == "OK"

        saved_sha = gitberth.db.app(app.slug)["CurrentCommit"]
        port = gitberth.db.web_port(app.id)["HostPort"]
        last_deploy = gitberth.db.last_deployment_id(app.id)
        server_row = gitberth.db.app(SERVER)
        # Gitberth's data, every app with what's under it, and the server's dump wherever the list puts it.
        wanted = {"gitberth", f"database:{SERVER}"} | {f"app:{a['Id']}" for a in gitberth.db.apps()}

        # ---- Save: Gitberth's data and the apps, the rig's own containers and volumes unticked.
        section = open_section(gitberth, page)
        chosen = keep_only(section, wanted)
        assert {"gitberth", f"app:{app.id}", f"app:{redis_row['Id']}", f"app:{server_row['Id']}"} <= chosen, chosen
        assert not [c for c in chosen if c.startswith(("container:gbe2e", "volume:gbe2e"))], chosen
        section.locator("#bk-pass").fill(PASSPHRASE)
        section.locator("#bk-pass-again").fill(PASSPHRASE)
        button = section.get_by_role("button", name="Save backup")
        until(button.is_enabled, 15, "Save backup to be offered", seen=lambda: flat(section.inner_text())[-400:])
        with page.expect_download(timeout=120_000) as download:
            button.click()
        file = download.value
        assert file.suggested_filename.endswith(".gbak"), file.suggested_filename
        path = os.path.join(folder, file.suggested_filename)
        file.save_as(path)
        assert file.failure() is None, file.failure()
        progress = section.locator("#bk-progress")
        until(lambda: flat(progress.inner_text()).startswith("Saved"), 120, "the progress line to say the save ended", seen=lambda: flat(progress.inner_text()))
        with open(path, "rb") as f:
            data = f.read()
        assert data.startswith(MAGIC), data[:16]
        for plain in (marker, row, kept, SECRET_VALUE, PASSPHRASE, app.slug, redis_row["Slug"], repo.full_name):
            assert plain.encode() not in data, f"the file holds {plain!r} as it is: it isn't encrypted"

        # ---- Wipe, as a new machine would be; and a commit the restore mustn't take.
        wiped = True
        wipe(gitberth)
        assert repo.commit({"marker.txt": "save and restore v2"}, message="After the save")["sha"] != saved_sha

        # ---- Restore: the dashboard offers it; upload, passphrase, summary, Restore.
        page = new_page()
        gitberth.open(page, "/")
        banner = page.locator(".gb-restore-banner")
        banner.wait_for(timeout=30_000)
        assert "Moving from another machine? Restore a backup." in flat(banner.inner_text()), banner.inner_text()
        banner.get_by_role("link", name="Restore a backup").click()
        page.locator("#backup #gb-restore-file").wait_for(state="attached", timeout=30_000)
        summary = upload_and_open(page, path)
        assert app.name in summary and "admin password of the machine the backup was saved on" in summary, summary
        started = engine.inspect(config.GITBERTH_CONTAINER)["State"]["StartedAt"]
        page.locator("#bk-summary").get_by_role("button", name="Restore everything…").click()
        dialog = page.locator(".mud-dialog")
        dialog.wait_for()
        confirm = dialog.get_by_role("button", name="Restore", exact=True)
        assert confirm.is_disabled(), "Restore works before restore is typed"
        dialog.locator("input").fill("restore")
        until(confirm.is_enabled, 10, "Restore to take the word")
        confirm.click()
        page.wait_for_url(re.compile(r"/restoring$"), timeout=60_000)

        # ---- The restart in the middle, and the end.
        through_the_restart(gitberth, engine, page, started)
        title = page.locator("h1.gb-display")
        until(lambda: title.inner_text().strip().startswith("Restored"), 600, "the restore to end", interval=2, seen=lambda: restoring_text(page))
        assert title.inner_text().strip() == "Restored", restoring_text(page)
        restored = True
        log = page.locator(".gb-terminal-body").inner_text()
        assert SECRET_VALUE not in log and PASSPHRASE not in log, "the restore's log shows a secret"
        assert not os.path.exists(os.path.join(config.GBDATA, "restore", "upload.gbak")), "the uploaded file is still there"

        # ---- Everything is back: ids, the commit, the port, the volume, the secret, the row, redis.
        after = gitberth.db.app(app.slug)
        assert after and after["Id"] == app.id, after
        deploy = gitberth.db.one("SELECT * FROM Deployments WHERE AppId = ? AND Id > ? ORDER BY Id LIMIT 1", app.id, last_deploy)
        assert deploy and (deploy["Status"], deploy["CommitSha"]) == ("Succeeded", saved_sha), f"the restore's deploy: {deploy}, not {saved_sha}"
        assert gitberth.db.web_port(app.id)["HostPort"] == port
        lan = gitberth.lan_url(app.id)
        gitberth.wait_answers(lan, timeout=120)
        until(lambda: get(f"{lan}/file?path=/data/br-marker.txt") == (200, marker), 60, "the marker in the app's volume",
              seen=lambda: get(f"{lan}/file?path=/data/br-marker.txt"))
        assert (get_json(f"{lan}/env") or {}).get(SECRET_NAME) == SECRET_VALUE, "the secret variable didn't come back"
        assert engine.env(app.container)["DATABASE_URL"] == url, "the app's DATABASE_URL changed"
        assert engine.exec(SERVER, "psql", "-X", "-t", "-A", url, "-c", "SELECT t FROM notes").strip() == row
        assert gitberth.db.app_by_id(redis_row["Id"]) is not None
        engine.wait_running(redis)
        until(lambda: engine.exec(redis, "redis-cli", "GET", "e2e-br").strip() == kept, 60, "redis's key")
        gitberth.open(page, "/")
        assert page.locator(".gb-restore-banner").count() == 0, "the dashboard still offers a restore"
    finally:
        if wiped and not restored:
            # A Gitberth left wiped or half restored: the session's GitHub token and watch interval again, for the scenarios after.
            gitberth._signed_in = None
            try:
                fix = new_page()
                gitberth.save_github_token(fix, baseline["token"])
                gitberth.save_watch_interval(fix, baseline["watch_seconds"])
            except Exception as e:  # noqa: BLE001 - cleaning up never hides the scenario's own failure
                print(f"[e2e] couldn't give the wiped Gitberth the session's GitHub token again: {e}")
        # What it seeded goes (a wiped Gitberth that didn't come back has nothing of it): the Git app before the server its
        # database is on. The apps fixture finds the app gone.
        if not config.KEEP and (restored or not wiped):
            try:
                cleanup = new_page()
                if app is not None and gitberth.db.app(app.slug) is not None:
                    gitberth.wait_idle(app.id, timeout=300)
                    gitberth.delete_app(cleanup, app)
                if token and redis_key:
                    error, gone = tool(gitberth, token, "delete_app", {"app": redis_key, "confirm": redis_row["Name"], "remove_volumes": True}, timeout=300)
                    if error:
                        print(f"[e2e] couldn't remove the redis app after group 40: {gone}")
                if made_server and engine.exists(SERVER):
                    remove_server(gitberth, cleanup, engine)
            except Exception as e:  # noqa: BLE001 - cleaning up never hides the scenario's own failure
                print(f"[e2e] couldn't remove what group 40 made: {e}")
        shutil.rmtree(folder, ignore_errors=True)


# ---- Picked items, into a Gitberth with other apps (plan2 P8) ----

def save_backup(gitberth, page, folder, wanted):
    """Settings › Save and restore with only the wanted items ticked (the rig's own left out), downloaded; its path."""
    section = open_section(gitberth, page)
    keep_only(section, wanted)
    section.locator("#bk-pass").fill(PASSPHRASE)
    section.locator("#bk-pass-again").fill(PASSPHRASE)
    button = section.get_by_role("button", name="Save backup")
    until(button.is_enabled, 15, "Save backup to be offered", seen=lambda: flat(section.inner_text())[-400:])
    with page.expect_download(timeout=120_000) as download:
        button.click()
    path = os.path.join(folder, download.value.suggested_filename)
    download.value.save_as(path)
    progress = section.locator("#bk-progress")
    until(lambda: flat(progress.inner_text()).startswith("Saved"), 120, "the save to end", seen=lambda: flat(progress.inner_text()))
    return path


def delete_with_data(gitberth, page, app):
    """The app's Remove, its named volumes and its databases' data too."""
    gitberth.open(page, f"/apps/{app.key}/settings")
    page.locator("#sec-remove").get_by_role("button", name=re.compile("^Remove")).click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    dialog.get_by_text("Also delete its named volumes").click()
    dialog.get_by_text("Also delete their data").click()
    dialog.get_by_role("button", name="Delete app").click()
    page.wait_for_url(lambda u: urlsplit(u).path == "/apps", timeout=60_000)


def restore_picked(gitberth, page, path, item, copy=False):
    """Restore from a file: the backup's item picked, as it was or as a copy under the name offered, restored and followed
    to Restored on a Restoring page that says nothing of a restart. Returns the copy's name, or None."""
    gitberth.open(page, "/settings#backup")
    page.locator("#backup #gb-restore-file").wait_for(state="attached", timeout=30_000)
    upload_and_open(page, path)
    summary = page.locator("#bk-summary")
    row = summary.locator(f"li[data-item='{item}']")
    row.locator("input[type=checkbox]").check()
    name = None
    if copy:
        choice = row.get_by_role("radio", name=re.compile("^Restore as a copy named"))
        if not choice.is_checked():
            choice.check()
        name = row.locator("input.gb-bk-copyname").input_value()
    else:
        until(lambda: "Comes back as it was." in flat(row.inner_text()), 10, "the row to say it comes back as it was", seen=row.inner_text)
    summary.get_by_role("button", name="Restore the picked items").click()
    gitberth.confirm(page, "Restore")
    page.wait_for_url(re.compile(r"/restoring\?picked=1$"), timeout=60_000)
    title = page.locator("h1.gb-display")
    until(lambda: title.inner_text().strip().startswith("Restored"), 600, "the picked restore to end", interval=2, seen=lambda: restoring_text(page))
    assert title.inner_text().strip() == "Restored", restoring_text(page)
    assert page.locator("#restore-restart-note").count() == 0, "a restore of picked items says Gitberth restarts"
    return name


def wait_server(gitberth, page, engine):
    """Settings › Databases says Gitberth uses the Postgres server, so an app's Add database is offered (else it says why
    not)."""
    def said():
        gitberth.open(page, "/settings")
        section = page.locator("#databases")
        section.wait_for()
        until(lambda: section.locator(".gb-skel").count() == 0, 30, "Settings › Databases to load", seen=section.inner_text)
        return flat(section.inner_text())

    def container():
        c = engine.inspect(SERVER) or {}
        return f"{(c.get('State') or {}).get('Status', 'none')}, on {sorted(((c.get('NetworkSettings') or {}).get('Networks') or {}))}"
    until(lambda: "Gitberth uses it" in said(), 60, "Gitberth to use the Postgres server", interval=3,
          seen=lambda: f"{said()[-600:]} | its container: {container()}")


@pytest.fixture(scope="module")
def picked_world(gitberth, github, engine, baseline):
    """A Gitberth with other apps (its Postgres server): a Git app with a marker in its volume, a secret variable and a row
    in a database of its own, Deploy on push off, saved; then a commit the restores mustn't take. What the scenarios made
    is removed at the end."""
    folder = tempfile.mkdtemp(prefix="gbe2e-picked-")
    w = SimpleNamespace(slugs=[], made_server=not engine.exists(SERVER))
    page = gitberth.new_page()
    try:
        print(f"[e2e] picked_world: the Postgres server: {create_server(gitberth, page, engine) or 'there already'}")
        w.repo = github.create_repo(f"bp-{secrets.token_hex(2)}", files={"marker.txt": "picked v1"})
        w.app = gitberth.create_git_app(page, w.repo.url)
        w.slugs.append(w.app.slug)
        first = gitberth.wait_deploy(w.app.id, timeout=420)
        wait_server(gitberth, page, engine)
        said = add_database(gitberth, page, w.app)
        assert said.startswith("Added the database"), said
        second = wait_live(gitberth, w.app, first["Id"])
        open_environment(page, gitberth, w.app)
        add_variable(page, SECRET_NAME, SECRET_VALUE)
        wait_bar(page, lambda line: line.startswith(PRODUCTION_TAKES), "the apply bar to say production takes the variable")
        third = apply_and_follow(page, gitberth)
        assert third["Status"] == "Succeeded" and third["Id"] > second["Id"], third
        token, _ = make_token(gitberth, page, f"bp-admin-{secrets.token_hex(2)}", "Admin")
        error, done = tool(gitberth, token, "update_app_settings", {"app": w.app.key, "auto_deploy": False, "wait": True}, timeout=300)
        assert not error, done
        lan = gitberth.lan_url(w.app.id)
        w.marker = f"marker-{secrets.token_hex(4)}"
        assert call("POST", f"{lan}/file?path=/data/br-marker.txt", body=w.marker.encode())[0] == 200
        w.row = f"row-{secrets.token_hex(4)}"
        url = engine.env(w.app.container)["DATABASE_URL"]
        engine.exec(SERVER, "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", url, "-c", f"CREATE TABLE notes (t text); INSERT INTO notes VALUES ('{w.row}');")
        w.port = gitberth.db.web_port(w.app.id)["HostPort"]
        w.sha = gitberth.db.app(w.app.slug)["CurrentCommit"]
        w.item = f"app:{w.app.id}"
        w.path = save_backup(gitberth, page, folder, {"gitberth", f"database:{SERVER}"} | {f"app:{a['Id']}" for a in gitberth.db.apps()})
        assert w.repo.commit({"marker.txt": "picked v2"}, message="After the save")["sha"] != w.sha
        page.context.close()
        yield w
    finally:
        if not page.is_closed():
            page.context.close()
        if not config.KEEP:
            cleanup = gitberth.new_page()
            try:
                for slug in reversed(w.slugs):
                    if (row := gitberth.db.app(slug)) is not None:
                        gitberth.wait_idle(row["Id"], timeout=300)
                        gitberth.delete_app(cleanup, App(row["Id"], row["Slug"], row["Name"]))
                if w.made_server and engine.exists(SERVER):
                    remove_server(gitberth, cleanup, engine)
            except Exception as e:  # noqa: BLE001 - cleaning up never hides a scenario's own failure
                print(f"[e2e] couldn't remove what group 40's picked restores made: {e}")
            finally:
                cleanup.context.close()
        shutil.rmtree(folder, ignore_errors=True)


def test_a_deleted_app_comes_back_picked_from_the_backup(picked_world, new_page, gitberth, engine, scenario):
    w = picked_world
    page = new_page()
    delete_with_data(gitberth, page, w.app)
    gitberth.wait_app(w.app.slug, gone=True)
    restore_picked(gitberth, page, w.path, w.item)

    # Back under its own name: its data, its secret, its database, the same host port, the commit it ran.
    row = gitberth.wait_app(w.app.slug, timeout=60)
    container = row["Container"] or f"gb-{row['Slug']}"
    assert gitberth.db.web_port(row["Id"])["HostPort"] == w.port
    lan = gitberth.lan_url(row["Id"])
    body = gitberth.wait_answers(lan, timeout=180)
    assert body.get("commit") == w.sha, f"it runs {body.get('commit')}, not the saved commit {w.sha}"
    assert get(f"{lan}/file?path=/data/br-marker.txt") == (200, w.marker)
    assert (get_json(f"{lan}/env") or {}).get(SECRET_NAME) == SECRET_VALUE, "the secret variable didn't come back"
    url = engine.env(container)["DATABASE_URL"]
    assert engine.exec(SERVER, "psql", "-X", "-t", "-A", url, "-c", "SELECT t FROM notes").strip() == w.row


def test_an_app_comes_back_as_a_copy_next_to_it(picked_world, new_page, gitberth, engine, scenario):
    w = picked_world
    page = new_page()
    original = gitberth.db.app(w.app.slug)
    assert original is not None, "the app isn't here to copy"
    name = restore_picked(gitberth, page, w.path, w.item, copy=True)
    assert name == f"{w.app.name}2", name
    row = until(lambda: gitberth.db.one("SELECT * FROM Apps WHERE Name = ?", name), 60, "the copy in Gitberth's database")
    w.slugs.append(row["Slug"])
    assert row["Id"] != original["Id"] and row["Slug"] != original["Slug"], row

    # Both answer: the copy on a port of its own, with the data, and a database of its own with the row.
    copy_lan, lan = gitberth.lan_url(row["Id"]), gitberth.lan_url(original["Id"])
    assert copy_lan != lan, "the copy got the original's host port"
    gitberth.wait_answers(copy_lan, timeout=180)
    gitberth.wait_answers(lan, timeout=60)
    assert get(f"{copy_lan}/file?path=/data/br-marker.txt") == (200, w.marker)
    copy_url = engine.env(row["Container"] or f"gb-{row['Slug']}")["DATABASE_URL"]
    url = engine.env(original["Container"] or f"gb-{original['Slug']}")["DATABASE_URL"]
    assert url_parts(copy_url)[5] != url_parts(url)[5] and url_parts(copy_url)[1] != url_parts(url)[1], "the copy uses the original's database"
    assert engine.exec(SERVER, "psql", "-X", "-t", "-A", copy_url, "-c", "SELECT t FROM notes").strip() == w.row
