"""Scenario 30: app templates (1.3).

New app › Templates is a gallery of self-hosted apps; Install makes one a Compose app with its database, secrets and
domain, then deploys it:
- the gallery lists every template, its search and category chips narrow it, and a card opens the template's page;
- a plain template (Memos) installs from its page: its first deploy works, the deploy page then says its first steps and
  offers Open, it answers on its LAN port, and its app page says which template it came from;
- a template with a Postgres database (Miniflux), with no Postgres server yet: its page says so and sets one up; Install
  then makes the app's own database and role, its connection string is set by Gitberth, its admin password is generated
  (a secret variable), and the app answers on its LAN port and signs in with that password;
- no secret is on any of its pages, in its deploy log, in Gitberth's log or in the audit log, which records each app as
  created from its template, and none of Gitberth's Docker work as made outside Gitberth;
- a domain added to Miniflux after install reaches it with one deploy without a build (Apply now): its APP_URL, and the
  BASE_URL its file takes from it, become the domain's https:// address;
- a template anyone could take over until it's set up (Memos), installed with a domain, gets that domain behind Cloudflare
  Access before its first deploy: a visitor who isn't signed in is sent to sign in;
- Delete with its volumes, images and its database's data leaves nothing behind.
"""
import base64
import json
import os
import re
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from urllib.parse import unquote, urlsplit

from e2e import audit, config
from e2e.net import get
from e2e.wait import until
from test_12_commit_statuses import add_domain
from test_16_protect import ALLOWED, access_app, domains_on, save_emails
from test_27_databases import SERVER, SERVER_SAID, create_server, databases_on_server, remove_server, roles_on_server


@dataclass
class TemplateApp:
    """An app installed from a template: its container is its web service's member, gb-<slug>_<web>."""
    id: int
    slug: str
    name: str
    web: str

    @property
    def key(self):
        return f"gb-{self.slug}_{self.web}"

    @property
    def container(self):
        return self.key

    @property
    def project(self):
        return f"gb-{self.slug}"


def flat(text):
    return " ".join((text or "").split())


def open_template(gitberth, page, template_id):
    """The template's page, once its plan is in (its address shows)."""
    gitberth.open(page, f"/apps/new?template={template_id}")
    page.locator("#gb-tpl-name").wait_for()
    needs = page.locator("section[aria-labelledby='gb-tpl-needs']")
    until(lambda: "…" not in needs.locator("dl").inner_text(), 60, "the template's plan", seen=lambda: needs.inner_text())
    return needs


def install(gitberth, page, template_id, name=None, domain=None):
    """Install on the template's page (with a domain when given, which needs domains on); returns the app, once its first
    deploy's page is open."""
    needs = open_template(gitberth, page, template_id)
    if name:
        page.locator("#gb-tpl-appname").fill(name)
    if domain:
        page.locator("#gb-tpl-domain").fill(domain)
        # The plan follows once typing pauses: the address shows the domain's.
        until(lambda: f"https://{domain}" in needs.locator("dl").inner_text(), 60, "the plan to take the domain",
              seen=lambda: needs.inner_text() + " " + gitberth.banners(page))
    button = needs.get_by_role("button", name=re.compile("^Install "))
    until(button.is_enabled, 60, "Install to be offered", seen=lambda: gitberth.banners(page))
    button.click()
    try:
        page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"), timeout=180_000)
    except Exception:  # noqa: BLE001
        raise AssertionError(f"Install didn't open the first deploy: {page.url} {gitberth.banners(page)}") from None
    app_id = int(re.search(r"/apps/(\d+)/deployments/", gitberth.path_of(page)).group(1))
    row = gitberth.db.app_by_id(app_id)
    return TemplateApp(row["Id"], row["Slug"], row["Name"], row["WebService"])


def delete_everything(gitberth, page, app, images=True):
    """Settings › Remove with its named volumes and its databases' data, and its images unless images=False."""
    gitberth.open(page, f"/apps/{app.key}/settings")
    page.locator("#sec-remove").get_by_role("button", name=re.compile("^Remove")).click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    # Its images go by default: the box is ticked already.
    for label in (() if images else ("Also delete its images",)) + ("Also delete its named volumes", "Also delete their data"):
        box = dialog.get_by_text(label)
        if box.count():
            box.first.click()
    dialog.get_by_role("button", name="Delete app").click()
    page.wait_for_url(lambda u: urlsplit(u).path == "/apps", timeout=120_000)


