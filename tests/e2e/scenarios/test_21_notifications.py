"""Scenario 21: notifications (1.2).

- Settings › Notifications takes each kind of destination through its dialog (Discord, Slack, ntfy with a token, a
  webhook with a token, mail through a mail server with a sign-in), sends each a test as it's added, and lists each with
  where it goes (never its secret) and how its last delivery went.
- A destination that refuses says why, in the row and when its test is sent again.
- A failed deploy reaches every destination, in each one's own shape, linking to the deploy's log; a success only once
  "A deploy succeeds" is on.
- An app that crashes is told about once (it restarted itself), and a crash right after isn't told again; an app left
  out on its Settings tab isn't told about at all.
- A failing health check is told, and so is its passing again.
"""
import json
import re

import pytest

from e2e.net import call
from e2e.standin import Notify
from e2e.wait import stays, until

SECTION = "#notifications"

BUILD_FAILS = """FROM python:3.12-alpine
WORKDIR /app
COPY . .
RUN echo the build breaks here && exit 9
EXPOSE 8080
CMD ["python3", "-u", "/app/server.py"]
"""

# The test app with a quick health check of its own: /sick?on=1 makes it fail, ?on=0 pass again.
WITH_HEALTHCHECK = """FROM python:3.12-alpine
WORKDIR /app
COPY . .
EXPOSE 8080
HEALTHCHECK --interval=2s --timeout=2s --retries=2 CMD test ! -f /tmp/sick
CMD ["python3", "-u", "/app/server.py"]
"""

HOSTS = {"discord": "discord.com", "slack": "hooks.slack.com", "ntfy": "ntfy.e2e.test", "webhook": "notify.e2e.test"}


def flat(text):
    return " ".join((text or "").split())


def section(gitberth, page):
    gitberth.open(page, "/settings")
    s = page.locator(SECTION)
    s.wait_for()
    return s


def rows(page):
    return page.locator(f"{SECTION} .gb-notirow")


def row_text(page, title):
    return flat(rows(page).filter(has=page.locator(".gb-regrow-t", has_text=re.compile(f"^{re.escape(title)}$"))).first.inner_text())


def add_destination(gitberth, page, kind, fields):
    """The dialog, as someone fills it in: kind is the segment's label, fields maps a field's label to its value
    (Security: its segment). Returns the snackbar's text."""
    s = section(gitberth, page)
    s.get_by_role("button", name="Add a destination").click()
    dialog = page.get_by_role("dialog")
    dialog.wait_for()
    dialog.get_by_role("radio", name=kind, exact=True).click()
    for label, value in fields.items():
        if label == "Security":
            dialog.get_by_role("radio", name=value, exact=True).click()
        else:
            dialog.get_by_label(label, exact=True).fill(str(value))
    dialog.get_by_role("button", name="Add and send a test").click()
    return gitberth.snackbar(page, r"^Added ")


def set_event(gitberth, page, title, on):
    s = section(gitberth, page)
    switch = s.get_by_role("switch", name=title, exact=True)
    switch.wait_for()
    if (switch.get_attribute("aria-checked") == "true") != on:
        switch.click()
        until(lambda: switch.get_attribute("aria-checked") == ("true" if on else "false"), 10, f"{title} to be {'on' if on else 'off'}")


def remove_all(gitberth):
    """Takes every destination out again, one per fresh page: a page left open while deliveries come in redraws its list."""
    for _ in range(3 * 10):
        page = gitberth.new_page()
        try:
            section(gitberth, page)
            if rows(page).count() == 0:
                return
            title = flat(rows(page).first.locator(".gb-regrow-t").inner_text())
            page.get_by_role("button", name=f"Remove {title}", exact=True).click()
            # The confirmation's own button: Settings has other Remove buttons (the GitHub token's) behind the dialog.
            page.get_by_role("dialog").get_by_role("button", name="Remove", exact=True).click()
            gitberth.snackbar(page, rf"^Removed {re.escape(title)}\.")
        finally:
            page.close()
    raise AssertionError("the destinations couldn't all be removed")

