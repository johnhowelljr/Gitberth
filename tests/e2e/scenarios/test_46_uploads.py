"""Scenario 46: deploy a folder (B6).

- A static site, zipped with its folder, uploaded on New app's Upload a folder: Gitberth unpacks it, detects a static
  website (port 80), fills the name in from the file (site-xxxx.zip is site-xxxx), and Deploy brings it up in one click.
  Its page says Uploaded folder, its first deploy's commit is the archive's SHA-256 and its message "Upload of …", and the
  site answers with its own page.
- A Node app's folder, chosen in the browser (webkitdirectory), with a node_modules folder in it: the browser sends the
  folder's files with their paths, node_modules left out, and Gitberth detects a Node server and brings it up.
- A new version of that app through the API (POST /api/v1/apps/<key>/upload, curl -F file=@…) with an Operate token,
  waited for: it's a deploy of its own (trigger Upload) and the app answers with the new version. A Read token is refused.
- A zip-slip archive (an entry ../../evil.txt) is refused on New app with a sentence, and by the API with 400: nothing of it
  is unpacked anywhere.
"""
import hashlib
import io
import json
import os
import re
import secrets
import tarfile
import tempfile
import zipfile

from e2e import config
from e2e.gitberth import App
from e2e.net import call, get
from e2e.wait import until
from test_29_mcp import make_token


def flat(text):
    return " ".join((text or "").split())


