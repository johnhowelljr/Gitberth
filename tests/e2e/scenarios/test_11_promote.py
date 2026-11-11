"""Scenario 11: Promote.

An app made from the test app, with a variable for production and one for previews (E2E_TIER), and previews of pull
requests on. A pull request from the branch "feature" gets its preview; its commit's Dockerfile declares ARG E2E_FAIL
and fails its build when that is "yes" (nothing sets it at first).
- From the Previews tab's menu, Promote asks first (its dialog says what happens), then production builds the preview's
  commit with production's variables: VERCEL_ENV=production, VERCEL_GIT_COMMIT_REF=feature, the preview's commit, and
  production's E2E_TIER. The deploy's trigger is Promote ("Promoted" on its page), and the parent's Activity says so.
- Production stays on it through several watcher passes; a new commit on main moves it on.
- Promoted again, a manual Deploy builds main's commit again and lets production follow main.
- After a rollback, a Promote that fails (production's E2E_FAIL=yes fails its build) lets go of production without the
  watcher deploying main's commit over the rollback: production stays on the rolled-back version.
- A preview whose first build failed can't be promoted: the menu's Promote is disabled.
"""
import re
from types import SimpleNamespace

import pytest

from e2e.wait import stays
from test_02_pull_request_previews import TESTAPP_DOCKERFILE, activity_texts, new_app, open_pull, own_apps, preview_row, unique, wait_deployed, wait_finished
from test_04_preview_limit import answer_dialog, menu_item, read_dialog
from test_09_scoped_variables import PREVIEW, PRODUCTION, PRODUCTION_TAKES, add_variable, open_environment, wait_bar
from test_10_build_time_secrets import apply_and_follow
from test_19_declared_volumes import wait_final

FAILS_WHEN = ("E2E_FAIL", "yes")


def failing_dockerfile():
    """The test app's Dockerfile, plus a step that fails when the build is given E2E_FAIL=yes."""
    with open(TESTAPP_DOCKERFILE, encoding="utf-8") as f:
        dockerfile = f.read()
    step = 'ARG E2E_FAIL\nRUN test "$E2E_FAIL" != "yes"\n'
    assert "EXPOSE 8080" in dockerfile, dockerfile
    return dockerfile.replace("EXPOSE 8080", step + "EXPOSE 8080")


def served(gitberth, app_id):
    """What the app serves now: its /env, and its marker (its repo's marker.txt, as / says it)."""
    url = gitberth.lan_url(app_id)
    return gitberth.wait_answers(url, "/env"), gitberth.wait_answers(url, "/").get("marker")


def promote_from_menu(page, gitberth, app, label):
    """The Previews tab's menu › Promote to production…, its dialog read and answered Promote; the page goes to the
    production deploy it started. Returns (the dialog, that deploy's id)."""
    gitberth.open(page, f"/apps/{app.key}/previews")
    row = preview_row(page, label)
    row.wait_for()
    row.locator(f"button[aria-label='More for {label}']").click()
    entry = menu_item(page, "Promote to production")
    entry.wait_for()
    entry.click()
    dialog = read_dialog(page)
    answer_dialog(page, "Promote")
    page.wait_for_url(re.compile(rf"/apps/{app.id}/deployments/\d+$"), timeout=60_000)
    return dialog, int(gitberth.path_of(page).rsplit("/", 1)[-1])


def production(gitberth, app):
    return gitberth.db.app(app.slug)


def deploys_after(gitberth, app, after):
    return [d for d in gitberth.db.deployments(app.id) if d["Id"] > after]


# ---- the world, step by step ----

@pytest.fixture(scope="module")
def world(gitberth, github, standins):
    """The app, its two E2E_TIER values applied, previews of pull requests on, and the pull request's preview built."""
    with own_apps(gitberth, standins, "promote") as (page, made):
        repo, app, first = new_app(page, gitberth, github, made.track, unique("prm"))
        open_environment(page, gitberth, app)
        add_variable(page, "E2E_TIER", "tier-production", PRODUCTION)
        add_variable(page, "E2E_TIER", "tier-preview", PREVIEW)
        wait_bar(page, lambda line: line.startswith(PRODUCTION_TAKES), "the apply bar to say production takes the variables")
        apply_and_follow(page, gitberth)
        said = gitberth.set_previews(page, app, mode="Pull requests")
        assert said.startswith("Saved"), said
        n, sha = open_pull(repo, "feature", "Promote me", marker="feature", files={"Dockerfile": failing_dockerfile()})
        preview = gitberth.wait_preview(app.id, pr=n, timeout=120)
        wait_deployed(gitberth, preview["Id"], sha)
        yield SimpleNamespace(page=page, repo=repo, app=app, first=first, n=n, sha=sha, preview=preview, main1=repo.branch_sha("main"))