@pytest.fixture(scope="module")
def destinations(gitberth, standins):
    """Every kind of destination, added through Settings once for the module, and removed again after it."""
    notify = Notify(standins)
    page = gitberth.new_page()
    mark = standins.seq()
    said = {
        "discord": add_destination(gitberth, page, "Discord", {"Name": "Ops", "Webhook URL": Notify.DISCORD}),
        "slack": add_destination(gitberth, page, "Slack", {"Name": "Team", "Webhook URL": Notify.SLACK}),
        "ntfy": add_destination(gitberth, page, "ntfy", {"Name": "Phone", "Topic URL": Notify.NTFY, "Access token": "tk_e2e_ntfy"}),
        "webhook": add_destination(gitberth, page, "Webhook", {"Name": "Automations", "URL": Notify.WEBHOOK, "Bearer token": "e2e-webhook-token"}),
        "email": add_destination(gitberth, page, "Email", {
            "Name": "Mail", "Mail server": Notify.SMTP_HOST, "Port": Notify.SMTP_PORT, "Security": "None", "User name": "gitberth@e2e.test",
            "Password": "e2e-mail-password", "From": "gitberth@e2e.test", "To": "ops@e2e.test, me@e2e.test"}),
    }
    try:
        yield {"notify": notify, "page": page, "mark": mark, "said": said}
    finally:
        remove_all(gitberth)
        set_event(gitberth, gitberth.new_page(), "A deploy succeeds", False)


def test_each_destination_is_added_with_a_test_and_listed_without_its_secret(destinations, gitberth):
    d = destinations
    notify, page, mark = d["notify"], d["page"], d["mark"]
    assert all(s.endswith(", and a test went there.") for s in d["said"].values()), d["said"]
    ui = gitberth.db.setting("ui.address")

    discord = notify.wait(mark, HOSTS["discord"])[0]
    assert discord["path"] == "/api/webhooks/1234567/e2e-discord-secret" and discord["method"] == "POST", discord
    embed = discord["body"]["embeds"][0]
    assert embed["title"] == "Gitberth can reach Ops" and embed["url"] == f"{ui}settings#notifications", embed
    assert discord["body"]["allowed_mentions"] == {"parse": []}, discord["body"]

    slack = notify.wait(mark, HOSTS["slack"])[0]
    assert slack["body"]["text"].startswith(":information_source: *Gitberth can reach Team*"), slack["body"]

    ntfy = notify.wait(mark, HOSTS["ntfy"])[0]
    assert ntfy["path"] == "/gitberth-e2e" and ntfy["token"] == "tk_e2e_ntfy", ntfy
    assert ntfy["headers"]["Title"] == "Gitberth can reach Phone" and ntfy["headers"]["Click"] == f"{ui}settings#notifications", ntfy["headers"]
    assert "This is a test from Settings" in ntfy["body_text"], ntfy

    hook = notify.wait(mark, HOSTS["webhook"])[0]
    assert hook["token"] == "e2e-webhook-token" and hook["headers"]["X-Gitberth-Event"] == "test", hook
    assert hook["body"]["event"] == "test" and hook["body"]["title"] == "Gitberth can reach Automations", hook["body"]

    mail = notify.wait_mail(mark)[0]
    assert mail["from"] == "gitberth@e2e.test" and sorted(mail["to"]) == ["me@e2e.test", "ops@e2e.test"], mail
    assert mail["auth"] == "gitberth@e2e.test", mail
    assert mail["subject"] == "[Gitberth] Gitberth can reach Mail" and "This is a test from Settings" in mail["text"], mail

    # The list: where each goes, never its secret, and its test.
    section(gitberth, page)
    assert rows(page).count() == 5
    discord_row = row_text(page, "Ops")
    assert "Discord · https://discord.com/…" in discord_row and "Sent just now: Gitberth can reach Ops" in discord_row, discord_row
    assert "e2e-discord-secret" not in flat(page.locator(SECTION).inner_text())
    assert "ntfy · ntfy.e2e.test/gi••••" in row_text(page, "Phone")
    assert "Email · ops@e2e.test, me@e2e.test via smtp.e2e.test" in row_text(page, "Mail")
    # Kept encrypted: nothing secret in Gitberth's settings table.
    raw = gitberth.db.setting("notify.destinations") or ""
    assert raw and "e2e-discord-secret" not in raw and "e2e-mail-password" not in raw and "tk_e2e_ntfy" not in raw
    assert not gitberth.errors(page), gitberth.errors(page)