def secret_keys(gitberth, app_id):
    """The names of the app's own variables Gitberth keeps as secret."""
    return [v["Key"] for v in gitberth.db.query("SELECT Key FROM EnvVars WHERE AppId = ? AND IsSecret = 1", app_id)]


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def unsigned_status(url):
    """The status a browser that isn't signed in gets, its redirect not followed."""
    try:
        with urllib.request.build_opener(_NoRedirect).open(url, timeout=15) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code


def leftovers(engine, app):
    return {
        "containers": [c["Names"] for c in engine.containers(label=f"gitberth.app={app.slug}")],
        "volumes": [v for v in engine.volumes(prefix=app.project) if v.startswith(app.project + "_")],
        "network": engine.network_exists(f"{app.project}_default"),
        "images": engine.images(f"gitberth/{app.slug}/{app.web}"),
        "folder": os.path.exists(os.path.join(config.GBDATA, "compose", app.slug)),
    }


def test_the_gallery_lists_searches_and_filters_the_templates(page, gitberth):
    gitberth.open(page, "/apps/new")
    page.locator(".gb-srccard", has_text="Templates").click()
    gallery = page.locator("section[aria-labelledby='gb-tpl-title']")
    gallery.wait_for()
    cards = gallery.locator("a.gb-tpl-card")
    total = cards.count()
    assert 15 <= total <= 20, total
    assert gallery.get_by_role("radio", name=re.compile(r"^All\s*" + str(total) + "$")).get_attribute("aria-checked") == "true"
    # Every card has its icon, served to a signed-in browser only.
    src = cards.first.locator("img").get_attribute("src")
    assert unsigned_status(gitberth.url("/" + src)) in (302, 401), "an icon is served to a browser that isn't signed in"
    icon = page.request.get(gitberth.url("/" + src))
    assert icon.status == 200 and icon.headers["content-type"].startswith("image/"), (icon.status, icon.headers)
    assert "default-src 'none'" in icon.headers.get("content-security-policy", "")

    # Search: every word somewhere in a template.
    gallery.get_by_label("Search templates").fill("rss feeds")
    until(lambda: [c.strip() for c in cards.locator(".gb-tpl-card-name").all_inner_texts()] == ["Miniflux"], 15, "the search to find Miniflux alone",
          seen=lambda: cards.locator(".gb-tpl-card-name").all_inner_texts())
    gallery.get_by_label("Search templates").fill("")
    # A category chip.
    gallery.get_by_role("radio", name=re.compile("^Reading")).click()
    until(lambda: sorted(c.strip() for c in cards.locator(".gb-tpl-card-name").all_inner_texts()) == ["Linkding", "Miniflux"], 15,
          "the Reading chip", seen=lambda: cards.locator(".gb-tpl-card-name").all_inner_texts())
    gallery.get_by_label("Search templates").fill("nothing-matches-this")
    until(lambda: "No template matches" in gallery.inner_text(), 15, "the empty state")
    gallery.get_by_label("Search templates").fill("")

    # A card opens the template's page: what it is, its links, what it needs, its settings.
    cards.filter(has_text="Miniflux").click()
    page.wait_for_url(re.compile(r"/apps/new\?template=miniflux$"))
    page.locator("#gb-tpl-name").wait_for()
    text = flat(page.locator("#gb-page").inner_text())
    for said in ("Install Miniflux", "What it is", "What it needs", "Postgres", "miniflux/miniflux:2.", "Admin's user name", "Once it's running"):
        assert said in text, (said, text[:2000])
    assert page.locator(".gb-tpl-links").get_by_role("link", name="Source").get_attribute("href") == "https://github.com/miniflux/v2"
    page.get_by_role("link", name="All templates").click()
    page.wait_for_url(re.compile(r"/apps/new\?from=templates$"))
    gallery.wait_for()
    assert not gitberth.errors(page), gitberth.errors(page)


