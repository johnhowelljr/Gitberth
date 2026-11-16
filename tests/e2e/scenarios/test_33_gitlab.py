"""Scenario 33: GitLab, against the GitLab stand-in at https://gitlab.e2e.test (a self-managed GitLab's address, which
Settings › GitLab is given).

- Settings › GitLab checks a token before it saves it: one GitLab refuses isn't saved; one without the api scope is
  saved with a banner naming the scope and what it's for; one with api shows none. The token is never shown again: not
  on the page read again, nor in Gitberth's database, log or audit log, and GitLab only ever gets it as PRIVATE-TOKEN.
- New app from a private project in a nested group (e2e/<group>/apps/<project>): without a token it says why and makes
  nothing; with one it's checked, analyzed and deployed, git sending the token every time. The app is stored as Owner
  gitlab:<namespace> and Repo <project>, its page links to the project, and it's told VERCEL_GIT_PROVIDER=gitlab.
- CI/CD variables by environment scope: production gets * and production (production's value of a name wins over *'s),
  a preview * and preview/*. A masked one is secret in the log, a protected one reaches production only, and a file one
  or one of any other scope is never passed: the deploy log says which and why. The Environment tab lists them with
  their scopes and flags, and saves and deletes one for previews in GitLab.
- A push deploys through the watcher; GitLab gets the commit status, running and then success with the app's address,
  on main. With build filters, a docs-only push is skipped, from GitLab's compare of the running commit and the new
  one, and its commit gets success, never running.
- A merge request's preview, "MR !1": built from refs/merge-requests/1/head with the variables for previews; statuses
  running then success on its branch; exactly one note on the merge request, made once (POST) and then edited (PUTs);
  a new commit redeploys it and edits the same note; merging removes it, and production deploys the merge.
- A fork's merge request gets no preview while forks are off (nothing of it fetched, the fork never asked); once
  they're on it does, built from the target project's refs/merge-requests/<iid>/head, its statuses on no branch.
- Instant deploys: with a host name, the keeper adds Gitberth's project hook (push and merge request events, the secret
  token Settings › GitLab shows, SSL verification on), and Settings lists it. A push's Push Hook comes through
  Cloudflare and the tunnel's /hooks/gitlab route and deploys at once ("Push on GitLab"); a wrong token, or none, is a
  bare 401, and nothing deploys.
- An agent's create_app over the HTTP API takes a GitLab project's URL; get_app says the app is GitLab's, and
  get_settings tells GitLab's address, user and scopes, never the token.

The scenarios share one GitLab app (the module's `world`): its first deploy is New app's, and each scenario adds its own
step to it. Left out, and unit-tested instead (GitLabApiTests, GitLabIntegrationTests, GitLabPipelineTests,
GitLabPreviewTests): GitLab's 429 and Retry-After, a GitLab without /personal_access_tokens/self (before 15.5, or a
project token), a merge request closed rather than merged, a Merge Request Hook's poke, a project GitLab refuses a hook,
a status or a note on, the two switches turned off, a note someone deleted, the endpoint's 405, size limit and
throttling, a host name moved (the hook PUT), and gitlab.com as the address.
"""
import json
import re
import urllib.parse
from types import SimpleNamespace

import pytest

from e2e import audit
from e2e.gitberth import App
from e2e.github_reports import COMMENT_MARKER, parse_comment
from e2e.gitlab import GitLab
from e2e.net import call
from e2e.wait import stays, until
# Shared rather than copied: scenario 2's previews, 12's Settings › Domains, 14's instant deploys, 22's build filters and
# 29's API tokens.
from test_02_pull_request_previews import PULLS_EVERY, activity_texts, fetched, leftovers, own_apps, preview_row, unique, wait_deployed
from test_12_commit_statuses import domains_turned_on
from test_14_instant_deploys import OWN_SERVICE, PATH_RULE, TURNED_ON, instant_deploys, kv, notes, webhook_rules
from test_22_build_filters import set_filters
from test_29_mcp import make_token

SECTION = "#gitlab"
HOST = GitLab.HOST
DOTS = "•" * 16
HIDDEN = "•" * 10
API_FOR = "commit statuses, merge request comments, Gitberth's project hooks (instant deploys) and saving CI/CD variables"
NEEDS_TOKEN = "{} needs a GitLab token. Add one in Settings › GitLab (read_api and read_repository scopes)."
FILE_NOTE = "Not used from GitLab: GL_FILE, a file variable. Gitberth passes plain ones (Variable type) only."
PROTECTED_NOTE = "Not given to previews: GL_PROTECTED, protected in GitLab, which only production gets."

# The project's CI/CD variables, as someone set them in GitLab (environment scope * unless said).
MASKED = "masked-value-1234"   # GitLab masks a value of 8 or more of its characters
VARIABLES = [
    {"key": "GL_EVERY", "value": "every deploy"},
    {"key": "GL_WHERE", "value": "anywhere"},
    {"key": "GL_WHERE", "value": "production", "environment_scope": "production"},
    {"key": "GL_WHERE", "value": "a preview", "environment_scope": "preview/*"},
    {"key": "GL_PROD", "value": "production only", "environment_scope": "production"},
    {"key": "GL_PREVIEW", "value": "previews only", "environment_scope": "preview/*"},
    {"key": "GL_MASKED", "value": MASKED, "masked": True},
    {"key": "GL_PROTECTED", "value": "protected value", "protected": True},
    {"key": "GL_FILE", "value": "a file's contents", "variable_type": "file"},
    {"key": "GL_STAGING", "value": "staging only", "environment_scope": "staging"},
]
PRODUCTION_GETS = {"GL_EVERY": "every deploy", "GL_WHERE": "production", "GL_PROD": "production only", "GL_MASKED": MASKED,
                   "GL_PROTECTED": "protected value"}
