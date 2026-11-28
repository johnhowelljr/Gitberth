"""Scenario 41: automatic backups (Settings › Automatic backups, src/Gitberth/Backup/AutoBackups.cs).

- Settings lists Automatic backups right before Save and restore, with its tip, and Settings › Notifications has its
  switch, An automatic backup fails, on by default.
- A folder on the rig's host: the passphrase set, the folder (made by Gitberth's helper), keep 2, only Gitberth's data and
  a container of the scenario's own (a marker in its volume) ticked; Test the destination works; three Back up now leave
  the newest two of this host's files there, and a file of someone else's stays. The container and its volume are
  removed, then one of the folder's backups is listed, fetched (Use this one), refused with a wrong passphrase, opened
  with its own, and the container restored from it, its marker back.
- An S3 server in the rig: the bucket made, Test the destination works, Back up now puts one file there, which Restore
  lists with its key and opens with the passphrase. It's SeaweedFS's S3 gateway, with an access key and secret it checks
  (Signature Version 4, multipart): MinIO's images aren't public any more (Docker Hub has none, Quay refuses).
- A Samba server in the rig (SMB): Test the destination works (Gitberth builds its smbclient and curl helper the first
  time), Back up now puts one file on the share, which Restore lists.
- An FTP server in the rig (vsftpd, no TLS: curl asks for it and goes on without): Test the destination works, Back up now
  puts one file in its folder, which Restore lists; the password never shows on the page.

About 4 minutes: SeaweedFS's image pulled and the Samba, FTP and helper images built once per rig volume, three small
backups to the folder, one each to the bucket, the share and the FTP server, one restore of one container. What it made
goes at the end.
"""
import hashlib
import hmac
import re
import secrets
import time
import urllib.request
from datetime import datetime, timezone
from types import SimpleNamespace

import pytest

from e2e.engine import RIG_NETWORK, docker
from e2e.wait import until

# Test values that look like nothing real (R3): the passphrase, and the share's and the bucket's secrets.
PASSPHRASE = "tok-9f8e7d6c5b4a"
SECRET = "s3cr3t-api-key"
ACCESS = "gbe2e-access"
# An S3 server that checks its access key and signatures: SeaweedFS's S3 gateway, its one identity in a file it's given.
S3_SERVER = "chrislusf/seaweedfs:latest"
S3_IDENTITIES = ('{"identities":[{"name":"gbe2e","credentials":[{"accessKey":"%s","secretKey":"%s"}],'
                 '"actions":["Admin","Read","List","Tagging","Write"]}]}') % (ACCESS, SECRET)
SAMBA = "gbe2e-samba:1"
ALPINE = "alpine:3.21"
SAMBA_DOCKERFILE = f"""FROM {ALPINE}
RUN apk add --no-cache samba && adduser -D -H -s /sbin/nologin e2e && mkdir -p /share && chown e2e /share \\
 && (echo '{SECRET}'; echo '{SECRET}') | smbpasswd -a -s e2e
RUN printf '[global]\\n server role = standalone server\\n map to guest = never\\n smb ports = 445\\n server min protocol = SMB2\\n disable netbios = yes\\n load printers = no\\n printing = bsd\\n printcap name = /dev/null\\n disable spoolss = yes\\n[backups]\\n path = /share\\n read only = no\\n valid users = e2e\\n force user = e2e\\n' > /etc/samba/smb.conf
CMD ["smbd", "--foreground", "--no-process-group", "--debug-stdout"]
"""
FTP = "gbe2e-ftp:1"
FTP_DOCKERFILE = f"""FROM {ALPINE}
RUN apk add --no-cache vsftpd && adduser -D -h /home/e2e e2e && echo 'e2e:{SECRET}' | chpasswd
RUN printf 'listen=YES\\nlisten_ipv6=NO\\nanonymous_enable=NO\\nlocal_enable=YES\\nwrite_enable=YES\\nlocal_umask=077\\nchroot_local_user=YES\\nallow_writeable_chroot=YES\\npasv_enable=YES\\npasv_min_port=21100\\npasv_max_port=21110\\nseccomp_sandbox=NO\\nbackground=NO\\n' > /etc/vsftpd/vsftpd.conf
CMD ["vsftpd", "/etc/vsftpd/vsftpd.conf"]
"""
AUTO_FILE = re.compile(r"^gitberth-auto-[A-Za-z0-9_-]+-\d{8}-\d{6}\.gbak$")


