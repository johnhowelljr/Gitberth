"""Scenario 13: pull request comments.

- A pull request's preview keeps one comment on the pull request (Gitberth's marker line first), made when its first
  build starts and edited on every deploy after: Building (no address yet), then Ready with the preview's address,
  commit and time and a link to the deploy's log; Building and Ready again for the next commit; then Failed for a
  commit whose build fails, still with the address of the version that runs on, and nothing of why, nor the secret the
  build printed. Its commit statuses go under "Gitberth preview: <parent>".
- Someone deletes the comment on GitHub: the next deploy makes a new one, and keeps that one up to date.
- Turning comments off in Settings › GitHub stops them (the comment stays as it was, while statuses still go); back
  on, the next deploy edits the same comment again.

A branch's preview, which has no pull request, gets a status and no comment: see test_12's
test_a_branch_preview_reports_under_its_parents_preview_context.
"""
import secrets
from datetime import datetime, timedelta, timezone

from e2e.github_reports import (COMMENTS, comment_writes, context, leaks, parse_comment, set_github_switch, set_variables,
                                wait_comment, wait_deployment, wait_status, written)
from e2e.wait import stays, until

# A build that prints the app's secret on its way to failing. The repo's own Dockerfile is used as it is, and declares
# the build-time variable, so docker build hands it over.
BUILD_FAILS = """FROM python:3.12-alpine
WORKDIR /app
COPY . .
ARG E2E_API_TOKEN
RUN echo calling the release API with $E2E_API_TOKEN && echo release API: deploy key rejected && exit 7
EXPOSE 8080
CMD ["python3", "-u", "/app/server.py"]
"""


def bare(url):
    return url.split("://", 1)[1]


def recent(when):
    """A comment's time (to the minute) is now's."""
    now = datetime.now(timezone.utc)
    return when is not None and now - timedelta(minutes=3) <= when <= now + timedelta(minutes=1)


def open_pull_preview(page, gitberth, github, standins, apps, uniq, prefix, variables=None):
    """A repo and its app (named apart from the repo, given the variables), previews of pull requests on, then a pull
    request from a branch with one commit. Returns (repo, app, its number, the preview's row, the commit, a marker
    taken just before the pull request opened)."""
    repo = github.create_repo(uniq(prefix), files={"marker.txt": "production"})
    app = apps.track(gitberth.create_git_app(page, repo.url, name=f"Shop {repo.name}"))
    gitberth.wait_deploy(app.id, timeout=420)
    if variables:
        wait_deployment(gitberth, set_variables(gitberth, page, app, variables))
    before = standins.seq()
    assert "Saved" in gitberth.set_previews(page, app, mode="Pull requests")
    repo.wait_pulls_listed(since=before, timeout=90)
    repo.create_branch("feature")
    sha = repo.commit({"marker.txt": "feature one"}, branch="feature", message="Start the feature")["sha"]
    mark = standins.seq()
    number = repo.open_pull("feature", title="Try the feature")["number"]
    preview = gitberth.wait_preview(app.id, pr=number, timeout=120)
    assert preview["Kind"] == "PullRequest" and preview["Slug"] == f"{app.slug}-pr-{number}", preview
    return repo, app, number, preview, sha, mark