PREVIEW_GETS = {"GL_EVERY": "every deploy", "GL_WHERE": "a preview", "GL_PREVIEW": "previews only", "GL_MASKED": MASKED}
NEVER = ("GL_FILE", "GL_STAGING")
# The Environment tab's list of them: name, its tags (scope, protected, file, not used) and the value shown.
EDITOR = sorted([
    ["GL_EVERY", ["*"], "every deploy"],
    ["GL_FILE", ["*", "file"], "a file's contents"],
    ["GL_MASKED", ["*"], HIDDEN],
    ["GL_PREVIEW", ["preview/*"], "previews only"],
    ["GL_PROD", ["production"], "production only"],
    ["GL_PROTECTED", ["*", "protected"], "protected value"],
    ["GL_STAGING", ["staging", "not used"], "staging only"],
    ["GL_WHERE", ["*"], "anywhere"],
    ["GL_WHERE", ["production"], "production"],
    ["GL_WHERE", ["preview/*"], "a preview"],
])
EDITOR_ROWS = """rows => rows.map(r => {
    const name = r.querySelector('.gb-repo-var-name');
    const own = [...name.childNodes].filter(n => n.nodeType === Node.TEXT_NODE).map(n => n.textContent).join('').trim();
    return [own, [...name.querySelectorAll('.gb-tag')].map(t => t.textContent.trim()), r.querySelector('.gb-repo-var-value').textContent.trim()];
})"""


def flat(text):
    return " ".join((text or "").split())


def bare(url):
    return url.split("://", 1)[1]


# ---- Settings › GitLab ----

def address(gitberth):
    """Gitberth's GitLab as saved (gitlab.com when none is)."""
    return (gitberth.db.setting("gitlab.url") or "https://gitlab.com").rstrip("/")


def open_gitlab(gitberth, page):
    gitberth.open(page, "/settings")
    section = page.locator(SECTION)
    section.locator("#gl-url").wait_for()
    return section


def save_gitlab(gitberth, page, address=None, token=None):
    """Settings › GitLab: the address and a new token typed in, then its button (Save, Save token or Replace token).
    Returns what the snackbar said."""
    section = open_gitlab(gitberth, page)
    if address is not None:
        section.locator("#gl-url").fill(address)
    if token is not None:
        section.locator("#gl-token").fill(token)
    button = section.get_by_role("button", name=re.compile(r"^(Save|Save token|Replace token)$"))
    until(button.is_enabled, 10, "Settings › GitLab's save button to take what was typed")
    button.click()
    return flat(gitberth.snackbar(page, "."))


def remove_gitlab_token(gitberth, page):
    """Settings › GitLab › Remove token, its confirm answered; nothing when no token is saved."""
    section = open_gitlab(gitberth, page)
    remove = section.get_by_role("button", name="Remove token", exact=True)
    if remove.count() == 0:
        return None
    remove.click()
    gitberth.confirm(page, "Remove")
    return flat(gitberth.snackbar(page, "The GitLab token is removed"))


def ensure_token(gitberth, page, token):
    """The api token saved, unless it is already."""
    if gitberth.db.setting("gitlab.token") is None or gitberth.db.setting("gitlab.scopes") != "api":
        said = save_gitlab(gitberth, page, token=token)
        assert said == "Token saved.", said


# ---- what GitLab heard ----

def status_writes(project, since, sha):
    """Gitberth's commit status POSTs on the commit after the marker, in order, however they were answered."""
    return project.api_calls(since, path=rf"/statuses/{sha}$", method="POST")


def wait_status_writes(project, since, sha, count, timeout=60):
    return until(lambda: (lambda w: w if len(w) >= count else None)(status_writes(project, since, sha)), timeout,
                 f"{count} commit status write(s) on {sha[:7]}", seen=lambda: [(c["body"], c["status"]) for c in status_writes(project, since, sha)])


def written(calls):
    """Status writes as (state, name, ref, answer)."""
    return [((c.get("body") or {}).get("state"), (c.get("body") or {}).get("name"), (c.get("body") or {}).get("ref"), c["status"]) for c in calls]


def gitberths_notes(project, iid):
    """The merge request's notes that Gitberth wrote (they start with its marker), oldest first."""
    return [n for n in project.notes(mr=iid, system=False) if n["body"].startswith(COMMENT_MARKER)]


def note_writes(project, since, iid):
    """Gitberth's note writes on the merge request after the marker, in order: POST (a new one) or PUT (an edit)."""
    return [c for c in project.api_calls(since, path=rf"/merge_requests/{iid}/notes(/\d+)?$") if c["method"] in ("POST", "PUT")]


def wait_note(project, iid, state, commit, timeout=60):
    """Waits until Gitberth's one note on the merge request says this state for this commit (a short SHA); returns it."""
    def check():
        mine = gitberths_notes(project, iid)
        if len(mine) != 1:
            return None
        said = parse_comment(mine[0]["body"])
        return mine[0] if (said["state"], said["commit"]) == (state, commit) else None

    return until(check, timeout, f"Gitberth's one note on !{iid} to say {state} for {commit}",
                 seen=lambda: [(n["id"], n["edits"], parse_comment(n["body"])) for n in gitberths_notes(project, iid)])


def ensure_previews(gitberth, page, app, forks):
    """The Settings tab's Previews: merge requests, forks on or off; saved only when that's a change."""
    row = gitberth.db.app(app.slug)
    if row["PreviewMode"] == "PullRequests" and bool(row["PreviewForks"]) == forks:
        return
    said = gitberth.set_previews(page, app, mode="Pull requests", forks=forks)
    assert said.startswith("Saved"), said


