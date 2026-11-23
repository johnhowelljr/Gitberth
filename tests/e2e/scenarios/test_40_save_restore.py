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

About 4 minutes with the rig's test timings: one Git build before the save and one after, Postgres made once per rig
volume. What it seeded is removed at the end; a Postgres server it found stays.
"""
import os
import re
import secrets
import shutil
import tempfile
from urllib.parse import parse_qs, urlsplit

from playwright.sync_api import TimeoutError as PlaywrightTimeout

from e2e import config
from e2e.engine import docker
from e2e.net import call, get, get_json
from e2e.wait import until
from test_09_scoped_variables import PRODUCTION_TAKES, add_variable, open_environment, wait_bar
from test_10_build_time_secrets import apply_and_follow
from test_19_declared_volumes import add_row
from test_27_databases import SERVER, add_database, create_server, remove_server, wait_live
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
    """Unticks every row but the wanted items and what's under them; returns the ids left ticked."""
    for item, parent, ticked, free in save_rows(section):
        if ticked and free and item not in wanted and parent not in wanted:
            box = section.locator(f".gb-bk-list li[data-item='{item}'] input[type=checkbox]")
            # One its parent took with it is unticked already.
            if box.is_checked():
                box.uncheck()
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
        page.locator("#bk-summary").get_by_role("button", name="Restore…").click()
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