def test_a_pull_requests_preview_keeps_one_comment_up_to_date(page, gitberth, github, standins, apps, uniq, scenario):
    secret = "e2e-made-up-" + secrets.token_hex(12)
    repo, app, n, preview, first, mark = open_pull_preview(page, gitberth, github, standins, apps, uniq, "pc",
                                                           variables=[{"name": "E2E_API_TOKEN", "value": secret, "build_time": True}])
    ui = gitberth.db.setting("ui.address")

    # Building: one comment, made once the preview's commit is known and before its build, with no address yet.
    made = until(lambda: comment_writes(repo, mark), 240, f"Gitberth's comment on PR #{n}",
                 seen=lambda: repo.api_calls(mark))[0]
    assert (made["method"], made["status"]) == ("POST", 201) and made["path"].endswith(f"/issues/{n}/comments"), made
    building = written(made)
    assert building["marker"] and (building["parent"], building["state"]) == (app.name, "Building"), building
    assert (building["commit"], building["address"], building["address_text"]) == (first[:7], None, "No address yet"), building
    one = gitberth.wait_deploy(preview["Id"], timeout=420)
    assert one["CommitSha"] == first, one
    assert building["log"] == f"{ui}apps/{preview['Id']}/deployments/{one['Id']}", building
    assert recent(building["updated"]), building

    # Ready: the same comment, edited, with the preview's address (which answers), its commit, the time and the log.
    url = gitberth.lan_url(preview["Id"])
    comments = wait_comment(repo, n, "Ready", first[:7])
    assert len(comments) == 1, comments
    comment_id = comments[0]["id"]
    ready = parse_comment(comments[0]["body"])
    assert (ready["parent"], ready["address"], ready["address_text"]) == (app.name, url + "/", bare(url)), ready
    assert ready["log"] == building["log"] and recent(ready["updated"]) and not ready["failed_note"], ready
    assert gitberth.wait_answers(url, "/")["marker"] == "feature one"
    # Gitberth keeps the comment's ID with the preview; the comment is the token's user's.
    assert gitberth.preview(app.id, pr=n)["CommentId"] == comment_id
    assert comments[0]["user"]["login"] == "e2e-bot", comments[0]["user"]
    # Its commit statuses: under the parent's preview context.
    ctx = context(app.name, preview=True)
    assert [(s["context"], s["state"]) for s in repo.statuses(sha=first)] == [(ctx, "pending"), (ctx, "success")]

    # A new commit on the pull request: the same comment says Building (with the address of the version that still
    # runs), then Ready for the new commit.
    since = standins.seq()
    second = repo.commit({"marker.txt": "feature two"}, branch="feature", message="More of the feature")["sha"]
    two = gitberth.wait_deploy(preview["Id"], after=one["Id"], timeout=300)
    assert two["CommitSha"] == second, two
    comments = wait_comment(repo, n, "Ready", second[:7])
    assert [c["id"] for c in comments] == [comment_id], comments
    writes = comment_writes(repo, since)
    assert [(w["method"], written(w)["state"]) for w in writes] == [("PATCH", "Building"), ("PATCH", "Ready")], writes
    assert all(w["path"].endswith(f"/issues/comments/{comment_id}") for w in writes), writes
    assert (written(writes[0])["commit"], written(writes[0])["address"]) == (second[:7], url + "/"), written(writes[0])
    assert parse_comment(comments[0]["body"])["log"] == f"{ui}apps/{preview['Id']}/deployments/{two['Id']}"

    # A commit whose build fails, having printed the secret: Failed, for that commit, with the address of the version
    # that still runs and the link to the log, and nothing of why.
    since = standins.seq()
    third = repo.commit({"Dockerfile": BUILD_FAILS}, branch="feature", message="Call the release API while building")["sha"]
    three = gitberth.wait_deploy(preview["Id"], after=two["Id"], statuses=("Failed",), timeout=300)
    assert three["CommitSha"] == third, three
    comments = wait_comment(repo, n, "Failed", third[:7])
    assert [c["id"] for c in comments] == [comment_id], comments
    failed = parse_comment(comments[0]["body"])
    assert failed["failed_note"] and (failed["address"], failed["address_text"]) == (url + "/", bare(url)), failed
    assert failed["log"] == f"{ui}apps/{preview['Id']}/deployments/{three['Id']}" and recent(failed["updated"]), failed
    writes = comment_writes(repo, since)
    assert [(w["method"], written(w)["state"]) for w in writes] == [("PATCH", "Building"), ("PATCH", "Failed")], writes
    failure = wait_status(repo, third, ctx, "failure", timeout=60)
    assert (failure["description"], failure["target_url"]) == ("Preview failed: see the deploy log on Gitberth", failed["log"]), failure
    # The version before it still answers, and the build did print the secret (its log has the line, masked).
    assert gitberth.wait_answers(url, "/")["marker"] == "feature two"
    assert "calling the release API with" in gitberth.deploy_log(three["Id"]), gitberth.deploy_log(three["Id"], tail=40)

    # The comment's whole life: made once, edited on every deploy, never a second one.
    history = [(w["method"], written(w)["state"]) for w in comment_writes(repo, mark)]
    assert history == [("POST", "Building"), ("PATCH", "Ready"), ("PATCH", "Building"), ("PATCH", "Ready"),
                       ("PATCH", "Building"), ("PATCH", "Failed")], history
    assert [(c["id"], c["edits"]) for c in repo.comments(n)] == [(comment_id, 5)], repo.comments(n)

    # Nothing GitHub received carries the secret, the deploy's error, or the build's output.
    found = leaks(standins, scenario.seq, [secret, three["Error"], "deploy key rejected", "exit code"])
    assert not found, f"Gitberth sent GitHub: {found}"


