"""Scenario 37: the tokens Gitberth uses (the token watch, src/Gitberth/Credentials), and the attention board their
warnings go on.

- Settings › Notifications has the switch A token is about to expire or is refused, on unless someone turned it off.
- With nothing that needs attention, the dashboard has no attention banner and the top bar no pill.
- A GitHub token 30 days from its expiry says nothing; moved to 6 days on GitHub, Check the token tells once (a
  token.expiring webhook), the dashboard gets a warn banner and Settings its "renew it soon" tag; checked again, nothing
  more is sent. Dismissed on the dashboard, the banner stays gone after a reload.
- A token revoked on GitHub: Check the token says so at once, with a critical banner in Settings and on the dashboard,
  the top bar's pill, one token.refused notice and its audit entry. Saving a made-up token GitHub refuses changes none of
  it; saving a good one takes the banner and the pill away at once.
- A GitLab token moved to 3 days from its expiry is told; revoked, it's refused with GitLab's own word. A Cloudflare token
  with an end date 10 days away shows it in Settings and on the dashboard as soon as it's saved; disabled in Cloudflare
  it's refused (critical banner, one notice), active again it's taken (audit domains.token.accepted).
"""
import datetime
import re

import pytest

from e2e import audit
from e2e.standin import Notify
from e2e.wait import stays, until
from test_15_refusals import token_saved
from test_16_protect import domains_on
from test_21_notifications import add_destination, remove_all
from test_33_gitlab import SECTION as GITLAB, gl_on, open_gitlab, remove_gitlab_token, save_gitlab  # noqa: F401 - gl_on is a fixture

HOOK_HOST = "notify.e2e.test"


def flat(text):
    return " ".join((text or "").split())


def github_expiry(days):
    """A GitHub token's expiry as the stand-in takes it (and GitHub's header says it), days from now."""
    return (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=days)).strftime("%Y-%m-%d %H:%M:%S UTC")


def iso_in(days):
    return (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=days)).strftime("%Y-%m-%dT%H:%M:%SZ")


@pytest.fixture(scope="module")
def notify(gitberth, standins):
    """A webhook destination for the module's notices, removed again after it."""
    page = gitberth.new_page()
    try:
        add_destination(gitberth, page, "Webhook", {"Name": "Tokens", "URL": Notify.WEBHOOK})
        yield Notify(standins)
    finally:
        try:
            remove_all(gitberth)
        finally:
            page.context.close()


def notices(notify, since, event, credential):
    """The webhook notices since the marker of this event about this token, as their JSON bodies."""
    return [c["body"] for c in notify.received(since, HOOK_HOST)
            if isinstance(c.get("body"), dict) and c["body"].get("event") == event and c["body"].get("credential") == credential]


def wait_notice(notify, since, event, credential, timeout=60):
    return until(lambda: notices(notify, since, event, credential), timeout, f"a {event} notice about the {credential} token",
                 seen=lambda: [(c.get("body") or {}).get("title") for c in notify.received(since, HOOK_HOST)])[0]


def check(gitberth, page, section, what):
    """Settings › <section> › Check the token; returns what the snackbar said."""
    gitberth.open(page, "/settings")
    s = page.locator(section)
    s.get_by_role("button", name="Check the token", exact=True).click()
    return flat(gitberth.snackbar(page, what))


def dashboard_banner(gitberth, page, title):
    """The dashboard's attention banner with this title (waited for), after opening the dashboard."""
    gitberth.open(page, "/")
    page.locator(".gb-kpis2").wait_for()
    banner = page.locator(".gb-banner.gb-attention").filter(has=page.locator(".gb-banner-title", has_text=title))
    banner.first.wait_for(timeout=30_000)
    return banner.first


def attention_titles(page):
    return [flat(t) for t in page.locator(".gb-banner.gb-attention .gb-banner-title").all_inner_texts()]


def test_the_token_switch_is_on_and_nothing_asks_for_attention(page, gitberth):
    gitberth.open(page, "/settings")
    switch = page.locator("#notifications").get_by_role("switch", name="A token is about to expire or is refused", exact=True)
    switch.wait_for()
    assert switch.get_attribute("aria-checked") == "true", "A token is about to expire or is refused isn't on"

    gitberth.open(page, "/")
    page.locator(".gb-kpis2").wait_for()
    assert page.locator(".gb-banner.gb-attention").count() == 0, gitberth.banners(page)
    assert page.locator(".gb-topbar .gb-attnpill").count() == 0, "the top bar has a pill with nothing to say"
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


