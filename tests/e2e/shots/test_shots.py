"""The docs' screenshots, in order. A step sets part of the docs' demo up, through Gitberth's UI as someone would and
through the stand-ins as GitHub, GitLab and Cloudflare would (its name has "step" in it, so -k "step or <shot>" keeps
every step); a shot takes one of the pictures docs/ shows (test_<name>, with _ for -). Some steps take pictures on the
way, of what only shows then (New app's steps, a dialog, a box shown once). See run.sh in this folder.

The demo is the docs' own: host homelab at 192.168.50.10, Gitberth at http://192.168.50.10:8080, apps website,
orders-api and inventory from github.com/you, storefront (a Compose app) too, postgres, redis, uptime-kuma, whoami,
mailpit, adminer, backup-agent and Miniflux; example.com through Cloudflare (the stand-in), the API token Claude Code.
Docker Hub's search and tags come from its stand-in (real image names, illustrative numbers); an image's config before
its pull, and the pulls, from the real Docker Hub."""
import hashlib
import json
import os
import re
import time

import pytest

import demo as D
from e2e import config
from e2e.engine import docker
from e2e.gitberth import App
from e2e.github_reports import set_github_switch
from e2e.standin import DockerHub
from e2e.wait import until
from test_09_scoped_variables import add_variable
from test_12_commit_statuses import add_domain
from test_16_protect import save_emails
from test_23_deploy_hooks import create_hook
from test_25_down_page import set_down_page
from test_26_uptime_status import save_interval
from test_27_databases import add_database, create_server
from test_30_templates import delete_everything, install, open_template
from test_33_gitlab import save_gitlab
from test_14_instant_deploys import set_hook_host

ZONE = "example.com"
DOMAIN = "app.example.com"
HOOKS = "hooks.example.com"
STATUS = "status.example.com"
ACTIVE = ("Queued", "Cloning", "Building", "Starting")
KUMA = "louislam/uptime-kuma:1"


def seed_uptime_kuma(hub, digest):
    """The Docker Hub stand-in's louislam/uptime-kuma, as its default seed has it (its numbers illustrative), but with 1.23.16
    (also 1.23 and 1) naming the image Docker Hub gives uptime-kuma:1 now: the image the app runs, so Running now shows."""
    platforms = ["linux/amd64", "linux/arm64/v8", "linux/arm/v7"]

    def named(names, image, days, size):
        return [{"name": n, "digest": image, "pushed": days, "platforms": platforms, "size": size} for n in names]

    def other(label):
        return "sha256:" + hashlib.sha256(f"shots:{label}".encode()).hexdigest()

    tags = (named(["2.5.11", "2.5", "2", "latest"], other("2.5.11"), 9, 160_200_000) + named(["2.5.10"], other("2.5.10"), 30, 160_100_000)
            + named(["2.4.3", "2.4"], other("2.4.3"), 75, 159_800_000) + named(["1.23.16", "1.23", "1"], digest, 210, 147_300_000)
            + named(["1.23.15"], other("1.23.15"), 260, 147_200_000) + named(["2.6.0-beta.1", "beta"], other("beta"), 5, 161_100_000))
    hub.seed("louislam/uptime-kuma", tags, search={"badge": "none", "stars": 1104, "pulls": 162_000_000, "description": "A fancy self-hosted monitoring tool",
                                                   "architectures": ["amd64", "arm64", "arm"], "updated_days": 9})


def flat(text):
    return " ".join((text or "").split())


def app_row(gitberth, name):
    """The app's row in Gitberth's database (never a preview's)."""
    return gitberth.db.one("SELECT * FROM Apps WHERE Name = ? AND Id NOT IN (SELECT AppId FROM Previews) ORDER BY Id DESC LIMIT 1", name)


def app_of(gitberth, name):
    row = until(lambda: app_row(gitberth, name), 60, f"the app {name}")
    return App(row["Id"], row["Slug"], row["Name"])


def key_of(gitberth, name):
    """The app's page key, as Gitberth makes it: its container's name (gb-website, gb-storefront_web), or for a container
    someone else started (redis, whoami) the container's own name."""
    row = app_row(gitberth, name)
    if row is None:
        return name
    return row.get("Container") or f"gb-{row['Slug']}"


def busy(gitberth, but=()):
    return gitberth.db.query(f"SELECT Id, AppId, Status FROM Deployments WHERE Status IN ({','.join('?' * len(ACTIVE))})", *ACTIVE)


def wait_idle(gitberth, timeout=900, but_apps=()):
    """Every deploy over (but the apps' given)."""
    until(lambda: not [d for d in busy(gitberth) if d["AppId"] not in but_apps], timeout, "every deploy to be over", interval=2,
          seen=lambda: busy(gitberth))


def top(page):
    page.evaluate("scrollTo(0, 0)")
    page.wait_for_timeout(300)


def settings_section(shooter, page, sid):
    shooter.open(page, "/settings")
    s = page.locator(f"#{sid}")
    s.wait_for()
    return s


def menu_item(page, text):
    return page.locator(".mud-menu-item:visible, [role=menuitem]:visible").filter(has_text=re.compile(rf"^\s*{re.escape(text)}")).first


def button(page, label):
    """A button by its aria-label: the button itself, not the MudMenu activator around it, which has the same name."""
    return page.locator(f"button[aria-label='{label}']").first


def mask_token(page):
    """A new API token as the docs show one: its first characters, then dots, wherever the page shows it."""
    return page.evaluate("""() => {
        const token = (document.body.innerText.match(/gbt_[A-Za-z0-9_-]{43}/) || [])[0];
        if (!token) return 0;
        const masked = token.slice(0, 12) + '\\u2022'.repeat(32);
        const walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
        let n = 0;
        for (let t = walk.nextNode(); t; t = walk.nextNode())
            if (t.textContent.includes(token)) { t.textContent = t.textContent.split(token).join(masked); n++; }
        return document.body.innerText.includes(token.slice(12)) ? -1 : n;
    }""")


