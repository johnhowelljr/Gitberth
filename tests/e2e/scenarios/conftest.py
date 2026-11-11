"""Shared fixtures for the end-to-end scenarios. See ../README.md for what each gives a scenario.

Every test session starts from a fresh Gitberth and empty stand-ins (run.sh test resets them). The session then signs
in, gives Gitberth a GitHub token the stand-in accepts, and sets the watcher to look every 10 seconds. Each scenario
gets a marker (scenario.seq) so it only looks at the calls it caused, has its injected failures cleared, and removes
the apps it registered with `apps` at the end (unless E2E_KEEP is set). A failing scenario's report carries Gitberth's
warnings and errors from its run and a screenshot of each of its pages (in the rig's /e2e/out).

A call the stand-ins have no route for gets a bare 404 (or Cloudflare's 7003), which Gitberth reads as a missing
permission or as "not set up": a gap in the stand-ins would pass for Gitberth's behaviour. So a scenario during which
Gitberth made such a call fails, and so does the run when one came between scenarios."""
import os
import re
import secrets
import time

import pytest
from playwright.sync_api import sync_playwright

from e2e import config
from e2e.engine import Engine, gitberth_logs
from e2e.gitberth import Gitberth
from e2e.standin import Cloudflare, GitHub, StandIns

WATCH_SECONDS = 10


# ---- the rig ----

@pytest.fixture(scope="session")
def standins():
    s = StandIns()
    s.wait_up()
    return s


@pytest.fixture(scope="session")
def github(standins):
    """The GitHub stand-in: repos from the app templates, commits, branches, forks, pull requests, tokens, and what
    Gitberth asked of it (statuses, comments, hooks, deliveries)."""
    return GitHub(standins)


@pytest.fixture(scope="session")
def cloudflare(standins):
    """The Cloudflare stand-in: zones, DNS records, the tunnel's configuration, Zero Trust and Access applications."""
    return Cloudflare(standins)


@pytest.fixture(scope="session")
def engine():
    """The rig's Docker engine, the one Gitberth drives: containers, images, volumes, env, labels, exec, logs."""
    return Engine()


@pytest.fixture(scope="session")
def browser():
    with sync_playwright() as p:
        b = p.chromium.launch(args=["--disable-dev-shm-usage"])
        yield b
        b.close()


@pytest.fixture(scope="session")
def gitberth(browser):
    """Gitberth under test: pages, flows (New app, Deploy, previews, removal, Settings), its database read only, and
    waits on its states."""
    gb = Gitberth(browser)
    gb.wait_up()
    return gb


@pytest.fixture(scope="session")
def gh_token(github):
    """The GitHub token Gitberth has saved for the whole session (the stand-in's user e2e-bot, every permission)."""
    return github.token(login="e2e-bot")


@pytest.fixture(scope="session", autouse=True)
def baseline(gitberth, gh_token):
    """What every scenario can count on: signed in, the session's token saved, the watcher looking every 10 seconds."""
    page = gitberth.new_page()
    try:
        gitberth.save_github_token(page, gh_token)
        gitberth.save_watch_interval(page, WATCH_SECONDS)
    finally:
        page.context.close()
    return {"token": gh_token, "watch_seconds": WATCH_SECONDS}


# ---- per scenario ----

class Scenario:
    """What a scenario started from: seq (the stand-ins' marker), t0 (the time, for Gitberth's log), and its pages."""

    def __init__(self, seq, name):
        self.seq = seq
        self.t0 = time.time()
        self.name = name
        self.pages = []


# The scenarios' own calls to the stand-ins (github.rest, cloudflare.rest) say who they are: a check of the rig may ask
# for a route the stand-ins don't have, on purpose.
RUNNER_AGENT = "gbe2e-runner"


def unrouted(standins, since):
    """The calls since the marker that no stand-in route answered, but the scenarios' own."""
    return [c for c in standins.calls(since=since, unrouted=True) if (c.get("headers") or {}).get("User-Agent") != RUNNER_AGENT]


