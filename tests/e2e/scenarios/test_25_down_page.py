"""Scenario 25: the down page (1.3).

An app with a domain (the test app, whose Dockerfile declares VOLUME /data, so a deploy has to stop it before it starts
the new version: the plain swap).
- Stopping it sends its domain to Gitberth's down page through the tunnel: a 503 with Retry-After, no-store and noindex,
  the host name and "isn't running", never the app's name; the rule goes to Gitberth's own service with the domain's
  marker host. The Domains card says so; its preview (signed in only) shows the same page.
- Gitberth's own pages never answer a marker host: /login, /_blazor/negotiate, /hooks/github, /apps and /css/app.css asked
  on the LAN with that Host get only the down page; an unknown marker a bare 404.
- Starting it sends the domain back to the app, the marker gone.
- A deploy that has to stop it shows the down page meanwhile, then the new version: never Cloudflare's error.
- With its down page turned off, a stopped app's domain goes back to the app's port (Cloudflare's 502), and on again, to
  the down page.
- A protected domain whose protection can't be applied (Cloudflare Access failing) shows only the down page, never the
  app; once Access is back, Fix serves it behind Access again.
"""
import re
import time
from types import SimpleNamespace

import pytest

from e2e.github_reports import set_variables, wait_deployment
from e2e.net import call
from e2e.wait import until
from test_02_pull_request_previews import new_app, own_apps, unique, wait_deployed
from test_12_commit_statuses import add_domain
from test_16_protect import ALLOWED, card, domains_on, flat, save_emails, toggle_protected
from test_17_fail_closed import fix

ACCESS_APPS = r"/access/apps$"
DOWN_TITLE = "This site is temporarily unavailable"


def header(result, name):
    return next((v for k, v in (result.get("headers") or {}).items() if k.lower() == name.lower()), None)


def is_down_page(result):
    return result.get("status") == 503 and header(result, "X-Gitberth-Page") == "down"


def visit_until(cloudflare, url, check, what, who=None, timeout=90):
    """Visits the url through Cloudflare's edge until check(answer) holds; returns that answer."""
    return until(lambda: (lambda r: r if check(r) else None)(cloudflare.visit(url, signed_in_as=who)), timeout, what,
                 interval=1, seen=lambda: (lambda r: {k: r.get(k) for k in ("status", "error", "body")} | {"page": header(r, "X-Gitberth-Page")})(cloudflare.visit(url, signed_in_as=who)))


def own_service_rule(cloudflare, host):
    """The tunnel's every-path rule for the host, or None."""
    return next((r for r in cloudflare.rules_for(host) if not r.get("path")), None)


def marker_of(rule):
    return ((rule or {}).get("originRequest") or {}).get("httpHostHeader")


def act(gitberth, page, app, verb):
    """The app page's Stop or Start, answered (Stop asks first)."""
    gitberth.open(page, f"/apps/{app.key}")
    page.locator(".gb-hero-actions").get_by_role("button", name=f"{verb} {app.name}").click()
    if verb == "Stop":
        gitberth.confirm(page, "Stop")
    return gitberth.snackbar(page, rf"^(Stopped|Started) ")


def row_text(gitberth, page, app, host, wanted):
    """The Domains card's line for the host, once it says what's wanted (Check again until it does)."""
    gitberth.open(page, f"/apps/{app.key}/network")
    row = card(page).locator(".gb-drow", has_text=host).first

    def said():
        text = flat(row.inner_text()) if row.count() else ""
        if wanted in text:
            return text
        again = card(page).get_by_role("button", name="Check again")
        if again.is_enabled():
            again.click()
        return None

    return until(said, 90, f"the Domains card to say {wanted!r}", interval=3, seen=lambda: flat(card(page).inner_text()))


def set_down_page(gitberth, page, app, on):
    """The Domains card's Down page switch, saved on its own."""
    gitberth.open(page, f"/apps/{app.key}/network")
    switch = card(page).get_by_role("switch", name="Down page")
    switch.wait_for()
    if (switch.get_attribute("aria-checked") == "true") != on:
        switch.click()
        return gitberth.snackbar(page, "The down page is (on|off)")
    return None