def mask_hook_urls(page):
    """A deploy hook's URL as the docs show it: the secret part after /hooks/deploy/ cut to its first characters and
    dots (anyone with the whole URL can deploy the app). Returns how many were masked, -1 when one is left whole."""
    return page.evaluate("""() => {
        const rx = /\\/hooks\\/deploy\\/([A-Za-z0-9_-]{20,})/g;
        const walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
        let n = 0;
        for (let t = walk.nextNode(); t; t = walk.nextNode())
            if (rx.test(t.textContent)) {
                rx.lastIndex = 0;
                t.textContent = t.textContent.replace(rx, (m, token) => '/hooks/deploy/' + token.slice(0, 8) + '\\u2022'.repeat(16));
                n++;
            }
        rx.lastIndex = 0;
        return rx.test(document.body.innerText) ? -1 : n;
    }""")


# ======================================================================== the host and the stand-ins

def test_step_01_host(engine, demo):
    """What someone started on the host before Gitberth: redis, and whoami on an older image tagged latest (so an update
    shows); and a local image built on the host. A run before this one's are removed first. And the digest Docker Hub
    gives uptime-kuma:1 now, for the Docker Hub stand-in (step 2): the image is pulled to read it, then removed, so New
    app still pulls it (step 13)."""
    for name in ("whoami", "redis"):
        docker("rm", "-f", name, check=False)
    docker("volume", "rm", "-f", "redis-data", check=False)
    docker("pull", "-q", "traefik/whoami:v1.10.1", timeout=300)
    docker("tag", "traefik/whoami:v1.10.1", "traefik/whoami:latest")
    docker("pull", "-q", "redis:7-alpine", timeout=300)
    docker("run", "-d", "--name", "redis", "--restart", "unless-stopped", "-v", "redis-data:/data", "redis:7-alpine")
    docker("run", "-d", "--name", "whoami", "--restart", "unless-stopped", "-p", "8000:80", "traefik/whoami:latest")
    docker("build", "-q", "-t", "backup-agent:1.0", "-", input=D.BACKUP_AGENT, timeout=300)
    docker("pull", "-q", KUMA, timeout=600)
    digests = json.loads(docker("image", "inspect", "--format", "{{json .RepoDigests}}", KUMA))
    demo.kuma_digest = next(d.split("@", 1)[1] for d in digests if d.startswith("louislam/uptime-kuma@"))
    docker("image", "rm", KUMA)


def test_step_02_standins(github, gitlab, cloudflare, standins, demo):
    """github.com/you's repos (their files committed by You), gitlab.com/you/website, a token on each, Cloudflare's
    example.com with Zero Trust's team homelab, and Docker Hub's louislam/uptime-kuma with 1 naming the image it gives now
    (so the app made from it in step 13 shows Running now in its tag picker)."""
    demo.gh_token = github.token(login="you", expires="2027-05-17 00:00:00 UTC")
    demo.repos = {}
    for name, files in (("website", D.WEBSITE), ("orders-api", D.ORDERS_API), ("inventory", D.INVENTORY), ("storefront", D.STOREFRONT)):
        repo = github.create_repo(name, owner="you", template=None, files={"README.md": f"# {name}\n"})
        standins.api("POST", f"/github/repos/you/{name}/commits", {"branch": "main", "files": files, "message": "First version",
                                                                  "author": D.AUTHOR, "pusher": "you"})
        demo.repos[name] = repo
    # Pull requests 1 to 11, closed long ago: the one the docs show is #12.
    site = demo.repos["website"]
    standins.api("POST", "/github/repos/you/website/commits", {"branch": "draft", "files": {"site/draft.html": "draft"},
                                                              "message": "Draft", "author": D.AUTHOR, "pusher": "you"})
    for _ in range(11):
        site.close_pull(site.open_pull("draft", title="Draft")["number"])
    site.delete_branch("draft")
    site.set_variables(PUBLIC_SITE_NAME="Example", PUBLIC_CONTACT="you@example.com")

    demo.gl_token = gitlab.token(username="you", scopes=["api"], expires_at="2027-05-17")
    files = {path.split("/", 1)[1] if path.startswith("site/") else path: text for path, text in D.WEBSITE.items()}
    gitlab.create_project("you/website", template=None, files=files, owner="you")

    cloudflare.zone(ZONE)
    cloudflare.set_team("homelab")
    demo.cf_token = cloudflare.settings_token()

    seed_uptime_kuma(DockerHub(standins), demo.kuma_digest)


# ======================================================================== Settings

def test_step_03_settings_github(gitberth, shooter, demo):
    page = shooter.page()
    said = gitberth.save_github_token(page, demo.gh_token)
    assert "saved" in said.lower(), said
    gitberth.save_watch_interval(page, 30)
    for title in ("Commit statuses", "Comments on pull requests"):
        set_github_switch(gitberth, page, title, True)


def test_step_04_settings_network(gitberth, shooter):
    page = shooter.page()
    s = settings_section(shooter, page, "network")
    s.locator("#lan-host").fill("192.168.50.10")
    s.get_by_role("button", name="Save", exact=True).click()
    gitberth.snackbar(page, ".")


def test_step_05_settings_domains(gitberth, shooter, cloudflare, demo):
    page = shooter.page()
    s = settings_section(shooter, page, "domains")
    switch = s.get_by_role("switch", name="Manage app domains through Cloudflare")
    if switch.get_attribute("aria-checked") != "true":
        switch.click()
        gitberth.snackbar(page, "Domains turned on")
    tunnel = cloudflare.state()["tunnel_id"]
    until(lambda: f"Tunnel {tunnel[:8]}" in s.inner_text(), 60, "Settings to find the tunnel", seen=s.inner_text)
    s.locator("#cf-token").fill(demo.cf_token)
    s.get_by_role("button", name=re.compile(r"^(Save|Replace) token$")).click()
    said = gitberth.snackbar(page, "Token saved|Cloudflare")
    assert said.startswith("Token saved"), said
    said, error = save_emails(gitberth, page, "you@example.com")
    assert not error, error


def test_step_07_settings_gitlab(gitberth, shooter, demo):
    page = shooter.page()
    said = save_gitlab(gitberth, page, token=demo.gl_token)
    assert said == "Token saved.", said
    s = page.locator("#gitlab")
    for title in ("Commit statuses", "Comments on merge requests"):
        switch = s.get_by_role("switch", name=title, exact=True)
        if switch.get_attribute("aria-checked") != "true":
            switch.click()
            until(lambda: switch.get_attribute("aria-checked") == "true", 15, f"GitLab's {title} to be on")


