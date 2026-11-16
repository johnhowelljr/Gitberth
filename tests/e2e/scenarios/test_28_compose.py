"""Scenario 28: Compose apps (1.3).

A repo with a compose.yaml of two services (the test app as web, built from the repo, and Redis as its cache) is one app:
- New app finds the Compose file and, with two services, offers Compose first; Create and deploy makes one app whose
  container is the web service's (gb-<slug>_web), its project gb-<slug>, every member labelled as the app's;
- the web answers on its LAN port, with the app's variables filling the file's ${…}, and reaches its cache by name;
- with a domain, it answers through Cloudflare and the tunnel;
- a new commit redeploys it; a broken one (the web service exits) rolls back to the release before, which keeps serving;
- a pull request's preview is a project of its own (gb-<preview slug>), its cache without host ports, never on production's
  network even when its file names it; a provider service, and a .env that's a link to Gitberth's environment, are refused;
- Stop and Start act on every service; Delete takes everything away (containers, network, volumes, images);
- a pasted two-service Compose file becomes one inline app that deploys; once its image's tag moved on, a failed deploy's
  rollback runs the image the release before ran, not the tag's new one;
- none of Gitberth's own Docker work is recorded as a change made outside Gitberth.
"""
import re
import time
from dataclasses import dataclass

from e2e import audit
from e2e.engine import docker
from e2e.net import get_json
from e2e.wait import until
from test_02_pull_request_previews import PULLS_EVERY
from test_12_commit_statuses import add_domain, domains_turned_on

COMPOSE = """services:
  web:
    build: .
    ports:
      - "8080:8080"
    environment:
      GREETING: ${GREETING:-hello}
      CACHE_HOST: cache
    depends_on:
      - cache
  cache:
    image: redis:8-alpine
    volumes:
      - cachedata:/data
volumes:
  cachedata:
"""

# The web service exits as it starts, and nothing restarts it: docker compose up --wait fails at once.
BROKEN = COMPOSE.replace("    build: .\n", "    build: .\n    restart: \"no\"\n    command: [\"python3\", \"-c\", \"import sys; sys.exit(3)\"]\n")

# A preview's file that names production's default network (D-1): the preview must stay on its own.
def on_productions_network(slug):
    return COMPOSE + f"networks:\n  default:\n    name: gb-{slug}_default\n"


# A provider service (D-2): Compose would run its type as a program where it runs.
WITH_PROVIDER = COMPOSE.replace("  cache:\n", "  evil:\n    provider:\n      type: sh\n      options:\n        x: y\n  cache:\n")

# The pasted app's site runs a tag of this rig's own, which the test moves to another image later (D-3).
MOVING = "e2e/moving:1"

PASTED = """services:
  site:
    image: e2e/moving:1
    command: ["python3", "-m", "http.server", "8000", "--directory", "/srv"]
    ports:
      - "8000:8000"
    environment:
      MESSAGE: ${MESSAGE:-pasted}
  cache:
    image: redis:8-alpine
"""


@dataclass
class ComposeApp:
    """A Compose app: its container is its web service's member, gb-<slug>_<web>."""
    id: int
    slug: str
    name: str
    web: str = "web"

    @property
    def key(self):
        return f"gb-{self.slug}_{self.web}"

    @property
    def container(self):
        return self.key

    @property
    def project(self):
        return f"gb-{self.slug}"

    def member(self, service):
        return f"gb-{self.slug}_{service}"


def flat(text):
    return " ".join((text or "").split())


def created_app(gitberth, page):
    """The app whose page (or first deploy's page) New app opened."""
    # Playwright follows the page's URL only while it's asked something: wait_for_url, not a poll of page.url.
    try:
        page.wait_for_url(re.compile(r"/apps/(gb-[a-z0-9-]+_[A-Za-z0-9._-]+|\d+/deployments/\d+)$"), timeout=180_000)
    except Exception:  # noqa: BLE001
        raise AssertionError(f"New app didn't open the new app: {page.url} {gitberth.banners(page)}") from None
    path = gitberth.path_of(page)
    if m := re.search(r"/apps/(\d+)/deployments/\d+$", path):
        row = gitberth.db.app_by_id(int(m.group(1)))
    else:
        key = path.rsplit("/", 1)[-1]
        row = until(lambda: gitberth.db.one("SELECT * FROM Apps WHERE Container = ?", key), 30, f"the app {key} in Gitberth's database")
    return ComposeApp(row["Id"], row["Slug"], row["Name"], row["WebService"])


