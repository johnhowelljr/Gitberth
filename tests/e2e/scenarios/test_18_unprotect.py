"""Scenario 18: unprotect (beyond test_16's Stop protecting).

- Stop protecting takes the check off the tunnel's rule before it deletes the Access application: cloudflared never
  checks sign-ins for an application that's gone (which would turn everyone away).
- With the Access application already deleted in Cloudflare's dashboard, the domain is still never open while
  protected; Stop protecting still strips the stale check, and anyone reaches the app again.
- Removing a protected domain cleans up the same way: its rule (check and all), DNS record and Access application go.
- Another service's rule with an Access check of its own, and its Access application, made in the dashboard, are never
  touched by any of it.
"""
import contextlib
import re
from types import SimpleNamespace

import pytest

from e2e.wait import until
from test_02_pull_request_previews import new_app, own_apps, unique
from test_04_preview_limit import answer_dialog, read_dialog
from test_12_commit_statuses import add_domain
from test_16_protect import ALLOWED, access_app, card, domains_on, flat, save_emails, service_rule, toggle_protected, visit

THEIR_SERVICE = "http://localhost:9"


def behind_access(rule):
    return bool(rule and (rule.get("originRequest") or {}).get("access"))


def cf_calls(standins, since):
    return standins.calls(since, host="api.cloudflare.com")


def protect(gitberth, cloudflare, w, host):
    _, said = toggle_protected(gitberth, w.page, w.app, host, "Protect")
    assert said == f"{host} is protected: visitors sign in first.", said
    cloudflare.wait_runs()
    rule = until(lambda: (lambda r: r if behind_access(r) else None)(service_rule(cloudflare, host)), 60, f"{host} behind Access",
                 seen=lambda: cloudflare.rules_for(host))
    return rule, access_app(cloudflare, host)


@contextlib.contextmanager
def their_protected_route(cloudflare, zone, host):
    """Another service's route, made in Cloudflare's dashboard: its Access application, and its rule ahead of the rest,
    checking that application's sign-in. Taken out again afterwards."""
    team = cloudflare.state()["team"]
    cloudflare.add_access_app("CI dashboard", host, emails=("ops@example.com",))
    application = next(a for a in cloudflare.access_apps() if a.get("domain") == host and a.get("name") == "CI dashboard")
    rule ={"hostname": host, "service": THEIR_SERVICE,
            "originRequest": {"access": {"required": True, "teamName": team, "audTag": [application["aud"]]}}}
    config = cloudflare.tunnel()["config"]
    config["ingress"] = [rule] + list(config.get("ingress") or [])
    cloudflare.wait_runs(cloudflare.set_tunnel_config(config)["version"])
    try:
        yield rule, application
    finally:
        config = cloudflare.tunnel()["config"]
        config["ingress"] = [r for r in config.get("ingress") or [] if r != rule]
        cloudflare.set_tunnel_config(config)
        with contextlib.suppress(Exception):
            cloudflare.delete_access_app(application["id"])


@pytest.fixture(scope="module")
def world(gitberth, github, cloudflare, standins):
    zone, team = f"{unique('uz')}.test", unique("team")
    cloudflare.zone(zone)
    cloudflare.set_team(team)
    with domains_on(gitberth, gitberth.new_page, cloudflare, cloudflare.settings_token()):
        with their_protected_route(cloudflare, zone, f"ci.{zone}") as (theirs, their_app):
            with own_apps(gitberth, standins, "unprotect") as (page, made):
                said, _ = save_emails(gitberth, page, ALLOWED)
                assert said.startswith("Saved"), said
                repo, app, _ = new_app(page, gitberth, github, made.track, unique("unp"))
                hosts = [f"a.{zone}", f"b.{zone}", f"c.{zone}"]
                for host in hosts:
                    add_domain(gitberth, page, app, host)
                yield SimpleNamespace(page=page, repo=repo, app=app, zone=zone, team=team, hosts=hosts, theirs=theirs, their_app=their_app)


