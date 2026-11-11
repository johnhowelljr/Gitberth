"""Scenario 20: Gitberth 1.0's basics still work in the rig.

- Every page needs sign-in: a browser that isn't signed in is sent to the sign-in page, /apps/new included.
- Sign-in (a wrong password is refused) and sign-out.
- New app from the stand-in GitHub: Check and analysis read the repo over git, Create and deploy builds it, it runs
  the repo's commit as production, it answers on its LAN address, and its Logs tab and deploy log show it.
- A new commit deploys by itself; Roll back (Activity) runs the earlier build again, and the watcher doesn't undo it.
- Settings save: the watcher's interval is kept, and a new GitHub token is checked with GitHub before it's saved.
"""
import re
from urllib.parse import parse_qs, urlsplit

from e2e.net import get, get_json
from e2e.wait import until


def test_every_page_needs_sign_in(anon_page, gitberth):
    for path in ("/apps/new", "/settings", "/apps"):
        anon_page.goto(gitberth.url(path))
        url = urlsplit(anon_page.url)
        assert url.path == "/login", f"{path} opened without signing in (at {anon_page.url})"
        assert parse_qs(url.query).get("ReturnUrl") == [path], f"{path} lost where it was going: {anon_page.url}"
        assert anon_page.locator("#password").is_visible()


def test_sign_in_and_out(new_page, gitberth):
    page = new_page(signed_in=False)
    assert not gitberth.attempt_sign_in(page, password="not-the-password"), "a wrong password signed in"
    assert page.locator(".gb-auth-error[role=alert]").is_visible(), "a wrong password wasn't explained"

    gitberth.sign_in(page)
    assert gitberth.path_of(page) == "/"
    page.locator(".gb-kpis2").wait_for()

    gitberth.sign_out(page)
    page.goto(gitberth.url("/apps"))
    assert gitberth.path_of(page) == "/login", "still signed in after signing out"


def marker_of(url):
    return (get_json(url.rstrip("/") + "/") or {}).get("marker")


def test_new_app_from_github_deploys_and_answers(page, gitberth, github, engine, apps, uniq, scenario):
    repo = github.create_repo(uniq("smoke"), files={"marker.txt": "smoke v1"})
    sha = repo.branch_sha("main")

    app = apps.track(gitberth.create_git_app(page, repo.url))
    assert app.slug == repo.name

    # New app read the repo over git: an ls-remote (Check), then a clone of main (the analysis).
    git_calls = repo.git_calls(scenario.seq)
    assert any((c.get("git") or {}).get("ls_remote") for c in git_calls), f"no ls-remote of {repo.full_name}: {git_calls}"
    assert any((c.get("git") or {}).get("command") == "fetch" for c in git_calls), f"no clone of {repo.full_name}: {git_calls}"

    deploy = gitberth.wait_deploy(app.id, timeout=420)
    assert deploy["Trigger"] == "Created"
    assert deploy["CommitSha"] == sha

    # It runs as gb-<slug>, from the image built from that commit, on the LAN at its own host port.
    assert engine.running(app.container)
    assert engine.image_of(app.container) == f"{app.image_repository}:{sha[:7]}"
    url = gitberth.lan_url(app.id)
    home = gitberth.wait_answers(url, "/")
    assert home["marker"] == "smoke v1"
    env = gitberth.wait_answers(url, "/env")
    assert env.get("VERCEL_ENV") == "production"
    assert env.get("VERCEL_GIT_COMMIT_SHA") == sha
    assert env.get("VERCEL_GIT_COMMIT_REF") == "main"
    assert env.get("VERCEL_GIT_REPO_OWNER") == repo.owner and env.get("VERCEL_GIT_REPO_SLUG") == repo.name

    # Its page shows it running at that commit, with its LAN address.
    gitberth.open(page, f"/apps/{app.key}")
    hero = page.locator(".gb-hero")
    until(lambda: sha[:7] in hero.inner_text(), 30, "the app page to show the commit", seen=hero.inner_text)
    assert url.split("://", 1)[1] in hero.inner_text(), f"the LAN address {url} isn't on the app's page"

    # The Logs tab streams the container's output.
    gitberth.open(page, f"/apps/{app.key}/logs")
    terminal = page.locator(".gb-terminal-body")
    until(lambda: "gbe2e test app listening" in terminal.inner_text(), 60, "the app's start-up line in its Logs tab",
          seen=terminal.inner_text)

    # The deploy's own page has its log, ending well, and says where it's live.
    gitberth.open(page, f"/apps/{app.id}/deployments/{deploy['Id']}")
    log = page.locator(".gb-terminal-body")
    until(lambda: "Deploy succeeded" in log.inner_text(), 30, "the deploy log to show the deploy succeeded", seen=log.inner_text)
    assert page.locator(".gb-banner", has_text="Live").is_visible()
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