def flat(text):
    return " ".join((text or "").split())


def card(gitberth, page):
    """Settings › Automatic backups, once its status has loaded."""
    gitberth.open(page, "/settings#auto-backup")
    section = page.locator("section#auto-backup")
    section.wait_for()
    section.locator("#ab-next").wait_for(timeout=30_000)
    return section


def rows(section):
    """The card's list of what's saved: (id, parent id or None, ticked, can be changed)."""
    return section.locator(".gb-bk-list li[data-item]").evaluate_all(
        """els => els.map(e => { const c = e.querySelector('input[type=checkbox]');
               return [e.dataset.item, e.dataset.parent || null, !!(c && c.checked), !!(c && !c.disabled)]; })""")


def only(section, wanted):
    """What's saved: every top-level row but the wanted ones unticked (what's under one follows it)."""
    details = section.locator("details.gb-ab-what")
    if details.get_attribute("open") is None:
        details.locator("summary").click()
    section.locator(".gb-bk-list li[data-item='gitberth']").wait_for(timeout=30_000)
    for item in wanted:
        section.locator(f".gb-bk-list li[data-item='{item}']").wait_for(timeout=30_000)
    for item, parent, ticked, free in rows(section):
        if parent is None and ticked and free and item not in wanted:
            section.locator(f".gb-bk-list li[data-item='{item}'] input[type=checkbox]").uncheck()
            until(lambda: not any(t for _, p, t, _ in rows(section) if p == item), 15, f"what's under {item} to follow it")
    for item in wanted:
        box = section.locator(f".gb-bk-list li[data-item='{item}'] input[type=checkbox]")
        if not box.is_checked():
            box.check()
    return {item for item, _, ticked, _ in rows(section) if ticked}


def set_passphrase(section):
    """The passphrase for automatic backups, once per Gitberth: its key's short ID."""
    if section.locator("#ab-key").count() == 0:
        section.locator("#ab-pass").fill(PASSPHRASE)
        section.locator("#ab-pass-again").fill(PASSPHRASE)
        section.get_by_role("button", name="Set passphrase").click()
        section.locator("#ab-key").wait_for(timeout=60_000)
    return re.search(r"key ([0-9a-f]{8})", section.locator("#ab-key").inner_text()).group(1)


def save(gitberth, page, section):
    section.get_by_role("button", name="Save", exact=True).click()
    gitberth.snackbar(page, r"^Saved\.", timeout=30_000)


def check_destination(section):
    """Test the destination, as the form has it: each of its steps worked."""
    section.get_by_role("button", name="Test the destination").click()
    result = section.locator("#ab-test")
    until(lambda: result.count() and flat(result.inner_text()), 240, "the destination's test to end", interval=1, seen=lambda: flat(section.inner_text())[-500:])
    said = flat(result.inner_text())
    assert said.startswith("It works:") and "Read it back." in said and "Removed it." in said, said
    return said


def runs(section):
    return [flat(t) for t in section.locator("#ab-runs li").all_inner_texts()]


def back_up_now(gitberth, page, section):
    """Back up now, followed until the card lists the run: its line."""
    before = len(runs(section))
    section.get_by_role("button", name="Back up now").click()
    gitberth.snackbar(page, r"^Backing up to ", timeout=30_000)
    until(lambda: len(runs(section)) > before, 240, "the backup to end", interval=1, seen=lambda: flat(section.inner_text())[:600])
    line = runs(section)[0]
    assert "Back up now" in line and "failed" not in line, f"{line} | {flat(section.locator('#ab-status').inner_text())}"
    return line