@pytest.fixture(scope="module")
def stopped(world, gitberth, cloudflare, standins):
    w = world
    host = w.hosts[0]
    _, w.a_app = protect(gitberth, cloudflare, w, host)
    mark = standins.seq()
    _, w.a_stop_said = toggle_protected(gitberth, w.page, w.app, host, "Stop protecting")
    cloudflare.wait_runs()
    until(lambda: access_app(cloudflare, host) is None, 60, "the Access application to go")
    calls = cf_calls(standins, mark)
    w.a_put = [c for c in calls if c["method"] == "PUT" and re.search(r"/cfd_tunnel/[^/]+/configurations$", c["path"]) and c["status"] == 200]
    w.a_delete = [c for c in calls if c["method"] == "DELETE" and c["path"].endswith(f"/access/apps/{w.a_app['id']}")]
    w.a_rule = service_rule(cloudflare, host)
    w.a_open = visit(cloudflare, f"https://{host}/")
    return w


@pytest.fixture(scope="module")
def deleted_in_dashboard(stopped, gitberth, cloudflare):
    w = stopped
    host = w.hosts[1]
    _, application = protect(gitberth, cloudflare, w, host)
    cloudflare.delete_access_app(application["id"])
    w.b_while_gone = visit(cloudflare, f"https://{host}/")
    _, w.b_stop_said = toggle_protected(gitberth, w.page, w.app, host, "Stop protecting")
    cloudflare.wait_runs()
    w.b_rule = until(lambda: (lambda r: r if r and not behind_access(r) else None)(service_rule(cloudflare, host)), 60,
                     "the stale check to be stripped", seen=lambda: cloudflare.rules_for(host))
    w.b_open = visit(cloudflare, f"https://{host}/")
    return w


@pytest.fixture(scope="module")
def removed(deleted_in_dashboard, gitberth, cloudflare):
    w = deleted_in_dashboard
    host = w.hosts[2]
    _, w.c_app = protect(gitberth, cloudflare, w, host)
    gitberth.open(w.page, f"/apps/{w.app.key}/network")
    card(w.page).get_by_role("button", name=f"Remove {host}").click()
    w.c_dialog = read_dialog(w.page)
    answer_dialog(w.page, "Remove")
    until(lambda: card(w.page).locator(".gb-drow", has_text=host).count() == 0, 60, f"{host} to leave the card", seen=lambda: flat(card(w.page).inner_text()))
    cloudflare.wait_runs()
    w.c_gone = until(lambda: not cloudflare.rules_for(host) and not cloudflare.dns_records(host) and access_app(cloudflare, host) is None, 60,
                     "its rule, DNS record and Access application to go",
                     seen=lambda: (cloudflare.rules_for(host), cloudflare.dns_records(host), access_app(cloudflare, host)))
    w.their_rule_after = next((r for r in cloudflare.ingress() if r.get("hostname") == w.theirs["hostname"]), None)
    w.their_app_after = next((a for a in cloudflare.access_apps() if a["id"] == w.their_app["id"]), None)
    return w


def test_stop_protecting_takes_the_check_off_before_it_deletes_the_application(stopped):
    w = stopped
    assert w.a_stop_said == f"{w.hosts[0]} is open to everyone again.", w.a_stop_said
    assert w.a_put and len(w.a_delete) == 1, (w.a_put, w.a_delete)
    assert w.a_put[0]["seq"] < w.a_delete[0]["seq"], "the Access application was deleted before the tunnel stopped checking it"
    assert w.a_rule is not None and not behind_access(w.a_rule), w.a_rule
    assert w.a_open["status"] == 200, w.a_open


def test_an_application_deleted_in_the_dashboard_still_never_opens_the_domain_and_its_stale_check_is_stripped(deleted_in_dashboard):
    w = deleted_in_dashboard
    assert w.b_while_gone["status"] != 200, w.b_while_gone
    assert w.b_stop_said == f"{w.hosts[1]} is open to everyone again.", w.b_stop_said
    assert w.b_open["status"] == 200, w.b_open


def test_removing_a_protected_domain_cleans_up_the_same_way(removed):
    w = removed
    title, _, buttons = w.c_dialog
    assert (title, buttons) == (f"Remove {w.hosts[2]}?", ["Cancel", "Remove"]), w.c_dialog
    assert w.c_gone


def test_another_services_access_check_and_application_are_never_touched(removed):
    w = removed
    assert w.their_rule_after == w.theirs, w.their_rule_after
    assert w.their_app_after is not None and w.their_app_after["aud"] == w.their_app["aud"], w.their_app_after