def test_step_08_settings_notifications(gitberth, shooter):
    """A Discord channel (the stand-in answers for discord.com) and an ntfy topic on a server that isn't there
    (ntfy.example.com: its test fails, as the docs allow). The Discord dialog is pictured before it's sent."""
    page = shooter.page()
    s = settings_section(shooter, page, "notifications")
    s.get_by_role("button", name="Add a destination").click()
    dialog = page.get_by_role("dialog")
    dialog.wait_for()
    dialog.get_by_role("radio", name="Discord", exact=True).click()
    dialog.get_by_label("Name", exact=True).fill("Homelab alerts")
    dialog.get_by_label("Webhook URL", exact=True).fill("https://discord.com/api/webhooks/1234567890/homelab-alerts")
    page.wait_for_timeout(500)
    shooter.detail(page, page.locator(".mud-dialog"), "notification-dialog", pad=0)
    dialog.get_by_role("button", name="Add and send a test").click()
    gitberth.snackbar(page, r"^Added ")
    s.get_by_role("button", name="Add a destination").click()
    dialog = page.get_by_role("dialog")
    dialog.wait_for()
    dialog.get_by_role("radio", name="ntfy", exact=True).click()
    dialog.get_by_label("Name", exact=True).fill("Phone")
    dialog.get_by_label("Topic URL", exact=True).fill("https://ntfy.example.com/homelab-alerts")
    dialog.get_by_role("button", name="Add and send a test").click()
    gitberth.snackbar(page, ".", timeout=60_000)


def test_step_09_settings_uptime(gitberth, shooter):
    """Checks every 60 seconds (the default), and the status page on: Status, a line under it, response times, and its
    host name on the internet."""
    page = shooter.page()
    s = settings_section(shooter, page, "uptime")
    box = s.locator("#uptime-interval")
    if box.input_value() != "60":
        save_interval(gitberth, page, 60)
        s = settings_section(shooter, page, "uptime")
    for title in ("Status page", "Show response times"):
        switch = s.get_by_role("switch", name=title)
        if switch.get_attribute("aria-checked") != "true":
            switch.click()
            until(lambda: switch.get_attribute("aria-checked") == "true", 10, f"{title} to be on")
    s.locator("#status-title").fill("Status")
    s.locator("#status-description").fill("The apps on our home lab.")
    s.locator("#status-host").fill(STATUS)
    s.get_by_role("button", name="Save status page").click()
    gitberth.snackbar(page, "^Saved")


def test_step_10_settings_databases(gitberth, shooter, engine):
    page = shooter.page()
    create_server(gitberth, page, engine)


def test_step_11_settings_own_lan(gitberth, shooter):
    page = shooter.page()
    s = settings_section(shooter, page, "own-lan")
    s.locator("#lan-block").fill("192.168.50.224/28")
    s.get_by_role("button", name="Test", exact=True).click()
    until(lambda: s.get_by_role("button", name="Turn on", exact=True).is_enabled(), 60, "Turn on to be offered", seen=s.inner_text)
    s.get_by_role("button", name="Turn on", exact=True).click()
    gitberth.snackbar(page, ".", timeout=60_000)


# ======================================================================== New app: templates, registry, by hand, local

def test_template_gallery(shooter):
    page = shooter.page()
    shooter.open(page, "/apps/new?from=templates", wait_for="#gb-tpl-title")
    page.locator(".gb-tpl-card").first.wait_for()
    shooter.window(page, "template-gallery")


def test_template_page(gitberth, shooter):
    page = shooter.page()
    open_template(gitberth, page, "miniflux")
    shooter.quiet(page)
    top(page)
    shooter.window(page, "template-page")


def test_step_12_template_uptime_kuma(gitberth, shooter, demo):
    """Uptime Kuma from its template, pictured on its first deploy's page with its First steps box, then removed: the
    docs' uptime-kuma comes from the registry (below)."""
    page = shooter.page()
    made = install(gitberth, page, "uptime-kuma")
    try:
        until(lambda: gitberth.db.one("SELECT Status FROM Deployments WHERE AppId = ? ORDER BY Id DESC LIMIT 1", made.id)["Status"] in
              ("Succeeded", "Failed"), 600, "Uptime Kuma's first deploy")
        page.reload()
        gitberth.wait_ready(page)
        page.locator(".gb-tpl-first-steps").wait_for(timeout=60_000)
        shooter.quiet(page)
        top(page)
        shooter.window(page, "template-first-steps")
    finally:
        delete_everything(gitberth, page, made)
        gitberth.wait_app(made.slug, gone=True, timeout=120)


def test_new_app_sources(shooter):
    page = shooter.page()
    shooter.open(page, "/apps/new", wait_for=".gb-srccard")
    shooter.window(page, "new-app-sources")


def test_step_13_registry_uptime_kuma(gitberth, shooter):
    """New app › Registry image: Docker Hub's results for postgres (Official and Verified Publisher, from the stand-in),
    then louislam/uptime-kuma:1 from Popular with its tags in the picker, each step pictured, then Create and start."""
    page = shooter.page()
    shooter.open(page, "/apps/new", wait_for=".gb-srccard")
    box = page.locator("input[placeholder^='Search Docker Hub']")
    box.fill("postgres")
    results = page.locator("#gb-reg-results [role=option]")
    until(lambda: results.count() >= 4 and results.locator(".gb-dh-badge--verified-publisher").count() > 0, 60, "Docker Hub's results for postgres",
          seen=lambda: page.locator(".gb-dh-search").inner_text())
    shooter.quiet(page)
    top(page)
    shooter.window(page, "new-app-registry")
    page.locator(".gb-tile", has=page.locator(".gb-tile-t", has_text="louislam/uptime-kuma")).first.click()
    field = page.locator("#gb-reg-tag")
    field.wait_for()
    until(lambda: page.locator("#gb-reg-tag-list [role=option]").count() > 0 and field.input_value() == "1", 60, "uptime-kuma's tags, with 1 chosen",
          seen=lambda: page.locator(".gb-tp").inner_text())
    shooter.quiet(page)
    shooter.detail(page, page.locator("section.gb-card", has=page.locator("#gb-reg-title")), "new-app-tags")
    top(page)
    page.get_by_role("button", name="Continue", exact=True).click()
    page.get_by_role("button", name="Review and create").wait_for(timeout=120_000)
    shooter.quiet(page)
    top(page)
    shooter.window(page, "new-app-configure")
    page.get_by_role("button", name="Review and create").click()
    page.get_by_role("button", name="Create and start").wait_for(timeout=120_000)
    until(lambda: page.get_by_role("button", name="Create and start").is_enabled(), 120, "Create and start to be offered")
    shooter.quiet(page)
    top(page)
    shooter.window(page, "new-app-review")
    page.get_by_role("button", name="Create and start").click()
    page.wait_for_url(re.compile(r"/apps/(?!new)[^/]+$"), timeout=600_000)