def test_a_github_token_weeks_from_expiry_is_said_once_and_on_the_dashboard(page, new_page, gitberth, github, standins, baseline, notify):
    token = github.token(login="e2e-bot", expires=github_expiry(30))
    with token_saved(gitberth, new_page, token, baseline) as settings:
        mark = standins.seq()
        # A month off: nothing to say, anywhere.
        gitberth.open(settings, "/")
        settings.locator(".gb-kpis2").wait_for()
        assert not [t for t in attention_titles(settings) if "GitHub" in t], attention_titles(settings)

        # Moved to 6 days on GitHub: Check the token learns it, and tells it once.
        github.change_token(token, expires=github_expiry(6))
        said = check(gitberth, settings, "#github", "GitHub took the token|GitHub refused|Couldn't ask GitHub")
        assert re.fullmatch(r"GitHub took the token: it's e2e-bot's, and it expires on \w{3} \d{1,2}, \d{4}\.", said), said
        notice = wait_notice(notify, mark, "token.expiring", "github")
        assert (notice["title"], notice["level"]) == ("The GitHub token expires in 6 days", "warning"), notice
        assert notice["expires"] and notice["app"] is None, notice
        tag = settings.locator("#github .gb-tag--warn")
        tag.wait_for()
        assert "renew it soon" in flat(tag.inner_text()), tag.inner_text()

        # The dashboard's warn banner.
        page_banner = dashboard_banner(gitberth, page, "The GitHub token expires in 6 days")
        assert "gb-tone-warn" in (page_banner.get_attribute("class") or ""), page_banner.get_attribute("class")
        assert "Make a new token on GitHub and save it in Settings › GitHub access." in flat(page_banner.inner_text()), page_banner.inner_text()

        # Checked again: nothing more is sent.
        check(gitberth, settings, "#github", "GitHub took the token")
        stays(lambda: len(notices(notify, mark, "token.expiring", "github")) == 1, 5, "one token.expiring notice")

        # Dismissed on the dashboard: gone, and still gone after a reload.
        page_banner = dashboard_banner(gitberth, page, "The GitHub token expires in 6 days")
        page_banner.get_by_role("button", name="Dismiss").click()
        page_banner.wait_for(state="detached")
        page.reload()
        gitberth.wait_ready(page)
        page.locator(".gb-kpis2").wait_for()
        assert "The GitHub token expires in 6 days" not in attention_titles(page), attention_titles(page)


def test_a_revoked_github_token_is_said_at_once_and_a_new_one_clears_it(page, new_page, gitberth, github, standins, baseline, notify):
    token = github.token(login="e2e-bot")
    first = audit.last_id()
    with token_saved(gitberth, new_page, token, baseline) as settings:
        mark = standins.seq()
        github.change_token(token, revoked=True)
        said = check(gitberth, settings, "#github", "GitHub took the token|GitHub refused|Couldn't ask GitHub")
        assert said == "GitHub refused the token: it has expired or was revoked. Save a new one.", said

        # Settings › GitHub access, the dashboard and the top bar say so, critically.
        section_banner = settings.locator("#github .gb-banner.gb-attention")
        section_banner.wait_for()
        assert flat(section_banner.locator(".gb-banner-title").inner_text()) == "GitHub refused the token", section_banner.inner_text()
        assert "gb-tone-critical" in (section_banner.get_attribute("class") or "")
        banner = dashboard_banner(gitberth, page, "GitHub refused the token")
        text = flat(banner.locator(".gb-banner-sub").inner_text())
        assert text.startswith("GitHub refused the token for e2e-bot ") and "Gitberth checked it" in text and "it has expired or was revoked." in text, text
        pill = page.locator(".gb-topbar .gb-attnpill")
        pill.wait_for()
        assert flat(pill.inner_text()) == "GitHub token refused", pill.inner_text()
        assert banner.get_by_role("button", name="Dismiss").count() == 0, "a refused token's banner can be dismissed"

        # One notice, and the audit log has it.
        notice = wait_notice(notify, mark, "token.refused", "github")
        assert (notice["title"], notice["level"]) == ("GitHub refused the token", "critical"), notice
        entry = until(lambda: audit.since(first, "action = ?", "settings.github.token.refused"), 30, "the refusal's audit entry")[0]
        assert (entry["outcome"], entry["actor_kind"]) == ("Failed", "Gitberth"), entry

        # A made-up token GitHub refuses isn't saved, and changes nothing about the saved one's refusal.
        said = gitberth.save_github_token(settings, "github_pat_" + "x" * 40)
        assert "invalid or expired" in said, said
        stays(lambda: len(notices(notify, mark, "token.refused", "github")) == 1, 5, "one token.refused notice")
        dashboard_banner(gitberth, page, "GitHub refused the token")

    # The session's token saved again (token_saved does): the banner and the pill go at once.
    gitberth.open(page, "/")
    page.locator(".gb-kpis2").wait_for()
    until(lambda: "GitHub refused the token" not in attention_titles(page), 15, "the refused banner to go", seen=lambda: attention_titles(page))
    assert page.locator(".gb-topbar .gb-attnpill").count() == 0, page.locator(".gb-topbar .gb-attnpill").all_inner_texts()