def folder_files(path):
    """The files in a folder of the rig's host, through a container that mounts it."""
    out = docker("run", "--rm", "-v", f"{path}:/d:ro", ALPINE, "ls", "-1", "/d", timeout=120)
    return sorted(line.strip() for line in out.splitlines() if line.strip())


def listed(gitberth, page):
    """Settings › Save and restore's list of the automatic backups at the destination: (name, its line) each."""
    gitberth.open(page, "/settings#backup")
    section = page.locator("#backup")
    section.locator("#ab-restore").wait_for(timeout=30_000)
    section.locator("#ab-restore").get_by_role("button", name="List them").click()
    until(lambda: section.locator("#ab-files li, #ab-none").count(), 120, "the automatic backups to be listed", seen=lambda: flat(section.inner_text())[-500:])
    items = section.locator("#ab-files li")
    return [(items.nth(i).get_attribute("data-file"), flat(items.nth(i).inner_text())) for i in range(items.count())]


def use_and_open(page, name):
    """Use this one on the listed file, then its passphrase: a wrong one refused, its own opening the summary."""
    section = page.locator("#backup")
    # The row's own button: its name says which backup ("Use the backup of 27 Nov 2026 03:00"), its words Use this one.
    button = section.locator(f"#ab-files li[data-file='{name}'] button")
    assert flat(button.inner_text()) == "Use this one", button.inner_text()
    button.click()
    uploaded = section.locator("#bk-uploaded")
    until(lambda: uploaded.count() and "is uploaded" in uploaded.inner_text(), 240, "the backup to be fetched", seen=lambda: flat(section.inner_text())[-500:])
    assert name in uploaded.inner_text(), uploaded.inner_text()
    box = section.locator("#bk-restore-pass")
    box.fill("not the passphrase")
    section.get_by_role("button", name="Open the file").click()
    until(lambda: section.locator("#bk-restore-pass ~ .gb-field-error, .gb-banner.gb-tone-critical").count(), 120, "a wrong passphrase to be refused")
    assert section.locator("#bk-summary").count() == 0, "a wrong passphrase opened the file"
    box.fill(PASSPHRASE)
    section.get_by_role("button", name="Open the file").click()
    summary = section.locator("#bk-summary")
    until(lambda: summary.count(), 180, "the file's summary", seen=lambda: flat(section.inner_text())[-500:])
    return summary


def discard(page):
    """The fetched file removed, so the next scenario's upload starts clean."""
    left = page.locator("#backup").get_by_role("button", name="Remove the file")
    if left.count():
        left.first.click()


# ---- S3: the bucket made with the same signature Gitberth uses (AWS Signature Version 4) ----

def s3_put_bucket(endpoint, bucket):
    host = endpoint.split("://", 1)[1]
    now = datetime.now(timezone.utc)
    amz, day = now.strftime("%Y%m%dT%H%M%SZ"), now.strftime("%Y%m%d")
    empty = hashlib.sha256(b"").hexdigest()
    canonical = f"PUT\n/{bucket}\n\nhost:{host}\nx-amz-content-sha256:{empty}\nx-amz-date:{amz}\n\nhost;x-amz-content-sha256;x-amz-date\n{empty}"
    scope = f"{day}/us-east-1/s3/aws4_request"
    to_sign = f"AWS4-HMAC-SHA256\n{amz}\n{scope}\n{hashlib.sha256(canonical.encode()).hexdigest()}"
    key = ("AWS4" + SECRET).encode()
    for part in (day, "us-east-1", "s3", "aws4_request"):
        key = hmac.new(key, part.encode(), hashlib.sha256).digest()
    signature = hmac.new(key, to_sign.encode(), hashlib.sha256).hexdigest()
    request = urllib.request.Request(f"{endpoint}/{bucket}", method="PUT", headers={
        "x-amz-date": amz, "x-amz-content-sha256": empty,
        "Authorization": f"AWS4-HMAC-SHA256 Credential={ACCESS}/{scope}, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature={signature}"})
    with urllib.request.urlopen(request, timeout=20) as r:
        assert r.status == 200, r.status