def by_hand(shooter, page, compose, text, name):
    shooter.open(page, "/apps/new", wait_for=".gb-srccard")
    page.locator(".gb-srccard", has_text="By hand").click()
    hand = page.locator("#gb-hand-title")
    hand.wait_for()
    if compose:
        page.get_by_role("radio", name="Compose", exact=True).click()
    page.locator("textarea.gb-paste").fill(text)
    page.wait_for_timeout(1500)
    shooter.quiet(page)
    hand.evaluate("e => scrollTo(0, e.getBoundingClientRect().top + scrollY - 120)")
    shooter.window(page, name)
    page.get_by_role("button", name="Continue", exact=True).last.click()
    create(page)


def create(page):
    page.get_by_role("button", name="Review and create").click(timeout=120_000)
    button = page.get_by_role("button", name="Create and start")
    button.wait_for(timeout=120_000)
    until(button.is_enabled, 120, "Create and start to be offered")
    button.click()
    page.wait_for_url(re.compile(r"/apps/(?!new)[^/]+$"), timeout=600_000)


def test_step_14_by_hand(shooter):
    page = shooter.page()
    by_hand(shooter, page, False, D.MAILPIT, "new-app-paste")
    by_hand(shooter, page, True, D.ADMINER, "new-app-paste-compose")


def test_step_15_local_image(shooter):
    page = shooter.page()
    shooter.open(page, "/apps/new", wait_for=".gb-srccard")
    page.locator(".gb-srccard", has_text="Local image").click()
    page.get_by_text("backup-agent:1.0").first.wait_for()
    shooter.quiet(page)
    top(page)
    shooter.window(page, "new-app-local")
    item = page.locator("div, li, tr").filter(has_text="backup-agent:1.0").filter(has=page.get_by_role("button", name="Use", exact=True)).last
    item.get_by_role("button", name="Use", exact=True).click()
    page.wait_for_timeout(1500)
    continue_button = page.get_by_role("button", name="Continue", exact=True)
    if continue_button.count() and continue_button.last.is_visible():
        continue_button.last.click()
    create(page)


# ======================================================================== New app: Git repositories

def test_step_16_git_website(gitberth, shooter, demo):
    """github.com/you/website's site folder, pictured once checked (the top of the page, then its Settings card)."""
    page = shooter.page()
    url = "https://github.com/you/website/tree/main/site"
    shooter.open(page, "/apps/new?url=" + url.replace("/", "%2F").replace(":", "%3A"))
    page.locator("#git-name").wait_for(timeout=180_000)
    # Gitberth offers website-site (the repo and its folder): the docs' app is website.
    page.locator("#git-name").fill("website")
    shooter.quiet(page)
    top(page)
    shooter.window(page, "new-app-github")
    page.locator("#git-name").evaluate("e => scrollTo(0, e.getBoundingClientRect().top + scrollY - 190)")
    page.wait_for_timeout(400)
    shooter.window(page, "new-app-github-settings")
    page.get_by_role("button", name="Create and deploy").click()
    page.wait_for_url(re.compile(r"/apps/gb-[a-z0-9-]+$"), timeout=120_000)


def test_step_17_git_apps(gitberth, shooter):
    page = shooter.page()
    for name in ("orders-api", "inventory"):
        gitberth.create_git_app(page, f"https://github.com/you/{name}")


def test_step_18_git_compose(gitberth, shooter):
    """github.com/you/storefront: a compose.yaml with three services, pictured once checked."""
    page = shooter.page()
    shooter.open(page, "/apps/new?url=https%3A%2F%2Fgithub.com%2Fyou%2Fstorefront")
    section = page.locator("section[aria-labelledby='gb-compose-title']")
    section.wait_for(timeout=180_000)
    page.locator("#git-name").wait_for()
    shooter.quiet(page)
    section.evaluate("e => scrollTo(0, e.getBoundingClientRect().top + scrollY - 90)")
    shooter.window(page, "new-app-git-compose")
    page.get_by_role("button", name="Create and deploy").click()
    page.wait_for_url(re.compile(r"/apps/(gb-[a-z0-9-]+_[A-Za-z0-9._-]+|\d+/deployments/\d+)$"), timeout=180_000)


def test_new_app_gitlab(shooter):
    page = shooter.page()
    shooter.open(page, "/apps/new?url=https%3A%2F%2Fgitlab.com%2Fyou%2Fwebsite")
    page.locator("#git-name").wait_for(timeout=180_000)
    shooter.quiet(page)
    top(page)
    shooter.window(page, "new-app-gitlab")


def test_new_app_railpack(gitberth, github, standins, shooter):
    """github.com/you/motd, a shell script Gitberth doesn't build itself: New app's card once Railpack has planned it (How
    it will run, the note that Railpack builds it, Railpack's plan). Only checked: nothing is created."""
    github.create_repo("motd", owner="you", template=None, files={"README.md": "# motd\n"})
    standins.api("POST", "/github/repos/you/motd/commits", {"branch": "main", "files": D.MOTD, "message": "First version",
                                                          "author": D.AUTHOR, "pusher": "you"})
    page = shooter.page()
    shooter.open(page, "/apps/new?url=https%3A%2F%2Fgithub.com%2Fyou%2Fmotd")
    page.locator("#git-name").wait_for(timeout=180_000)
    card = page.locator("section.gb-card").filter(has=page.locator(".gb-h3", has_text="How it will run"))
    card.locator("#git-railpack-note").wait_for(timeout=120_000)
    shooter.quiet(page)
    card.evaluate("e => scrollTo(0, e.getBoundingClientRect().top + scrollY - 90)")
    shooter.window(page, "new-app-railpack")


def test_step_19_template_miniflux(gitberth, shooter):
    page = shooter.page()
    install(gitberth, page, "miniflux")


def test_step_19b_instant_deploys(gitberth, shooter):
    """Instant deploys through hooks.example.com, once the repos are deployed: Gitberth adds its webhook to each, and
    Settings lists them."""
    page = shooter.page()
    said = set_hook_host(gitberth, page, HOOKS)
    assert HOOKS in said, said


