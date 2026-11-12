"""Scenario 16 (protect a domain), with scenario 8's preview domains.

Domains on with the token a user makes from what Settings lists (cloudflare.settings_token(): Cloudflare Tunnel, DNS and
Zone, plus both Access groups).
- Settings › Domains › Protection is Ready once Zero Trust is set up and someone may sign in, and says where visitors
  sign in. A token with only Access: Apps and Policies isn't enough, and the banner names the permission it lacks (the
  bug the rig found: Settings used to ask for that one alone).
- The Domains card's Protected switch asks first, then Cloudflare Access gets "Gitberth: <host>", letting in the saved
  addresses, and the tunnel's rule for the host checks Access (required, the team, the application's aud). Through
  Cloudflare's edge, a visitor who isn't signed in is sent to the sign-in page, an allowed address reaches the app, any
  other address doesn't.
- A new list of addresses changes the application's policy; an empty list is refused while a domain is protected.
- A pull request's preview gets its own domain under the app's (its DNS record too), protected as previews are by
  default; removing the preview takes its rule, record and Access application.
- With Zero Trust gone, the protected domain is never served open: while Gitberth still remembers the team (10 minutes)
  a sync keeps it behind Access; once Settings has asked Cloudflare afresh (and says Zero Trust isn't set up) the next
  sync holds it out of the tunnel, and its line says why. Once Zero Trust is back, the next sync serves it behind Access
  again.
- Stop protecting removes the Access application and the check, and anyone reaches the app again.
"""
import contextlib
import re
from types import SimpleNamespace

import pytest

from e2e.github_reports import set_variables, wait_deployment
from e2e.wait import until
from test_02_pull_request_previews import new_app, open_pull, own_apps, unique, wait_deployed
from test_04_preview_limit import answer_dialog, read_dialog, remove_from_previews_tab
from test_12_commit_statuses import add_domain

ALLOWED = "ann@example.com"
ALSO = "bob@example.com"
STRANGER = "eve@example.com"
ORG_PERMISSION = "Access: Organizations, Identity Providers, and Groups"


@contextlib.contextmanager
def domains_on(gitberth, new_page, cloudflare, token):
    """Settings › Domains: on, with this Cloudflare token; off again afterwards, the token removed, whatever happened."""
    page = new_page()
    gitberth.open(page, "/settings")
    section = page.locator("#domains")
    name = "Manage app domains through Cloudflare"
    try:
        switch = section.get_by_role("switch", name=name)
        if switch.get_attribute("aria-checked") != "true":
            switch.click()
            gitberth.snackbar(page, "Domains turned on")
        tunnel = cloudflare.state()["tunnel_id"]
        until(lambda: f"Tunnel {tunnel[:8]}" in section.inner_text(), 30, "the Domains card to find the tunnel", seen=section.inner_text)
        section.locator("#cf-token").fill(token)
        section.get_by_role("button", name=re.compile(r"^(Save|Replace) token$")).click()
        said = gitberth.snackbar(page, "Token saved|Cloudflare")
        assert said.startswith("Token saved"), said
        yield page
    finally:
        restore = new_page()
        gitberth.open(restore, "/settings")
        section = restore.locator("#domains")
        switch = section.get_by_role("switch", name=name)
        if switch.get_attribute("aria-checked") == "true":
            switch.click()
            gitberth.snackbar(restore, "Domains turned off")
        if section.get_by_role("button", name="Remove").count() > 0:
            section.get_by_role("button", name="Remove").click()
            gitberth.snackbar(restore, "Cloudflare token removed")


def flat(text):
    return " ".join((text or "").split())


def protection_section(gitberth, page):
    """Settings › Domains › Protection: (its heading's text, its help and banner text, the emails' row)."""
    gitberth.open(page, "/settings")
    head = page.locator("#protection")
    head.wait_for()
    row = page.locator(".gb-setrow").filter(has=page.locator("#protect-emails"))
    return head, row


def protection_text(gitberth, page):
    head, row = protection_section(gitberth, page)
    block = page.locator("#domains")
    return flat(head.inner_text()), flat(block.inner_text())


def save_emails(gitberth, page, text):
    """Types the addresses in and saves. Returns (the snackbar, or None when the field said what's wrong; the field's error)."""
    _, row = protection_section(gitberth, page)
    row.locator("#protect-emails").fill(text)
    row.get_by_role("button", name="Save", exact=True).click()
    error = row.locator(".gb-field-error")
    said = until(lambda: page.locator(".mud-snackbar").all_inner_texts() or (["(field)"] if error.count() else None), 30, "Save to answer")
    return flat(said[0]), flat(error.inner_text()) if error.count() else ""