def start_container(w):
    """The scenario's own container, a marker in its volume."""
    docker("run", "-d", "--name", w.name, "-v", f"{w.volume}:/data", ALPINE, "sh", "-c", f"echo {w.marker} > /data/m.txt && exec sleep 36000")


def ensure_container(w, engine):
    """The scenario's container, there and running whatever an earlier scenario did to it."""
    if not engine.running(w.name):
        docker("rm", "-f", w.name, check=False)
        start_container(w)
        engine.wait_running(w.name)


@pytest.fixture(scope="module")
def world(gitberth, engine):
    """A container of the scenario's own, a marker in its volume, and where the backups go: a folder on the rig's host,
    an S3 server and a Samba server (made when a scenario needs them). Everything goes at the end."""
    tag = secrets.token_hex(2)
    w = SimpleNamespace(tag=tag, name=f"gbe2e-ab-{tag}", volume=f"gbe2e-ab-{tag}-data", folder=f"/srv/gbe2e-ab-{tag}",
                        s3=f"gbe2e-s3-{tag}", samba=f"gbe2e-samba-{tag}", ftp=f"gbe2e-ftp-{tag}", bucket=f"gbe2e-ab-{tag}",
                        marker=f"marker-{secrets.token_hex(4)}", key=None)
    if not engine.image_exists(ALPINE):
        docker("pull", "-q", ALPINE, timeout=300)
    start_container(w)
    try:
        yield w
    finally:
        for c in (w.name, w.s3, w.samba, w.ftp):
            docker("rm", "-fv", c, check=False)
        docker("volume", "rm", "-f", w.volume, check=False)
        docker("run", "--rm", "-v", "/srv:/s", ALPINE, "rm", "-rf", f"/s/gbe2e-ab-{tag}", check=False, timeout=120)
        docker("image", "rm", "-f", SAMBA, FTP, check=False)
        # Automatic backups off again, the passphrase left (it opens nothing without the files, which are gone).
        page = gitberth.new_page()
        try:
            section = card(gitberth, page)
            section.get_by_role("radio", name="A folder on this host").click()
            section.locator("#ab-folder").fill("")
            section.get_by_role("radio", name="Off").click()
            save(gitberth, page, section)
        except Exception as e:  # noqa: BLE001 - cleaning up never hides a scenario's own failure
            print(f"[e2e] couldn't turn automatic backups off after group 41: {e}")
        finally:
            page.context.close()


def test_settings_lists_automatic_backups_and_their_notification_switch(page, gitberth):
    gitberth.open(page, "/settings")
    links = page.locator(".gb-toc a")
    links.first.wait_for()
    names = [t.strip() for t in links.all_inner_texts()]
    assert "Automatic backups" in names and names.index("Save and restore") == names.index("Automatic backups") + 1, names
    assert page.locator(".gb-toc a[href='settings#auto-backup']").get_attribute("data-tip") == "settings.backup.auto"
    section = card(gitberth, page)
    assert section.locator("#auto-backup-t").inner_text().strip() == "Automatic backups"
    assert "Off: no backup runs on its own." in flat(section.locator("#ab-next").inner_text())
    switch = page.locator("#notifications").get_by_role("switch", name="An automatic backup fails", exact=True)
    switch.wait_for()
    assert switch.get_attribute("aria-checked") == "true", "An automatic backup fails isn't on"
    assert not [e for e in gitberth.errors(page) if e.startswith("page:")], gitberth.errors(page)


