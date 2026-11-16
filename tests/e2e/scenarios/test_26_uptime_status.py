"""Scenario 26: uptime checks and the status page (1.3).

An app with a domain, checked from outside the way a visitor reaches it: over HTTPS through Cloudflare's edge and the
tunnel (the rig's stand-in plays the edge for any name: GITBERTH_UPTIME_CONNECT_TO), with a webhook destination for
notifications.
- Settings › Uptime and status page saves the check interval.
- Check now on the app's Uptime card checks https://<domain>/ through the edge, and the card says Up.
- Its container stopped outside Gitberth: the domain goes to the down page, two checks in a row find it ("Showing the
  down page"), the card says Down, and the webhook gets uptime.down for the app; started again, uptime.up, "It was down
  for …".
- The status page: off, /status is a bare 404; on, it's served on the LAN without signing in (/status and /status.json)
  and through its own host name in the tunnel (its rule carries the status page's marker), with the app, its state and
  the incident; never an address or a container's name. Off again, 404.
"""
import json
import re
from types import SimpleNamespace

import pytest

from e2e.engine import docker
from e2e.net import call
from e2e.standin import Notify
from e2e.wait import until
from test_02_pull_request_previews import new_app, own_apps, unique
from test_12_commit_statuses import add_domain
from test_16_protect import domains_on, flat
from test_21_notifications import add_destination, remove_all
from test_25_down_page import header, is_down_page, visit_until

SECTION = "#uptime"
# What the first failed check of the outage found: the tunnel's 502 when a pass came before the down page did, else it.
CAUSES = r"(Showing the down page|Server error 502)"


def uptime_section(gitberth, page):
    gitberth.open(page, "/settings")
    s = page.locator(SECTION)
    s.wait_for()
    return s


def save_interval(gitberth, page, seconds):
    s = uptime_section(gitberth, page)
    box = s.locator("#uptime-interval")
    box.fill(str(seconds))
    box.blur()
    s.get_by_role("button", name="Save", exact=True).click()
    return gitberth.snackbar(page, "^Saved")


def save_status_page(gitberth, page, on, title=None, host=None):
    """Settings › Uptime and status page: the status page on or off, its title and its host name, saved."""
    s = uptime_section(gitberth, page)
    switch = s.get_by_role("switch", name="Status page")
    switch.wait_for()
    if (switch.get_attribute("aria-checked") == "true") != on:
        switch.click()
    if on and title is not None:
        s.locator("#status-title").fill(title)
    if on and host is not None:
        s.locator("#status-host").fill(host)
    s.get_by_role("button", name="Save status page").click()
    return gitberth.snackbar(page, "^Saved")


def uptime_card(gitberth, page, app):
    gitberth.open(page, f"/apps/{app.key}")
    c = page.locator("section#uptime")
    c.wait_for()
    return c


def check_now(gitberth, page, app):
    """The Uptime card's Check now; returns the snackbar's text and the card's text after it."""
    c = uptime_card(gitberth, page, app)
    button = c.get_by_role("button", name="Check now")
    until(button.is_enabled, 30, "Check now to be offered", seen=lambda: flat(c.inner_text()))
    button.click()
    said = gitberth.snackbar(page, r"answers|didn't pass")
    return said, flat(c.inner_text())


@pytest.fixture(scope="module")
def world(gitberth, github, cloudflare, standins):
    zone = f"{unique('uz')}.test"
    cloudflare.zone(zone)
    page = gitberth.new_page()
    with domains_on(gitberth, gitberth.new_page, cloudflare, cloudflare.settings_token()):
        with own_apps(gitberth, standins, "uptime") as (apage, made):
            repo, app, _ = new_app(apage, gitberth, github, made.track, unique("upt"))
            host = f"web.{zone}"
            add_domain(gitberth, apage, app, host)
            notify = Notify(standins)
            try:
                yield SimpleNamespace(page=apage, repo=repo, app=app, zone=zone, host=host, url=f"https://{host}/", notify=notify)
            finally:
                remove_all(gitberth)
                with_default = gitberth.new_page()
                save_interval(gitberth, with_default, 60)
                if gitberth.db.setting("status.enabled") == "true" or gitberth.db.setting("status.host"):
                    s = uptime_section(gitberth, with_default)
                    switch = s.get_by_role("switch", name="Status page")
                    if switch.get_attribute("aria-checked") != "true":
                        switch.click()
                    s.locator("#status-host").fill("")
                    switch.click()
                    s.get_by_role("button", name="Save status page").click()
                    gitberth.snackbar(with_default, "^Saved")
    page.context.close()