@pytest.fixture(scope="module")
def promoted(world, gitberth):
    w = world
    w.before_promote = gitberth.db.last_deployment_id(w.app.id)
    w.dialog, deploy_id = promote_from_menu(w.page, gitberth, w.app, f"PR #{w.n}")
    w.promote = wait_final(gitberth, deploy_id)
    w.promote_page_shows_trigger = w.page.get_by_text("Promoted", exact=True).count() > 0
    w.env, w.page_text = served(gitberth, w.app.id)
    w.row_after_promote = production(gitberth, w.app)
    return w


@pytest.fixture(scope="module")
def pinned(promoted, gitberth, standins):
    """Three watcher passes with main where it was."""
    w = promoted
    mark = standins.seq()
    w.repo.wait_watcher_passes(3, since=mark, timeout=120)
    w.after_passes = deploys_after(gitberth, w.app, w.promote["Id"])
    w.row_after_passes = production(gitberth, w.app)
    return w


@pytest.fixture(scope="module")
def moved_on(pinned, gitberth):
    """A new commit on main: the watcher deploys it."""
    w = pinned
    w.main2 = w.repo.commit({"marker.txt": "main-2"}, message="main moves on")["sha"]
    w.main2_deploy = wait_deployed(gitberth, w.app.id, w.main2, after=w.promote["Id"])
    w.env2, w.page_text2 = served(gitberth, w.app.id)
    w.row_after_move = production(gitberth, w.app)
    return w


@pytest.fixture(scope="module")
def deployed_by_hand(moved_on, gitberth, standins):
    """Promoted again, then Deploy on the app's page."""
    w = moved_on
    _, again = promote_from_menu(w.page, gitberth, w.app, f"PR #{w.n}")
    w.promote2 = wait_final(gitberth, again)
    w.pinned_again = production(gitberth, w.app)["PromotedOverCommit"]
    manual = gitberth.deploy(w.page, w.app)
    w.manual = wait_final(gitberth, manual)
    w.row_after_manual = production(gitberth, w.app)
    mark = standins.seq()
    w.repo.wait_watcher_passes(2, since=mark, timeout=90)
    w.after_manual = deploys_after(gitberth, w.app, w.manual["Id"])
    return w


@pytest.fixture(scope="module")
def failed_after_rollback(deployed_by_hand, gitberth, standins):
    """Rolled back (Activity › Roll back to this, on the second promote's deploy), production's E2E_FAIL=yes added and
    applied, then Promote: its build fails. Then three watcher passes."""
    w = deployed_by_hand
    page = w.page
    gitberth.open(page, f"/apps/{w.app.key}/activity")
    button = page.get_by_role("button", name="Roll back to this").first
    button.wait_for(timeout=30_000)
    button.click()
    w.rollback_dialog = read_dialog(page)
    answer_dialog(page, "Roll back")
    page.wait_for_url(re.compile(rf"/apps/{w.app.id}/deployments/\d+$"), timeout=60_000)
    w.rollback = wait_final(gitberth, int(gitberth.path_of(page).rsplit("/", 1)[-1]))
    w.row_after_rollback = production(gitberth, w.app)
    _, w.rolled_back_text = served(gitberth, w.app.id)

    open_environment(page, gitberth, w.app)
    add_variable(page, FAILS_WHEN[0], FAILS_WHEN[1], PRODUCTION, build=True)
    wait_bar(page, lambda line: line.startswith(PRODUCTION_TAKES), "the apply bar to say production takes the variable")
    apply_and_follow(page, gitberth)
    w.before_failing = gitberth.db.last_deployment_id(w.app.id)

    _, failing = promote_from_menu(page, gitberth, w.app, f"PR #{w.n}")
    w.failed = wait_final(gitberth, failing)
    w.row_after_failed = production(gitberth, w.app)
    mark = standins.seq()
    w.repo.wait_watcher_passes(3, since=mark, timeout=120)
    w.after_failed = deploys_after(gitberth, w.app, w.failed["Id"])
    w.row_after_failed_passes = production(gitberth, w.app)
    _, w.text_after_failed = served(gitberth, w.app.id)
    return w


# ---- the checks ----