# ---- the GitLab app the scenarios share ----

@pytest.fixture(scope="module")
def gl_on(gitberth):
    """Settings › GitLab: Gitberth's GitLab is the stand-in's self-managed address. Afterwards the token is removed and
    the address is gitlab.com again, whatever happened."""
    page = gitberth.new_page()
    try:
        if address(gitberth) != GitLab.URL:
            said = save_gitlab(gitberth, page, address=GitLab.URL)
            assert said == f"Saved: Gitberth's GitLab is {HOST}.", said
        yield
    finally:
        try:
            remove_gitlab_token(gitberth, page)
            if address(gitberth) != "https://gitlab.com":
                save_gitlab(gitberth, page, address="https://gitlab.com")
        finally:
            page.context.close()


@pytest.fixture(scope="module")
def world(gitberth, gitlab, gl_token, gl_on, standins):
    """A private project in a nested group, its CI/CD variables set in GitLab; New app refused without a token; then the
    token saved, and the app New app makes of the project, its first deploy over. Removed at the end."""
    with own_apps(gitberth, standins, "gitlab") as (page, made):
        project = gitlab.create_project(f"e2e/{unique('gl')}/apps/{unique('web')}", visibility="private",
                                        files={"marker.txt": "gitlab v1"})
        project.set_variables(*VARIABLES)
        # No token: New app can't read the project, and says why.
        remove_gitlab_token(gitberth, page)
        gitberth.open(page, "/apps/new?url=" + urllib.parse.quote(project.url, safe=""))
        banner = page.locator(".gb-banner", has_text="Gitberth couldn't use it")
        banner.wait_for(timeout=60_000)
        refused = flat(banner.locator(".gb-banner-sub").inner_text())
        titles = [flat(t) for t in page.locator(".gb-banner-title").all_inner_texts()]
        made_none = gitberth.db.one("SELECT COUNT(*) AS n FROM Apps WHERE Repo = ?", project.path.rsplit("/", 1)[1])["n"] == 0
        # With the token: checked, analyzed, created and deployed.
        token_said = save_gitlab(gitberth, page, token=gl_token)
        first_audit = audit.last_id()
        mark = standins.seq()
        sha = project.branch_sha()
        app = made.track(gitberth.create_git_app(page, project.url))
        first = gitberth.wait_deploy(app.id, timeout=420)
        yield SimpleNamespace(project=project, app=app, first=first, sha=sha, mark=mark, first_audit=first_audit, refused=refused,
                              titles=titles, made_none=made_none, token_said=token_said)


# ---- the scenarios ----

def test_settings_gitlab_says_what_a_token_lacks_and_never_shows_it(page, gitberth, gitlab, gl_token, gl_on, standins, scenario):
    first = audit.last_id()
    reader = gitlab.token(username="e2e-bot", scopes=["read_api", "read_repository"])
    revoked = gitlab.token(username="e2e-bot")
    gitlab.change_token(revoked, revoked=True)
    tokens = (gl_token, reader, revoked)
    try:
        # A token GitLab refuses: said, and not saved.
        before = (gitberth.db.setting("gitlab.token"), gitberth.db.setting("gitlab.login"), gitberth.db.setting("gitlab.scopes"))
        said = save_gitlab(gitberth, page, token=revoked)
        assert said == "GitLab says this token is invalid, expired or revoked.", said
        assert (gitberth.db.setting("gitlab.token"), gitberth.db.setting("gitlab.login"), gitberth.db.setting("gitlab.scopes")) == before

        # Without api: saved, with a banner naming the scope it lacks and what for.
        mark = standins.seq()
        said = save_gitlab(gitberth, page, token=reader)
        assert said == "Token saved.", said
        section = page.locator(SECTION)
        lacks = section.locator(".gb-banner", has_text="The token lacks scopes Gitberth uses")
        lacks.wait_for()
        assert flat(lacks.locator(".gb-banner-sub").inner_text()) == f"api: for {API_FOR}", lacks.inner_text()
        assert "scopes read_api, read_repository" in flat(section.inner_text()), section.inner_text()
        assert (gitberth.db.setting("gitlab.login"), gitberth.db.setting("gitlab.scopes")) == ("e2e-bot", "read_api,read_repository")
        # GitLab was asked who the token is and which scopes it has, with the token as PRIVATE-TOKEN.
        asked = gitlab.calls(mark, path=r"^/api/v4/(user|personal_access_tokens/self)$")
        assert [(c["method"], c["path"], c["status"], c["token"]) for c in asked] == \
            [("GET", "/api/v4/user", 200, reader), ("GET", "/api/v4/personal_access_tokens/self", 200, reader)], asked

        # With api: no banner, and who it is, on which GitLab.
        said = save_gitlab(gitberth, page, token=gl_token)
        assert said == "Token saved.", said
        section = page.locator(SECTION)
        section.locator(".gb-pill2", has_text="Token saved").wait_for()
        text = flat(section.inner_text())
        assert f"for e2e-bot on {HOST}" in text and "scopes api" in text, text
        assert section.locator(".gb-banner", has_text="The token lacks scopes").count() == 0, text
        assert gitberth.db.setting("gitlab.scopes") == "api"
    finally:
        # The module's token is the api one again, whatever happened.
        ensure_token(gitberth, page, gl_token)

    # Never shown again: not on the page read again (its field is empty), nor in Gitberth's database, log or audit log;
    # and GitLab never got one in a URL.
    page.reload()
    gitberth.wait_ready(page)
    box = page.locator(SECTION).locator("#gl-token")
    box.wait_for()
    assert (box.input_value(), box.get_attribute("type")) == ("", "password")
    html = page.content()
    stored = gitberth.db.setting("gitlab.token")
    logged = gitberth.logs(since=int(scenario.t0) - 2)
    for t in tokens:
        assert t not in html, "a GitLab token is on the page"
        assert t not in stored, "a GitLab token is stored as it is"
        assert t not in logged, "a GitLab token is in Gitberth's log"
    assert not [c["path"] for c in gitlab.calls(scenario.seq) if "private_token" in (c.get("query") or {})]
    entries = until(lambda: (lambda e: e if len(e) >= 3 else None)(audit.since(first, "action LIKE 'settings.gitlab.%'")), 15,
                    "the three token saves in the audit log", seen=lambda: audit.since(first, "action LIKE 'settings.gitlab.%'"))
    said = [(e["action"], e["outcome"], e["summary"]) for e in entries]
    assert said[0] == ("settings.gitlab.token.saved", "Failed", f"Didn't save the GitLab token: {HOST} refused it."), said
    assert [(a, o) for a, o, _ in said[1:]] == [("settings.gitlab.token.saved", "Succeeded")] * 2, said
    assert all(re.fullmatch(r"(Saved|Replaced) the GitLab token for e2e-bot\.", s) for _, _, s in said[1:]), said
    assert [c for c in json.loads(entries[1]["changes"]) if c["field"] == "Scopes"] == [{"field": "Scopes", "from": None, "to": "read_api, read_repository"}], entries[1]
    assert not [t for e in entries for t in tokens if t in json.dumps(e, default=str)], "a GitLab token is in the audit log"
    assert not gitberth.errors(page), gitberth.errors(page)