def access_app(cloudflare, host):
    return next((a for a in cloudflare.access_apps() if a.get("domain") == host), None)


def emails_of(app):
    return sorted(i["email"]["email"] for p in (app or {}).get("policies") or [] for i in p.get("include") or [] if "email" in i)


def service_rule(cloudflare, host):
    """The tunnel's rule that sends the host to an app (not a 404 rule), or None."""
    return next((r for r in cloudflare.rules_for(host) if not str(r.get("service", "")).startswith("http_status:")), None)


def card(page):
    return page.locator("section[aria-labelledby='gb-domains-title']")


def toggle_protected(gitberth, page, app, host, answer):
    """The app's Network tab › Domains: the host's Protected switch, its dialog read and answered. Returns (the dialog,
    the snackbar)."""
    gitberth.open(page, f"/apps/{app.key}/network")
    switch = card(page).get_by_role("switch", name=f"Protected: {host}")
    switch.wait_for()
    until(switch.is_enabled, 30, "the Protected switch to be offered", seen=lambda: flat(card(page).inner_text()))
    switch.click()
    dialog = read_dialog(page)
    answer_dialog(page, answer)
    return dialog, flat(gitberth.snackbar(page, re.escape(host)))


def visit(cloudflare, url, who=None):
    return until(lambda: (lambda r: r if r.get("status") else None)(cloudflare.visit(url, signed_in_as=who)), 30, f"a visit to {url}")


# ---- the world, step by step ----

@pytest.fixture(scope="module")
def world(gitberth, github, cloudflare, standins, browser_pages):
    zone, team = f"{unique('pz')}.test", unique("team")
    cloudflare.zone(zone)
    cloudflare.set_team(team)
    with domains_on(gitberth, browser_pages, cloudflare, cloudflare.settings_token()):
        with own_apps(gitberth, standins, "protect") as (page, made):
            said, error = save_emails(gitberth, page, ALLOWED)
            ready_head, ready_text = protection_text(gitberth, page)
            repo, app, _ = new_app(page, gitberth, github, made.track, unique("prt"))
            host = f"app.{zone}"
            add_domain(gitberth, page, app, host)
            yield SimpleNamespace(page=page, repo=repo, app=app, zone=zone, team=team, host=host, url=f"https://{host}/",
                                  saved=said, ready_head=ready_head, ready_text=ready_text)


@pytest.fixture(scope="module")
def browser_pages(gitberth):
    return gitberth.new_page


@pytest.fixture(scope="module")
def protected(world, gitberth, cloudflare):
    w = world
    w.open_before = visit(cloudflare, w.url)
    w.protect_dialog, w.protect_said = toggle_protected(gitberth, w.page, w.app, w.host, "Protect")
    cloudflare.wait_runs()
    w.application = access_app(cloudflare, w.host)
    w.rule = service_rule(cloudflare, w.host)
    w.anonymous = visit(cloudflare, w.url)
    w.allowed = visit(cloudflare, w.url, ALLOWED)
    w.stranger = visit(cloudflare, w.url, STRANGER)
    return w


@pytest.fixture(scope="module")
def emails_changed(protected, gitberth, cloudflare):
    w = protected
    w.change_said, _ = save_emails(gitberth, w.page, f"{ALLOWED}\n{ALSO}")
    w.after_change = until(lambda: (lambda a: a if emails_of(a) == sorted([ALLOWED, ALSO]) else None)(access_app(cloudflare, w.host)), 30,
                           "the Access application to let both in", seen=lambda: emails_of(access_app(cloudflare, w.host)))
    w.empty_said, w.empty_field = save_emails(gitberth, w.page, "  ")
    w.emails_after_empty = gitberth.db.setting("protection.emails")
    return w


@pytest.fixture(scope="module")
def preview_domain(emails_changed, gitberth, cloudflare):
    w = emails_changed
    said = gitberth.set_previews(w.page, w.app, mode="Pull requests")
    assert said.startswith("Saved"), said
    w.n, w.sha = open_pull(w.repo, "feature", "Preview domain", marker="feature")
    w.preview = gitberth.wait_preview(w.app.id, pr=w.n, timeout=120)
    wait_deployed(gitberth, w.preview["Id"], w.sha)
    w.preview_domains = until(lambda: gitberth.db.domains(w.preview["Id"]), 30, "the preview's domain")
    w.preview_host = w.preview_domains[0]["Hostname"]
    cloudflare.wait_runs()
    w.preview_record = until(lambda: cloudflare.dns_records(w.preview_host), 60, "the preview's DNS record")
    w.preview_app = until(lambda: access_app(cloudflare, w.preview_host), 60, "the preview's Access application")
    w.preview_rule = until(lambda: service_rule(cloudflare, w.preview_host), 60, "the preview's rule")
    w.preview_anonymous = visit(cloudflare, f"https://{w.preview_host}/")
    remove_from_previews_tab(w.page, gitberth, w.app, f"PR #{w.n}")
    gitberth.wait_no_preview(w.app.id, pr=w.n, timeout=120)
    w.preview_gone = until(lambda: not cloudflare.dns_records(w.preview_host) and access_app(cloudflare, w.preview_host) is None
                           and not cloudflare.rules_for(w.preview_host), 120, "the preview's record, application and rules to go",
                           seen=lambda: (cloudflare.dns_records(w.preview_host), access_app(cloudflare, w.preview_host), cloudflare.rules_for(w.preview_host)))
    return w