def test_a_deleted_comment_is_made_again(page, gitberth, github, standins, apps, uniq):
    repo, app, n, preview, first, _ = open_pull_preview(page, gitberth, github, standins, apps, uniq, "pd")
    one = gitberth.wait_deploy(preview["Id"], timeout=420)
    old = wait_comment(repo, n, "Ready", first[:7])[0]["id"]

    # Someone deletes it on GitHub.
    repo.delete_comment(old)
    assert repo.comments(n) == []
    since = standins.seq()
    second = repo.commit({"marker.txt": "feature two"}, branch="feature", message="More of the feature")["sha"]
    two = gitberth.wait_deploy(preview["Id"], after=one["Id"], timeout=300)
    assert two["CommitSha"] == second, two

    # The next deploy makes a new one, and keeps that one up to date: one comment again.
    comments = wait_comment(repo, n, "Ready", second[:7])
    assert len(comments) == 1 and comments[0]["id"] != old, comments
    new = comments[0]["id"]
    # It tried to edit the old one, which GitHub no longer has (404), then made a new one (201) and edited that (200).
    writes = comment_writes(repo, since)
    assert [(w["method"], w["status"], written(w)["state"]) for w in writes] == [
        ("PATCH", 404, "Building"), ("POST", 201, "Building"), ("PATCH", 200, "Ready")], writes
    assert writes[0]["path"].endswith(f"/issues/comments/{old}") and writes[1]["path"].endswith(f"/issues/{n}/comments"), writes
    assert writes[2]["path"].endswith(f"/issues/comments/{new}"), writes
    assert gitberth.preview(app.id, pr=n)["CommentId"] == new
    assert parse_comment(comments[0]["body"])["log"].endswith(f"/apps/{preview['Id']}/deployments/{two['Id']}")


def test_turning_comments_off_stops_them_and_on_resumes(page, new_page, gitberth, github, standins, apps, uniq):
    repo, app, n, preview, first, _ = open_pull_preview(page, gitberth, github, standins, apps, uniq, "pw")
    one = gitberth.wait_deploy(preview["Id"], timeout=420)
    comments = wait_comment(repo, n, "Ready", first[:7])
    comment_id, edits = comments[0]["id"], comments[0]["edits"]
    ctx = context(app.name, preview=True)
    try:
        # Off: Settings › GitHub says so, keeps it, and shows it after a reload.
        said = set_github_switch(gitberth, page, COMMENTS, on=False)
        assert said and said.startswith("Previews don't comment on pull requests any more."), said
        assert gitberth.db.setting("github.comments") == "false"
        page.reload()
        gitberth.wait_ready(page)
        assert page.locator("#github-integration").get_by_role("switch", name=COMMENTS, exact=True).get_attribute("aria-checked") == "false"

        # A deploy while they're off: its statuses still go (that's the other switch), but the comment isn't touched.
        since = standins.seq()
        second = repo.commit({"marker.txt": "feature two"}, branch="feature", message="Deployed while comments are off")["sha"]
        two = gitberth.wait_deploy(preview["Id"], after=one["Id"], timeout=300)
        assert two["CommitSha"] == second, two
        wait_status(repo, second, ctx, "success", timeout=60)
        stays(lambda: comment_writes(repo, since) == [], 5, "no comment written while comments are off")
        comments = repo.comments(n)
        assert [(c["id"], c["edits"]) for c in comments] == [(comment_id, edits)], comments
        assert parse_comment(comments[0]["body"])["commit"] == first[:7]

        # On again: the next deploy edits the same comment.
        said = set_github_switch(gitberth, page, COMMENTS, on=True)
        assert said and said.startswith("Previews comment on their pull requests again."), said
        assert gitberth.db.setting("github.comments") is None
        third = repo.commit({"marker.txt": "feature three"}, branch="feature", message="Deployed with comments on again")["sha"]
        gitberth.wait_deploy(preview["Id"], after=two["Id"], timeout=300)
        comments = wait_comment(repo, n, "Ready", third[:7])
        assert [c["id"] for c in comments] == [comment_id], comments
    finally:
        set_github_switch(gitberth, new_page(), COMMENTS, on=True)