def test_new_app_from_a_private_project_in_a_nested_group_needs_the_token(world, page, gitberth, gl_token):
    w = world
    namespace, name = w.project.path.rsplit("/", 1)
    # Without a token New app said why, offered public projects only, and made nothing.
    assert w.refused == NEEDS_TOKEN.format(w.project.path), w.refused
    assert "Public projects only, for now" in w.titles, w.titles
    assert w.made_none
    assert w.token_said == "Token saved.", w.token_said

    # With it: a Git app of the project, stored as GitLab's (its namespace after "gitlab:"), from main.
    row = gitberth.db.app(w.app.slug)
    assert (row["Source"], row["Owner"], row["Repo"], row["Branch"], row["Subdirectory"], row["Name"]) == \
        ("Git", f"gitlab:{namespace}", name, "main", None, name), {k: row[k] for k in ("Source", "Owner", "Repo", "Branch", "Subdirectory", "Name")}
    assert (w.first["CommitSha"], w.first["Trigger"]) == (w.sha, "Created"), w.first
    # git sent the token (oauth2 and the token, as Basic) every time it asked.
    calls = w.project.git_calls(w.mark)
    assert calls and all(c["token"] == gl_token and c["status"] == 200 for c in calls), [(c["method"], c["path"], c["status"]) for c in calls]
    created = until(lambda: audit.since(w.first_audit, "action = 'app.created' AND app_id = ?", w.app.id), 15, "the app's creation in the audit log")
    assert created[0]["summary"] == f"Created {name} from {HOST}/{w.project.path} (main).", created[0]

    # The app knows where it comes from, as Vercel tells a GitLab project's apps.
    env = gitberth.wait_answers(gitberth.lan_url(w.app.id), "/env")
    expected = {"VERCEL_GIT_PROVIDER": "gitlab", "VERCEL_GIT_REPO_OWNER": namespace, "VERCEL_GIT_REPO_SLUG": name, "VERCEL_GIT_COMMIT_REF": "main"}
    assert {k: env.get(k) for k in expected} == expected
    # Its page links to the project on GitLab.
    gitberth.open(page, f"/apps/{w.app.key}")
    link = page.locator(f".gb-hero a.gb-src[href='{w.project.url}']")
    link.wait_for()
    assert flat(link.inner_text()) == "Git"
    assert not gitberth.errors(page), gitberth.errors(page)


def test_ci_cd_variables_reach_production_by_their_environment_scope(world, gitberth):
    w = world
    env = gitberth.wait_answers(gitberth.lan_url(w.app.id), "/env")
    assert {k: env.get(k) for k in PRODUCTION_GETS} == PRODUCTION_GETS
    assert [k for k in ("GL_PREVIEW", *NEVER) if k in env] == []
    log = gitberth.deploy_log(w.first["Id"])
    assert "Using 5 variables from the project's GitLab CI/CD variables (scopes * and production)" in log, log
    assert FILE_NOTE in log, log
    assert "Not given to previews" not in log
    # Masked in GitLab: secret here too, so the log never shows its value.
    assert MASKED not in log


def test_a_push_deploys_through_the_watcher_and_gitlab_gets_its_status(world, gitberth, gl_token, standins):
    w = world
    p = w.project
    gitberth.wait_idle(w.app.id)
    before = gitberth.db.last_deployment_id(w.app.id)
    mark = standins.seq()
    sha = p.commit({"marker.txt": "gitlab v2"}, message="Ship v2")["sha"]
    two = gitberth.wait_deploy(w.app.id, after=before, timeout=300)
    assert (two["CommitSha"], two["Trigger"]) == (sha, "Poll"), two
    url = gitberth.lan_url(w.app.id)
    until(lambda: (gitberth.wait_answers(url, "/") or {}).get("marker") == "gitlab v2", 60, "v2 to answer")

    # GitLab: running while it built, then success with the app's address, on main, under the app's name, with the token.
    name = f"Gitberth: {w.app.name}"
    writes = wait_status_writes(p, mark, sha, 2)
    assert written(writes) == [("running", name, "main", 201), ("success", name, "main", 201)], written(writes)
    assert {c["token"] for c in writes} == {gl_token}
    [status] = p.statuses(sha=sha, name=name)
    assert (status["status"], status["ref"], status["description"], status["target_url"]) == \
        ("success", "main", f"Deploy is live at {bare(url)}", url + "/"), status