@pytest.fixture(scope="module")
def zero_trust_gone(preview_domain, gitberth, cloudflare):
    w = preview_domain
    # Each sync is a deploy without a build (a variable added on the Environment tab): the sync is what's under test, and
    # a build would only add a pull of the base image from Docker Hub.
    cloudflare.set_team(None)
    # Gitberth remembers the team for 10 minutes: a sync meanwhile keeps the domain behind Access as it was, never open.
    w.cached_deploy = wait_deployment(gitberth, set_variables(gitberth, w.page, w.app, [{"name": "E2E_SYNC_1", "value": "zero trust gone"}]))
    cloudflare.wait_runs()
    w.cached_visit = visit(cloudflare, w.url)
    # Settings › Domains › Protection asks Cloudflare afresh and says why it isn't ready; the next sync holds the domain out.
    w.gone_head, w.gone_text = protection_text(gitberth, w.page)
    w.gone_deploy = wait_deployment(gitberth, set_variables(gitberth, w.page, w.app, [{"name": "E2E_SYNC_2", "value": "still gone"}]))
    cloudflare.wait_runs()
    w.held_rule = service_rule(cloudflare, w.host)
    w.held_visit = cloudflare.visit(w.url)
    gitberth.open(w.page, f"/apps/{w.app.key}/network")
    row = card(w.page).locator(".gb-drow", has_text=w.host).first
    w.held_line = until(lambda: (lambda t: t if "Cloudflare Access" in t else None)(flat(row.inner_text())), 60, "the domain's line to say why",
                        seen=lambda: flat(row.inner_text()))
    cloudflare.set_team(w.team)
    w.back_deploy = wait_deployment(gitberth, set_variables(gitberth, w.page, w.app, [{"name": "E2E_SYNC_3", "value": "zero trust back"}]))
    cloudflare.wait_runs()
    w.back_rule = until(lambda: (lambda r: r if r and (r.get("originRequest") or {}).get("access") else None)(service_rule(cloudflare, w.host)), 60,
                        "the domain to be served behind Access again", seen=lambda: cloudflare.rules_for(w.host))
    w.back_anonymous = visit(cloudflare, w.url)
    return w


@pytest.fixture(scope="module")
def unprotected(zero_trust_gone, gitberth, cloudflare):
    w = zero_trust_gone
    w.stop_dialog, w.stop_said = toggle_protected(gitberth, w.page, w.app, w.host, "Stop protecting")
    cloudflare.wait_runs()
    w.app_after_stop = until(lambda: access_app(cloudflare, w.host) is None, 60, "the Access application to go") and access_app(cloudflare, w.host)
    w.rule_after_stop = service_rule(cloudflare, w.host)
    w.open_again = visit(cloudflare, w.url)
    return w


# ---- the checks ----

# First, before the module's world turns domains on with the full token: this one has domains of its own.
def test_a_token_with_only_apps_and_policies_isnt_enough_and_settings_says_which_permission_is_missing(page, new_page, gitberth, cloudflare, uniq):
    cloudflare.zone(f"{uniq('pa')}.test")
    cloudflare.set_team(uniq("team"))
    with domains_on(gitberth, new_page, cloudflare, cloudflare.token(cloudflare.APPS_ONLY)):
        head, text = protection_text(gitberth, page)
        assert "Not set up" in head, head
        assert "Protection isn't ready" in text and ORG_PERMISSION in text, text