@pytest.fixture(scope="module")
def checked(world, gitberth, cloudflare, standins):
    w = world
    w.interval_said = save_interval(gitberth, w.page, 30)
    w.interval = gitberth.db.setting("uptime.intervalSeconds")
    mark = standins.seq()
    w.up_said, w.up_card = check_now(gitberth, w.page, w.app)
    w.edge_calls = cloudflare.edge_calls(mark, host=w.host)
    # Notifications go to a webhook (the rig's notify stand-in) from now on.
    w.added = add_destination(gitberth, w.page, "Webhook", {"Name": "Uptime", "URL": Notify.WEBHOOK})
    return w


@pytest.fixture(scope="module")
def outage(checked, gitberth, cloudflare, standins):
    w = checked
    since = standins.seq()
    # Stopped outside Gitberth: its domain goes to the down page once its container has been down a while.
    docker("stop", w.app.container)
    w.down_visit = visit_until(cloudflare, w.url, is_down_page, "the stopped container's domain to show the down page", timeout=120)
    w.first_said, _ = check_now(gitberth, w.page, w.app)
    w.second_said, w.down_card = check_now(gitberth, w.page, w.app)
    w.down_notice = w.notify.wait(since, "notify.e2e.test", lambda calls: any((c.get("body") or {}).get("event") == "uptime.down" for c in calls),
                                  "the uptime.down webhook", timeout=90)
    docker("start", w.app.container)
    w.back_visit = visit_until(cloudflare, w.url, lambda r: r.get("status") == 200, "the domain to reach the app again", timeout=120)
    w.back_said, w.back_card = check_now(gitberth, w.page, w.app)
    w.up_notice = w.notify.wait(since, "notify.e2e.test", lambda calls: any((c.get("body") or {}).get("event") == "uptime.up" for c in calls),
                                "the uptime.up webhook", timeout=90)
    return w


@pytest.fixture(scope="module")
def status_page(outage, gitberth, cloudflare):
    w = outage
    lan = gitberth.url("/status")
    w.off_lan = call("GET", lan)
    w.status_host = f"status.{w.zone}"
    w.title = f"E2E status <{w.zone}>"
    w.on_said = save_status_page(gitberth, w.page, True, title=w.title, host=w.status_host)
    cloudflare.wait_runs()
    w.status_rule = until(lambda: next((r for r in cloudflare.rules_for(w.status_host)), None), 60, "the status page's rule in the tunnel",
                          seen=cloudflare.ingress)
    w.status_record = until(lambda: cloudflare.dns_records(w.status_host), 60, "the status page's DNS record")
    w.lan = call("GET", lan)
    w.lan_json = call("GET", gitberth.url("/status.json"))
    w.tunnel = visit_until(cloudflare, f"https://{w.status_host}/", lambda r: r.get("status") == 200, "the status page through its host")
    # The edge keeps the first 4000 characters of an answer: enough for the JSON's start.
    w.tunnel_json = cloudflare.visit(f"https://{w.status_host}/status.json")
    w.tunnel_login = cloudflare.visit(f"https://{w.status_host}/login")
    w.off_said = save_status_page(gitberth, w.page, False)
    w.off_again = call("GET", lan)
    return w