def test_with_build_filters_a_docs_only_push_is_skipped_from_gitlabs_compare(world, page, gitberth, standins):
    w = world
    p = w.project
    gitberth.wait_idle(w.app.id)
    said = set_filters(gitberth, page, w.app, ignored="docs/\n*.md")
    assert said == "Saved.", said
    running = gitberth.db.app(w.app.slug)["CurrentCommit"]
    before = gitberth.db.last_deployment_id(w.app.id)
    mark = standins.seq()
    docs = p.commit({"docs/guide.md": "# Guide\n", "README.md": "# Readme\n"}, message="Write the docs")["sha"]
    skipped = gitberth.wait_deploy(w.app.id, after=before, statuses=("Skipped",), timeout=300)
    assert (skipped["CommitSha"], skipped["SkipReason"]) == (docs, "Only files the build filters leave out changed: README.md, docs/guide.md"), skipped
    assert gitberth.db.app(w.app.slug)["CurrentCommit"] == running
    # What changed came from GitLab's compare of the commit that runs and the new one: nothing was fetched for it.
    compared = p.api_calls(mark, path=r"/repository/compare$", method="GET")
    assert [(c["status"], c["query"].get("from"), c["query"].get("to"), c["query"].get("straight")) for c in compared] == \
        [(200, running, docs, "true")], compared
    # GitLab: success for the skipped commit, never running.
    name = f"Gitberth: {w.app.name}"
    wait_status_writes(p, mark, docs, 1)
    stays(lambda: written(status_writes(p, mark, docs)) == [("success", name, "main", 201)], 2, "one success, and nothing else, for the skipped commit")
    [status] = p.statuses(sha=docs, name=name)
    assert (status["status"], status["description"]) == ("success", "Skipped: nothing the app builds from changed"), status


@pytest.fixture(scope="class")
def mr(world, gitberth, standins):
    """Previews of merge requests on, then a merge request opened from a branch of the project: its preview made and
    deployed."""
    w = world
    p = w.project
    page = gitberth.new_page()
    try:
        ensure_previews(gitberth, page, w.app, forks=False)
    finally:
        page.context.close()
    mark = standins.seq()
    sha = p.commit({"marker.txt": "merge request one"}, branch="feature", message="Add a feature: first commit")["sha"]
    iid = p.open_mr("feature", title="Add a feature")["iid"]
    preview = gitberth.wait_preview(w.app.id, pr=iid, timeout=90)
    deploy = wait_deployed(gitberth, preview["Id"], sha)
    return SimpleNamespace(project=p, app=w.app, mark=mark, iid=iid, sha=sha, preview=preview, deploy=deploy)