def test_step_20_deploys_over(gitberth):
    wait_idle(gitberth)
    failed = gitberth.db.query("SELECT d.Id, a.Name, d.Error FROM Deployments d JOIN Apps a ON a.Id = d.AppId WHERE d.Status = 'Failed'")
    assert not failed, failed


# ======================================================================== the apps' settings

def test_step_21_website(gitberth, shooter, cloudflare, demo):
    """website: a Postgres database, its own variables (a secret, a build-time one, one for previews), app.example.com
    with the down page on, previews, uptime checks, pinned with the others."""
    page = shooter.page()
    website = app_of(gitberth, "website")
    demo.apps["website"] = website
    said = add_database(gitberth, page, website)
    assert said.startswith("Added"), said
    wait_idle(gitberth)
    gitberth.open(page, f"/apps/{website.key}/environment")
    page.locator("#sec-env").wait_for()
    add_variable(page, "CONTENTFUL_SPACE_ID", "x8w1example")
    add_variable(page, "CONTENTFUL_TOKEN", "cf-example-delivery-token")
    add_variable(page, "PUBLIC_ANALYTICS_ID", "G-EXAMPLE1", build=True)
    add_variable(page, "SHOW_PREVIEW_BANNER", "true", scope="Preview")
    bar = page.get_by_role("region", name="Unsaved changes")
    bar.get_by_role("button", name=re.compile(r"^(Review and apply|Apply)$")).click()
    page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"), timeout=60_000)
    wait_idle(gitberth)
    add_domain(gitberth, page, website, DOMAIN)
    set_down_page(gitberth, page, website, True)
    card = page.locator("section[aria-labelledby='gb-domains-title']")
    card.get_by_label("Down page message").fill("Back by 9:00 UTC.")
    card.get_by_role("button", name="Save message").click()
    gitberth.snackbar(page, "message")
    said = gitberth.set_previews(page, website, mode="Pull requests and branches", limit=3, days=7, forks=False, protect=True)
    assert said.startswith("Saved"), said


def test_step_22_uptime_checks_and_pins(gitberth, shooter):
    page = shooter.page()
    for name in ("website", "orders-api", "inventory", "uptime-kuma"):
        shooter.open(page, f"/apps/{key_of(gitberth, name)}")
        card = page.locator("section#uptime")
        card.wait_for()
        switch = card.get_by_role("switch", name="Uptime check")
        if switch.get_attribute("aria-checked") != "true":
            switch.click()
            until(lambda: switch.get_attribute("aria-checked") == "true", 15, f"{name}'s uptime check to be on")
    for name in ("website", "orders-api", "inventory", "postgres", "uptime-kuma"):
        shooter.open(page, f"/apps/{key_of(gitberth, name)}")
        button(page, "More actions").click()
        item = menu_item(page, "Pin to the menu")
        if item.count():
            item.click()
            page.wait_for_timeout(800)
        else:
            page.keyboard.press("Escape")


def test_step_23_own_lan_address(gitberth, shooter):
    """uptime-kuma gets an automatic own LAN address (instant, nothing restarts)."""
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'uptime-kuma')}/network")
    card = page.locator("section[aria-labelledby='gb-lanaddr-title']")
    card.wait_for()
    card.locator(".gb-choice[role=radio]", has_text="Automatic").first.click()
    card.get_by_role("button", name="Apply", exact=True).click()
    page.wait_for_timeout(1500)
    if page.locator(".mud-dialog").count():
        page.locator(".mud-dialog .mud-dialog-actions button").last.click()
    until(lambda: "192.168.50." in card.inner_text(), 60, "uptime-kuma's own LAN address", seen=card.inner_text)


def test_step_24_activity(gitberth, shooter, standins, demo):
    """A push to website's main (a deploy GitHub starts, through the webhook), and orders-api deployed again by hand."""
    page = shooter.page()
    website = app_of(gitberth, "website")
    before = gitberth.db.last_deployment_id(website.id)
    standins.api("POST", "/github/repos/you/website/commits", {"branch": "main", "files": {"site/about.html": D.page(
        "About", "<h1>About</h1>\n<p>A small static site, now with a contact line: you@example.com.</p>")}, "message": "Add a contact line",
        "author": D.AUTHOR, "pusher": "you"})
    gitberth.wait_deploy(website.id, after=before, timeout=600)
    orders = app_of(gitberth, "orders-api")
    demo.apps["orders-api"] = orders
    demo.manual_deploy = gitberth.deploy(page, orders)
    gitberth.wait_deploy(orders.id, after=demo.manual_deploy - 1, timeout=600)


def test_step_25_pull_request(gitberth, standins, demo):
    """Pull request #12 and its preview, ready."""
    website = app_of(gitberth, "website")
    standins.api("POST", "/github/repos/you/website/commits", {"branch": "pricing", "files": D.PRICING, "message": "Add a pricing page",
                                                              "author": D.AUTHOR, "pusher": "you"})
    pull = demo.repos["website"].open_pull("pricing", title="Add a pricing page")
    assert pull["number"] == 12, pull["number"]
    preview = gitberth.wait_preview(website.id, pr=12, timeout=300)
    gitberth.wait_deploy(preview["Id"], timeout=600)


def test_step_26_outside_change(gitberth, engine):
    """redis stopped and started again outside Gitberth (the audit log's "Stopped outside Gitberth")."""
    docker("stop", "redis")
    time.sleep(8)
    docker("start", "redis")
    engine.wait_running("redis")


def test_step_27_scheduled_job(gitberth, shooter):
    page = shooter.page()
    orders = app_of(gitberth, "orders-api")
    shooter.open(page, f"/apps/{key_of(gitberth, 'orders-api')}")
    card = page.locator("section.gb-card").filter(has=page.locator(".gb-card-title", has_text="Scheduled jobs")).first
    try:
        card.wait_for(timeout=60_000)
    except Exception:  # noqa: BLE001 - say what the build saw of vercel.json
        log = gitberth.deploy_log(gitberth.db.last_deployment_id(orders.id)).splitlines()
        raise AssertionError(f"orders-api has no Scheduled jobs card at {page.url}; its deploy log on crons: "
                             f"{[line for line in log if 'ron' in line][-6:]}") from None
    card.get_by_role("button", name=re.compile("^Run now")).first.click()
    page.wait_for_timeout(4000)
    shooter.quiet(page)
    shooter.detail(page, card, "scheduled-jobs")


