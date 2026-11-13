"""Scenario 17: fail closed (beyond test_16's Zero Trust gone).

- Protecting a domain while Cloudflare's tunnel API fails (502 on the configuration's PUT): the switch's answer says
  the domain isn't behind Access yet and that Gitberth tries again; the Domains card says "Served without Cloudflare
  Access…" and offers Fix. Once Cloudflare is back, Fix (what the 15-minute retry does, sooner) puts it behind Access.
- A protected domain while Cloudflare Access fails for a moment (503 on its applications): the next sync holds it out of
  the tunnel, says why and that Gitberth tries again, and it's never served open. Once Access is back, Fix serves it
  behind Access again.
"""
from types import SimpleNamespace

import pytest

from e2e.github_reports import set_variables, wait_deployment
from e2e.wait import until
from test_02_pull_request_previews import new_app, own_apps, unique
from test_12_commit_statuses import add_domain
from test_16_protect import ALLOWED, access_app, card, domains_on, flat, save_emails, service_rule, toggle_protected, visit

TUNNEL_PUT = r"/cfd_tunnel/[^/]+/configurations$"
ACCESS_APPS = r"/access/apps$"


def behind_access(rule):
    return bool(rule and (rule.get("originRequest") or {}).get("access"))


def row_text(gitberth, page, app, host):
    gitberth.open(page, f"/apps/{app.key}/network")
    row = card(page).locator(".gb-drow", has_text=host).first
    row.wait_for()
    return flat(row.inner_text())


def fix(gitberth, page, app, host):
    """The Domains card's Fix for the host, once it offers one."""
    gitberth.open(page, f"/apps/{app.key}/network")
    button = card(page).get_by_role("button", name=f"Fix {host}")
    until(lambda: button.count() > 0 and button.first.is_enabled(), 30, f"Fix for {host}", seen=lambda: flat(card(page).inner_text()))
    button.first.click()


@pytest.fixture(scope="module")
def world(gitberth, github, cloudflare, standins):
    zone, team = f"{unique('fz')}.test", unique("team")
    cloudflare.zone(zone)
    cloudflare.set_team(team)
    with domains_on(gitberth, gitberth.new_page, cloudflare, cloudflare.settings_token()):
        with own_apps(gitberth, standins, "failclosed") as (page, made):
            said, _ = save_emails(gitberth, page, ALLOWED)
            assert said.startswith("Saved"), said
            repo, app, _ = new_app(page, gitberth, github, made.track, unique("fcl"))
            host = f"app.{zone}"
            add_domain(gitberth, page, app, host)
            try:
                yield SimpleNamespace(page=page, repo=repo, app=app, zone=zone, team=team, host=host, url=f"https://{host}/")
            finally:
                standins.clear_failures()


@pytest.fixture(scope="module")
def tunnel_failed(world, gitberth, cloudflare, standins):
    w = world
    rule = standins.fail(TUNNEL_PUT, status=502, host="api.cloudflare.com", method="PUT")
    try:
        _, w.protect_said = toggle_protected(gitberth, w.page, w.app, w.host, "Protect")
        w.rule_while_failing = service_rule(cloudflare, w.host)
        w.row_while_failing = until(lambda: (lambda t: t if "Served without Cloudflare Access" in t else None)(row_text(gitberth, w.page, w.app, w.host)),
                                    60, "the card to say the domain is served without Access", seen=lambda: row_text(gitberth, w.page, w.app, w.host))
    finally:
        standins.clear_failures(rule)
    fix(gitberth, w.page, w.app, w.host)
    w.rule_after_fix = until(lambda: (lambda r: r if behind_access(r) else None)(service_rule(cloudflare, w.host)), 60,
                             "the domain to be behind Access after Fix", seen=lambda: cloudflare.rules_for(w.host))
    # Only now: Fix's click returns before its sync writes the tunnel, and the version before it is the one without Access.
    cloudflare.wait_runs()
    w.application = access_app(cloudflare, w.host)
    w.anonymous_after_fix = visit(cloudflare, w.url)
    w.row_after_fix = until(lambda: (lambda t: t if "behind Cloudflare Access" in t else None)(row_text(gitberth, w.page, w.app, w.host)), 60,
                            "the card to say the domain is behind Access", seen=lambda: row_text(gitberth, w.page, w.app, w.host))
    return w


