"""The docs' screenshots (run.sh in this folder), in the scenario runner: the stand-ins and the rig's helpers from
../scenarios, a browser that reaches Gitberth at http://192.168.50.10:8080 from the engine host's own address (relay.py),
and the Shooter, which takes a picture, checks it shows only the docs' example values, frames it and saves it as WebP.

Not the scenarios' conftest: nothing here signs in as the scenarios' bot or removes what it made. The session starts from
the fresh Gitberth and empty stand-ins `run.sh test` gives it."""
import json
import os
import re
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
# Gitberth as the docs show it: http://192.168.50.10:8080, the rig's own address on its LAN (run.sh sets it up so). Set
# before the helpers read it.
PUBLIC_HOST = os.environ.get("E2E_PUBLIC_HOST", "192.168.50.10")
BASE = f"http://{PUBLIC_HOST}:8080"
os.environ["E2E_GITBERTH_URL"] = BASE
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "scenarios"))

import pytest  # noqa: E402
from playwright.sync_api import sync_playwright  # noqa: E402

from e2e import config  # noqa: E402
from e2e.engine import Engine  # noqa: E402
from e2e.gitberth import Gitberth  # noqa: E402
from e2e.gitlab import GitLab  # noqa: E402
from e2e.standin import Cloudflare, GitHub, StandIns  # noqa: E402
from frame import Framer  # noqa: E402
from relay import Relay  # noqa: E402

W, H, DPR = 1440, 900, 2
OUT = os.path.join(config.OUT, "images")
RAW = os.path.join(config.OUT, "raw")

# What a picture must never show: the rig's and the server's own addresses and ports, and names that give the rig away.
NOT_EXAMPLES = [
    (re.compile(r"192\.168\.7\d\.\d+"), "a rig address"),
    (re.compile(r"\b10\.1\.1\.\d+"), "the server's address"),
    (re.compile(r"\b10\.89\.0\.\d+"), "the rig's inside network"),
    (re.compile(r"\b140\.82\.1(1[2-9]|2[0-7])\.\d+"), "GitHub's own address (the stand-in's webhook sender)"),
    (re.compile(r"\b1809\d\b"), "a rig port"),
    (re.compile(r"(?i)gbe2e|(?<![0-9a-f])e2e(?![0-9a-f])"), "an e2e name"),
    (re.compile(r"gbt_[A-Za-z0-9_-]{16,}"), "an API token"),
    (re.compile(r"/hooks/deploy/[A-Za-z0-9_-]{20,}"), "a deploy hook's secret URL"),
]


# ---- the rig ----

@pytest.fixture(scope="session")
def standins():
    s = StandIns()
    s.wait_up()
    return s


@pytest.fixture(scope="session")
def github(standins):
    return GitHub(standins)


@pytest.fixture(scope="session")
def gitlab(standins):
    """The GitLab stand-in as gitlab.com, Gitberth's default GitLab."""
    return GitLab(standins, host="gitlab.com")


@pytest.fixture(scope="session")
def cloudflare(standins):
    return Cloudflare(standins)


@pytest.fixture(scope="session")
def engine():
    return Engine()


@pytest.fixture(scope="session")
def relay():
    """The way the browser reaches Gitberth: from the engine host's own address (relay.py)."""
    r = Relay("gbe2e-runner:latest", PUBLIC_HOST, 8080)
    yield r
    r.close()


@pytest.fixture(scope="session")
def browser(relay):
    with sync_playwright() as p:
        b = p.chromium.launch(args=["--disable-dev-shm-usage"], proxy=relay.proxy)
        yield b
        b.close()


@pytest.fixture(scope="session")
def gitberth(browser):
    gb = Gitberth(browser, url=BASE)
    gb.wait_up()
    return gb


@pytest.fixture(scope="session")
def shooter(browser, gitberth):
    s = Shooter(browser, gitberth)
    yield s
    s.close()


@pytest.fixture(autouse=True)
def pages_closed(request):
    """Each test's pages are closed when it ends (after a failure's screenshots)."""
    yield
    if "shooter" in request.fixturenames:
        request.getfixturevalue("shooter").close_pages()


@pytest.fixture(scope="session")
def demo():
    """What the steps made, for the steps and shots after them: apps (by name), repos, tokens, ..."""
    class Demo:
        apps = {}
    return Demo()


# ---- pictures ----