def zip_bytes(entries):
    """A zip of [(path, text)], in memory."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as z:
        for path, text in entries:
            z.writestr(path, text)
    return buffer.getvalue()


def tgz_bytes(entries):
    """A .tar.gz of [(path, text)], in memory."""
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as t:
        for path, text in entries:
            data = text.encode()
            info = tarfile.TarInfo(path)
            info.size, info.mode = len(data), 0o644
            t.addfile(info, io.BytesIO(data))
    return buffer.getvalue()


def node_app(version):
    """A small Node server that says its version, on PORT."""
    return [
        ("package.json", json.dumps({"name": "todo-api", "version": "1.0.0", "private": True, "scripts": {"start": "node server.js"}})),
        ("server.js", "const http = require('http');\n"
                      "http.createServer((req, res) => { res.setHeader('Content-Type', 'application/json');"
                      f" res.end(JSON.stringify({{ app: 'todo-api', version: '{version}' }})); }})\n"
                      "  .listen(process.env.PORT || 3000);\n"),
    ]


def write(folder, entries):
    for path, text in entries:
        full = os.path.join(folder, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8") as f:
            f.write(text)


def multipart(filename, data):
    boundary = "----gbe2e" + secrets.token_hex(8)
    head = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
            "Content-Type: application/octet-stream\r\n\r\n").encode()
    return head + data + f"\r\n--{boundary}--\r\n".encode(), f"multipart/form-data; boundary={boundary}"


def upload_api(gitberth, token, name, filename, data, wait=False, headers=None, timeout=660):
    """POST /api/v1/apps/<name>/upload: (status, JSON answer or text)."""
    body, kind = multipart(filename, data)
    hdrs = {"Content-Type": kind}
    if token:
        hdrs["Authorization"] = f"Bearer {token}"
    hdrs.update(headers or {})
    status, _, text = call("POST", gitberth.url(f"/api/v1/apps/{name}/upload" + ("?wait=true" if wait else "")), body=body, headers=hdrs, timeout=timeout)
    try:
        return status, json.loads(text)
    except ValueError:
        return status, text


def open_upload(gitberth, page):
    gitberth.open(page, "/apps/new")
    page.locator(".gb-srccard", has_text="Upload a folder").click()
    page.locator("#gb-upload-zone").wait_for()


def deploy_from_new_app(gitberth, page, what):
    """New app's Deploy, once the upload is looked at: the new app's page, and its row."""
    page.locator("#upload-settings").wait_for(timeout=120_000)
    page.locator("#upload-settings").get_by_role("button", name="Deploy", exact=True).click()
    page.wait_for_url(re.compile(r"/apps/gb-[a-z0-9-]+$"), timeout=120_000)
    slug = gitberth.path_of(page).rsplit("/", 1)[-1][len("gb-"):]
    row = until(lambda: gitberth.db.app(slug), 30, f"the app made from {what}")
    return App(row["Id"], row["Slug"], row["Name"]), row


def kept_archives(app_id):
    folder = os.path.join(config.GBDATA, "uploads", str(app_id))
    return sorted(os.listdir(folder)) if os.path.isdir(folder) else []


def anything_named(name):
    found = []
    for root, _, files in os.walk(config.GBDATA):
        found += [os.path.join(root, f) for f in files if f == name]
    return found


class TestDeployAFolder:
    def test_a_static_site_zip_deploys_in_one_click(self, page, gitberth, apps, uniq):
        name = uniq("site")
        marker = f"hello from {name}"
        data = zip_bytes([(f"{name}/index.html", f"<!doctype html><title>{name}</title><h1>{marker}</h1>"), (f"{name}/css/app.css", "h1{color:red}")])
        path = os.path.join(tempfile.mkdtemp(prefix="gbe2e-upl-"), f"{name}.zip")
        with open(path, "wb") as f:
            f.write(data)

        open_upload(gitberth, page)
        page.set_input_files("#gb-upload-file", path)
        detected = page.locator("#upload-detected")
        detected.wait_for(timeout=120_000)
        assert "Static website" in flat(detected.locator(".gb-h3").inner_text()), flat(detected.inner_text())
        made = page.locator("#upload-made")
        assert f"{name}.zip" in flat(made.inner_text()), flat(made.inner_text())
        # The name comes from the file's, and its port is the static site's.
        until(lambda: page.locator("#upl-name").input_value() == name, 15, "the name to come from the file", seen=lambda: page.locator("#upl-name").input_value())
        app, row = deploy_from_new_app(gitberth, page, f"{name}.zip")
        apps.track(app)

        dep = gitberth.wait_deploy(app.id, timeout=420)
        sha = hashlib.sha256(data).hexdigest()
        assert (row["Source"], row["UploadName"], row["UploadSha"]) == ("Upload", f"{name}.zip", sha), row
        assert (dep["Trigger"], dep["CommitSha"], dep["CommitMessage"], dep["CommitAuthor"]) == ("Created", sha, f"Upload of {name}.zip", "admin"), dep
        assert gitberth.db.web_port(app.id)["ContainerPort"] == 80
        url = gitberth.lan_url(app.id)
        until(lambda: marker in get(url + "/")[1], 60, f"{url} to serve the uploaded page", seen=lambda: get(url + "/"))
        assert kept_archives(app.id) == [f"{sha}.zip"]

        # Its page says where it came from, and has the new-version drop zone and the curl line.
        gitberth.open(page, f"/apps/{app.key}")
        hero = page.locator(".gb-hero")
        hero.get_by_text("Uploaded folder").wait_for()
        assert sha[:7] in flat(hero.inner_text()), flat(hero.inner_text())
        facts = page.locator("#upload-facts")
        facts.wait_for()
        assert f"{name}.zip" in flat(facts.inner_text()) and "the newest upload" in flat(facts.inner_text()), flat(facts.inner_text())
        assert page.locator("#gb-upl-zone").count() == 1
        curl = flat(page.locator(".gb-code pre", has_text="curl").first.inner_text())
        assert curl.startswith('curl -X POST -H "Authorization: Bearer $GITBERTH_TOKEN" -F file=@') and curl.endswith(f"/api/v1/apps/{app.key}/upload"), curl
        assert not gitberth.errors(page), gitberth.errors(page)

    def test_a_node_folder_chosen_in_the_browser_deploys_and_takes_a_new_version_through_the_api(self, page, gitberth, apps, uniq):
        # The Node app's folder on the runner's disk, with a node_modules folder the upload leaves out.
        folder_name = uniq("todo")
        node = os.path.join(tempfile.mkdtemp(prefix="gbe2e-upl-"), folder_name)
        write(node, node_app("v1") + [("node_modules/leftpad/index.js", "module.exports = 1;\n"), ("node_modules/leftpad/package.json", "{}")])

        open_upload(gitberth, page)
        page.set_input_files("#gb-upload-folder", node)
        detected = page.locator("#upload-detected")
        detected.wait_for(timeout=120_000)
        assert "Node" in flat(detected.locator(".gb-h3").inner_text()), flat(detected.inner_text())
        made = flat(page.locator("#upload-made").inner_text())
        assert folder_name in made and "a folder" in made and "2 files" in made, made
        app, row = deploy_from_new_app(gitberth, page, folder_name)
        apps.track(app)

        dep = gitberth.wait_deploy(app.id, timeout=480)
        assert (row["Source"], row["UploadName"]) == ("Upload", folder_name), row
        assert dep["CommitMessage"] == f"Upload of {folder_name}", dep
        url = gitberth.lan_url(app.id)
        answer = gitberth.wait_answers(url, "/", timeout=90)
        assert answer == {"app": "todo-api", "version": "v1"}, answer
        # What's kept is the folder as a tar, its files under its name, node_modules left out.
        [archive] = kept_archives(app.id)
        assert archive == f"{row['UploadSha']}.tar", archive
        with tarfile.open(os.path.join(config.GBDATA, "uploads", str(app.id), archive)) as t:
            names = sorted(t.getnames())
        assert f"{folder_name}/server.js" in names and f"{folder_name}/package.json" in names, names
        assert not any("node_modules" in n for n in names), names

        # ---- A new version through the API, as a script sends it (curl -F file=@…).
        before = gitberth.db.last_deployment_id(app.id)
        token, _ = make_token(gitberth, page, uniq("upl"), "Operate")
        data = tgz_bytes([(f"todo-v2/{p}", t) for p, t in node_app("v2")])
        # A Read token may not, nor anyone without a token, nor a browser page.
        reader, _ = make_token(gitberth, page, uniq("upr"), "Read")
        status, said = upload_api(gitberth, reader, app.key, "todo-v2.tar.gz", data)
        assert status == 403 and "needs Operate" in said["error"], (status, said)
        status, said = upload_api(gitberth, None, app.key, "todo-v2.tar.gz", data)
        assert status == 401, (status, said)
        status, said = upload_api(gitberth, token, app.key, "todo-v2.tar.gz", data, headers={"Origin": "http://evil.example"})
        assert status == 403, (status, said)
        assert gitberth.db.last_deployment_id(app.id) == before

        status, answer = upload_api(gitberth, token, app.key, "todo-v2.tar.gz", data, wait=True)
        assert status == 200, (status, answer)
        sha = hashlib.sha256(data).hexdigest()
        assert answer["created"] is False and answer["upload"] == {"name": "todo-v2.tar.gz", "sha": sha, "size": len(data)}, answer
        assert answer["deploy"]["status"] == "Succeeded", answer["deploy"]
        dep = gitberth.db.deployment(answer["deploy"]["deployment_id"])
        assert (dep["Trigger"], dep["CommitSha"], dep["CommitMessage"]) == ("Upload", sha, "Upload of todo-v2.tar.gz"), dep
        assert (dep["RequestedBy"] or "").startswith("ApiToken|Api|"), dep["RequestedBy"]
        assert gitberth.wait_answers(url, "/", timeout=60) == {"app": "todo-api", "version": "v2"}
        # Both uploads are kept, and the new one is the app's newest.
        assert sorted(a.split(".")[0] for a in kept_archives(app.id)) == sorted([row["UploadSha"], sha]), kept_archives(app.id)
        assert gitberth.db.app_by_id(app.id)["UploadSha"] == sha
    def test_a_zip_slip_archive_is_refused_and_nothing_of_it_is_unpacked(self, page, gitberth, uniq):
        name = uniq("slip")
        evil = f"evil-{secrets.token_hex(3)}.txt"
        data = zip_bytes([("index.html", "<h1>innocent</h1>"), (f"../../{evil}", "pwned")])
        path = os.path.join(tempfile.mkdtemp(prefix="gbe2e-upl-"), f"{name}.zip")
        with open(path, "wb") as f:
            f.write(data)

        open_upload(gitberth, page)
        page.set_input_files("#gb-upload-file", path)
        banner = page.locator(".gb-banner", has_text="Gitberth didn't take it")
        banner.wait_for(timeout=60_000)
        assert "leads out of its folder" in flat(banner.inner_text()) and "isn't unpacked" in flat(banner.inner_text()), flat(banner.inner_text())
        assert page.locator("#upload-detected").count() == 0

        token, _ = make_token(gitberth, page, uniq("ups"), "Admin")
        status, said = upload_api(gitberth, token, name, f"{name}.zip", data)
        assert status == 400 and "leads out of its folder" in said["error"], (status, said)
        assert gitberth.db.app(name) is None
        assert anything_named(evil) == [], anything_named(evil)
        # Nothing of it waits in staging either: a refused upload leaves nothing behind.
        staged = []
        for root, _, files in os.walk(os.path.join(config.GBDATA, "work", "uploads")):
            staged += [os.path.join(root, f) for f in files if f == "upload.zip" and os.path.getsize(os.path.join(root, f)) == len(data)]
        assert staged == [], staged