def act(gitberth, page, app, verb):
    """The app page's Stop or Start (Stop asks first); returns the snackbar."""
    gitberth.open(page, f"/apps/{app.key}")
    page.locator(".gb-hero-actions").get_by_role("button", name=f"{verb} {app.name}").click()
    if verb == "Stop":
        gitberth.confirm(page, "Stop")
    return gitberth.snackbar(page, r"^(Stopped|Started) ")


def fill_variable(gitberth, page, app, name, value):
    """The Environment tab: the variable New app made for the Compose file's ${name} gets a value, then Apply; returns the
    deploy without a build that applies it."""
    gitberth.open(page, f"/apps/{app.key}/environment")
    table = page.get_by_role("table", name="Environment variables")
    table.wait_for()
    table.get_by_label(f"Value of {name}", exact=True).fill(value)
    bar = page.get_by_role("region", name="Unsaved changes")
    until(lambda: "deploy without a build" in bar.inner_text(), 30, "the apply bar to say a deploy without a build applies it", seen=bar.inner_text)
    bar.get_by_role("button", name=re.compile(r"^(Review and apply|Apply)$")).click()
    page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"), timeout=60_000)
    return int(gitberth.path_of(page).rsplit("/", 1)[-1])


def members(engine, app):
    return {c["Labels"].split("gitberth.service=")[1].split(",")[0]: c for c in engine.containers(label=f"gitberth.app={app.slug}")
            if "gitberth.service=" in c["Labels"]}