def test_backups_go_to_a_host_folder_keep_the_newest_and_restore(world, page, gitberth, engine):
    ensure_container(world, engine)
    section = card(gitberth, page)
    world.key = set_passphrase(section)
    section.get_by_role("radio", name="A folder on this host").click()
    section.locator("#ab-folder").fill(world.folder)
    keep = section.locator("#ab-keep")
    keep.fill("2")
    keep.press("Tab")
    chosen = only(section, {"gitberth", f"container:{world.name}"})
    # Its volume comes with it (and its image, when Docker Hub didn't say it has it); none of the rig's own.
    assert {"gitberth", f"container:{world.name}", f"volume:{world.volume}"} <= chosen, chosen
    assert not [c for c in chosen if c.startswith(("container:", "volume:", "folder:", "app:")) and world.name not in c], chosen
    check_destination(section)
    save(gitberth, page, section)

    # A file of someone else's in the folder: keep-N never counts it.
    docker("run", "--rm", "-v", f"{world.folder}:/d", ALPINE, "sh", "-c", "echo mine > /d/notes.txt")
    for _ in range(3):
        back_up_now(gitberth, page, card(gitberth, page))
        time.sleep(1.1)
    files = folder_files(world.folder)
    autos = [f for f in files if AUTO_FILE.match(f)]
    assert len(autos) == 2 and "notes.txt" in files and len(files) == 3, files
    section = card(gitberth, page)
    assert len(runs(section)) == 3, runs(section)
    assert "The last backup:" in flat(section.locator("#ab-last").inner_text())

    # The container and its data go; one of the folder's backups brings them back.
    docker("rm", "-f", world.name)
    docker("volume", "rm", world.volume)
    found = listed(gitberth, page)
    assert sorted(n for n, _ in found) == autos, found
    newest, line = found[0]
    assert newest == max(autos) and f"key {world.key} (the passphrase set now)" in line, found
    summary = use_and_open(page, newest)
    row = summary.locator(f"li[data-item='container:{world.name}']")
    row.locator("input[type=checkbox]").check()
    until(lambda: "Comes back as it was." in flat(row.inner_text()), 10, "the row to say it comes back as it was", seen=row.inner_text)
    summary.get_by_role("button", name="Restore the picked items").click()
    gitberth.confirm(page, "Restore")
    page.wait_for_url(re.compile(r"/restoring\?picked=1$"), timeout=60_000)
    title = page.locator("h1.gb-display")
    until(lambda: title.inner_text().strip().startswith("Restored"), 300, "the restore to end", interval=2,
          seen=lambda: flat(page.locator("main").inner_text())[-800:])
    assert title.inner_text().strip() == "Restored", flat(page.locator("main").inner_text())[-800:]
    engine.wait_running(world.name)
    assert engine.exec(world.name, "cat", "/data/m.txt").strip() == world.marker
    gitberth.open(page, "/settings#backup")
    discard(page)


def test_backups_go_to_an_s3_bucket(world, page, gitberth, engine):
    ensure_container(world, engine)
    if not engine.image_exists(S3_SERVER):
        docker("pull", "-q", S3_SERVER, timeout=300)
    docker("run", "-d", "--name", world.s3, "--network", RIG_NETWORK, "--entrypoint", "sh", S3_SERVER, "-c",
           f"printf '%s' '{S3_IDENTITIES}' > /tmp/s3.json && exec weed server -dir=/data -s3 -s3.config=/tmp/s3.json")
    endpoint = f"http://{world.s3}:8333"

    def made():
        try:
            s3_put_bucket(endpoint, world.bucket)
            return True
        except OSError:
            return False
    until(made, 90, "the S3 server to make the bucket", interval=1, seen=lambda: engine.logs(world.s3, tail=5))

    section = card(gitberth, page)
    world.key = world.key or set_passphrase(section)
    section.get_by_role("radio", name="An S3 bucket").click()
    section.locator("#ab-s3-endpoint").fill(endpoint)
    section.locator("#ab-s3-region").fill("us-east-1")
    section.locator("#ab-s3-bucket").fill(world.bucket)
    section.locator("#ab-s3-prefix").fill("backups/gitberth")
    section.locator("#ab-s3-key").fill(ACCESS)
    section.locator("#ab-s3-secret").fill(SECRET)
    only(section, {"gitberth", f"container:{world.name}"})
    check_destination(section)
    save(gitberth, page, section)
    back_up_now(gitberth, page, card(gitberth, page))

    found = listed(gitberth, page)
    assert len(found) == 1 and AUTO_FILE.match(found[0][0]) and f"key {world.key}" in found[0][1], found
    summary = use_and_open(page, found[0][0])
    assert world.name in flat(summary.inner_text()), flat(summary.inner_text())
    discard(page)