# ======================================================================== updates: whoami

def test_step_28_whoami_update(gitberth, shooter):
    """whoami: Update available, the dialog before it, the update itself, and Roll back after."""
    page = shooter.page()
    shooter.open(page, "/apps/whoami")
    card = page.locator("section, .gb-card").filter(has=page.locator(".gb-card-title", has_text="Updates")).first
    card.wait_for()
    if "Not checked yet" in card.inner_text() or "Update available" not in card.inner_text():
        card.get_by_role("button", name="Check now").click()
        until(lambda: "Update available" in card.inner_text() or "available" in card.inner_text().lower(), 90,
              "whoami's update", seen=card.inner_text)
    shooter.quiet(page)
    shooter.detail(page, card, "update-available")
    page.locator(".gb-hero-actions").get_by_role("button", name="Update", exact=True).click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    page.wait_for_timeout(2500)
    shooter.detail(page, dialog, "update-dialog", pad=0)
    dialog.get_by_role("button", name=re.compile("^Update ")).click()
    until(lambda: "Roll back to" in page.locator("main").inner_text(), 240, "the update to be done")
    # The dialog stays open on what the update did: closed, as someone would.
    for _ in range(3):
        if page.locator(".mud-dialog").count() == 0:
            break
        done = page.locator(".mud-dialog").get_by_role("button", name=re.compile("^(Done|Close)$"))
        done.first.click() if done.count() else page.keyboard.press("Escape")
        page.wait_for_timeout(800)
    page.locator(".mud-dialog").wait_for(state="detached")
    card = page.locator("section, .gb-card").filter(has=page.locator(".gb-card-title", has_text="Updates")).first
    shooter.quiet(page)
    shooter.detail(page, card, "update-rollback")


# ======================================================================== shots taken on the way

def test_step_29_deploy_hook(gitberth, shooter):
    """website's deploy hook Contentful, pictured just after it's made."""
    page = shooter.page()
    website = app_of(gitberth, "website")
    section = create_hook(gitberth, page, website, "Contentful")
    page.wait_for_timeout(800)
    assert mask_hook_urls(page) >= 2, "the new hook's URLs aren't on the page to mask"
    shooter.detail(page, section, "app-deploy-hooks")


def test_step_30_api_token(gitberth, shooter):
    """Settings › AI agents and API, just after Create token for Claude Code (Operate)."""
    page = shooter.page()
    s = settings_section(shooter, page, "agents")
    s.locator("#token-name").fill("Claude Code")
    s.locator(".gb-seg[aria-label='Access'] .gb-seg-btn", has_text="Operate").click()
    button = s.get_by_role("button", name="Create token")
    until(button.is_enabled, 10, "Create token to take the name")
    button.click()
    gitberth.snackbar(page, re.escape("Token Claude Code is ready."))
    page.wait_for_timeout(800)
    assert mask_token(page) >= 2, "the new token isn't on the page to mask"
    shooter.detail(page, s, "settings-agents")


# ======================================================================== pages

def test_dashboard(shooter):
    page = shooter.page()
    shooter.open(page, "/")
    shooter.window(page, "dashboard")


def test_apps_list(shooter):
    page = shooter.page()
    shooter.open(page, "/apps", wait_for=".gb-list-foot")
    shooter.window(page, "apps-list")


def test_apps_row_menu(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, "/apps", wait_for=".gb-list-foot")
    button(page, "Actions for uptime-kuma").click()
    menu_item(page, "Restart").wait_for()
    page.wait_for_timeout(500)
    shooter.window(page, "apps-row-menu", mouse=False)


def test_app_overview(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}")
    shooter.window(page, "app-overview")


def test_app_more_menu(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}")
    button(page, "More actions").click()
    menu_item(page, "Rename").wait_for()
    page.wait_for_timeout(500)
    shooter.window(page, "app-more-menu", mouse=False)


def test_app_environment(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}/environment", wait_for="#sec-env")
    shooter.window(page, "app-environment")


def test_app_network(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}/network")
    shooter.window(page, "app-network")


def test_app_settings(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}/settings", wait_for="#sec-general")
    shooter.window(page, "app-settings")


def test_app_logs(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'mailpit')}/logs")
    page.wait_for_timeout(3000)
    shooter.window(page, "app-logs")


def test_app_activity(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}/activity")
    shooter.window(page, "app-activity")


def test_deploy_log(gitberth, shooter, demo):
    page = shooter.page()
    orders = app_of(gitberth, "orders-api")
    shooter.open(page, f"/apps/{orders.id}/deployments/{demo.manual_deploy}")
    page.wait_for_timeout(1500)
    shooter.window(page, "deploy-log")


def test_app_uptime(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}")
    card = page.locator("section#uptime")
    if "Up" not in card.inner_text():
        card.get_by_role("button", name="Check now").click()
        page.wait_for_timeout(4000)
    shooter.detail(page, card, "app-uptime")


def test_app_databases(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}/settings", wait_for="#sec-databases")
    shooter.detail(page, page.locator("#sec-databases"), "app-databases")


def test_app_compose_services(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'storefront')}")
    card = page.locator("section", has=page.locator(".gb-card-title", has_text="Services")).first
    until(lambda: "3 of 3 running" in flat(card.inner_text()), 60, "the Services card", seen=card.inner_text)
    shooter.detail(page, card, "app-compose-services")


def test_own_lan_address(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'uptime-kuma')}/network")
    shooter.detail(page, page.locator("section[aria-labelledby='gb-lanaddr-title']"), "own-lan-address")


def test_app_settings_restart(gitberth, shooter):
    """uptime-kuma's Restart section with a weekly scheduled restart typed in, not applied."""
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'uptime-kuma')}/settings", wait_for="#sec-restart")
    section = page.locator("#sec-restart")
    section.evaluate("e => e.scrollIntoView({block: 'center'})")
    if "Every" not in section.inner_text():
        section.get_by_role("switch").first.click()
    # Its amount and unit show once the server has drawn the switch on.
    unit = section.locator("select[aria-label='Unit']")
    many = section.locator("input[aria-label='How many']")
    many.wait_for()
    unit.select_option("Days")
    until(lambda: unit.input_value() == "Days", 15, "the unit to be days", seen=unit.input_value)
    many.fill("7")
    # Out of the field, as after typing it (no focus ring in the picture); the server has it then too.
    many.evaluate("e => e.blur()")
    page.wait_for_timeout(500)
    until(lambda: many.input_value() == "7" and unit.input_value() == "Days", 15, "every 7 days",
          seen=lambda: (many.input_value(), unit.input_value()))
    page.wait_for_timeout(800)
    shooter.detail(page, section, "app-settings-restart")