def test_a_repo_with_a_compose_file_is_one_app_with_all_its_services(page, new_page, gitberth, github, cloudflare, engine, apps, uniq):
    first_entry = audit.last_id()
    repo = github.create_repo(uniq("cmp"), files={"compose.yaml": COMPOSE, "marker.txt": "compose v1"})

    # ---- New app finds the Compose file; with two services, Compose is the first choice.
    gitberth.open(page, f"/apps/new?url={repo.url}")
    section = page.locator("section[aria-labelledby='gb-compose-title']")
    section.wait_for(timeout=180_000)
    assert "This repo has a compose.yaml with 2 services" in flat(section.inner_text()), section.inner_text()
    assert section.get_by_role("radio", name=re.compile("^With Compose")).get_attribute("aria-checked") == "true"
    assert section.get_by_role("radiogroup", name="Web service").get_by_role("radio", name="web").get_attribute("aria-checked") == "true"
    page.locator("#git-name").wait_for()
    page.get_by_role("button", name="Create and deploy").click()
    app = apps.track(created_app(gitberth, page))
    assert (app.web, gitberth.db.app_by_id(app.id)["ComposeFile"], gitberth.db.app_by_id(app.id)["Source"]) == ("web", "compose.yaml", "Git")
    one = gitberth.wait_deploy(app.id, timeout=600)
    sha = repo.branch_sha("main")
    assert one["CommitSha"] == sha, one
    log = gitberth.deploy_log(one["Id"])
    assert "Reading compose.yaml with docker compose config" in log, log[-3000:]
    assert f"Building web (gitberth/{app.slug}/web:{sha[:7]})" in log, log[-3000:]

    # ---- One project, every member the app's, the web service its container.
    found = members(engine, app)
    assert sorted(found) == ["cache", "web"], found
    for service, c in found.items():
        labels = engine.labels(c["Names"])
        assert c["Names"] == app.member(service), c["Names"]
        assert (labels["gitberth.managed"], labels["gitberth.app"], labels["gitberth.service"], labels["com.docker.compose.project"]) == \
            ("true", app.slug, service, app.project), labels
        assert labels["gitberth.deployment"] == str(one["Id"]), labels
        assert engine.inspect(c["Names"])["HostConfig"]["RestartPolicy"]["Name"] == "unless-stopped"
    assert engine.image_of(app.container) == f"gitberth/{app.slug}/web:{sha[:7]}"
    # The web service is on the shared network as the app's name; the cache only on the app's own.
    web_networks = engine.inspect(app.container)["NetworkSettings"]["Networks"]
    assert app.slug in (web_networks["gitberth"].get("Aliases") or []), web_networks
    assert engine.networks(app.member("cache")) == [f"{app.project}_default"], engine.networks(app.member("cache"))
    assert engine.host_ports(app.member("cache")) == {"6379/tcp": []}, engine.host_ports(app.member("cache"))

    # ---- On its LAN port, with the file's default for GREETING, reaching its cache by name.
    lan = gitberth.lan_url(app.id)
    env = gitberth.wait_answers(lan, "/env")
    assert (env["GREETING"], env["CACHE_HOST"]) == ("hello", "cache"), env
    engine.exec(app.container, "python3", "-c", "import socket; socket.create_connection(('cache', 6379), timeout=5).close()")

    # ---- The Apps list shows one app, every service counted.
    gitberth.open(page, "/apps")
    row = page.locator(".gb-arow", has_text=app.name)
    until(lambda: "Running 2/2" in flat(row.first.inner_text()), 60, "the Apps row to say Running 2/2", seen=lambda: row.first.inner_text())
    assert app.member("cache") not in gitberth.app_list_keys(page)
    # Its page: a Services card with both.
    gitberth.open(page, f"/apps/{app.key}")
    card = page.locator("section", has=page.locator(".gb-card-title", has_text="Services"))
    card.wait_for()
    until(lambda: "2 of 2 running" in flat(card.inner_text()), 30, "the Services card", seen=card.inner_text)
    assert "cache" in card.inner_text() and "redis:8-alpine" in card.inner_text(), card.inner_text()

    # ---- A variable of the app's fills the file's ${GREETING} (New app made it, empty): a deploy without a build applies
    # it, and only the web service, which uses it, is recreated: the cache keeps running.
    cache_id = engine.inspect(app.member("cache"))["Id"]
    applied = fill_variable(gitberth, page, app, "GREETING", "from gitberth")
    two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=300)
    assert (two["Id"], two["Trigger"]) == (applied, "ConfigChange"), two
    until(lambda: (get_json(lan + "/env") or {}).get("GREETING") == "from gitberth", 60, "GREETING from the app's variable")
    assert engine.inspect(app.member("cache"))["Id"] == cache_id, "the cache was recreated though nothing of it changed"
    assert "Unchanged since the release that runs, so not restarted: cache." in gitberth.deploy_log(two["Id"]), gitberth.deploy_log(two["Id"], tail=40)

    # ---- Through a domain.
    zone = f"{uniq('cz')}.test"
    cloudflare.zone(zone)
    host = f"app.{zone}"
    with domains_turned_on(gitberth, new_page, cloudflare):
        add_domain(gitberth, page, app, host)
        through = until(lambda: (lambda r: r if r.get("status") == 200 else None)(cloudflare.visit(f"https://{host}/")), 90,
                        "the app through its domain", seen=lambda: cloudflare.visit(f"https://{host}/"))
        assert '"marker": "compose v1"' in through["body"], through["body"]

        # ---- A new commit redeploys it.
        v2 = repo.commit({"marker.txt": "compose v2"}, message="Compose v2")["sha"]
        three = until(lambda: gitberth.db.one("SELECT * FROM Deployments WHERE AppId = ? AND CommitSha = ? AND Status IN ('Succeeded','Failed','RolledBack')", app.id, v2),
                      420, "the new commit's deploy")
        assert three["Status"] == "Succeeded", (three["Error"], gitberth.deploy_log(three["Id"], tail=40))
        until(lambda: (get_json(lan + "/") or {}).get("marker") == "compose v2", 60, "the new commit on the LAN")
        until(lambda: '"marker": "compose v2"' in (cloudflare.visit(f"https://{host}/").get("body") or ""), 60, "the new commit on the domain")

        # ---- A broken commit: its web service exits, so the release before comes back, and keeps serving.
        bad = repo.commit({"compose.yaml": BROKEN, "marker.txt": "compose broken"}, message="Broken")["sha"]
        four = until(lambda: gitberth.db.one("SELECT * FROM Deployments WHERE AppId = ? AND CommitSha = ? AND Status IN ('Succeeded','Failed','RolledBack')", app.id, bad),
                     420, "the broken commit's deploy")
        assert four["Status"] == "RolledBack", (four["Status"], four["Error"], gitberth.deploy_log(four["Id"], tail=40))
        assert f"Bringing the release of deploy #{three['Id']} back" in gitberth.deploy_log(four["Id"]), gitberth.deploy_log(four["Id"], tail=40)
        until(lambda: (get_json(lan + "/") or {}).get("marker") == "compose v2", 90, "the release before to serve again")
        assert engine.image_of(app.container) == f"gitberth/{app.slug}/web:{v2[:7]}"
        assert sorted(members(engine, app)) == ["cache", "web"]
        good = repo.commit({"compose.yaml": COMPOSE, "marker.txt": "compose v3"}, message="Fixed")["sha"]
        until(lambda: gitberth.db.one("SELECT * FROM Deployments WHERE AppId = ? AND CommitSha = ? AND Status = 'Succeeded'", app.id, good), 420, "the fix to deploy")

    # ---- A pull request's preview: a project of its own, its cache without host ports.
    gitberth.set_previews(page, app, mode="Pull requests")
    # The pull request's file names production's default network: the preview stays on its own (D-1).
    pr_sha = repo.commit({"marker.txt": "compose preview", "compose.yaml": on_productions_network(app.slug)}, branch="feature", base="main",
                         message="Preview")["sha"]
    pull = repo.open_pull("feature", title="Compose preview")
    preview = gitberth.wait_preview(app.id, pr=pull["number"], timeout=180)

    def preview_deploy(sha):
        return until(lambda: gitberth.db.one("SELECT * FROM Deployments WHERE AppId = ? AND CommitSha = ? AND Status IN ('Succeeded','Failed','RolledBack')",
                                             preview["Id"], sha), 420, f"the preview's deploy of {sha[:7]}")

    pdeploy = preview_deploy(pr_sha)
    assert pdeploy["Status"] == "Succeeded", (pdeploy["Error"], gitberth.deploy_log(pdeploy["Id"], tail=40))
    pv = ComposeApp(preview["Id"], preview["Slug"], preview["Name"])
    assert preview["Container"] == pv.key, preview
    assert engine.labels(pv.member("cache"))["com.docker.compose.project"] == pv.project
    assert engine.host_ports(pv.member("cache")) == {"6379/tcp": []}
    assert (get_json(gitberth.lan_url(pv.id) + "/") or {}).get("marker") == "compose preview"
    assert f"{pv.project}_cachedata" in engine.volumes(), engine.volumes(prefix="gb-")
    assert engine.networks(pv.member("cache")) == [f"{pv.project}_default"], engine.networks(pv.member("cache"))
    assert f"{app.project}_default" not in engine.networks(pv.container), engine.networks(pv.container)
    assert "The network default is the preview's own, not " + f"{app.project}_default" in gitberth.deploy_log(pdeploy["Id"])

    # A provider service (a program Compose would run where it runs) is refused, and the preview keeps running as it was (D-2).
    bad = repo.commit({"compose.yaml": WITH_PROVIDER}, branch="feature", message="A provider")["sha"]
    refused = preview_deploy(bad)
    assert refused["Status"] == "Failed" and "is a provider service" in (refused["Error"] or ""), (refused["Status"], refused["Error"])
    assert not engine.exists(f"{pv.project}_evil") and not engine.exists(pv.member("evil"))
    # A .env that's a link to Gitberth's own environment is never read (D-5).
    linked = repo.commit({"compose.yaml": COMPOSE, ".env": {"symlink": "/proc/1/environ"}}, branch="feature", message="A linked .env")["sha"]
    refused = preview_deploy(linked)
    assert refused["Status"] == "Failed" and "link out of the repo" in (refused["Error"] or ""), (refused["Status"], refused["Error"])
    assert (get_json(gitberth.lan_url(pv.id) + "/") or {}).get("marker") == "compose preview"
    # Without a webhook the watcher finds a closed pull request on its next list of them (PULLS_EVERY): Stop and Start
    # are tried meanwhile.
    repo.close_pull(pull["number"])

    # ---- Stop and Start act on every service.
    assert act(gitberth, page, app, "Stop").startswith("Stopped")
    until(lambda: all(c["State"] == "exited" for c in members(engine, app).values()), 60, "every service to stop",
          seen=lambda: {s: c["State"] for s, c in members(engine, app).items()})
    assert act(gitberth, page, app, "Start").startswith("Started")
    until(lambda: all(c["State"] == "running" for c in members(engine, app).values()) and len(members(engine, app)) == 2, 90,
          "every service to run again", seen=lambda: {s: c["State"] for s, c in members(engine, app).items()})
    gitberth.wait_answers(lan, "/env")

    # ---- The closed pull request's preview goes, with its whole project.
    gitberth.wait_no_preview(app.id, pr=pull["number"], timeout=PULLS_EVERY + 120)
    until(lambda: not engine.containers(label=f"gitberth.app={pv.slug}"), 60, "the preview's containers to go")
    assert not engine.network_exists(f"{pv.project}_default")
    assert not engine.volume_exists(f"{pv.project}_cachedata"), engine.volumes(prefix="gb-")

    # ---- Gitberth's own Docker work is never a change made outside Gitberth.
    time.sleep(audit.JOIN_WAIT)
    outside = [(e["action"], e["summary"]) for e in audit.since(first_entry, "actor_kind = 'Docker'")
               if app.slug in (e["summary"] or "") or app.slug in (e["container"] or "")]
    assert outside == [], outside

    # ---- Delete: everything goes.
    gitberth.delete_app(page, app)
    until(lambda: not engine.containers(label=f"gitberth.app={app.slug}"), 120, "the app's containers to go",
          seen=lambda: [c["Names"] for c in engine.containers(label=f"gitberth.app={app.slug}")])
    assert not engine.network_exists(f"{app.project}_default")
    assert not engine.volume_exists(f"{app.project}_cachedata"), engine.volumes(prefix="gb-")
    assert engine.images(f"gitberth/{app.slug}/web") == [], engine.images()
    assert not gitberth.errors(page), gitberth.errors(page)