class TestMergeRequestPreview:
    def test_it_is_built_from_its_merge_request_ref_with_the_variables_for_previews(self, mr, page, gitberth):
        n = mr.iid
        p = mr.preview
        assert (p["Slug"], p["Name"]) == (f"{mr.app.slug}-pr-{n}", f"{mr.app.name} · MR !{n}"), p
        assert (p["Kind"], p["PullRequest"], p["Ref"], p["PreviewBranch"], p["PreviewTitle"], p["HeadSha"], p["FromFork"]) == \
            ("PullRequest", n, f"refs/merge-requests/{n}/head", "feature", "Add a feature", mr.sha, 0), p
        assert f"Preview MR !{n} added: Add a feature." in activity_texts(gitberth, mr.app), activity_texts(gitberth, mr.app)
        asked, wanted, shown = fetched(mr.project, mr.mark, f"refs/merge-requests/{n}/head")
        assert asked and mr.sha in wanted, f"Gitberth didn't fetch refs/merge-requests/{n}/head ({mr.sha[:7]}); its git requests: {shown}"

        url = gitberth.lan_url(p["Id"])
        assert gitberth.wait_answers(url, "/")["marker"] == "merge request one"
        env = gitberth.wait_answers(url, "/env")
        namespace, name = mr.project.path.rsplit("/", 1)
        expected = {"VERCEL_ENV": "preview", "VERCEL_GIT_PULL_REQUEST_ID": str(n), "VERCEL_GIT_COMMIT_REF": "feature",
                    "VERCEL_GIT_COMMIT_SHA": mr.sha, "VERCEL_GIT_PROVIDER": "gitlab", "VERCEL_GIT_REPO_OWNER": namespace,
                    "VERCEL_GIT_REPO_SLUG": name}
        assert {k: env.get(k) for k in expected} == expected
        # A preview's variables: * and preview/* (the preview's value of a name winning), never a protected one.
        assert {k: env.get(k) for k in PREVIEW_GETS} == PREVIEW_GETS
        assert [k for k in ("GL_PROD", "GL_PROTECTED", *NEVER) if k in env] == []
        log = gitberth.deploy_log(mr.deploy["Id"])
        for line in (f"A preview of {mr.app.name} (MR !{n}): its settings are {mr.app.name}'s, and so are its variables for previews",
                     "Using 4 variables from the project's GitLab CI/CD variables (scopes * and preview/*)", PROTECTED_NOTE, FILE_NOTE):
            assert line in log, line
        assert MASKED not in log

        # The parent's Previews tab: its label, linking to the merge request on GitLab.
        gitberth.open(page, f"/apps/{mr.app.key}/previews")
        row = preview_row(page, f"MR !{n}")
        row.wait_for()
        assert row.locator(".gb-drow-host a.gb-mono").get_attribute("href") == f"{mr.project.url}/-/merge_requests/{n}"
        assert not gitberth.errors(page), gitberth.errors(page)

    def test_gitlab_gets_its_statuses_and_one_note_on_the_merge_request(self, mr, gitberth, gl_token):
        n = mr.iid
        url = gitberth.lan_url(mr.preview["Id"])
        name = f"Gitberth preview: {mr.app.name}"
        # Running, then success with the preview's address, on its branch.
        writes = wait_status_writes(mr.project, mr.mark, mr.sha, 2)
        assert written(writes) == [("running", name, "feature", 201), ("success", name, "feature", 201)], written(writes)
        [status] = mr.project.statuses(sha=mr.sha, name=name)
        assert (status["status"], status["ref"], status["description"], status["target_url"]) == \
            ("success", "feature", f"Preview is live at {bare(url)}", url + "/"), status
        # One note: made when the build started, then edited to say the preview is ready, where, and at which commit.
        note = wait_note(mr.project, n, "Ready", mr.sha[:7])
        said = parse_comment(note["body"])
        assert (said["marker"], said["parent"], said["address"], note["author"]["username"]) == (True, mr.app.name, url + "/", "e2e-bot"), note
        writes = note_writes(mr.project, mr.mark, n)
        assert [(c["method"], c["status"]) for c in writes] == [("POST", 201), ("PUT", 200)], [(c["method"], c["path"], c["status"]) for c in writes]
        assert writes[1]["path"].endswith(f"/notes/{note['id']}") and {c["token"] for c in writes} == {gl_token}, writes[1]["path"]
        assert gitberth.preview(mr.app.id, pr=n)["CommentId"] == note["id"]

    def test_a_new_commit_redeploys_it_and_edits_the_same_note(self, mr, gitberth, standins):
        n = mr.iid
        note = gitberths_notes(mr.project, n)[0]
        mark = standins.seq()
        sha = mr.project.commit({"marker.txt": "merge request two"}, branch="feature", message="Add a feature: second commit")["sha"]
        wait_deployed(gitberth, mr.preview["Id"], sha, after=mr.deploy["Id"])
        assert gitberth.preview(mr.app.id, pr=n)["Id"] == mr.preview["Id"], "a new preview was made rather than the same one redeployed"
        url = gitberth.lan_url(mr.preview["Id"])
        until(lambda: (gitberth.wait_answers(url, "/") or {}).get("marker") == "merge request two", 60, "the new commit to answer")
        writes = wait_status_writes(mr.project, mark, sha, 2)
        assert [s for s, _, _, _ in written(writes)] == ["running", "success"], written(writes)
        assert wait_note(mr.project, n, "Ready", sha[:7])["id"] == note["id"]
        assert [(c["method"], c["path"].rsplit("/", 1)[-1]) for c in note_writes(mr.project, mark, n)] == [("PUT", str(note["id"]))] * 2

    def test_merging_removes_it(self, mr, gitberth, engine):
        n = mr.iid
        slug = mr.preview["Slug"]
        before = gitberth.db.last_deployment_id(mr.app.id)
        merged = mr.project.merge_mr(n)["sha"]
        # Without a hook, the watcher's next list of the open merge requests notices (every 20 s with the rig's timings,
        # 5 minutes with production's): a merge moves no merge request's head.
        gitberth.wait_no_preview(mr.app.id, pr=n, timeout=PULLS_EVERY + 120)
        assert f"Preview MR !{n} removed: the merge request was merged." in activity_texts(gitberth, mr.app), activity_texts(gitberth, mr.app)
        until(lambda: not leftovers(engine, slug), 60, f"the engine to forget {slug}", seen=lambda: leftovers(engine, slug))
        # The merge is a push to main: production deploys it.
        production = gitberth.wait_deploy(mr.app.id, after=before, timeout=300)
        assert production["CommitSha"] == merged, production
        assert len(gitberths_notes(mr.project, n)) == 1


def test_a_forks_merge_request_gets_no_preview_until_forks_are_on(world, page, gitberth, standins):
    w = world
    p = w.project
    ensure_previews(gitberth, page, w.app, forks=False)
    fork = p.fork(owner="e2e-forker")
    mark = standins.seq()
    sha = fork.commit({"marker.txt": "from a fork"}, branch="from-fork", message="From a fork")["sha"]
    n = p.open_mr("from-fork", source=fork, title="From a fork")["iid"]

    # Forks off: Gitberth lists the merge requests, and makes nothing of the fork's: no preview, nothing fetched, and the
    # fork itself never asked.
    opened = standins.seq()
    listed = p.wait_mrs_listed(opened, timeout=90)
    assert listed["status"] == 200, listed
    # The pass that listed them made its previews; the next one starts once it's over.
    p.wait_watcher_passes(1, since=listed["seq"], timeout=60)
    assert gitberth.preview(w.app.id, pr=n) is None, "a fork's merge request was previewed with forks off"
    asked, wanted, shown = fetched(p, mark, f"refs/merge-requests/{n}/head")
    assert not asked and sha not in wanted, f"with forks off, Gitberth fetched the fork's code: {shown}"
    assert not fork.git_calls(mark) and not fork.api_calls(mark), "Gitberth asked the fork itself"

    # Forks on: its preview, built from the target project's merge request head at the fork's commit.
    ensure_previews(gitberth, page, w.app, forks=True)
    preview = gitberth.wait_preview(w.app.id, pr=n, timeout=90)
    assert (preview["Name"], preview["FromFork"], preview["Ref"], preview["PreviewBranch"], preview["HeadSha"]) == \
        (f"{w.app.name} · MR !{n}", 1, f"refs/merge-requests/{n}/head", "from-fork", sha), preview
    wait_deployed(gitberth, preview["Id"], sha)
    assert gitberth.wait_answers(gitberth.lan_url(preview["Id"]), "/")["marker"] == "from a fork"
    asked, wanted, shown = fetched(p, mark, f"refs/merge-requests/{n}/head")
    assert asked and sha in wanted, f"Gitberth didn't fetch refs/merge-requests/{n}/head ({sha[:7]}); its git requests: {shown}"
    assert not fork.git_calls(mark) and not fork.api_calls(mark), "Gitberth asked the fork itself"
    # Its statuses are on the target project, on no branch: the fork's branch is another project's.
    writes = wait_status_writes(p, mark, sha, 2)
    assert [(s, ref) for s, _, ref, _ in written(writes)] == [("running", None), ("success", None)], written(writes)