def test_a_refusing_destination_says_why(destinations, gitberth):
    notify, page = destinations["notify"], destinations["page"]
    notify.answer(HOSTS["discord"], 404, times=1)
    section(gitberth, page)
    rows(page).filter(has_text="Ops").get_by_role("button", name="Send a test to Ops").click()
    said = gitberth.snackbar(page, r"^The test didn't go")
    assert said == "The test didn't go: Discord says the URL doesn't exist (404): the webhook was deleted, or the URL is wrong.", said
    until(lambda: "Failed just now: Discord says the URL doesn't exist (404)" in row_text(page, "Ops"), 15, "the row to say why",
          seen=lambda: row_text(page, "Ops"))
    assert "e2e-discord-secret" not in row_text(page, "Ops")


def test_a_failed_deploy_reaches_every_destination_and_a_success_once_its_on(destinations, gitberth, github, apps, uniq, standins):
    notify, page = destinations["notify"], destinations["page"]
    repo = github.create_repo(uniq("nf"), files={"marker.txt": "notify v1"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    first = gitberth.wait_deploy(app.id, timeout=420)
    ui = gitberth.db.setting("ui.address")

    # A success isn't told until it's turned on.
    since = standins.seq()
    sha = repo.commit({"marker.txt": "notify v2"}, message="v2")["sha"]
    two = gitberth.wait_deploy(app.id, after=first["Id"], timeout=300)
    stays(lambda: not notify.received(since, HOSTS["discord"]), 5, "no notice of a success while it's off")
    set_event(gitberth, page, "A deploy succeeds", True)
    since = standins.seq()
    sha = repo.commit({"marker.txt": "notify v3"}, message="v3")["sha"]
    three = gitberth.wait_deploy(app.id, after=two["Id"], timeout=300)
    live = notify.wait(since, HOSTS["discord"])[0]["body"]["embeds"][0]
    assert live["title"] == f"{app.name} is live", live
    assert live["description"] == f"Deploy #{three['Id']} (New commit, commit {sha[:7]}) succeeded.", live
    assert live["url"] == f"{ui}apps/{app.id}/deployments/{three['Id']}", live

    # A build that fails: every destination hears of it, in its own shape, with the link to the log.
    since = standins.seq()
    broken = repo.commit({"Dockerfile": BUILD_FAILS}, message="Break the build")["sha"]
    failed = gitberth.wait_deploy(app.id, after=three["Id"], statuses=("Failed",), timeout=300)
    title = f"Deploy of {app.name} failed"
    log_url = f"{ui}apps/{app.id}/deployments/{failed['Id']}"
    text = f"Deploy #{failed['Id']} (New commit, commit {broken[:7]}) failed."

    embed = notify.wait(since, HOSTS["discord"])[0]["body"]["embeds"][0]
    assert (embed["title"], embed["url"], embed["color"]) == (title, log_url, 0xDA3633), embed
    assert embed["description"].startswith(text), embed
    slack = notify.wait(since, HOSTS["slack"])[0]["body"]["text"]
    assert slack.startswith(f":rotating_light: *{title}*") and f"<{log_url}|Open in Gitberth>" in slack, slack
    ntfy = notify.wait(since, HOSTS["ntfy"])[0]
    assert (ntfy["headers"]["Title"], ntfy["headers"]["Priority"], ntfy["headers"]["Tags"], ntfy["headers"]["Click"]) == (title, "4", "rotating_light", log_url), ntfy["headers"]
    assert ntfy["body_text"].startswith(text), ntfy
    hook = notify.wait(since, HOSTS["webhook"])[0]["body"]
    assert (hook["event"], hook["level"], hook["app"], hook["appId"], hook["deployment"], hook["commit"], hook["trigger"], hook["url"]) == \
        ("deploy.failed", "critical", app.name, app.id, failed["Id"], broken, "Poll", log_url), hook
    mail = notify.wait_mail(since)[0]
    assert mail["subject"] == f"[Gitberth] {title}" and log_url in mail["text"] and text in mail["text"], mail
    # Each once.
    stays(lambda: len(notify.received(since, HOSTS["discord"])) == 1, 5, "one notice per deploy")


def crash(gitberth, app, code=3):
    status, _, _ = call("POST", f"{gitberth.lan_url(app.id)}/exit?code={code}", b"", {})
    assert status == 200, status


def test_a_crash_is_told_once_and_an_app_left_out_isnt(destinations, gitberth, github, apps, uniq, standins, engine):
    notify, page = destinations["notify"], destinations["page"]
    repo = github.create_repo(uniq("nc"), files={"marker.txt": "crashes"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    gitberth.wait_deploy(app.id, timeout=420)
    ui = gitberth.db.setting("ui.address")

    since = standins.seq()
    crash(gitberth, app)
    gitberth.wait_answers(gitberth.lan_url(app.id), "/")
    embed = notify.wait(since, HOSTS["discord"], timeout=90)[0]["body"]["embeds"][0]
    assert embed["title"] == f"{app.name} crashed", embed
    assert embed["description"] == "Its process exited with code 3 and it restarted itself. Its log on Gitberth has the details.", embed
    assert embed["url"] == f"{ui}apps/{app.container}/logs", embed
    # Again at once: counted, not told (a crash loop says so once, then a count every 30 minutes).
    since = standins.seq()
    crash(gitberth, app)
    gitberth.wait_answers(gitberth.lan_url(app.id), "/")
    stays(lambda: not notify.received(since, HOSTS["discord"]), 25, "no second notice inside the window")

    # Another app, left out on its Settings tab: its crash isn't told.
    other_repo = github.create_repo(uniq("nq"), files={"marker.txt": "quiet"})
    other = apps.track(gitberth.create_git_app(page, other_repo.url))
    gitberth.wait_deploy(other.id, timeout=420)
    gitberth.open(page, f"/apps/{other.key}/settings")
    switch = page.locator("#sec-general").get_by_role("switch", name="Notifications", exact=True)
    switch.wait_for()
    assert switch.get_attribute("aria-checked") == "true"
    switch.click()
    bar = page.locator(".gb-applybar")
    bar.wait_for()
    bar.get_by_role("button", name=re.compile(r"^(Apply|Review and apply)$")).click()
    confirm = page.get_by_role("dialog").get_by_role("button", name=re.compile(r"^Apply"))
    try:
        confirm.click(timeout=3000)
    except Exception:
        pass
    until(lambda: other.container in json.loads(gitberth.db.setting("notify.muted") or "[]"), 30, f"{other.container} to be left out",
          seen=lambda: gitberth.db.setting("notify.muted"))
    since = standins.seq()
    crash(gitberth, other)
    gitberth.wait_answers(gitberth.lan_url(other.id), "/")
    stays(lambda: not notify.received(since, HOSTS["discord"]), 25, "no notice about an app that's left out")


def test_a_failing_health_check_is_told_and_its_passing_again(destinations, gitberth, github, apps, uniq, standins):
    notify, page = destinations["notify"], destinations["page"]
    repo = github.create_repo(uniq("nh"), files={"marker.txt": "health", "Dockerfile": WITH_HEALTHCHECK})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    gitberth.wait_deploy(app.id, timeout=420)
    url = gitberth.lan_url(app.id)

    since = standins.seq()
    assert call("POST", f"{url}/sick?on=1", b"", {})[0] == 200
    failing = notify.wait(since, HOSTS["discord"], timeout=90)[0]["body"]["embeds"][0]
    assert failing["title"] == f"{app.name}'s health check is failing", failing
    since = standins.seq()
    assert call("POST", f"{url}/sick?on=0", b"", {})[0] == 200
    passing = notify.wait(since, HOSTS["discord"], timeout=90)[0]["body"]["embeds"][0]
    assert passing["title"] == f"{app.name}'s health check passes again", passing