def test_templates_install_with_their_database_and_secrets_and_leave_nothing_behind(page, new_page, gitberth, engine, cloudflare, apps, uniq, scenario):
    first = audit.last_id()
    secrets = set()

    # ---- A plain template: Memos.
    memos = apps.track(install(gitberth, page, "memos"))
    assert memos.name.startswith("Memos"), memos
    row = gitberth.db.app_by_id(memos.id)
    assert (row["Source"], row["TemplateId"], row["WebService"], row["Container"]) == ("Compose", "memos", "memos", memos.key), row
    deploy = gitberth.wait_deploy(memos.id, timeout=900)
    assert deploy["Trigger"] == "Created", deploy
    # The deploy page, once it's up: Open and its first steps.
    first_steps = page.locator(".gb-banner", has_text="First steps with")
    first_steps.wait_for(timeout=60_000)
    assert "The first account you make is the admin." in first_steps.inner_text()
    assert page.get_by_role("link", name="Open", exact=True).count() >= 1
    lan = gitberth.lan_url(memos.id)
    until(lambda: get(lan + "/healthz")[0] == 200, 120, "Memos on its LAN port", seen=lambda: get(lan + "/healthz"))
    # Its app page says which template it came from, and it's a Compose app like any other.
    gitberth.open(page, f"/apps/{memos.key}")
    tag = page.get_by_role("link", name=re.compile("^Template: Memos"))
    tag.wait_for()
    assert tag.get_attribute("href") == "apps/new?template=memos"

    # ---- A template with a Postgres database: Miniflux. With no server, its page offers to set one up.
    made_server = False
    if not engine.exists(SERVER):
        needs = open_template(gitberth, page, "miniflux")
        banner = needs.locator(".gb-banner", has_text="It needs a Postgres server")
        banner.wait_for()
        assert needs.get_by_role("button", name="Install Miniflux").is_disabled()
        banner.get_by_role("button", name="Set up a Postgres server").click()
        said = flat(gitberth.snackbar(page, SERVER_SAID, timeout=600_000))
        assert said == f"The Postgres server {SERVER} is ready: new databases go on it.", said
        made_server = True
        until(lambda: needs.get_by_role("button", name="Install Miniflux").is_enabled(), 60, "Install once the server is there",
              seen=lambda: gitberth.banners(page))
    else:
        create_server(gitberth, page, engine)
    try:
        flux = apps.track(install(gitberth, page, "miniflux"))
        row = gitberth.db.app_by_id(flux.id)
        assert (row["Source"], row["TemplateId"]) == ("Compose", "miniflux"), row
        # Its database: made before its first deploy, a database and a role of its own on the server.
        dbs = gitberth.db.query("SELECT * FROM ManagedDatabases WHERE AppId = ?", flux.id)
        assert [(d["Kind"], d["Server"], d["Variable"]) for d in dbs] == [("Postgres", SERVER, "DATABASE_URL")], dbs
        database, role = dbs[0]["Name"], dbs[0]["Role"]
        assert database in databases_on_server(engine) and role in roles_on_server(engine)
        assert sorted(secret_keys(gitberth, flux.id)) == ["ADMIN_PASSWORD"], secret_keys(gitberth, flux.id)
        deploy = gitberth.wait_deploy(flux.id, timeout=900)
        assert deploy["Trigger"] == "Created", deploy
        env = engine.env(flux.container)
        url = env["DATABASE_URL"]
        parts = urlsplit(url)
        assert (parts.scheme, unquote(parts.username), parts.hostname, parts.path.lstrip("/"), parts.query) == ("postgres", role, SERVER, database, "sslmode=disable"), \
            url.replace(parts.password or "", "****")
        password = env["ADMIN_PASSWORD"]
        assert re.fullmatch(r"[A-Za-z0-9]{20}", password)
        secrets |= {password, unquote(parts.password)}
        lan = gitberth.lan_url(flux.id)
        until(lambda: get(lan + "/healthcheck")[0] == 200, 120, "Miniflux on its LAN port", seen=lambda: get(lan + "/healthcheck"))
        # It signs in with the generated password (its API takes the admin's user name and password).
        req = urllib.request.Request(lan + "/v1/me", headers={"Authorization": "Basic " + base64.b64encode(f"{env['ADMIN_USERNAME']}:{password}".encode()).decode()})
        with urllib.request.urlopen(req, timeout=15) as r:
            me = json.loads(r.read().decode())
        assert me["username"] == "admin" and me["is_admin"], me

        # ---- No secret on any page, in a deploy log, Gitberth's log or the audit log.
        shown = []
        for path in ("/apps/new?template=miniflux", f"/apps/{flux.id}/deployments/{deploy['Id']}", f"/apps/{flux.key}", f"/apps/{flux.key}/settings",
                     f"/apps/{flux.key}/environment", f"/apps/{flux.key}/activity", "/audit"):
            gitberth.open(page, path)
            page.wait_for_timeout(1500)
            shown.append(page.content())
        logs = [gitberth.deploy_log(d["Id"]) for d in gitberth.db.deployments(flux.id) + gitberth.db.deployments(memos.id)]
        entries = audit.since(first)
        kept = json.dumps(entries) + "\n".join(logs) + gitberth.logs(since=scenario.t0) + "\n".join(shown)
        leaked = [s for s in secrets if s and s in kept]
        assert leaked == [], f"{len(leaked)} secret(s) shown or kept"
        created = [e["summary"] for e in entries if e["action"] == "app.created"]
        assert any("from the template memos" in s for s in created) and any("from the template miniflux" in s for s in created), created
        time.sleep(audit.JOIN_WAIT)
        outside = [(e["action"], e["summary"]) for e in audit.since(first, "actor_kind = 'Docker'")
                   if any(a.slug in ((e["summary"] or "") + (e["container"] or "")) for a in (memos, flux))]
        assert outside == [], outside

        # ---- Domains: Miniflux gets one after install, and Memos, which anyone could take over until it's set up, one at install.
        zone, team = f"{uniq('tz')}.test", uniq("team")
        cloudflare.zone(zone)
        cloudflare.set_team(team)
        try:
            with domains_on(gitberth, new_page, cloudflare, cloudflare.settings_token()):
                save_emails(gitberth, page, ALLOWED)
                # A domain added after install: one deploy without a build (Apply now) tells Miniflux its new address.
                host = f"rss.{zone}"
                add_domain(gitberth, page, flux, host)
                before = gitberth.db.last_deployment_id(flux.id)
                gitberth.open(page, f"/apps/{flux.key}")
                pending = page.locator(".gb-banner", has_text="Saved, not live yet")
                pending.wait_for(timeout=60_000)
                pending.get_by_role("button", name="Apply now").click()
                page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"), timeout=60_000)
                applied = gitberth.wait_deploy(flux.id, after=before, timeout=300)
                assert applied["Trigger"] == "ConfigChange", applied
                assert engine.env(flux.container).get("BASE_URL") == f"https://{host}", engine.env(flux.container).get("BASE_URL")
                assert f"APP_URL=https://{host}" in gitberth.deploy_log(applied["Id"]), gitberth.deploy_log(applied["Id"], tail=30)
                until(lambda: cloudflare.visit(f"https://{host}/healthcheck").get("status") == 200, 90, "Miniflux through its domain",
                      seen=lambda: cloudflare.visit(f"https://{host}/healthcheck"))

                # A domain at install, for a template whose first visitor sets it up: behind Cloudflare Access before it runs.
                notes = f"notes.{zone}"
                guarded = apps.track(install(gitberth, page, "memos", domain=notes))
                assert [(d["Hostname"], d["Protected"]) for d in gitberth.db.domains(guarded.id)] == [(notes, 1)], gitberth.db.domains(guarded.id)
                gitberth.wait_deploy(guarded.id, timeout=600)
                steps = page.locator(".gb-banner", has_text="First steps with")
                steps.wait_for(timeout=60_000)
                assert "behind Cloudflare Access" in steps.inner_text(), steps.inner_text()
                anonymous = until(lambda: (lambda r: r if r.get("status") == 302 else None)(cloudflare.visit(f"https://{notes}/")), 90,
                                  "a visitor who isn't signed in to be sent to sign in", seen=lambda: cloudflare.visit(f"https://{notes}/"))
                assert "cloudflareaccess.com" in (anonymous["headers"].get("Location") or ""), anonymous
                assert access_app(cloudflare, notes) is not None

                # ---- Delete: everything goes, its database and role too, and the domains' Access application.
                for app in (guarded, memos, flux):
                    delete_everything(gitberth, page, app)
                    until(lambda: not any(leftovers(engine, app).values()), 120, f"{app.name} to leave nothing behind", seen=lambda: leftovers(engine, app))
                until(lambda: access_app(cloudflare, notes) is None, 90, "the Access application Gitberth made to go")
                save_emails(gitberth, page, "")
        finally:
            cloudflare.set_team(None)
        assert database not in databases_on_server(engine) and role not in roles_on_server(engine)
        assert gitberth.db.query("SELECT * FROM ManagedDatabases WHERE AppId = ?", flux.id) == []
        assert not engine.image_exists("neosmemo/memos:26.11.0") and not engine.image_exists("miniflux/miniflux:2.3.5"), engine.images()
        assert not gitberth.errors(page), gitberth.errors(page)
    finally:
        if made_server:
            remove_server(gitberth, page, engine)
