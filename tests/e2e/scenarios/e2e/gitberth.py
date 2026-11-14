"""Gitberth under test: its pages (Playwright), its database (read only, for waiting on states), its deploy logs, and
the flows the scenarios share (New app, Deploy, previews settings, removing an app, Settings)."""
import contextlib
import os
import re
import sqlite3
import time
from dataclasses import dataclass
from urllib.parse import quote, urlsplit

from playwright.sync_api import TimeoutError as PlaywrightTimeout

from . import config
from .engine import Engine, gitberth_logs
from .net import get, get_json
from .wait import until

# A deploy's states once it's over (DeployStatus). Skipped: a new commit the build filters, or its message, left alone.
FINAL = ("Succeeded", "Failed", "RolledBack", "Cancelled", "Skipped")


@dataclass
class App:
    """An app Gitberth has: its database id, slug and name. Its page and container are gb-<slug>."""
    id: int
    slug: str
    name: str

    @property
    def key(self):
        return f"gb-{self.slug}"

    @property
    def container(self):
        return f"gb-{self.slug}"

    @property
    def image_repository(self):
        return f"gitberth/{self.slug}"


class Db:
    """Gitberth's SQLite database, read only (short read-only connections, so Gitberth's writes are never held up). Tables
    and columns are the EF Core entities' names: Apps, Deployments, Previews, Ports, Volumes, EnvVars, Domains, Settings,
    ActivityEntries. Enums are stored as their names ("Succeeded", "Branch"), booleans as 0/1, times as UTC text."""

    def __init__(self, path=os.path.join(config.GBDATA, "gitberth.db")):
        self.path = path

    def query(self, sql, *params):
        for attempt in range(20):
            try:
                with contextlib.closing(sqlite3.connect(f"file:{self.path}?mode=ro", uri=True, timeout=10, isolation_level=None)) as c:
                    c.row_factory = sqlite3.Row
                    return [dict(r) for r in c.execute(sql, params).fetchall()]
            except sqlite3.OperationalError:
                if attempt == 19:
                    raise
                time.sleep(0.25)
        return []

    def one(self, sql, *params):
        rows = self.query(sql, *params)
        return rows[0] if rows else None

    def app(self, slug):
        return self.one("SELECT * FROM Apps WHERE Slug = ?", slug)

    def app_by_id(self, app_id):
        return self.one("SELECT * FROM Apps WHERE Id = ?", app_id)

    def apps(self):
        return self.query("SELECT * FROM Apps ORDER BY Id")

    def deployments(self, app_id):
        """Newest first."""
        return self.query("SELECT * FROM Deployments WHERE AppId = ? ORDER BY Id DESC", app_id)

    def deployment(self, deployment_id):
        return self.one("SELECT * FROM Deployments WHERE Id = ?", deployment_id)

    def last_deployment_id(self, app_id):
        row = self.one("SELECT MAX(Id) AS Id FROM Deployments WHERE AppId = ?", app_id)
        return (row or {}).get("Id") or 0

    def previews(self, parent_id):
        """The parent's previews: each preview's Apps row, with its Previews row as Kind, PreviewBranch, PullRequest,
        PreviewTitle, Ref, HeadSha, FromFork, CommentId, LastActivityAt, PreviewCreatedAt."""
        return self.query(
            "SELECT a.*, p.Kind AS Kind, p.Branch AS PreviewBranch, p.PullRequest AS PullRequest, p.Title AS PreviewTitle, p.Ref AS Ref,"
            " p.HeadSha AS HeadSha, p.FromFork AS FromFork, p.CommentId AS CommentId, p.LastActivityAt AS LastActivityAt,"
            " p.CreatedAt AS PreviewCreatedAt FROM Previews p JOIN Apps a ON a.Id = p.AppId WHERE p.ParentAppId = ? ORDER BY a.Id", parent_id)

    def ports(self, app_id):
        return self.query("SELECT * FROM Ports WHERE AppId = ? ORDER BY Id", app_id)

    def web_port(self, app_id):
        return self.one("SELECT * FROM Ports WHERE AppId = ? AND IsWeb = 1", app_id)

    def volumes(self, app_id):
        return self.query("SELECT * FROM Volumes WHERE AppId = ? ORDER BY Id", app_id)

    def env_vars(self, app_id):
        """Names, scopes and flags only: values are encrypted."""
        return self.query("SELECT Id, AppId, Key, IsSecret, BuildTime, Scope FROM EnvVars WHERE AppId = ? ORDER BY Id", app_id)

    def domains(self, app_id):
        return self.query("SELECT * FROM Domains WHERE AppId = ? ORDER BY Id", app_id)

    def activity(self, app_id):
        return self.query("SELECT * FROM ActivityEntries WHERE AppId = ? ORDER BY Id", app_id)

    def setting(self, key):
        row = self.one("SELECT Value FROM Settings WHERE Key = ?", key)
        return row["Value"] if row else None


