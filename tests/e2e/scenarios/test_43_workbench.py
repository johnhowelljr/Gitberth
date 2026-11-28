"""Scenario 43: terminal, files and config files (B3).

- An uploaded app (nginx with a VOLUME), made through the API. Its terminal's WebSocket refuses a browser that isn't
  signed in, a socket without a ticket (or with a made-up one), and one from another site's page (Origin). Its Terminal
  tab connects and runs a command typed into it, and the app's Activity has the terminal opened and closed.
- Its Files tab uploads a file into the volume (owned like the folder; nginx then serves it), makes a folder, renames,
  downloads and deletes a file; a folder swapped for a link out of the volume meanwhile is refused.
- Its Settings tab's Config files: one added is mounted at the app's next deploy and served; its contents changed are seen
  by the app at once, with no deploy.
"""
import base64
import http.client
import os
import tempfile
from urllib.parse import urlsplit

from e2e.gitberth import App
from e2e.net import get
from e2e.wait import until
from test_29_mcp import make_token
from test_46_uploads import flat, upload_api, zip_bytes

DOCKERFILE = """FROM nginx:alpine
COPY index.html /usr/share/nginx/html/index.html
VOLUME /usr/share/nginx/html/data
EXPOSE 80
"""
DATA = "/usr/share/nginx/html/data"
CONFIG = "/usr/share/nginx/html/conf.txt"


def site(name):
    return zip_bytes([(f"{name}/Dockerfile", DOCKERFILE), (f"{name}/index.html", f"<!doctype html><title>{name}</title><h1>{name}</h1>")])


def handshake(gitberth, page, path, origin=None, signed_in=True):
    """A WebSocket opening handshake to Gitberth, as a browser sends it: (status, the answer's text)."""
    u = urlsplit(gitberth.url(path))
    headers = {"Connection": "Upgrade", "Upgrade": "websocket", "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": base64.b64encode(os.urandom(16)).decode()}
    if signed_in:
        headers["Cookie"] = "; ".join(f"{c['name']}={c['value']}" for c in page.context.cookies())
    if origin:
        headers["Origin"] = origin
    conn = http.client.HTTPConnection(u.hostname, u.port, timeout=15)
    try:
        conn.request("GET", u.path + (f"?{u.query}" if u.query else ""), headers=headers)
        r = conn.getresponse()
        return r.status, r.read().decode(errors="replace")
    finally:
        conn.close()


def row(page, name):
    return page.locator(f".gb-files-row[data-name='{name}']")


def answer_dialog(page, value):
    dialog = page.get_by_role("dialog")
    dialog.wait_for()
    dialog.locator("#gb-file-name").fill(value)
    dialog.get_by_role("button", name="OK", exact=True).click()
    dialog.wait_for(state="detached")