def test_instant_deploys_add_the_project_hook_and_a_push_deploys_at_once(world, page, new_page, gitberth, cloudflare, standins, gl_token, uniq):
    w = world
    p = w.project
    zone = f"{uniq('gw')}.test"
    host = f"hooks.{zone}"
    url = f"https://{host}/hooks/gitlab"
    cloudflare.zone(zone)
    gitberth.wait_idle(w.app.id)
    first = audit.last_id()

    with domains_turned_on(gitberth, new_page, cloudflare):
        mark = standins.seq()
        with instant_deploys(gitberth, new_page, host) as (settings, said):
            assert said == TURNED_ON.format(host), said
            # The tunnel takes /hooks/gitlab too, by the one path rule.
            assert cloudflare.rules_for(host) == webhook_rules(host), cloudflare.ingress()
            assert (f"Routed https://{host}/hooks/github, /hooks/gitlab and /hooks/deploy/… to Gitberth, for GitHub's and GitLab's "
                    "webhooks and deploy hooks") in notes(settings), notes(settings)

            # ---- The keeper adds Gitberth's project hook, with the api token, and records it.
            hook = until(lambda: next((h for h in p.hooks() if h["url"] == url and h["made_by"] == "api"), None), 60,
                         f"Gitberth's project hook for {url}", seen=lambda: [(h["id"], h["made_by"], h["url"]) for h in p.hooks()])
            assert (hook["push_events"], hook["merge_requests_events"], hook["enable_ssl_verification"]) == (True, True, True), hook
            made = p.api_calls(mark, path=r"/hooks$", method="POST")
            assert [(c["status"], c["token"]) for c in made] == [(201, gl_token)], made
            assert {k: v for k, v in made[0]["body"].items() if k != "token"} == \
                {"url": url, "push_events": True, "merge_requests_events": True, "enable_ssl_verification": True}, made[0]["body"]
            kept = until(lambda: json.loads(gitberth.db.setting("gitlab.webhookIds") or "{}").get(p.path.lower()), 30, "Gitberth to record its hook")
            assert (kept["Id"], kept["Url"]) == (hook["id"], url), kept

            # ---- Settings › GitLab: the hook's URL, its secret token (hidden until shown: the hook's), its events, and the
            # project's line.
            section = open_gitlab(gitberth, page)
            assert flat(kv(section, "URL").locator("code").inner_text()) == url
            secret = kv(section, "Secret token")
            code = secret.locator("code")
            assert flat(code.inner_text()) == DOTS
            secret.get_by_role("button", name="Show the secret token").click()
            until(lambda: flat(code.inner_text()) == hook["token"], 10, "Show to reveal the hook's secret token", seen=code.inner_text)
            assert flat(kv(section, "Trigger").inner_text()) == "Push events, Merge request events"
            lines = section.locator("ul[aria-label=\"GitLab projects' hooks\"] li")
            until(lambda: f"{p.path}: Webhook added" in [flat(t) for t in lines.all_inner_texts()], 30, "Settings to list the project's hook",
                  seen=lines.all_inner_texts)

            # ---- A push: its Push Hook comes through Cloudflare and the tunnel's route, and it deploys at once.
            cloudflare.wait_runs()
            p.wait_watcher_passes(1, since=standins.seq())   # pushed right after a look: the next poll is 10 s away
            before = gitberth.db.last_deployment_id(w.app.id)
            pushed = p.commit({"marker.txt": "instant"}, message="Pushed for an instant deploy")
            record = until(lambda: next((d for d in p.deliveries(mark, event="push") if d["hook_id"] == hook["id"]), None), 30,
                           "GitLab's Push Hook delivery")
            cf = (record.get("route") or {}).get("cloudflared") or {}
            assert (record["status"], record["response_body"], record["error"], record["headers"]["X-Gitlab-Event"]) == (202, "", None, "Push Hook"), record
            assert (record["via_tunnel"], cf.get("rule_host"), cf.get("rule_path"), cf.get("service")) == (True, host, PATH_RULE, OWN_SERVICE), record["route"]
            assert record["payload"]["after"] == pushed["sha"]
            two = gitberth.wait_deploy(w.app.id, after=before, timeout=300)
            # Only the watcher's pass that took the delivery's poke starts one as Webhook, never a poll.
            assert (two["CommitSha"], two["Trigger"]) == (pushed["sha"], "Webhook"), two

            # ---- A wrong token, or none: a bare 401, and nothing deploys.
            for token in ("bad", "none"):
                got = p.send_delivery(event="push", token=token)
                assert (got["status"], got["response_body"]) == (401, ""), got
            stays(lambda: gitberth.db.last_deployment_id(w.app.id) == two["Id"], 3, "nothing deployed for the refused deliveries")

    # The deploy says where it came from: its page, and the audit log's entry for it, by GitLab.
    gitberth.open(page, f"/apps/{w.app.id}/deployments/{two['Id']}")
    meta = page.locator(".gb-hero-meta")
    meta.wait_for()
    assert "Push on GitLab" in [flat(t) for t in meta.locator(".gb-tag").all_inner_texts()], meta.inner_text()
    queued = until(lambda: audit.since(first, "action = 'app.deploy.queued' AND app_id = ?", w.app.id), 15, "the deploy in the audit log")
    assert (queued[-1]["actor_kind"], queued[-1]["actor"]) == ("GitLab", "GitLab"), queued[-1]
    assert not gitberth.errors(page), gitberth.errors(page)