def describe_calls(calls, limit=10):
    shown = [f"{c['method']} {c['host']}{c['path']} (from {(c.get('headers') or {}).get('User-Agent') or 'no User-Agent'})"
             for c in calls[:limit]]
    return "; ".join(shown) + (f"; and {len(calls) - limit} more" if len(calls) > limit else "")


@pytest.fixture(autouse=True)
def scenario(request, standins):
    s = Scenario(standins.seq(), request.node.name)
    standins.clear_failures()
    request.node.gb_scenario = s
    yield s
    try:
        standins.clear_failures()
    except Exception as e:  # noqa: BLE001 - never hide the scenario's own result
        print(f"[e2e] couldn't clear the injected failures: {e}")
    try:
        gaps = unrouted(standins, s.seq)
    except Exception as e:  # noqa: BLE001
        print(f"[e2e] couldn't read the stand-ins' calls: {e}")
        gaps = []
    if gaps:
        pytest.fail(f"The stand-ins had no route for {len(gaps)} call(s) during this scenario. Each got a bare 404 (or Cloudflare's "
                    f"7003), which Gitberth reads as a missing permission or as not set up, so the rig needs those routes: "
                    f"{describe_calls(gaps)}", pytrace=False)


def _track(scenario, page):
    """The scenario's pages get a screenshot each when it fails."""
    scenario.pages.append(page)
    return page


@pytest.fixture
def page(gitberth, scenario):
    """A signed-in page of its own. Closed after the scenario (with a screenshot when it failed)."""
    p = _track(scenario, gitberth.new_page())
    yield p
    p.context.close()


@pytest.fixture
def anon_page(gitberth, scenario):
    """A page of a browser that isn't signed in."""
    p = _track(scenario, gitberth.new_page(signed_in=False))
    yield p
    p.context.close()


@pytest.fixture
def new_page(gitberth, scenario):
    """Makes more pages (new_page(signed_in=False) for one that isn't signed in); all closed after the scenario."""
    made = []

    def make(signed_in=True, width=1440, height=1000):
        p = _track(scenario, gitberth.new_page(signed_in, width, height))
        made.append(p)
        return p

    yield make
    for p in made:
        p.context.close()


@pytest.fixture
def uniq():
    """Unique short names, so scenarios never collide: uniq("bp") gives "bp-3fa9". Keep prefixes short: an app's slug,
    plus a branch for its previews, must stay within 40 characters."""
    return lambda prefix: f"{prefix}-{secrets.token_hex(2)}"


class Apps:
    """The apps a scenario made, removed (with their images and volumes) when it ends, unless E2E_KEEP is set."""

    def __init__(self, gitberth):
        self.gitberth = gitberth
        self.made = []

    def track(self, app):
        self.made.append(app)
        return app


@pytest.fixture
def apps(gitberth, standins):
    registry = Apps(gitberth)
    yield registry
    # Its apps are removed with GitHub and Cloudflare working again: pytest ends this fixture before `scenario`, so the
    # scenario's injected failures would otherwise still hold, and a domain's removal would wait for Gitberth's retry.
    try:
        standins.clear_failures()
    except Exception as e:  # noqa: BLE001 - cleaning up never fails a scenario
        print(f"[e2e] couldn't clear the injected failures: {e}")
    if config.KEEP or not registry.made:
        return
    page = gitberth.new_page()
    try:
        for app in reversed(registry.made):
            try:
                if gitberth.db.app(app.slug) is not None:
                    # Gitberth refuses to remove an app while it deploys: let a deploy the scenario left running end.
                    gitberth.wait_idle(app.id, timeout=300)
                    gitberth.delete_app(page, app)
            except Exception as e:  # noqa: BLE001 - cleaning up never fails a scenario
                print(f"[e2e] couldn't remove {app.slug} after the scenario: {e}")
    finally:
        page.context.close()


# ---- the report: failures with their assertion text and Gitberth's log ----

LEVEL = re.compile(r"^(trce|dbug|info|warn|fail|crit): ")