def test_terminal_files_and_config_files(page, gitberth, apps, uniq, engine):
    name = uniq("wb")
    token, _ = make_token(gitberth, page, uniq("wbt"), "Admin")
    status, answer = upload_api(gitberth, token, name, f"{name}.zip", site(name), wait=True)
    assert status == 200 and answer["deploy"]["status"] == "Succeeded", (status, answer)
    app = apps.track(App(answer["id"], gitberth.db.app_by_id(answer["id"])["Slug"], name))
    url = gitberth.lan_url(app.id)
    until(lambda: name in get(url + "/")[1], 60, f"{url} to answer", seen=lambda: get(url + "/"))
    own = f"{urlsplit(gitberth.url('/')).scheme}://{urlsplit(gitberth.url('/')).netloc}"

    # ---- The terminal's WebSocket refuses what isn't Gitberth's own page with a ticket.
    gitberth.open(page, f"/apps/{app.key}")
    assert handshake(gitberth, page, "/terminal", own, signed_in=False)[0] == 401
    status, said = handshake(gitberth, page, "/terminal", own)
    assert status == 403 and "ticket is missing" in said, (status, said)
    status, said = handshake(gitberth, page, "/terminal?ticket=made-up", own)
    assert status == 403 and "ticket is missing" in said, (status, said)
    status, said = handshake(gitberth, page, "/terminal?ticket=made-up", "http://evil.example")
    assert status == 403 and "Gitberth's own pages" in said, (status, said)

    # ---- The Terminal tab: a command typed in runs in the app's container.
    gitberth.open(page, f"/apps/{app.key}/terminal")
    card = page.locator("#gb-terminal-card")
    card.get_by_role("button", name="Connect", exact=True).click()
    until(lambda: "connected" in flat(card.locator(".gb-card-sub").inner_text()), 30, "the terminal to connect",
          seen=lambda: flat(card.inner_text()))
    page.locator("#gb-term").click()
    page.keyboard.type("echo hi-$((6*7)) && cat /etc/hostname")
    page.keyboard.press("Enter")
    rows = page.locator("#gb-term .xterm-rows")
    until(lambda: "hi-42" in rows.inner_text(), 30, "the command's output in the terminal", seen=lambda: rows.inner_text())
    card.get_by_role("button", name="Disconnect").click()
    until(lambda: "Connect again" in flat(card.inner_text()), 30, "the terminal to close", seen=lambda: flat(card.inner_text()))
    notes = lambda: [a["Text"] for a in gitberth.db.activity(app.id) if a["Kind"] == "Terminal"]
    until(lambda: any(t.startswith("Terminal closed after") for t in notes()), 30, "the terminal's closing on the Activity", seen=notes)
    assert any(t.startswith("Terminal opened by admin") and "/bin/sh" in t for t in notes()), notes()

    # ---- The Files tab: the volume, an upload owned like its folder (and served), a folder, rename, download, delete.
    engine.exec(app.container, "chown", "101:101", DATA)
    gitberth.open(page, f"/apps/{app.key}/files")
    page.locator("#gb-files-card").wait_for()
    assert DATA in page.locator("#gb-files-mount").inner_text()
    upload = os.path.join(tempfile.mkdtemp(prefix="gbe2e-files-"), "hello.txt")
    with open(upload, "w", encoding="utf-8") as f:
        f.write(f"hello from {name}\n")
    page.set_input_files("#gb-files-input", upload)
    row(page, "hello.txt").wait_for(timeout=30_000)
    assert "101:101" in flat(row(page, "hello.txt").inner_text()), flat(row(page, "hello.txt").inner_text())
    until(lambda: get(url + "/data/hello.txt")[1] == f"hello from {name}\n", 20, "nginx to serve the uploaded file", seen=lambda: get(url + "/data/hello.txt"))

    page.locator("#gb-files-card").get_by_role("button", name="New folder").click()
    answer_dialog(page, "docs")
    row(page, "docs").wait_for(timeout=20_000)

    page.get_by_role("button", name="Rename hello.txt", exact=True).click()
    answer_dialog(page, "hi.txt")
    row(page, "hi.txt").wait_for(timeout=20_000)
    assert row(page, "hello.txt").count() == 0

    with page.expect_download() as download:
        page.get_by_role("button", name="Download hi.txt", exact=True).click()
    with open(download.value.path(), encoding="utf-8") as f:
        assert f.read() == f"hello from {name}\n"

    page.get_by_role("button", name="Delete hi.txt", exact=True).click()
    gitberth.confirm(page, "Delete")
    until(lambda: row(page, "hi.txt").count() == 0, 20, "hi.txt to go")
    assert get(url + "/data/hi.txt")[0] == 404

    # A folder swapped for a link out of the volume, while the page still shows it: refused.
    engine.exec(app.container, "sh", "-c", f"rm -r {DATA}/docs && ln -s /etc {DATA}/docs")
    row(page, "docs").get_by_role("button", name="docs", exact=True).click()
    banner = page.locator("#gb-files-card .gb-banner", has_text="It couldn't be read")
    banner.wait_for(timeout=20_000)
    assert "leads outside the volume" in flat(banner.inner_text()), flat(banner.inner_text())

    # ---- A config file: mounted at the next deploy, and its contents changed in place.
    gitberth.open(page, f"/apps/{app.key}/settings")
    section = page.locator("#sec-configs")
    section.wait_for()
    section.get_by_role("button", name="Add config file").click()
    section.locator("#cfg-path").fill(CONFIG)
    section.locator("#cfg-content").fill("config v1\n")
    section.locator("#gb-cfg-editor").get_by_role("button", name="Add config file").click()
    said = flat(gitberth.snackbar(page, r"^Added /usr/share/nginx/html/conf\.txt"))
    assert "next deploy mounts it" in said, said
    entry = section.locator(f".gb-cfg[data-path='{CONFIG}']")
    entry.wait_for()
    assert "At the next deploy" in flat(entry.inner_text()), flat(entry.inner_text())
    status, answer = upload_api(gitberth, token, app.key, f"{name}.zip", site(name), wait=True)
    assert status == 200 and answer["deploy"]["status"] == "Succeeded", (status, answer)
    until(lambda: get(url + "/conf.txt")[1] == "config v1\n", 30, "the app to serve its config file", seen=lambda: get(url + "/conf.txt"))
    mounts = [m for m in engine.inspect(app.container)["Mounts"] if m["Destination"] == CONFIG]
    assert len(mounts) == 1 and mounts[0]["RW"] is False, mounts

    gitberth.open(page, f"/apps/{app.key}/settings")
    section = page.locator("#sec-configs")
    section.locator(f".gb-cfg[data-path='{CONFIG}']").wait_for()
    assert "Mounted" in flat(section.locator(f".gb-cfg[data-path='{CONFIG}']").inner_text())
    section.get_by_role("button", name=f"Change {CONFIG}", exact=True).click()
    section.locator("#cfg-content").fill("config v2\n")
    section.locator("#gb-cfg-editor").get_by_role("button", name="Save config file").click()
    said = flat(gitberth.snackbar(page, r"^Saved /usr/share/nginx/html/conf\.txt"))
    assert "sees the new contents at once" in said, said
    until(lambda: get(url + "/conf.txt")[1] == "config v2\n", 20, "the app to see the new contents, with no deploy", seen=lambda: get(url + "/conf.txt"))
    assert not gitberth.errors(page), gitberth.errors(page)