def test_a_gitlab_token_and_a_cloudflare_token(page, new_page, gitberth, gitlab, cloudflare, standins, gl_on, notify):
    # GitLab: a token a month from its expiry, moved to 3 days; then revoked.
    today = datetime.datetime.now(datetime.timezone.utc).date()
    token = gitlab.token(username="e2e-bot", scopes=["api"], expires_at=(today + datetime.timedelta(days=30)).isoformat())
    try:
        assert save_gitlab(gitberth, page, token=token) == "Token saved."
        mark = standins.seq()
        gitlab.change_token(token, expires_at=(today + datetime.timedelta(days=3)).isoformat())
        said = check(gitberth, page, GITLAB, "GitLab took the token|GitLab says|Couldn't ask GitLab")
        assert said == "GitLab took the token: it's e2e-bot's.", said
        notice = wait_notice(notify, mark, "token.expiring", "gitlab")
        assert notice["title"] == "The GitLab token expires in 3 days", notice

        gitlab.change_token(token, revoked=True)
        said = check(gitberth, page, GITLAB, "GitLab took the token|GitLab says|Couldn't ask GitLab")
        assert said == "GitLab says this token is invalid, expired or revoked.", said
        banner = open_gitlab(gitberth, page).locator(".gb-banner.gb-attention")
        banner.wait_for()
        assert flat(banner.locator(".gb-banner-title").inner_text()) == "GitLab refused the token", banner.inner_text()
        assert "GitLab says it was revoked." in flat(banner.inner_text()), banner.inner_text()
        assert wait_notice(notify, mark, "token.refused", "gitlab")["level"] == "critical"
    finally:
        remove_gitlab_token(gitberth, page)

    # Cloudflare: a token with an end date 10 days away shows it as soon as it's saved.
    cf_token = cloudflare.token(expires_on=iso_in(10))
    first = audit.last_id()
    with domains_on(gitberth, new_page, cloudflare, cf_token) as settings:
        settings.reload()
        gitberth.wait_ready(settings)
        section = settings.locator("#domains")
        tag = section.locator(".gb-tag--warn", has_text="token expires")
        tag.wait_for()
        assert "renew it soon" in flat(tag.inner_text()), tag.inner_text()
        assert "gb-tone-info" in (dashboard_banner(gitberth, page, "The Cloudflare API token expires in 10 days").get_attribute("class") or "")

        mark = standins.seq()
        cloudflare.change_token(cf_token, status="disabled")
        said = check(gitberth, settings, "#domains", "Cloudflare took the token|Cloudflare refused|Couldn't ask Cloudflare")
        assert said == "Cloudflare refused the token: it has expired, or was disabled or deleted. Save a new one.", said
        banner = dashboard_banner(gitberth, page, "Cloudflare refused Gitberth's API token")
        assert "gb-tone-critical" in (banner.get_attribute("class") or "")
        assert "the routes already in your tunnel keep working" in flat(banner.inner_text()), banner.inner_text()
        wait_notice(notify, mark, "token.refused", "cloudflare")
        stays(lambda: len(notices(notify, mark, "token.refused", "cloudflare")) == 1, 5, "one token.refused notice about Cloudflare's token")

        # Active again: taken, and recorded so.
        cloudflare.change_token(cf_token, status="active")
        said = check(gitberth, settings, "#domains", "Cloudflare took the token|Cloudflare refused|Couldn't ask Cloudflare")
        assert said.startswith("Cloudflare took the token, and it expires on "), said
        until(lambda: audit.since(first, "action = ?", "domains.token.accepted"), 30, "the audit entry for the token taken again")
        gitberth.open(page, "/")
        page.locator(".gb-kpis2").wait_for()
        assert "Cloudflare refused Gitberth's API token" not in attention_titles(page), attention_titles(page)