class TestProtect:
    def test_protection_is_ready_with_the_token_settings_asks_for(self, world):
        w = world
        assert w.saved == "Saved. Protected domains let in these addresses.", w.saved
        assert "Ready" in w.ready_head, w.ready_head
        assert f"Visitors sign in at {w.team}.cloudflareaccess.com." in w.ready_text, w.ready_text
        assert "Protection isn't ready" not in w.ready_text, w.ready_text

    def test_settings_names_both_access_permissions(self, world, gitberth):
        _, text = protection_text(gitberth, world.page)
        assert "Account › Access: Apps and Policies › Edit" in text and f"Account › {ORG_PERMISSION} › Read" in text, text

    def test_the_switch_asks_first(self, protected):
        w = protected
        title, body, buttons = w.protect_dialog
        assert (title, buttons) == (f"Protect {w.host}?", ["Cancel", "Protect"]), w.protect_dialog
        assert "Settings › Domains › Protection" in body, body
        assert w.protect_said == f"{w.host} is protected: visitors sign in first.", w.protect_said

    def test_access_gets_an_application_for_the_saved_addresses(self, protected):
        w = protected
        assert w.application is not None and w.application["name"] == f"Gitberth: {w.host}", w.application
        assert emails_of(w.application) == [ALLOWED], w.application

    def test_the_tunnel_rule_checks_access(self, protected):
        w = protected
        access = (w.rule or {}).get("originRequest", {}).get("access")
        assert access == {"required": True, "teamName": w.team, "audTag": [w.application["aud"]]}, w.rule

    def test_only_allowed_visitors_reach_the_app(self, protected):
        w = protected
        assert w.open_before["status"] == 200, w.open_before
        assert w.anonymous["status"] == 302 and "cloudflareaccess.com" in (w.anonymous["headers"].get("Location") or ""), w.anonymous
        assert w.allowed["status"] == 200 and "gbe2e-testapp" in (w.allowed["body"] or ""), w.allowed
        assert w.stranger["status"] != 200, w.stranger


class TestEmails:
    def test_a_new_list_changes_the_applications_policy(self, emails_changed):
        assert emails_of(emails_changed.after_change) == sorted([ALLOWED, ALSO])

    def test_an_empty_list_is_refused_while_a_domain_is_protected(self, emails_changed):
        w = emails_changed
        assert "Turn protection off on those domains first" in (w.empty_field + " " + w.empty_said), (w.empty_said, w.empty_field)
        assert ALLOWED in (w.emails_after_empty or ""), w.emails_after_empty


class TestPreviewDomain:
    def test_a_preview_gets_a_domain_of_its_own_with_its_record(self, preview_domain):
        w = preview_domain
        assert re.fullmatch(rf"app-pr-{w.n}\.{re.escape(w.zone)}", w.preview_host), w.preview_host
        assert w.preview_record and w.preview_record[0]["proxied"], w.preview_record

    def test_it_is_protected_like_previews_are_by_default(self, preview_domain):
        w = preview_domain
        assert w.preview_domains[0]["Protected"] in (1, True), w.preview_domains
        assert w.preview_app["name"] == f"Gitberth: {w.preview_host}", w.preview_app
        assert (w.preview_rule.get("originRequest") or {}).get("access", {}).get("audTag") == [w.preview_app["aud"]], w.preview_rule
        assert w.preview_anonymous["status"] == 302, w.preview_anonymous

    def test_removing_the_preview_takes_its_domain_with_it(self, preview_domain):
        assert preview_domain.preview_gone


class TestFailClosed:
    def test_while_gitberth_still_knows_the_team_the_domain_stays_behind_access(self, zero_trust_gone):
        assert zero_trust_gone.cached_visit["status"] == 302, zero_trust_gone.cached_visit

    def test_settings_says_zero_trust_isnt_set_up(self, zero_trust_gone):
        w = zero_trust_gone
        assert "Not set up" in w.gone_head, w.gone_head
        assert "Cloudflare Zero Trust isn't set up for this account yet." in w.gone_text, w.gone_text

    def test_without_zero_trust_the_protected_domain_is_held_out_never_served_open(self, zero_trust_gone):
        w = zero_trust_gone
        assert w.held_rule is None, w.held_rule
        assert w.held_visit["status"] != 200, w.held_visit
        assert "Cloudflare Access" in w.held_line, w.held_line

    def test_once_zero_trust_is_back_the_next_sync_serves_it_behind_access(self, zero_trust_gone):
        w = zero_trust_gone
        assert w.back_anonymous["status"] == 302, w.back_anonymous


class TestUnprotect:
    def test_stop_protecting_asks_and_removes_the_application_and_the_check(self, unprotected):
        w = unprotected
        title, _, buttons = w.stop_dialog
        assert (title, buttons) == (f"Stop protecting {w.host}?", ["Cancel", "Stop protecting"]), w.stop_dialog
        assert w.stop_said == f"{w.host} is open to everyone again.", w.stop_said
        assert w.app_after_stop is None
        assert w.rule_after_stop is not None and not (w.rule_after_stop.get("originRequest") or {}).get("access"), w.rule_after_stop

    def test_anyone_reaches_the_app_again(self, unprotected):
        assert unprotected.open_again["status"] == 200, unprotected.open_again