class TestPromote:
    def test_promote_asks_first_and_says_what_happens(self, promoted):
        w = promoted
        title, body, buttons = w.dialog
        assert (title, buttons) == (f"Promote PR #{w.n} to production?", ["Cancel", "Promote"]), w.dialog
        assert "builds" in body and "with production's variables" in body and "until main gets a new one, or until you deploy it by hand" in body, body
        assert "The preview keeps running." in body, body

    def test_production_builds_the_previews_commit_with_productions_variables(self, promoted, gitberth):
        w = promoted
        d = w.promote
        assert d["Status"] == "Succeeded", f"{d['Status']}: {d['Error']}\n{gitberth.deploy_log(d['Id'], tail=40)}"
        assert (d["Trigger"], d["CommitSha"]) == ("Promote", w.sha), d
        env = w.env
        assert env.get("VERCEL_ENV") == "production", env
        assert env.get("VERCEL_GIT_COMMIT_REF") == "feature", env
        assert env.get("VERCEL_GIT_COMMIT_SHA") == w.sha, env
        assert env.get("E2E_TIER") == "tier-production", env
        assert w.page_text == "feature", w.page_text
        assert w.row_after_promote["CurrentCommit"] == w.sha, w.row_after_promote
        assert w.row_after_promote["PromotedOverCommit"] == w.main1, w.row_after_promote

    def test_the_deploy_and_the_activity_say_it_was_promoted(self, promoted, gitberth):
        w = promoted
        assert w.promote_page_shows_trigger, "the deploy's page doesn't name its trigger 'Promoted'"
        wanted = f"Preview PR #{w.n} promoted: production builds {w.sha[:7]} and stays on it until the next commit on main."
        assert wanted in activity_texts(gitberth, w.app), activity_texts(gitberth, w.app)[-5:]

    def test_the_preview_keeps_running(self, promoted, engine):
        assert engine.running(f"gb-{promoted.preview['Slug']}")

    def test_production_stays_on_it_while_main_stays_where_it_was(self, pinned):
        w = pinned
        assert w.after_passes == [], [(d["Id"], d["Trigger"], d["CommitSha"]) for d in w.after_passes]
        assert (w.row_after_passes["CurrentCommit"], w.row_after_passes["PromotedOverCommit"]) == (w.sha, w.main1), w.row_after_passes

    def test_a_new_commit_on_main_moves_it_on(self, moved_on):
        w = moved_on
        assert w.main2_deploy["Trigger"] in ("Poll", "Webhook"), w.main2_deploy
        assert w.page_text2 == "main-2", w.page_text2
        assert w.env2.get("VERCEL_GIT_COMMIT_REF") == "main", w.env2
        assert w.row_after_move["PromotedOverCommit"] is None, w.row_after_move

    def test_a_manual_deploy_builds_main_again_and_lets_production_follow_it(self, deployed_by_hand):
        w = deployed_by_hand
        assert w.promote2["Status"] == "Succeeded", w.promote2
        assert w.pinned_again == w.main2
        assert (w.manual["Status"], w.manual["Trigger"], w.manual["CommitSha"]) == ("Succeeded", "Manual", w.main2), w.manual
        assert w.row_after_manual["PromotedOverCommit"] is None, w.row_after_manual
        assert w.after_manual == [], [(d["Id"], d["Trigger"], d["CommitSha"]) for d in w.after_manual]


class TestAFailedPromoteAfterARollback:
    def test_the_rollback_went_back_to_the_promoted_build(self, failed_after_rollback):
        w = failed_after_rollback
        title, _, buttons = w.rollback_dialog
        assert title.startswith("Roll back to #") and buttons == ["Cancel", "Roll back"], w.rollback_dialog
        assert (w.rollback["Status"], w.rollback["Trigger"]) == ("Succeeded", "Rollback"), w.rollback
        assert w.rolled_back_text == "feature", w.rolled_back_text

    def test_the_promote_fails_and_lets_go_of_production(self, failed_after_rollback, gitberth):
        w = failed_after_rollback
        assert (w.failed["Status"], w.failed["Trigger"]) == ("Failed", "Promote"), w.failed
        assert w.row_after_failed["PromotedOverCommit"] is None, w.row_after_failed

    def test_the_watcher_doesnt_deploy_main_over_the_rollback(self, failed_after_rollback):
        w = failed_after_rollback
        assert w.after_failed == [], [(d["Id"], d["Trigger"], d["CommitSha"], d["Status"]) for d in w.after_failed]
        assert w.row_after_failed_passes["CurrentCommit"] == w.row_after_rollback["CurrentCommit"], (w.row_after_rollback, w.row_after_failed_passes)
        assert w.text_after_failed == "feature", w.text_after_failed


def test_a_preview_whose_first_build_failed_cant_be_promoted(page, gitberth, github, apps, uniq):
    repo, app, _ = new_app(page, gitberth, github, apps.track, uniq("prf"))
    said = gitberth.set_previews(page, app, mode="Pull requests")
    assert said.startswith("Saved"), said
    with open(TESTAPP_DOCKERFILE, encoding="utf-8") as f:
        broken = f.read().replace("EXPOSE 8080", "RUN false\nEXPOSE 8080")
    n, sha = open_pull(repo, "broken", "Never builds", files={"Dockerfile": broken})
    preview = gitberth.wait_preview(app.id, pr=n, timeout=120)
    first = wait_finished(gitberth, preview["Id"], sha)
    assert first["Status"] == "Failed", first

    gitberth.open(page, f"/apps/{app.key}/previews")
    row = preview_row(page, f"PR #{n}")
    row.wait_for()
    row.locator(f"button[aria-label='More for PR #{n}']").click()
    entry = menu_item(page, "Promote to production")
    entry.wait_for()
    assert entry.get_attribute("aria-disabled") == "true" or "mud-disabled" in (entry.get_attribute("class") or ""), entry.get_attribute("class")
    stays(lambda: not [d for d in gitberth.db.deployments(app.id) if d["Trigger"] == "Promote"], 3, "no promote")