def test_app_settings_updates(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, "/apps/whoami/settings", wait_for="#sec-source")
    shooter.detail(page, page.locator("#sec-source"), "app-settings-updates")


def test_app_settings_tags(gitberth, shooter):
    """uptime-kuma's Settings › Image › Choose tag…: its tags newest stable first, on its variant, with the row it runs
    marked Running now (step 2 gave the stand-in its digest); then Cancel."""
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'uptime-kuma')}/settings", wait_for="#sec-source")
    page.locator("#sec-source").get_by_role("button", name="Choose tag…").click()
    dialog = page.locator(".mud-dialog")
    running = dialog.locator("#gb-dlg-tag-list [role=option]").filter(has_text="Running now")
    until(lambda: running.count() == 1, 60, "the row uptime-kuma runs, marked Running now", seen=lambda: dialog.inner_text())
    shooter.quiet(page)
    shooter.detail(page, dialog, "app-settings-tags", pad=0)
    dialog.get_by_role("button", name="Cancel").click()


def test_app_settings_build(gitberth, shooter):
    """website's Settings › Source: its repository and branch, and Build's three ways (Detect automatically chosen,
    Railpack, Custom Dockerfile) with what Gitberth detected."""
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}/settings", wait_for="#sec-source")
    page.locator("#sec-source").get_by_role("radiogroup", name="Build", exact=True).wait_for()
    shooter.quiet(page)
    shooter.detail(page, page.locator("#sec-source"), "app-settings-build")


def test_change_dialog(gitberth, shooter):
    """adminer's memory limit changed and Apply: the dialog, then Cancel and Discard."""
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'adminer')}/settings", wait_for="#sec-resources")
    box = page.locator("input[aria-label='Memory limit']")
    box.fill("256")
    box.dispatch_event("change")
    page.wait_for_timeout(800)
    page.get_by_role("region", name="Unsaved changes").get_by_role("button", name=re.compile(r"^(Review and apply|Apply)$")).click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    page.wait_for_timeout(2500)
    shooter.detail(page, dialog, "change-dialog", pad=0)
    dialog.get_by_role("button", name="Cancel").click()
    page.get_by_role("button", name="Discard").click()


def test_publish_port_dialog(gitberth, shooter):
    """postgres's 5432 switched from Internal to LAN, and the dialog that would apply it; cancelled."""
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'postgres')}/network")
    ports = page.locator("section", has=page.locator(".gb-card-title", has_text="Ports")).first
    ports.get_by_role("radio", name="LAN").first.click()
    page.wait_for_timeout(1000)
    page.get_by_role("button", name=re.compile(r"^(Review and apply|Apply)$")).first.click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    page.wait_for_timeout(2500)
    shooter.detail(page, dialog, "publish-port-dialog", pad=0)
    dialog.get_by_role("button", name="Cancel").click()
    discard = page.get_by_role("button", name="Discard")
    if discard.count():
        discard.first.click()


def test_remove_app_dialog(gitberth, shooter):
    """redis's Remove dialog with its volumes ticked; never confirmed."""
    page = shooter.page()
    shooter.open(page, "/apps/redis")
    button(page, "More actions").click()
    menu_item(page, "Remove").click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    dialog.get_by_text(re.compile("Also delete its (named )?volumes")).first.click()
    page.wait_for_timeout(800)
    shooter.detail(page, dialog, "remove-app-dialog", pad=0)
    dialog.get_by_role("button", name="Cancel").click()


def test_health(shooter):
    page = shooter.page()
    shooter.open(page, "/health")
    shooter.window(page, "health")


def test_health_charts(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, "/health")
    page.get_by_role("button", name="Show uptime-kuma's charts").click()
    hide = page.get_by_role("button", name="Hide uptime-kuma's charts")
    hide.wait_for()
    # The row and its charts, found afresh at each step: the page redraws with every reading, so a mark set on one of
    # its elements can be gone by the time the picture is taken.
    row = page.locator(".gb-hitem.open").filter(has=hide)
    # Its lines drawn (gitberth.js fills each chart's canvas), not the empty state of a container that just started.
    row.locator(".gb-chart-canvas > *").first.wait_for(timeout=90_000)
    page.wait_for_timeout(1500)
    shooter.detail(page, row, "health-charts", pad=10)


def test_network_page(shooter):
    page = shooter.page()
    shooter.open(page, "/network")
    shooter.window(page, "network-page")


def test_network_check_port(shooter):
    page = shooter.page()
    shooter.open(page, "/network")
    box = page.locator("input[aria-label='Check a port']")
    box.fill("8080")
    box.press("Enter")
    page.wait_for_timeout(2500)
    card = page.locator("section, .gb-card").filter(has=page.locator("input[aria-label='Check a port']")).last
    shooter.detail(page, card, "network-check-port")


def test_settings(shooter):
    page = shooter.page()
    shooter.open(page, "/settings")
    shooter.window(page, "settings")


@pytest.mark.parametrize("sid,name", [
    ("github", "settings-github"), ("github-integration", "settings-github-integration"), ("gitlab", "settings-gitlab"),
    ("notifications", "settings-notifications"), ("registries", "settings-registries"), ("updates", "settings-updates"),
    ("domains", "settings-domains"), ("uptime", "settings-uptime"), ("network", "settings-network"),
    ("own-lan", "settings-own-lan"), ("deploys", "settings-deploys"), ("databases", "settings-databases"),
    ("audit", "settings-audit"), ("keep", "settings-keep"), ("disk", "settings-disk"),
])
def test_settings_section(shooter, sid, name):
    page = shooter.page()
    s = settings_section(shooter, page, sid)
    shooter.quiet(page)
    shooter.detail(page, s, name)


def test_settings_previews(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}/settings", wait_for="#sec-previews")
    shooter.detail(page, page.locator("#sec-previews"), "settings-previews")