@pytest.fixture(scope="module")
def world(gitberth, github, cloudflare, standins):
    zone = f"{unique('dz')}.test"
    cloudflare.zone(zone)
    with domains_on(gitberth, gitberth.new_page, cloudflare, cloudflare.settings_token()):
        with own_apps(gitberth, standins, "downpage") as (page, made):
            repo, app, first = new_app(page, gitberth, github, made.track, unique("dpg"))
            host = f"app.{zone}"
            add_domain(gitberth, page, app, host)
            domain = gitberth.db.domains(app.id)[0]
            try:
                yield SimpleNamespace(page=page, repo=repo, app=app, zone=zone, host=host, url=f"https://{host}/", domain_id=domain["Id"], first=first)
            finally:
                standins.clear_failures()


@pytest.fixture(scope="module")
def stopped(world, gitberth, cloudflare):
    w = world
    w.live_before = visit_until(cloudflare, w.url, lambda r: r.get("status") == 200, "the app to answer through the tunnel")
    w.stop_said = act(gitberth, w.page, w.app, "Stop")
    w.down = visit_until(cloudflare, w.url, is_down_page, "the domain to show the down page")
    w.down_json = cloudflare.visit(w.url + "api/orders", headers={"Accept": "application/json"})
    w.down_rule = own_service_rule(cloudflare, w.host)
    w.card_line = row_text(gitberth, w.page, w.app, w.host, "Shows the down page")
    # The preview, signed in, and not.
    preview = gitberth.url(f"/down-page/preview?host={w.host}")
    signed_in = gitberth.new_page()
    w.preview = signed_in.goto(preview)
    w.preview_text = flat(signed_in.locator("body").inner_text())
    signed_in.context.close()
    anon = gitberth.new_page(signed_in=False)
    anon.goto(preview)
    w.preview_anon_path = gitberth.path_of(anon)
    anon.context.close()
    # Gitberth's own pages, asked on the LAN with the domain's marker host.
    marker = f"d-{w.domain_id}.down.gitberth.invalid"
    w.forged = {path: call(method, gitberth.url(path), b"" if method == "POST" else None, {"Host": marker})
                for method, path in (("GET", "/login"), ("POST", "/_blazor/negotiate"), ("POST", "/hooks/github"), ("GET", "/apps"),
                                     ("GET", "/css/app.css"), ("GET", "/"))}
    w.unknown_marker = call("GET", gitberth.url("/login"), None, {"Host": "nothing.gitberth.invalid"})
    return w


@pytest.fixture(scope="module")
def started(stopped, gitberth, cloudflare):
    w = stopped
    w.start_said = act(gitberth, w.page, w.app, "Start")
    w.back = visit_until(cloudflare, w.url, lambda r: r.get("status") == 200 and "gbe2e-testapp" in (r.get("body") or ""), "the domain to reach the app again")
    w.back_rule = own_service_rule(cloudflare, w.host)
    return w


@pytest.fixture(scope="module")
def deployed(started, gitberth, cloudflare):
    """A new commit, deployed by the plain swap, with the domain visited all along."""
    w = started
    before = gitberth.db.last_deployment_id(w.app.id)
    sha = w.repo.commit({"marker.txt": "down page v2"}, message="v2")["sha"]
    seen = []
    deadline = time.monotonic() + 420
    while time.monotonic() < deadline:
        r = cloudflare.visit(w.url)
        seen.append((r.get("status"), header(r, "X-Gitberth-Page"), "down page v2" in (r.get("body") or "")))
        d = gitberth.db.one("SELECT * FROM Deployments WHERE AppId = ? AND Id > ? ORDER BY Id DESC LIMIT 1", w.app.id, before)
        if d and d["Status"] in ("Succeeded", "Failed", "RolledBack", "Cancelled") and seen[-1][2]:
            break
        time.sleep(0.3)
    w.deploy = wait_deployed(gitberth, w.app.id, sha, after=before)
    w.deploy_log = gitberth.deploy_log(w.deploy["Id"])
    w.seen = seen
    return w