@pytest.fixture(scope="module")
def access_failed(tunnel_failed, gitberth, cloudflare, standins):
    w = tunnel_failed
    rule = standins.fail(ACCESS_APPS, status=503, host="api.cloudflare.com", method="GET")
    try:
        # A sync while Access fails: a deploy without a build (a variable added).
        w.held_deploy = wait_deployment(gitberth, set_variables(gitberth, w.page, w.app, [{"name": "E2E_SYNC", "value": "access fails"}]))
        cloudflare.wait_runs()
        w.held_rule = service_rule(cloudflare, w.host)
        w.held_log = gitberth.deploy_log(w.held_deploy["Id"], tail=60)
        w.held_visit = visit(cloudflare, w.url)
        w.held_row = row_text(gitberth, w.page, w.app, w.host)
    finally:
        standins.clear_failures(rule)
    fix(gitberth, w.page, w.app, w.host)
    w.back_rule = until(lambda: (lambda r: r if behind_access(r) else None)(service_rule(cloudflare, w.host)), 60,
                        "the domain to be served behind Access again", seen=lambda: cloudflare.rules_for(w.host))
    # Only now: Fix's click returns before its sync writes the tunnel, and the version before it holds the domain out.
    # Waiting earlier let a signed-in visit reach a cloudflared still running that one (its catch-all 404).
    cloudflare.wait_runs()
    w.back_anonymous = visit(cloudflare, w.url)
    w.back_allowed = visit(cloudflare, w.url, ALLOWED)
    return w


class TestTunnelFailsWhileProtecting:
    def test_the_switch_says_its_not_behind_access_yet_and_that_gitberth_tries_again(self, tunnel_failed):
        said = tunnel_failed.protect_said
        assert said.startswith(f"{tunnel_failed.host} isn't behind Cloudflare Access yet, so anyone may still open it."), said
        assert "Gitberth tries again every 15 minutes." in said, said

    def test_the_card_says_served_without_access_and_offers_fix(self, tunnel_failed):
        w = tunnel_failed
        assert not behind_access(w.rule_while_failing), w.rule_while_failing
        assert "Served without Cloudflare Access, so anyone can open it. Fix puts it behind Access" in w.row_while_failing, w.row_while_failing

    def test_once_cloudflare_is_back_fix_puts_it_behind_access(self, tunnel_failed):
        w = tunnel_failed
        assert w.application is not None
        assert w.rule_after_fix["originRequest"]["access"] == {"required": True, "teamName": w.team, "audTag": [w.application["aud"]]}, w.rule_after_fix
        assert w.anonymous_after_fix["status"] == 302, w.anonymous_after_fix


class TestAccessFailsForAMoment:
    def test_the_domain_is_held_out_never_served_open(self, access_failed):
        w = access_failed
        assert w.held_deploy["Status"] == "Succeeded", w.held_deploy
        assert w.held_rule is None, w.held_rule
        assert w.held_visit["status"] != 200, w.held_visit

    def test_the_sync_says_why_and_that_gitberth_tries_again(self, access_failed):
        w = access_failed
        line = next((l for l in w.held_log.splitlines() if f"{w.host} isn't served: it's protected, and Cloudflare Access can't be used" in l), None)
        assert line is not None, w.held_log
        assert "Gitberth tries again every 15 minutes." in line, line
        assert "Served without" not in w.held_row, w.held_row

    def test_once_access_is_back_it_is_served_behind_access_again(self, access_failed):
        w = access_failed
        assert w.back_anonymous["status"] == 302, w.back_anonymous
        assert w.back_allowed["status"] == 200, w.back_allowed