def test_the_environment_tab_lists_the_variables_and_saves_one_for_previews(world, page, gitberth, standins):
    w = world
    p = w.project
    gitberth.open(page, f"/apps/{w.app.key}/environment")
    stored = page.locator(".gb-card", has_text="Stored in GitLab")
    stored.wait_for()
    rows = stored.locator(".gb-repo-var")
    until(lambda: rows.count() >= len(EDITOR), 30, "the project's variables listed", seen=stored.inner_text)
    assert sorted(rows.evaluate_all(EDITOR_ROWS)) == EDITOR, rows.evaluate_all(EDITOR_ROWS)
    assert stored.get_by_role("link", name="Open in GitLab").get_attribute("href") == f"{p.url}/-/settings/ci_cd#js-cicd-variables-settings"
    # A masked one shows when asked.
    stored.get_by_role("button", name="Show GL_MASKED (every deploy)").click()
    until(lambda: MASKED in stored.inner_text(), 10, "the masked value to show")

    # Saved to GitLab for previews: not there in that scope (the PUT finds none), so it's added; then deleted.
    first = audit.last_id()
    mark = standins.seq()
    stored.get_by_label("Name", exact=True).fill("GL_SAVED")
    stored.get_by_label("Value", exact=True).fill("saved-from-gitberth")
    stored.get_by_label("Environment scope").select_option("preview/*")
    stored.get_by_role("button", name="Save to GitLab").click()
    said = flat(gitberth.snackbar(page, "^Saved GL_SAVED to GitLab"))
    assert said == "Saved GL_SAVED to GitLab (previews). Deploy the app to use it.", said
    saved = [(v["environment_scope"], v["value"], v["variable_type"], v["masked"], v["protected"]) for v in p.variables() if v["key"] == "GL_SAVED"]
    assert saved == [("preview/*", "saved-from-gitberth", "env_var", False, False)], saved
    writes = [(c["method"], c["status"]) for c in p.api_calls(mark, path=r"/variables(/GL_SAVED)?$") if c["method"] != "GET"]
    assert writes == [("PUT", 404), ("POST", 201)], writes
    entry = until(lambda: audit.since(first, "action = 'app.env.gitlab.set' AND target = 'GL_SAVED'"), 15, "the save in the audit log")[0]
    assert (entry["app_id"], entry["actor"], entry["summary"]) == (w.app.id, "admin", f"Saved the variable GL_SAVED to {p.path}'s CI/CD variables in GitLab "
                                                                   "(scope preview/*): every preview of an app from the project gets it at its next deploy."), entry
    stored.get_by_role("button", name="Delete GL_SAVED (previews)").click()
    gitberth.confirm(page, "Delete")
    gitberth.snackbar(page, "^Deleted GL_SAVED from GitLab")
    assert [v for v in p.variables() if v["key"] == "GL_SAVED"] == []
    assert not gitberth.errors(page), gitberth.errors(page)


def test_an_agent_creates_an_app_from_a_gitlab_project_over_the_api(page, gitberth, gitlab, gl_token, gl_on, apps, uniq):
    ensure_token(gitberth, page, gl_token)
    project = gitlab.create_project(f"e2e/{uniq('ag')}/{uniq('api')}", files={"marker.txt": "from an agent"})
    namespace, name = project.path.rsplit("/", 1)
    agent = uniq("agent")
    token, _ = make_token(gitberth, page, agent, "Admin")

    def api(tool, args, timeout=60):
        status, _, text = call("POST", gitberth.url(f"/api/v1/tools/{tool}"), body=args, headers={"Authorization": f"Bearer {token}"}, timeout=timeout)
        return status, text

    status, text = api("create_app", {"source": "git", "repo": project.url, "wait": True}, timeout=660)
    assert status == 200, text
    done = json.loads(text)
    row = gitberth.db.app_by_id(done["app_id"])
    app = apps.track(App(row["Id"], row["Slug"], row["Name"]))
    assert (done["status"], done["trigger"], done["requested_by"]) == ("Succeeded", "Created", f"the token {agent}"), done
    assert (row["Owner"], row["Repo"], row["Name"], row["Branch"]) == (f"gitlab:{namespace}", name, name, "main"), row
    assert gitberth.wait_answers(gitberth.lan_url(app.id), "/")["marker"] == "from an agent"

    # get_app: a Git app from GitLab, by its project's path.
    status, text = api("get_app", {"app": app.key})
    assert status == 200, text
    git = json.loads(text)["git"]
    assert (git["host"], git["repo"], git["branch"]) == ("gitlab", project.path, "main"), git
    # get_settings: GitLab's address, the token's user and scopes, and never the token.
    status, text = api("get_settings", {})
    assert status == 200, text
    assert json.loads(text)["gitlab"] == {"url": GitLab.URL, "token_saved": True, "login": "e2e-bot", "scopes": ["api"],
                                          "commit_statuses": True, "merge_request_comments": True}, text
    assert gl_token not in text