def test_new_commit_deploys_and_rolls_back(page, gitberth, github, engine, apps, uniq, standins):
    repo = github.create_repo(uniq("roll"), files={"marker.txt": "roll v1"})
    first = repo.branch_sha("main")
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    url = gitberth.lan_url(app.id)
    until(lambda: marker_of(url) == "roll v1", 60, "the first version to answer", seen=lambda: get(url))

    # A new commit on the branch deploys by itself, on the watcher's next look.
    second = repo.commit({"marker.txt": "roll v2"}, message="Second version")["sha"]
    two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=300)
    assert two["CommitSha"] == second and two["Trigger"] == "Poll", two
    until(lambda: marker_of(url) == "roll v2", 60, "the second version to answer", seen=lambda: get(url))

    # Activity › Roll back to this, on the first deploy: the container runs the first build again.
    gitberth.open(page, f"/apps/{app.key}/activity")
    button = page.locator(".gb-row", has_text=first[:7]).get_by_role("button", name="Roll back to this").first
    button.wait_for()
    button.click()
    gitberth.confirm(page, "Roll back")
    page.wait_for_url(re.compile(r"/deployments/\d+$"))
    back = gitberth.wait_deploy(app.id, after=two["Id"], timeout=300)
    assert back["Trigger"] == "Rollback" and back["CommitSha"] == first, back
    assert engine.image_of(app.container) == f"{app.image_repository}:{first[:7]}"
    until(lambda: marker_of(url) == "roll v1", 60, "the first version to answer again", seen=lambda: get(url))

    # A manual rollback isn't undone: through two more looks, the watcher doesn't deploy the branch's newer commit again.
    repo.wait_watcher_passes(2, since=standins.seq())
    assert gitberth.db.last_deployment_id(app.id) == back["Id"], gitberth.db.deployments(app.id)[:2]
    assert marker_of(url) == "roll v1"


def test_settings_save(page, gitberth, github, standins, baseline, scenario):
    # Deploys: the interval is saved, and the page shows it again after a reload.
    gitberth.save_watch_interval(page, 11)
    page.reload()
    gitberth.wait_ready(page)
    assert page.locator("#watch-interval").input_value() == "11"
    assert gitberth.db.setting("watch.intervalSeconds") == "11"
    gitberth.save_watch_interval(page, baseline["watch_seconds"])

    # GitHub access: a new token is checked with GitHub's /user before it's saved, and the page names its owner.
    token = github.token(login="e2e-smoke")
    said = gitberth.save_github_token(page, token)
    assert "e2e-smoke" in said, f"the snackbar said: {said}"
    checks = [c for c in standins.calls(scenario.seq, host="api.github.com", path="^/user$") if c["token"] == token]
    assert checks, "Gitberth saved the token without asking GitHub who it belongs to"
    page.reload()
    gitberth.wait_ready(page)
    github_section = page.locator("#github").inner_text()
    assert "Token saved" in github_section and "e2e-smoke" in github_section, github_section

    # A token GitHub refuses isn't saved.
    refused = "not-a-token-" + token[-6:]
    page.locator("#gh-token").fill(refused)
    page.locator("#github").get_by_role("button", name=re.compile("^Replace token$")).click()
    said = gitberth.snackbar(page, "invalid|expired|rejected")
    assert "e2e-smoke" in page.locator("#github").inner_text(), "a refused token replaced the saved one"

    # The session's own token goes back, for the scenarios after this one.
    gitberth.save_github_token(page, baseline["token"])
    assert get(gitberth.url("/login"))[0] == 200