def audit_pick(entries):
    """The entry to open: a change admin made on Gitberth's pages with at least two fields in What changed; an app's
    settings first (the guide's own example), then Gitberth's; the most fields (up to 5), then the newest."""
    mine = [e for e in entries if e.get("actorKind") == "User" and e.get("via") == "UI"
            and e.get("outcome") == "Succeeded" and len(e.get("changes") or []) >= 2]
    if not mine:
        return None
    return max(mine, key=lambda e: (e["action"] == "app.settings.changed", e["action"].startswith("settings."),
                                    min(len(e["changes"]), 5), e["id"]))


def test_audit_log(shooter):
    """The Audit log page with one entry open. It's found in the log's own export and opened by its link
    (/audit?entry=N, as the copy button by its number makes), so the list stays at its top, as the page opens. The list
    behind the panel is blurred, so only the panel is read for values that aren't the docs' examples."""
    page = shooter.page()
    shooter.open(page, "/audit")
    page.locator(".audit-row:not(.audit-row--skel)").first.wait_for()
    export = page.evaluate("""async () => {
        const r = await fetch('/audit/export?format=jsonl&range=all', {cache: 'no-store'});
        return r.ok ? await r.text() : ''; }""")
    entries = [json.loads(line) for line in export.splitlines() if line.strip()]
    entry = audit_pick(entries)
    assert entry, f"no change by admin with what changed among the log's {len(entries)} entries"
    shooter.open(page, f"/audit?entry={entry['id']}")
    panel = page.locator(".audit-panel")
    panel.wait_for()
    until(lambda: "What changed" in panel.inner_text(), 15, f"entry {entry['id']} ({entry['action']}) to be open",
          seen=lambda: flat(panel.inner_text())[:200])
    page.wait_for_timeout(700)  # the panel slides in
    shooter.window(page, "audit-log", mouse=False, legible=".audit-panel")


def test_help_menu(shooter):
    page = shooter.page()
    shooter.open(page, "/apps", wait_for=".gb-list-foot")
    button(page, "Help").click()
    menu_item(page, "Help topics").wait_for()
    page.wait_for_timeout(500)
    shooter.window(page, "help-menu", mouse=False)


def test_help_page(shooter):
    page = shooter.page()
    shooter.open(page, "/help/guides/deploys-and-rollbacks")
    page.locator(".gb-mermaid[data-drawn] .gb-mermaid-svg svg").first.wait_for(timeout=30_000)
    page.wait_for_timeout(1000)
    shooter.window(page, "help-page")


def test_charts_fit_the_column(shooter):
    """Not a picture: every flow chart in Help, at this 1440-pixel window, against its page's column. Help draws a chart
    at no less than 80% of its own size, so a wider one runs past the column and scrolls sideways; those are listed (in
    charts.txt too), and the docs should draw them another way."""
    page = shooter.page()
    shooter.open(page, "/help")
    hrefs = sorted(set(page.locator("a[href^='help/']").evaluate_all("els => els.map(e => e.getAttribute('href').split('#')[0])")))
    lines, wide = [], []
    for href in hrefs:
        shooter.open(page, "/" + href)
        blocks = page.locator(".gb-mermaid")
        if blocks.count() == 0:
            continue
        until(lambda: page.locator(".gb-mermaid[data-drawn], .gb-mermaid[data-failed]").count() == blocks.count(), 60,
              f"{href}'s charts to be drawn")
        for i, m in enumerate(page.evaluate("""() => [...document.querySelectorAll('.gb-mermaid')].map(b => {
                const s = b.querySelector('.gb-mermaid-svg svg');
                return {drawn: b.hasAttribute('data-drawn'), natural: s ? Math.round(s.viewBox.baseVal.width) : 0,
                        shown: s ? Math.round(s.getBoundingClientRect().width) : 0, room: b.clientWidth - 36,
                        scrolls: b.scrollWidth > b.clientWidth + 1}; })""")):
            line = f"{href} #{i + 1}: natural {m['natural']} px, drawn {m['shown']} px, column {m['room']} px" + \
                   (": SCROLLS" if m["scrolls"] else "") + ("" if m["drawn"] else ": NOT DRAWN")
            lines.append(line)
            if m["scrolls"] or not m["drawn"]:
                wide.append(line)
    with open(os.path.join(config.OUT, "charts.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print("\n".join(lines))
    assert not wide, "charts that don't fit their column: " + "; ".join(wide)


def test_tip_open(gitberth, shooter):
    """An app's Settings tab with the tip for Gather stats open."""
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}/settings", wait_for="#sec-general")
    label = page.locator("[data-tip], .gb-tipped").filter(has_text=re.compile(r"^\s*Gather stats\s*$")).first
    label.evaluate("e => e.scrollIntoView({block: 'center'})")
    label.hover()
    page.locator("#gb-tipcard.on").wait_for(timeout=10_000)
    page.wait_for_timeout(600)
    shooter.window(page, "tip-open", mouse=False)


def test_status_page(shooter):
    page = shooter.page(signed_in=False)
    page.goto(shooter.gb.url("/status"))
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(1000)
    shooter.window(page, "status-page")


def test_down_page(gitberth, shooter):
    page = shooter.page()
    page.goto(shooter.gb.url(f"/down-page/preview?host={DOMAIN}"))
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(800)
    shooter.window(page, "down-page")


def test_login(shooter):
    page = shooter.page(signed_in=False)
    page.goto(shooter.gb.url("/login"))
    page.locator("#username").fill("admin")
    page.wait_for_timeout(800)
    shooter.window(page, "login")


# ======================================================================== last: the preview that's still building

def test_step_31_branch_preview(gitberth, standins):
    """feature/login's preview, which takes a while to build (its own Dockerfile pauses)."""
    website = app_of(gitberth, "website")
    standins.api("POST", "/github/repos/you/website/commits", {"branch": "feature/login", "files": D.LOGIN, "message": "Add a sign-in page",
                                                              "author": D.AUTHOR, "pusher": "you"})
    preview = gitberth.wait_preview(website.id, branch="feature/login", timeout=300)
    until(lambda: (gitberth.db.one("SELECT Status FROM Deployments WHERE AppId = ? ORDER BY Id DESC LIMIT 1", preview["Id"]) or {})
          .get("Status") == "Building", 300, "the branch preview's build to be under way")


def test_app_previews(gitberth, shooter):
    page = shooter.page()
    shooter.open(page, f"/apps/{key_of(gitberth, 'website')}/previews")
    page.wait_for_timeout(1500)
    shooter.window(page, "app-previews")