class Gitberth:
    def __init__(self, browser, url=config.GITBERTH_URL, password=config.ADMIN_PASSWORD):
        self.browser = browser
        self.base = url.rstrip("/")
        self.password = password
        self.db = Db()
        self.engine = Engine()
        self._signed_in = None
        self._errors = {}

    # ---- pages ----

    def url(self, path=""):
        return self.base + "/" + path.lstrip("/")

    def wait_up(self, timeout=180):
        until(lambda: get(self.url("/login"))[0] == 200, timeout, "Gitberth's sign-in page", interval=2)

    def new_page(self, signed_in=True, width=1440, height=1000):
        """A page in a browser context of its own (signed in as admin, or not). Its console errors: errors(page)."""
        context = self.browser.new_context(storage_state=self.signed_in_state() if signed_in else None,
                                           viewport={"width": width, "height": height}, color_scheme="dark")
        context.set_default_timeout(30_000)
        page = context.new_page()
        errors = self._errors.setdefault(id(page), [])
        page.on("console", lambda m: errors.append(f"console: {m.text}") if m.type == "error" else None)
        page.on("pageerror", lambda e: errors.append(f"page: {e}"))
        return page

    def errors(self, page):
        """The browser's errors on this page so far (console errors and uncaught exceptions)."""
        return list(self._errors.get(id(page), []))

    def signed_in_state(self):
        """A signed-in browser's cookies, made once and shared by every signed-in page."""
        if self._signed_in is None:
            page = self.new_page(signed_in=False)
            try:
                self.sign_in(page)
                self._signed_in = page.context.storage_state()
            finally:
                page.context.close()
        return self._signed_in

    def open(self, page, path, ready=True):
        page.goto(self.url(path))
        if ready:
            self.wait_ready(page)
        return page

    @staticmethod
    def wait_ready(page, timeout=45_000):
        """The interactive app has rendered (its splash is gone), so clicks reach Blazor."""
        page.locator("#gb-splash").wait_for(state="detached", timeout=timeout)

    @staticmethod
    def path_of(page):
        return urlsplit(page.url).path

    def sign_in(self, page, password=None):
        """Signs in as admin (with the rig's password unless given) and waits for the dashboard."""
        assert self.attempt_sign_in(page, password), f"signing in failed: {self.banners(page)} {page.locator('.gb-auth-error').all_inner_texts()}"
        self.wait_ready(page)

    def attempt_sign_in(self, page, password=None):
        """Submits the sign-in form: True when it signed in, False when the sign-in page said why not."""
        page.goto(self.url("/login"))
        page.fill("#username", "admin")
        page.fill("#password", password if password is not None else self.password)
        with page.expect_navigation(timeout=30_000):
            page.click("button[type=submit]")
        if urlsplit(page.url).path != "/login":
            return True
        page.locator(".gb-auth-error").wait_for(timeout=10_000)
        return False

    def sign_out(self, page):
        page.locator("button.gb-avatar").click()
        page.locator(".mud-menu-item", has_text="Sign out").click()
        page.wait_for_url(lambda u: urlsplit(u).path == "/login", timeout=30_000)

    @staticmethod
    def snackbar(page, text, timeout=30_000):
        """Waits for a snackbar whose text matches (a regex) and returns its text."""
        loc = page.locator(".mud-snackbar").filter(has_text=re.compile(text))
        loc.first.wait_for(timeout=timeout)
        return loc.first.inner_text()

    @staticmethod
    def banners(page):
        """The text of every banner on the page (problems Gitberth shows), for failure messages."""
        try:
            return " | ".join(t.strip().replace("\n", " ") for t in page.locator(".gb-banner").all_inner_texts()) or "(no banner)"
        except Exception as e:  # noqa: BLE001
            return f"(couldn't read the banners: {e})"

    @staticmethod
    def confirm(page, yes):
        """Answers a confirmation dialog (MudBlazor's message box) with its yes button."""
        dialog = page.locator(".mud-dialog")
        dialog.wait_for()
        dialog.get_by_role("button", name=yes).click()
        dialog.wait_for(state="detached")

    def tabs(self, page):
        """The App page's tab names, in order."""
        page.locator(".gb-tabbar .gb-tabbar-item").first.wait_for()
        return [t.strip().split("\n")[0].strip() for t in page.locator(".gb-tabbar .gb-tabbar-item").all_inner_texts()]

    def app_list_keys(self, page, query=None):
        """The apps the Apps list shows (their keys: container names), after a search when query is given."""
        self.open(page, "/apps" + (f"?q={quote(query)}" if query else ""))
        page.locator(".gb-list-foot, .gb-empty").first.wait_for()
        hrefs = page.locator(".gb-arow:not(.gb-arow-head) a.gb-arow-title").evaluate_all("els => els.map(e => e.getAttribute('href'))")
        return [h.rstrip("/").rsplit("/", 1)[-1] for h in hrefs if h]

    def app_list_total(self, page):
        """How many apps the Apps list counts ("6 of 6 apps" under it)."""
        self.open(page, "/apps")
        foot = page.locator(".gb-list-foot")
        foot.wait_for()
        return int(re.search(r"of (\d+) apps?", foot.inner_text()).group(1))

    def dashboard_app_total(self, page):
        """How many apps the dashboard counts (its Apps figure: "5 running of 6")."""
        self.open(page, "/")
        figure = page.locator(".gb-kpis2 .gb-hs-v").first
        text = until(lambda: (lambda t: t if re.search(r"of \d+", t) else None)(figure.inner_text()), 30,
                     "the dashboard's Apps figure", seen=figure.inner_text)
        return int(re.search(r"of (\d+)", text).group(1))

    def top_search(self, page, text, until_has=None, timeout=60):
        """The top bar's search for the text: the titles of the apps it offers, or [] once it says no app matches the text.
        It reads the apps when it's first focused, so the first search on a page should name a title it must find
        (until_has) to wait for that read; later searches on the same page are answered from it. That first read is
        usually instant, but has taken over 20 s on a busy rig."""
        box = page.locator("#gb-search-input")
        results = page.locator("#gb-search-results")
        box.fill(text)

        def answer():
            if results.count() == 0:
                return None
            empty = results.locator(".gb-search-empty")
            if empty.count() > 0:
                return ("none", []) if text.strip() in empty.inner_text() and until_has is None else None
            titles = [t.strip() for t in results.locator(".gb-search-hit-t").all_inner_texts()]
            return ("hits", titles) if until_has is None or until_has in titles else None

        what = f"the top bar's search for {text!r} to answer" + (f" with {until_has!r}" if until_has else "")
        started = time.monotonic()
        found = until(answer, timeout, what, interval=0.5, seen=lambda: results.inner_text() if results.count() else "(nothing shown)")[1]
        print(f"[e2e] the top bar's search for {text!r} answered in {time.monotonic() - started:.1f}s")
        # Cleared, and gone from the page, so the next search's answer can't be this one's.
        box.fill("")
        results.wait_for(state="detached")
        return found

    # ---- flows ----

    def create_git_app(self, page, repo_url, name=None, timeout=180_000):
        """New app › GitHub repository: checks and analyzes the repo, takes the settings as offered (the name too, unless
        given), and Create and deploy. Returns the App; its first deploy runs on (wait_deploy)."""
        self.open(page, "/apps/new?url=" + quote(repo_url, safe=""))
        name_box = page.locator("#git-name")
        try:
            name_box.wait_for(timeout=timeout)
        except PlaywrightTimeout:
            raise AssertionError(f"New app didn't get to the settings for {repo_url}: {self.banners(page)}") from None
        if name:
            # Blazor handles the input before the click, in order, so Create takes the new name.
            name_box.fill(name)
        page.get_by_role("button", name="Create and deploy").click()
        try:
            page.wait_for_url(re.compile(r"/apps/gb-[a-z0-9-]+$"), timeout=120_000)
        except PlaywrightTimeout:
            raise AssertionError(f"Create and deploy didn't open the new app's page: {self.banners(page)}") from None
        slug = self.path_of(page).rsplit("/", 1)[-1][len("gb-"):]
        row = until(lambda: self.db.app(slug), 30, f"the app {slug} in Gitberth's database")
        return App(row["Id"], row["Slug"], row["Name"])

    def app(self, slug):
        row = self.db.app(slug)
        assert row is not None, f"Gitberth has no app {slug}"
        return App(row["Id"], row["Slug"], row["Name"])

    def wait_app(self, slug, timeout=60, gone=False):
        """Waits for Gitberth to have an app with this slug (or, with gone, to have none); returns its row."""
        if gone:
            return until(lambda: self.db.app(slug) is None, timeout, f"Gitberth to forget the app {slug}")
        return until(lambda: self.db.app(slug), timeout, f"Gitberth to have an app {slug}",
                     seen=lambda: [a["Slug"] for a in self.db.apps()])

    def deploy(self, page, app):
        """The app page's Deploy: returns the deployment it started."""
        self.open(page, f"/apps/{app.key}")
        page.locator(".gb-hero-actions").get_by_role("button", name="Deploy").click()
        page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"))
        return int(self.path_of(page).rsplit("/", 1)[-1])

    def set_previews(self, page, app, mode=None, limit=None, days=None, forks=None, protect=None):
        """The Settings tab's Previews section, saved. mode: "Off", "Pull requests" or "Pull requests and branches".
        Turning previews off answers its confirmation. Returns the snackbar's text."""
        self.open(page, f"/apps/{app.key}/settings")
        section = page.locator("#sec-previews")
        section.wait_for()
        if mode is not None:
            section.get_by_role("radio", name=mode, exact=True).click()
        if limit is not None:
            section.locator("#set-preview-limit").fill(str(limit))
        if days is not None:
            section.locator("#set-preview-days").fill(str(days))
        for title, wanted in (("Also pull requests from forks", forks), ("Protect previews' domains", protect)):
            if wanted is not None:
                switch = section.get_by_role("switch", name=title)
                if (switch.get_attribute("aria-checked") == "true") != wanted:
                    switch.click()
        section.get_by_role("button", name="Save previews").click()
        if mode == "Off" and page.locator(".mud-dialog").count() > 0:
            self.confirm(page, "Turn off and remove")
        return self.snackbar(page, "Saved|Previews are off")

    def delete_app(self, page, app, volumes=True):
        """Settings › Remove: deletes the app with its images (and named volumes unless volumes=False)."""
        self.open(page, f"/apps/{app.key}/settings")
        page.locator("#sec-remove").get_by_role("button", name=re.compile("^Remove")).click()
        dialog = page.locator(".mud-dialog")
        try:
            dialog.wait_for()
        except PlaywrightTimeout:
            said = page.locator(".mud-snackbar").all_inner_texts()
            raise AssertionError(f"Remove didn't ask to delete {app.slug}; Gitberth said: {said or self.banners(page)}") from None
        if volumes:
            dialog.get_by_text("Also delete its named volumes").click()
        dialog.get_by_role("button", name="Delete app").click()
        page.wait_for_url(lambda u: urlsplit(u).path == "/apps", timeout=60_000)

    def save_watch_interval(self, page, seconds):
        """Settings › Deploys: how often the watcher looks for new commits."""
        self.open(page, "/settings")
        box = page.locator("#watch-interval")
        box.fill(str(seconds))
        box.blur()
        page.locator("#deploys").get_by_role("button", name="Save").click()
        return self.snackbar(page, "^Saved")

    def save_github_token(self, page, token):
        """Settings › GitHub access: saves the token (Gitberth checks it with GitHub's /user first)."""
        self.open(page, "/settings")
        page.locator("#gh-token").fill(token)
        page.locator("#github").get_by_role("button", name=re.compile("^(Save|Replace) token$")).click()
        return self.snackbar(page, "Token saved|GitHub")

    # ---- waiting on Gitberth's states ----

    def wait_deploy(self, app_id, after=0, statuses=("Succeeded",), timeout=300):
        """Waits for the app's newest deploy after deployment `after` to be over, and checks it ended in one of statuses.
        On a wrong ending it fails with the deploy's error and the end of its log."""
        def done():
            d = self.db.one("SELECT * FROM Deployments WHERE AppId = ? AND Id > ? ORDER BY Id DESC LIMIT 1", app_id, after)
            return d if d and d["Status"] in FINAL else None

        d = until(done, timeout, f"a deploy of app {app_id} (after #{after}) to finish", interval=2,
                  seen=lambda: [{k: x[k] for k in ("Id", "Trigger", "Status", "CommitSha", "Error")} for x in self.db.deployments(app_id)[:3]])
        if d["Status"] not in statuses:
            raise AssertionError(f"Deploy #{d['Id']} of app {app_id} ended {d['Status']}, not {'/'.join(statuses)}: {d['Error']}\n"
                                 f"--- the end of its log ---\n{self.deploy_log(d['Id'], tail=40)}")
        return d

    def wait_idle(self, app_id, timeout=300):
        """Waits until neither the app nor any of its previews has a deploy queued or running."""
        active = ("Queued", "Cloning", "Building", "Starting")

        def busy():
            ids = [app_id] + [p["Id"] for p in self.db.previews(app_id)]
            marks = ",".join("?" * len(ids))
            return self.db.query(f"SELECT Id, AppId, Status FROM Deployments WHERE AppId IN ({marks}) AND Status IN ('Queued','Cloning','Building','Starting')", *ids)

        until(lambda: not busy(), timeout, f"app {app_id}'s deploys to end ({'/'.join(active)})", interval=2, seen=busy)

    def deploy_log(self, deployment_id, tail=None):
        path = os.path.join(config.GBDATA, "logs", f"deploy-{deployment_id}.log")
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                lines = f.read().splitlines()
        except OSError as e:
            return f"(no log: {e})"
        return "\n".join(lines[-tail:] if tail else lines)

    def preview(self, parent_id, branch=None, pr=None):
        for p in self.db.previews(parent_id):
            if (branch is None or p["PreviewBranch"] == branch) and (pr is None or p["PullRequest"] == pr):
                return p
        return None

    def wait_preview(self, parent_id, branch=None, pr=None, timeout=120):
        """Waits for the parent's preview of the branch (or pull request) to exist; returns its row (see Db.previews)."""
        what = f"PR #{pr}" if pr is not None else f"branch {branch}"
        return until(lambda: self.preview(parent_id, branch, pr), timeout, f"a preview of {what} (parent app {parent_id})",
                     seen=lambda: [(p["Slug"], p["PreviewBranch"], p["PullRequest"]) for p in self.db.previews(parent_id)])

    def wait_no_preview(self, parent_id, branch=None, pr=None, timeout=120):
        what = f"PR #{pr}" if pr is not None else f"branch {branch}"
        return until(lambda: self.preview(parent_id, branch, pr) is None, timeout, f"the preview of {what} to go",
                     seen=lambda: [(p["Slug"], p["PreviewBranch"], p["PullRequest"]) for p in self.db.previews(parent_id)])

    def lan_url(self, app_id):
        """Where the LAN reaches the app's web port: http://<GITBERTH_PUBLIC_HOST>:<its host port>."""
        port = self.db.web_port(app_id)
        assert port and port["HostPort"], f"app {app_id} has no published web port: {self.db.ports(app_id)}"
        return f"http://{config.PUBLIC_HOST}:{port['HostPort']}"

    @staticmethod
    def wait_answers(url, path="/", timeout=60):
        """Waits for the test app at url to answer path with JSON, and returns it."""
        return until(lambda: get_json(url.rstrip("/") + path), timeout, f"{url}{path} to answer", seen=lambda: get(url.rstrip("/") + path))

    @staticmethod
    def logs(since=None, tail=None):
        """Gitberth's own log."""
        return gitberth_logs(since=since, tail=tail)