@pytest.fixture(scope="module")
def switched_off(deployed, gitberth, cloudflare):
    w = deployed
    w.off_said = set_down_page(gitberth, w.page, w.app, False)
    act(gitberth, w.page, w.app, "Stop")
    # Without its down page, a stopped app's domain goes where the app will be: Cloudflare's error meanwhile.
    w.off_visit = visit_until(cloudflare, w.url, lambda r: r.get("status") == 502, "the stopped app's domain to give Cloudflare's 502")
    w.off_rule = own_service_rule(cloudflare, w.host)
    w.on_said = set_down_page(gitberth, w.page, w.app, True)
    w.on_visit = visit_until(cloudflare, w.url, is_down_page, "the domain to show the down page again")
    act(gitberth, w.page, w.app, "Start")
    visit_until(cloudflare, w.url, lambda r: r.get("status") == 200, "the app to answer again")
    return w


@pytest.fixture(scope="module")
def held(switched_off, gitberth, cloudflare, standins):
    """Protected, then Cloudflare Access fails for a moment: the domain is held back on its down page."""
    w = switched_off
    w.team = unique("team")
    cloudflare.set_team(w.team)
    said, _ = save_emails(gitberth, w.page, ALLOWED)
    assert said.startswith("Saved"), said
    _, protect_said = toggle_protected(gitberth, w.page, w.app, w.host, "Protect")
    assert protect_said == f"{w.host} is protected: visitors sign in first.", protect_said
    cloudflare.wait_runs()
    visit_until(cloudflare, w.url, lambda r: r.get("status") == 200, "the protected app for a signed-in visitor", who=ALLOWED)
    rule = standins.fail(ACCESS_APPS, status=503, host="api.cloudflare.com", method="GET")
    try:
        # A sync while Access fails: a deploy without a build (a variable added).
        w.held_deploy = wait_deployment(gitberth, set_variables(gitberth, w.page, w.app, [{"name": "E2E_DOWN_SYNC", "value": "access fails"}]))
        cloudflare.wait_runs()
        w.held_rule = own_service_rule(cloudflare, w.host)
        w.held_signed_in = visit_until(cloudflare, w.url, is_down_page, "the held domain to show only the down page", who=ALLOWED)
        w.held_anonymous = cloudflare.visit(w.url)
        w.held_line = row_text(gitberth, w.page, w.app, w.host, "Shows only the down page")
        w.held_log = gitberth.deploy_log(w.held_deploy["Id"], tail=80)
    finally:
        standins.clear_failures(rule)
    fix(gitberth, w.page, w.app, w.host)
    w.back_rule = until(lambda: (lambda r: r if r and (r.get("originRequest") or {}).get("access") and not marker_of(r) else None)(own_service_rule(cloudflare, w.host)),
                        90, "the domain to be served behind Access again", seen=lambda: cloudflare.rules_for(w.host))
    cloudflare.wait_runs()
    w.back_allowed = visit_until(cloudflare, w.url, lambda r: r.get("status") == 200, "the app behind Access again", who=ALLOWED)
    return w


class TestStopped:
    def test_the_domain_shows_the_down_page_through_the_tunnel(self, stopped):
        w = stopped
        assert w.live_before["status"] == 200, w.live_before
        assert w.stop_said == f"Stopped {w.app.name}.", w.stop_said
        assert is_down_page(w.down), w.down
        assert header(w.down, "Retry-After") == "600", w.down["headers"]
        assert "no-store" in (header(w.down, "Cache-Control") or ""), w.down["headers"]
        assert "noindex" in (header(w.down, "X-Robots-Tag") or ""), w.down["headers"]
        body = w.down["body"]
        assert DOWN_TITLE in body and w.host in body and "It isn&#39;t running right now." not in body, body[:600]
        assert "It isn't running right now." in body, body[:800]
        assert w.app.name not in body and w.app.container not in body, body

    def test_a_client_asking_for_json_gets_json(self, stopped):
        w = stopped
        assert w.down_json["status"] == 503, w.down_json
        assert '"host":"' + w.host + '"' in w.down_json["body"] and '"reason":"not_running"' in w.down_json["body"], w.down_json["body"]

    def test_the_rule_goes_to_gitberth_with_the_domains_marker(self, stopped):
        w = stopped
        assert w.down_rule["service"] == "http://localhost:8080", w.down_rule
        assert marker_of(w.down_rule) == f"d-{w.domain_id}.down.gitberth.invalid", w.down_rule

    def test_the_domains_card_says_why(self, stopped):
        w = stopped
        assert "Down page" in w.card_line and f"Shows the down page: {w.app.name} is stopped." in w.card_line, w.card_line

    def test_the_preview_is_the_page_and_for_someone_signed_in_only(self, stopped):
        w = stopped
        assert w.preview.status == 200, w.preview.status
        assert DOWN_TITLE in w.preview_text and w.host in w.preview_text, w.preview_text
        assert w.preview_anon_path == "/login", w.preview_anon_path

    def test_gitberths_own_pages_never_answer_a_marker_host(self, stopped):
        w = stopped
        for path, (status, headers, body) in w.forged.items():
            page = next((v for k, v in headers.items() if k.lower() == "x-gitberth-page"), None)
            assert (status, page) == (503, "down"), (path, status, headers)
            assert DOWN_TITLE in body and "blazor" not in body.lower() and "<base href" not in body, (path, body[:400])
        status, _, body = w.unknown_marker
        assert (status, body) == (404, ""), w.unknown_marker