class TestChecks:
    def test_the_interval_is_saved(self, checked):
        assert checked.interval_said.startswith("Saved"), checked.interval_said
        assert checked.interval == "30", checked.interval

    def test_check_now_checks_the_domain_through_the_edge_and_the_card_says_up(self, checked):
        w = checked
        assert w.up_said.startswith(f"{w.app.name} answers: HTTP 200"), w.up_said
        assert "Up" in w.up_card and f"https://{w.host}/" in w.up_card, w.up_card
        assert w.edge_calls, "no uptime check reached the edge"
        last = w.edge_calls[-1]
        assert (last["method"], last["path"], last["status"]) == ("GET", "/", 200), last
        assert (last.get("headers") or {}).get("User-Agent", "").startswith("Gitberth-Uptime/"), last.get("headers")
        assert w.added.endswith(", and a test went there."), w.added


class TestOutage:
    def test_two_checks_find_the_down_page_and_the_app_is_down(self, outage):
        w = outage
        assert is_down_page(w.down_visit), w.down_visit
        assert w.first_said.endswith("Showing the down page"), w.first_said
        # Down since its first failed check, whichever found it first.
        assert re.search(rf"Down since .*: {CAUSES}", w.down_card), w.down_card

    def test_the_webhook_hears_it_go_down_and_come_back(self, outage):
        w = outage
        down = next(c["body"] for c in w.down_notice if (c.get("body") or {}).get("event") == "uptime.down")
        assert (down["title"], down["app"], down["container"], down["address"], down["level"]) == \
            (f"{w.app.name} is down", w.app.name, w.app.container, f"https://{w.host}/", "critical"), down
        # What the first failed check found: Cloudflare's 502 when a pass came before the down page did, else the down page.
        assert re.search(CAUSES, down["text"]), down
        up = next(c["body"] for c in w.up_notice if (c.get("body") or {}).get("event") == "uptime.up")
        assert up["title"] == f"{w.app.name} is back up" and up["text"].startswith("It was down for "), up
        assert w.back_said.startswith(f"{w.app.name} answers: HTTP 200"), w.back_said
        assert "Up" in w.back_card, w.back_card


class TestStatusPage:
    def test_off_it_is_a_bare_404(self, status_page):
        status, _, body = status_page.off_lan
        assert (status, body) == (404, ""), status_page.off_lan
        status, _, body = status_page.off_again
        assert (status, body) == (404, ""), status_page.off_again

    def test_on_the_lan_it_lists_the_app_and_the_incident_without_signing_in(self, status_page):
        w = status_page
        status, headers, html = w.lan
        assert status == 200, w.lan
        assert "E2E status &lt;" + w.zone + "&gt;" in html, html[:600]
        assert w.app.name in html and "Operational" in html, html[:3000]
        assert re.search(rf"was down for .* UTC: {CAUSES}", html), html[-3000:]
        assert w.host not in html and w.app.container not in html, "the status page shows an address or a container's name"
        status, _, text = w.lan_json
        data = json.loads(text)
        app = next(a for a in data["apps"] if a["name"] == w.app.name)
        assert app["status"] == "up" and len(app["days"]) == 90, app
        assert any(i["app"] == w.app.name and re.fullmatch(CAUSES, i["cause"]) for i in data["incidents"]), data["incidents"]
        assert w.host not in text and w.app.container not in text

    def test_through_its_own_host_name_in_the_tunnel(self, status_page):
        w = status_page
        assert w.on_said.startswith("Saved"), w.on_said
        assert w.status_rule["service"] == "http://localhost:8080", w.status_rule
        assert (w.status_rule.get("originRequest") or {}).get("httpHostHeader") == "status.gitberth.invalid", w.status_rule
        assert w.status_record and w.status_record[0]["proxied"], w.status_record
        assert header(w.tunnel, "X-Gitberth-Page") == "status", w.tunnel
        assert "E2E status &lt;" in w.tunnel["body"], w.tunnel["body"][:600]
        assert w.tunnel_json["status"] == 200 and w.tunnel_json["body"].startswith('{"title":"E2E status '), w.tunnel_json
        # Nothing else of Gitberth's through that host.
        assert w.tunnel_login["status"] == 404 and w.tunnel_login["body"] == "", w.tunnel_login