def test_backups_go_to_an_smb_share(world, page, gitberth, engine):
    ensure_container(world, engine)
    docker("build", "-q", "-t", SAMBA, "-", input=SAMBA_DOCKERFILE, timeout=600)
    docker("run", "-d", "--name", world.samba, SAMBA)
    engine.wait_running(world.samba)
    ip = engine.address(world.samba, "bridge")
    until(lambda: "smbd version" in engine.logs(world.samba) or engine.exec(world.samba, "sh", "-c", "netstat -ltn | grep -q ':445 ' && echo up", check=False).strip() == "up",
          60, "Samba to listen", interval=1, seen=lambda: engine.logs(world.samba, tail=10))

    section = card(gitberth, page)
    world.key = world.key or set_passphrase(section)
    section.get_by_role("radio", name="A network share (SMB)").click()
    section.locator("#ab-share").fill(f"//{ip}/backups/gitberth")
    section.locator("#ab-smb-user").fill("e2e")
    section.locator("#ab-smb-pass").fill(SECRET)
    only(section, {"gitberth", f"container:{world.name}"})
    check_destination(section)
    save(gitberth, page, section)
    back_up_now(gitberth, page, card(gitberth, page))

    on_share = [f for f in engine.exec(world.samba, "ls", "-1", "/share/gitberth").split() if f]
    assert len(on_share) == 1 and AUTO_FILE.match(on_share[0]), on_share
    found = listed(gitberth, page)
    assert [n for n, _ in found] == on_share and f"key {world.key}" in found[0][1], found
    # The share's password and the bucket's secret never show on the page.
    gitberth.open(page, "/settings#auto-backup")
    assert SECRET not in page.content()


def test_backups_go_to_an_ftp_server(world, page, gitberth, engine):
    ensure_container(world, engine)
    docker("build", "-q", "-t", FTP, "-", input=FTP_DOCKERFILE, timeout=600)
    # --init: vsftpd as PID 1 crashes (SIGSEGV) when it reaps a process it didn't start, after a few dozen sessions.
    docker("run", "-d", "--init", "--name", world.ftp, FTP)
    engine.wait_running(world.ftp)
    ip = engine.address(world.ftp, "bridge")
    until(lambda: engine.exec(world.ftp, "sh", "-c", "netstat -ltn | grep -q ':21 ' && echo up", check=False).strip() == "up",
          60, "the FTP server to listen", interval=1, seen=lambda: engine.logs(world.ftp, tail=10))

    section = card(gitberth, page)
    world.key = world.key or set_passphrase(section)
    section.get_by_role("radio", name="An FTP server").click()
    section.locator("#ab-ftp-address").fill(f"ftp://{ip}/backups/gitberth")
    section.locator("#ab-ftp-user").fill("e2e")
    section.locator("#ab-ftp-pass").fill(SECRET)
    only(section, {"gitberth", f"container:{world.name}"})
    check_destination(section)
    save(gitberth, page, section)
    back_up_now(gitberth, page, card(gitberth, page))

    on_server = [f for f in engine.exec(world.ftp, "ls", "-1", "/home/e2e/backups/gitberth").split() if f]
    assert len(on_server) == 1 and AUTO_FILE.match(on_server[0]), on_server
    found = listed(gitberth, page)
    assert [n for n, _ in found] == on_server and f"key {world.key}" in found[0][1], found
    gitberth.open(page, "/settings#auto-backup")
    assert SECRET not in page.content()