def gitberth_log_excerpt(since, limit=60):
    """Gitberth's warnings and errors since the time (each with its message lines), then its last lines."""
    try:
        text = gitberth_logs(since=int(since) - 2)
        tail = gitberth_logs(tail=16).splitlines()
    except Exception as e:  # noqa: BLE001
        return f"(couldn't read Gitberth's log: {e})"
    entries, current = [], None
    for line in text.splitlines():
        if LEVEL.match(line):
            current = [line]
            entries.append(current)
        elif current is not None:
            current.append(line)
    bad = [e for e in entries if e[0][:4] in ("warn", "fail", "crit")]
    lines = [line for e in bad for line in e[:12]]
    out = (lines[-limit:] if lines else ["(no warnings or errors during the scenario)"]) + ["... its last lines:"] + tail
    return "\n".join(out)


REPORT = {"failed": [], "started": time.time(), "unrouted": [], "notes": []}


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item, call):
    outcome = yield
    report = outcome.get_result()
    if report.failed:
        s = getattr(item, "gb_scenario", None)
        excerpt = gitberth_log_excerpt(s.t0 if s else REPORT["started"])
        report.sections.append(("Gitberth's log during the scenario", excerpt))
        shots = []
        for i, p in enumerate(getattr(s, "pages", []) if s else []):
            try:
                if not p.is_closed():
                    path = os.path.join(config.OUT, f"{re.sub(r'[^A-Za-z0-9_.-]', '_', item.name)}-{report.when}-{i}.png")
                    p.screenshot(path=path, full_page=True)
                    shots.append(f"{p.url} -> {os.path.basename(path)}")
            except Exception as e:  # noqa: BLE001
                shots.append(f"(no screenshot: {e})")
        if shots:
            report.sections.append(("Screenshots (in the rig's /e2e/out)", "\n".join(shots)))
        crash = getattr(report.longrepr, "reprcrash", None)
        message = crash.message if crash else str(report.longrepr)[-2000:]
        REPORT["failed"].append({"test": item.nodeid, "when": report.when, "message": message, "log": excerpt, "shots": shots})


def pytest_sessionfinish(session, exitstatus):
    """The run fails when any call but the scenarios' own found no stand-in route, between scenarios too (the session's
    setup, a watcher pass after a scenario ended)."""
    try:
        gaps = unrouted(StandIns(), 0)
    except Exception as e:  # noqa: BLE001
        REPORT["notes"].append(f"couldn't read the stand-ins' calls for ones without a route: {e}")
        return
    if gaps:
        REPORT["unrouted"] = gaps
        session.exitstatus = pytest.ExitCode.TESTS_FAILED


def pytest_terminal_summary(terminalreporter):
    stats = terminalreporter.stats
    counts = [(len(stats.get(k, [])), k) for k in ("passed", "failed", "error", "skipped", "xfailed", "xpassed")]
    duration = time.time() - REPORT["started"]
    summary = ", ".join(f"{n} {k}" for n, k in counts if n) or "no tests ran"
    if REPORT["unrouted"]:
        summary += f"; {len(REPORT['unrouted'])} call(s) found no stand-in route, which fails the run"
    lines = [f"SUMMARY: {summary} in {duration:.0f}s"]
    if REPORT["unrouted"]:
        lines.append("UNROUTED (each got a bare 404 or Cloudflare's 7003; the stand-ins need these routes): "
                     + describe_calls(REPORT["unrouted"], 20))
    lines += [f"NOTE: {n}" for n in REPORT["notes"]]
    lines += [f"PASSED: {r.nodeid}" for r in stats.get("passed", [])]
    for f in REPORT["failed"]:
        lines.append("")
        lines.append(f"FAILED: {f['test']} ({f['when']})")
        lines.append("  " + f["message"].replace("\n", "\n  "))
        if f["shots"]:
            lines.append("  screenshots: " + "; ".join(f["shots"]))
        lines.append("  Gitberth's log during it:")
        lines.append("    " + f["log"].replace("\n", "\n    "))
    try:
        os.makedirs(config.OUT, exist_ok=True)
        with open(os.path.join(config.OUT, "report.txt"), "w") as fh:
            fh.write("\n".join(lines) + "\n")
    except OSError as e:
        terminalreporter.write_line(f"couldn't write the report: {e}")