class TestStarted:
    def test_the_domain_goes_back_to_the_app_without_the_marker(self, started):
        w = started
        assert w.start_said == f"Started {w.app.name}.", w.start_said
        assert w.back["status"] == 200, w.back
        assert w.back_rule["service"] != "http://localhost:8080" and marker_of(w.back_rule) is None, w.back_rule


class TestDeploy:
    def test_a_plain_swap_shows_the_down_page_and_then_the_new_version_never_an_error(self, deployed):
        w = deployed
        statuses = [s for s, _, _ in w.seen]
        assert any(page == "down" and s == 503 for s, page, _ in w.seen), w.seen
        assert w.seen[-1][0] == 200 and w.seen[-1][2], w.seen[-6:]
        assert all(s in (200, 503) for s in statuses), [x for x in w.seen if x[0] not in (200, 503)]
        # Down page only while the container was replaced: from the old version to it, then to the new one.
        first_down = next(i for i, x in enumerate(w.seen) if x[1] == "down")
        assert not any(x[2] for x in w.seen[:first_down]), "the new version answered before the old one was stopped"
        assert "to the down page while the container is replaced" in w.deploy_log, w.deploy_log[-2000:]
        assert "Sending visitors back to the app" in w.deploy_log, w.deploy_log[-2000:]


class TestDownPageOff:
    def test_off_a_stopped_apps_domain_gives_cloudflares_error_and_on_the_down_page(self, switched_off):
        w = switched_off
        assert w.off_said.startswith("The down page is off"), w.off_said
        assert w.off_visit["status"] == 502 and header(w.off_visit, "X-Gitberth-Page") is None, w.off_visit
        assert marker_of(w.off_rule) is None and w.off_rule["service"] != "http://localhost:8080", w.off_rule
        assert w.on_said.startswith("The down page is on"), w.on_said
        assert is_down_page(w.on_visit), w.on_visit


class TestHeldBack:
    def test_a_protected_domain_access_cant_be_applied_to_shows_only_the_down_page(self, held):
        w = held
        assert w.held_deploy["Status"] == "Succeeded", w.held_deploy
        assert w.held_rule["service"] == "http://localhost:8080" and marker_of(w.held_rule) == f"d-{w.domain_id}.down.gitberth.invalid", w.held_rule
        assert not (w.held_rule.get("originRequest") or {}).get("access"), w.held_rule
        assert is_down_page(w.held_signed_in), w.held_signed_in
        # It says nothing beyond its host name.
        assert w.held_signed_in["body"].count(w.host) >= 1 and "running" not in w.held_signed_in["body"], w.held_signed_in["body"][:800]
        assert "gbe2e-testapp" not in w.held_signed_in["body"]
        assert w.held_anonymous["status"] != 200, w.held_anonymous
        assert "Shows only the down page until it can be put behind Cloudflare Access" in w.held_line, w.held_line
        line = next((l for l in w.held_log.splitlines() if f"{w.host} shows only Gitberth's down page: it's protected, and Cloudflare Access can't be used" in l), None)
        assert line is not None, w.held_log

    def test_once_access_is_back_fix_serves_it_behind_access_again(self, held):
        w = held
        assert w.back_rule["service"] != "http://localhost:8080", w.back_rule
        assert w.back_allowed["status"] == 200 and "gbe2e-testapp" in w.back_allowed["body"], w.back_allowed