class Shooter:
    """Pages as the docs show them (1440 x 900, twice the pixels, dark, UTC), and their pictures: window() for the whole
    window, detail() for one element with a margin around it. Each is checked for values that aren't the docs' examples,
    framed (frame.py) and saved as images/<name>.webp, with the raw PNG in raw/."""

    def __init__(self, browser, gitberth):
        self.browser = browser
        self.gb = gitberth
        self.framer = Framer(browser, f"{BASE}/fonts/inter-latin.woff2")
        self.contexts = []
        self.made = {}
        self.problems = {}
        os.makedirs(OUT, exist_ok=True)
        os.makedirs(RAW, exist_ok=True)

    def close(self):
        self.framer.close()

    def page(self, signed_in=True, height=H):
        """A page of its own (signed in as admin, or not), closed when the test ends."""
        context = self.browser.new_context(storage_state=self.gb.signed_in_state() if signed_in else None,
                                           viewport={"width": W, "height": height}, device_scale_factor=DPR,
                                           color_scheme="dark", locale="en-US", timezone_id="UTC")
        context.set_default_timeout(30_000)
        self.contexts.append(context)
        return context.new_page()

    def close_pages(self):
        for context in self.contexts:
            try:
                context.close()
            except Exception:  # noqa: BLE001 - a page that's gone already
                pass
        self.contexts = []

    def open(self, page, path, wait_for=None):
        """Opens the path and waits for Gitberth's page to be drawn: the splash gone, no loading placeholders left."""
        page.goto(self.gb.url(path))
        self.gb.wait_ready(page)
        if wait_for:
            page.locator(wait_for).first.wait_for()
        self.quiet(page)
        return page

    @staticmethod
    def quiet(page, timeout=20):
        """No loading placeholders left on the page, then a moment for charts and fonts."""
        end = time.time() + timeout
        while time.time() < end:
            if page.locator(".gb-skel:visible").count() == 0:
                break
            page.wait_for_timeout(300)
        page.evaluate("document.fonts.ready.then(() => true)")
        page.wait_for_timeout(700)

    def calm(self, page, mouse=True):
        """No focus ring from a click or the page's own focus on navigation, no tip or hover from the pointer."""
        page.evaluate("""() => { const a = document.activeElement;
            if (a && a !== document.body && !a.matches('input,textarea,select')) a.blur(); }""")
        if mouse:
            page.mouse.move(W - 3, H - 3)
        page.wait_for_timeout(450)

    # What a picture leaves out: a snackbar, which only says what was just done; and in a detail, the sticky top bar,
    # which would cover the top of an element taller than the window.
    HIDE = {"snackbars": ".mud-snackbar-provider, .mud-snackbar { visibility: hidden !important; }",
            "topbar": ".gb-topbar { visibility: hidden !important; }"}

    @classmethod
    def hide(cls, page, *what):
        page.evaluate("""css => { let s = document.getElementById('gb-shot-style');
            if (!s) { s = document.createElement('style'); s.id = 'gb-shot-style'; document.head.appendChild(s); }
            s.textContent = css; }""", " ".join(cls.HIDE[w] for w in what))

    @staticmethod
    def unhide(page):
        page.evaluate("() => document.getElementById('gb-shot-style')?.remove()")

    def window(self, page, name, mouse=True, legible=None):
        """The window as it is (its scroll position kept). legible: a selector for the only part that can be read, when
        the page blurs the rest behind it (a side panel's scrim); the check reads that part's text alone."""
        self.calm(page, mouse)
        self.hide(page, "snackbars")
        try:
            clip = {"x": 0, "y": 0, "width": W, "height": page.viewport_size["height"]}
            self.check(page, name, clip, root=legible)
            png = page.screenshot()
        finally:
            self.unhide(page)
        return self.save(name, png, "window", page, clip["width"], clip["height"])

    def detail(self, page, locator, name, pad=14, mouse=True):
        """One element with pad pixels around it, in the middle of the window; one taller than the window gets a window
        just tall enough for it."""
        el = locator.first
        el.wait_for()
        vh = page.viewport_size["height"]
        self.hide(page, "snackbars", "topbar")
        try:
            need = int(el.bounding_box()["height"] + 2 * pad + 48)
            if need > vh:
                page.set_viewport_size({"width": W, "height": need})
                page.wait_for_timeout(500)
            el.evaluate("e => e.scrollIntoView({block: 'center', inline: 'nearest'})")
            page.wait_for_timeout(300)
            self.calm(page, mouse)
            box = el.bounding_box()
            x0, y0 = max(0, round(box["x"] - pad)), max(0, round(box["y"] - pad))
            x1 = min(W, round(box["x"] + box["width"] + pad))
            y1 = min(page.viewport_size["height"], round(box["y"] + box["height"] + pad))
            clip = {"x": x0, "y": y0, "width": x1 - x0, "height": y1 - y0}
            self.check(page, name, clip)
            png = page.screenshot(clip=clip)
        finally:
            self.unhide(page)
            if page.viewport_size["height"] != vh:
                page.set_viewport_size({"width": W, "height": vh})
        return self.save(name, png, "detail", page, clip["width"], clip["height"])

    def check(self, page, name, clip, root=None):
        """The text the picture shows (inputs' values too), looked at for values that aren't the docs' examples. root: a
        selector for the only legible part, whose own text alone is read."""
        texts = page.evaluate("""([c, root]) => {
            const inside = r => r.width > 0 && r.height > 0 && r.right > c.x && r.left < c.x + c.width && r.bottom > c.y && r.top < c.y + c.height;
            const out = [];
            const top = (root && document.querySelector(root)) || document.body;
            const walk = document.createTreeWalker(top, NodeFilter.SHOW_TEXT);
            for (let n = walk.nextNode(); n; n = walk.nextNode()) {
                const t = n.textContent.trim();
                if (!t) continue;
                const range = document.createRange(); range.selectNodeContents(n);
                if ([...range.getClientRects()].some(inside)) out.push(t);
            }
            for (const e of top.querySelectorAll('input, textarea')) {
                if (e.type !== 'password' && e.value && inside(e.getBoundingClientRect())) out.push(e.value);
            }
            return out;
        }""", [clip, root])
        hits = [f"{why}: {t[:120]!r}" for t in texts for rx, why in NOT_EXAMPLES if rx.search(t)]
        if hits:
            self.problems[name] = sorted(set(hits))

    def save(self, name, png, kind, page, width, height):
        path = page.evaluate("location.pathname")
        with open(os.path.join(RAW, name + ".png"), "wb") as f:
            f.write(png)
        with open(os.path.join(RAW, name + ".json"), "w", encoding="utf-8") as f:
            json.dump({"name": name, "kind": kind, "path": path, "width": width, "height": height, "dpr": DPR}, f)
        webp = self.framer.frame(png, kind, width, height, path)
        out = os.path.join(OUT, name + ".webp")
        with open(out, "wb") as f:
            f.write(webp)
        self.made[name] = (kind, width, height, len(webp))
        print(f"[shots] {name}: {kind} {width}x{height}, {len(webp) // 1024} KB")
        if name in self.problems:
            raise AssertionError(f"{name} shows values that aren't the docs' examples: {'; '.join(self.problems[name])}")
        return out