def test_a_pasted_compose_file_is_one_inline_app_with_all_its_services(page, gitberth, engine, apps, uniq):
    name = uniq("paste")
    # The site's image is a tag of the rig's own, on Python for now.
    docker("pull", "-q", "python:3.12-alpine", timeout=300)
    docker("tag", "python:3.12-alpine", MOVING)
    python_id = docker("image", "inspect", "--format", "{{.Id}}", MOVING).strip()
    gitberth.open(page, "/apps/new?from=paste")
    page.get_by_role("radiogroup", name="How").get_by_role("radio", name="Compose").click()
    page.get_by_label("A Compose file").fill(PASTED)
    choice = page.get_by_role("radio", name=re.compile("^One app with all 2 services"))
    choice.wait_for(timeout=30_000)
    assert choice.get_attribute("aria-checked") == "true"
    assert page.get_by_role("radiogroup", name="Web service").get_by_role("radio", name="site").get_attribute("aria-checked") == "true"
    assert page.locator("#gb-whole-port").input_value() == "8000"
    page.locator("#gb-whole-name").fill(name)
    page.get_by_role("button", name="Create and deploy").click()
    app = apps.track(created_app(gitberth, page))
    row = gitberth.db.app_by_id(app.id)
    assert (row["Source"], row["WebService"], row["Container"]) == ("Compose", "site", f"gb-{app.slug}_site"), row
    assert row["ComposeYamlProtected"] and "redis" not in row["ComposeYamlProtected"], "the file is kept encrypted"
    first = gitberth.wait_deploy(app.id, timeout=600)
    assert first["Trigger"] == "Created", first
    assert sorted(members(engine, app)) == ["cache", "site"]
    assert engine.env(app.container)["MESSAGE"] == "pasted"
    lan = gitberth.lan_url(app.id)
    serves = lambda: (lambda r: r if r and r[0] == 200 else None)(__import__("e2e.net", fromlist=["get"]).get(lan + "/"))  # noqa: E731
    until(serves, 60, "the pasted app on the LAN")
    # The deploy recorded what it ran, and keeps it by a tag of the app's own.
    pin = f"gitberth/{app.slug}/site:pin-{python_id.removeprefix('sha256:')[:12]}"
    assert pin in engine.images(f"gitberth/{app.slug}/site"), engine.images(f"gitberth/{app.slug}/site")

    # ---- The tag moves on to an image the site can't run on (no Python in it); then a change of the file deploys it, fails,
    # and the release before comes back with the image it ran, not the tag's new one (D-3).
    docker("tag", "redis:8-alpine", MOVING)
    gitberth.open(page, f"/apps/{app.key}/settings")
    yaml = page.locator("#set-compose-yaml")
    yaml.wait_for()
    yaml.fill(PASTED.replace("MESSAGE: ${MESSAGE:-pasted}", "MESSAGE: ${MESSAGE:-pasted}\n      EXTRA: \"1\""))
    page.get_by_role("button", name="Save and deploy").click()
    page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"), timeout=60_000)
    second = gitberth.wait_deploy(app.id, after=first["Id"], statuses=("RolledBack",), timeout=420)
    log = gitberth.deploy_log(second["Id"])
    assert f"Bringing the release of deploy #{first['Id']} back" in log, log[-3000:]
    assert f"site ({pin})" in log, log[-3000:]
    assert engine.image_of(app.container) == pin, engine.image_of(app.container)
    assert engine.inspect(app.container)["Image"] == python_id
    until(serves, 60, "the release before to serve again")

    gitberth.delete_app(page, app)
    until(lambda: not engine.containers(label=f"gitberth.app={app.slug}"), 120, "the pasted app's containers to go")
    assert engine.images(f"gitberth/{app.slug}/site") == [], engine.images()
    assert not gitberth.errors(page), gitberth.errors(page)
    docker("image", "rm", MOVING, check=False)