# ---- the report ----

REPORT = {"started": time.time(), "failed": []}


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item, call):
    outcome = yield
    report = outcome.get_result()
    if report.failed:
        crash = getattr(report.longrepr, "reprcrash", None)
        message = crash.message if crash else str(report.longrepr)[-1500:]
        REPORT["failed"].append((item.name, report.when, message))
        # What the page showed when it failed: a screenshot of each open page, for the next try.
        try:
            for i, page in enumerate(p for c in item.funcargs["shooter"].contexts for p in c.pages):
                if not page.is_closed():
                    page.screenshot(path=os.path.join(config.OUT, f"failed-{item.name}-{i}.png"))
        except Exception:  # noqa: BLE001 - a report never fails the run
            pass


def pytest_terminal_summary(terminalreporter):
    stats = terminalreporter.stats
    counts = [(len(stats.get(k, [])), k) for k in ("passed", "failed", "error", "skipped")]
    summary = ", ".join(f"{n} {k}" for n, k in counts if n) or "no tests ran"
    lines = [f"SUMMARY: {summary} in {time.time() - REPORT['started']:.0f}s"]
    made = {}
    try:
        for f in sorted(os.listdir(OUT)):
            if f.endswith(".webp"):
                made[f[:-5]] = os.path.getsize(os.path.join(OUT, f))
    except OSError:
        pass
    lines.append(f"PICTURES: {len(made)}: " + ", ".join(f"{n} ({s // 1024} KB)" for n, s in made.items()))
    for name, when, message in REPORT["failed"]:
        lines.append(f"FAILED: {name} ({when}): " + message.replace("\n", "\n  "))
    os.makedirs(config.OUT, exist_ok=True)
    with open(os.path.join(config.OUT, "report.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")
