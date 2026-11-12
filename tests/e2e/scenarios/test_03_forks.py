"""Scenario 3: pull requests from forks.

- Forks off (as an app's previews start): a fork's pull request isn't previewed, through more looks and a new commit
  on it, and its code is never fetched, while a pull request from the repo's own branch is. The Previews settings show
  the forks switch off, without its warning.
- Forks on: flipping the switch shows its warning at once ("Anyone's code, built and run on this server"), and it
  stays with the saved setting; the fork's pull request is then previewed, built from the base repo's
  refs/pull/<n>/head at the fork's commit (the fork itself is never asked). The Previews tab tags it as a fork.
- Turning forks off removes its preview at once (the save itself does), and it's never built again, even while
  GitHub's list of pull requests can't be read (an injected 502), nor after new commits on the fork.
- A fork's preview runs without the parent's added capabilities, security options (but no-new-privileges) or GPUs, and
  its deploy log (the file and its page) says what was left out; a pull request from the repo's own branch keeps the
  capabilities and security options.
"""
import re
from types import SimpleNamespace

import pytest

from e2e.wait import until
from test_02_pull_request_previews import (FORKER, activity_texts, fetched, leftovers, log_lines, new_app, open_pull, own_apps, preview_row,
                                           previews_card, runtime_extras, text_of, unique, wait_deployed)

FORKS_SWITCH = "Also pull requests from forks"
FORKS_SUB = "Off: only pull requests from branches of this repo get a preview."
WARNING_TITLE = "Anyone's code, built and run on this server"
WARNING_TEXT = ("Whoever opens a pull request from a fork decides what its preview builds and runs here, on your network. "
                "Turn this on only if you trust everyone who can open one.")
FORKS_OFF = "pull requests from forks aren't previewed any more"


def forks_section(page):
    """The Settings tab's Previews section, its forks switch and the warning that comes with it."""
    section = page.locator("#sec-previews")
    return section, section.get_by_role("switch", name=FORKS_SWITCH), section.locator(".gb-banner", has_text=WARNING_TITLE)


def nothing_of_the_fork(gitberth, engine, repo, fork, app, number, since, shas):
    """What shows the fork's pull request was previewed or its code fetched after the marker: a preview, its app row,
    anything of it in the engine, a fetch of its ref or commits, a request to the fork itself."""
    slug = f"{app.slug}-pr-{number}"
    found = []
    if gitberth.preview(app.id, pr=number) is not None:
        found.append(f"a preview of PR #{number}")
    if gitberth.db.app(slug) is not None:
        found.append(f"an app {slug}")
    found += leftovers(engine, slug)
    asked, wanted, shown = fetched(repo, since, f"refs/pull/{number}/head")
    if asked or wanted & set(shas):
        found.append(f"a fetch of its code: {shown}")
    calls = fork.git_calls(since) + fork.api_calls(since)
    if calls:
        found.append(f"requests to the fork itself: {[(c['method'], c['host'], c['path']) for c in calls]}")
    return found


def test_with_forks_off_a_forks_pull_request_isnt_previewed(page, gitberth, github, engine, apps, uniq, standins):
    repo, app, _ = new_app(page, gitberth, github, apps.track, uniq("fko"))
    fork = repo.fork(FORKER)
    mark = standins.seq()
    fork_n, fork_sha = open_pull(repo, "from-fork", "From a fork", marker="from a fork", fork=fork)
    own_n, own_sha = open_pull(repo, "own-work", "From the repo", marker="own work")
    said = gitberth.set_previews(page, app, mode="Pull requests")
    assert said.startswith("Saved"), said
    row = gitberth.db.app(app.slug)
    assert (row["PreviewMode"], row["PreviewForks"]) == ("PullRequests", 0), "forks must be off until someone turns them on"
    # The section says so: its switch is off, and the warning that comes with forks doesn't show.
    section, switch, warning = forks_section(page)
    assert switch.get_attribute("aria-checked") == "false"
    assert FORKS_SUB in text_of(section)
    assert warning.count() == 0, "the forks warning shows while forks are off"

    # The pull request from the repo's own branch gets its preview; the fork's doesn't.
    own = gitberth.wait_preview(app.id, pr=own_n, timeout=90)
    wait_deployed(gitberth, own["Id"], own_sha)
    assert gitberth.preview(app.id, pr=fork_n) is None, "a fork's pull request was previewed with forks off"

    # A new commit on the fork's branch moves its pull request's head: Gitberth asks GitHub again, and still makes none.
    again = standins.seq()
    fork_sha2 = fork.commit({"marker.txt": "from a fork, again"}, branch="from-fork", message="From a fork, again")["sha"]
    listed = repo.wait_pulls_listed(again, timeout=90)
    assert listed["status"] == 200, listed
    repo.wait_watcher_passes(2, since=listed["seq"], timeout=90)
    found = nothing_of_the_fork(gitberth, engine, repo, fork, app, fork_n, mark, {fork_sha, fork_sha2})
    assert not found, f"with forks off, the fork's pull request got {found}"

    # The Previews tab lists the repo's own pull request alone.
    gitberth.open(page, f"/apps/{app.key}/previews")
    preview_row(page, f"PR #{own_n}").wait_for()
    assert preview_row(page, f"PR #{fork_n}").count() == 0, "the Previews tab lists the fork's pull request"
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


def test_turning_forks_on_shows_its_warning_and_previews_the_forks_pull_request(page, gitberth, github, engine, apps, uniq, standins):
    repo, app, _ = new_app(page, gitberth, github, apps.track, uniq("fkn"))
    fork = repo.fork(FORKER)
    mark = standins.seq()
    fork_n, fork_sha = open_pull(repo, "from-fork", "From a fork", marker="from a fork", fork=fork)

    gitberth.open(page, f"/apps/{app.key}/settings")
    section, switch, warning = forks_section(page)
    section.wait_for()
    # With previews off there's nothing about forks; with pull requests chosen, the switch shows, off, and no warning.
    assert switch.count() == 0, "the forks switch shows while previews are off"
    section.get_by_role("radio", name="Pull requests", exact=True).click()
    switch.wait_for()
    assert switch.get_attribute("aria-checked") == "false" and warning.count() == 0
    assert "Every open pull request gets a preview, updated with each new commit, and removed when it's closed or merged." in text_of(section)
    assert FORKS_SUB in text_of(section)
    # Flipped on, the warning shows at once, before anything is saved.
    switch.click()
    warning.wait_for()
    assert switch.get_attribute("aria-checked") == "true"
    assert text_of(warning.locator(".gb-banner-title")) == WARNING_TITLE
    assert text_of(warning.locator(".gb-banner-sub")) == WARNING_TEXT
    assert warning.get_attribute("role") == "alert", "the warning isn't announced as one"
    assert gitberth.db.app(app.slug)["PreviewForks"] == 0, "flipping the switch saved it before Save"
    section.get_by_role("button", name="Save previews").click()
    said = gitberth.snackbar(page, "^Saved")
    assert "looks at the repo again" in said, said
    row = gitberth.db.app(app.slug)
    assert (row["PreviewMode"], row["PreviewForks"]) == ("PullRequests", 1), {k: row[k] for k in ("PreviewMode", "PreviewForks")}

    # The fork's pull request is previewed: from the base repo's refs/pull/<n>/head, at the fork's commit.
    preview = gitberth.wait_preview(app.id, pr=fork_n, timeout=90)
    assert (preview["Slug"], preview["FromFork"]) == (f"{app.slug}-pr-{fork_n}", 1), preview
    assert (preview["Kind"], preview["Ref"], preview["PreviewBranch"], preview["PreviewTitle"], preview["HeadSha"]) == \
        ("PullRequest", f"refs/pull/{fork_n}/head", "from-fork", "From a fork", fork_sha), preview
    wait_deployed(gitberth, preview["Id"], fork_sha)
    container = f"gb-{preview['Slug']}"
    assert engine.running(container) and engine.image_of(container) == f"gitberth/{preview['Slug']}:{fork_sha[:7]}"
    url = gitberth.lan_url(preview["Id"])
    assert gitberth.wait_answers(url, "/")["marker"] == "from a fork"
    env = gitberth.wait_answers(url, "/env")
    expected = {"VERCEL_ENV": "preview", "VERCEL_GIT_PULL_REQUEST_ID": str(fork_n), "VERCEL_GIT_COMMIT_REF": "from-fork",
                "VERCEL_GIT_COMMIT_SHA": fork_sha}
    assert {k: env.get(k) for k in expected} == expected
    asked, wanted, shown = fetched(repo, mark, f"refs/pull/{fork_n}/head")
    assert asked and fork_sha in wanted, f"Gitberth didn't fetch refs/pull/{fork_n}/head ({fork_sha[:7]}); its git requests: {shown}"
    calls = fork.git_calls(mark) + fork.api_calls(mark)
    assert not calls, f"Gitberth asked the fork itself: {[(c['method'], c['host'], c['path']) for c in calls]}"

    # The settings, read again, show forks on, with the warning.
    gitberth.open(page, f"/apps/{app.key}/settings")
    section, switch, warning = forks_section(page)
    switch.wait_for()
    assert switch.get_attribute("aria-checked") == "true"
    warning.wait_for()

    # The Previews tab tags it as a fork, and says what a fork's preview runs without.
    gitberth.open(page, f"/apps/{app.key}/previews")
    row = preview_row(page, f"PR #{fork_n}")
    row.wait_for()
    tag = row.locator(".gb-drow-host .gb-tag", has_text="Fork")
    assert tag.count() == 1 and tag.get_attribute("title") == "Its code comes from someone else's copy of the repo"
    pill = row.locator(".gb-pill2")
    until(lambda: text_of(pill) == "Ready", 60, "the fork's preview to say Ready", seen=lambda: text_of(pill))
    foot = text_of(previews_card(page).locator(".gb-card-foot"))
    assert "A pull request from a fork also runs without added capabilities, security options or GPUs." in foot, foot
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


def test_turning_forks_off_removes_its_preview_at_once_and_never_builds_it_again(page, gitberth, github, engine, apps, uniq, standins):
    repo, app, _ = new_app(page, gitberth, github, apps.track, uniq("fkf"))
    said = gitberth.set_previews(page, app, mode="Pull requests", forks=True)
    assert said.startswith("Saved"), said
    fork = repo.fork(FORKER)
    fork_n, fork_sha = open_pull(repo, "from-fork", "From a fork", marker="from a fork", fork=fork)
    preview = gitberth.wait_preview(app.id, pr=fork_n, timeout=90)
    wait_deployed(gitberth, preview["Id"], fork_sha)
    slug = preview["Slug"]
    assert preview["FromFork"] == 1 and engine.running(f"gb-{slug}"), preview

    # GitHub's list of the repo's pull requests can't be read from now on.
    standins.fail(rf"^/repos/{re.escape(repo.full_name)}/pulls$", status=502, host="api.github.com")

    # Forks off on the Settings tab: the warning goes with the switch, and saving removes the fork's preview.
    gitberth.open(page, f"/apps/{app.key}/settings")
    section, switch, warning = forks_section(page)
    switch.wait_for()
    assert switch.get_attribute("aria-checked") == "true"
    warning.wait_for()
    switch.click()
    warning.wait_for(state="detached")
    section.get_by_role("button", name="Save previews").click()
    gitberth.snackbar(page, "^Saved")
    # At once: when the save says it's done, the preview is gone (the watcher's next look isn't needed for it).
    still = gitberth.preview(app.id, pr=fork_n)
    left = leftovers(engine, slug)
    assert still is None and not left, f"right after the save, the fork's preview is {'still there' if still else 'gone'}, and the engine has {left}"
    removed = standins.seq()
    assert gitberth.db.app(app.slug)["PreviewForks"] == 0
    assert f"Preview PR #{fork_n} removed: {FORKS_OFF}." in activity_texts(gitberth, app), activity_texts(gitberth, app)

    # Never built again: a new commit on the fork moves its pull request's head, Gitberth asks GitHub (502), and makes
    # nothing of it...
    fork_sha2 = fork.commit({"marker.txt": "from a fork, again"}, branch="from-fork", message="From a fork, again")["sha"]
    listed = repo.wait_pulls_listed(removed, timeout=90)
    assert listed["status"] == 502 and listed["injected"], listed
    repo.wait_watcher_passes(2, since=listed["seq"], timeout=90)
    found = nothing_of_the_fork(gitberth, engine, repo, fork, app, fork_n, removed, {fork_sha, fork_sha2})
    assert not found, f"with forks turned off and the pull request list failing, the fork's pull request got {found}"

    # ...nor once GitHub answers again and the fork moves on again.
    standins.clear_failures()
    mark = standins.seq()
    fork_sha3 = fork.commit({"marker.txt": "from a fork, once more"}, branch="from-fork", message="From a fork, once more")["sha"]
    answered = until(lambda: [c for c in repo.api_calls(mark, path=r"/pulls$", method="GET") if c["status"] == 200], 90,
                     "Gitberth to read the pull requests again", seen=lambda: [(c["path"], c["status"]) for c in repo.api_calls(mark)])
    repo.wait_watcher_passes(2, since=answered[0]["seq"], timeout=90)
    found = nothing_of_the_fork(gitberth, engine, repo, fork, app, fork_n, removed, {fork_sha, fork_sha2, fork_sha3})
    assert not found, f"with forks turned off, the fork's pull request got {found}"
    assert gitberth.db.previews(app.id) == []

    # The Previews tab has none.
    gitberth.open(page, f"/apps/{app.key}/previews")
    empty = previews_card(page).locator(".gb-empty", has_text="No previews yet")
    empty.wait_for()
    assert "Open a pull request and its preview shows up here within a minute." in text_of(empty), text_of(empty)
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


# ---- what a fork's preview runs with ----

def save_for_next_deploy(page, gitberth, app, label, value):
    """The app's Settings tab, Advanced section: one field set and saved with the apply bar's Save for next deploy, so
    production keeps running as it is. Returns the snackbar's text."""
    gitberth.open(page, f"/apps/{app.key}/settings")
    page.locator("#sec-advanced").locator(f"[aria-label='{label}']").fill(value)
    save = page.locator(".gb-applybar").get_by_role("button", name="Save for next deploy")
    save.wait_for(timeout=30_000)
    save.click()
    return gitberth.snackbar(page, "^Saved")


def capabilities(inspect):
    """A container's added capabilities, as their names (Docker may keep them as CAP_<name>)."""
    return sorted(c.upper().removeprefix("CAP_") for c in (inspect["HostConfig"].get("CapAdd") or []))


def no_new_privileges(option):
    return option.strip().lower() in ("no-new-privileges", "no-new-privileges:true", "no-new-privileges=true")


@pytest.fixture(scope="class")
def reach(gitberth, github, standins):
    """An app whose settings reach past its container, saved on its Settings tab for its next deploy (production keeps
    running as it is): added capabilities and security options, then GPUs too. With forks on, a pull request from the
    repo's own branch is previewed while the parent has the capabilities and security options, and one from a fork once
    it has GPUs as well. (The rig has no GPU: a container given one wouldn't start, so the repo's own preview comes
    first.)"""
    with own_apps(gitberth, standins, "fork-reach") as (page, made):
        repo, app, _ = new_app(page, gitberth, github, made.track, unique("fkx"))
        said = save_for_next_deploy(page, gitberth, app, "Capabilities added", "NET_ADMIN, SYS_PTRACE")
        assert "next deploy" in said, said
        said = save_for_next_deploy(page, gitberth, app, "Security options", "no-new-privileges\nseccomp=unconfined")
        assert "next deploy" in said, said
        until(lambda: (lambda x: x.get("capAdd") and x.get("securityOpt"))(runtime_extras(gitberth.db.app(app.slug))), 15,
              "the parent's capabilities and security options to be saved", seen=lambda: runtime_extras(gitberth.db.app(app.slug)))
        said = gitberth.set_previews(page, app, mode="Pull requests", forks=True)
        assert said.startswith("Saved"), said
        own_n, own_sha = open_pull(repo, "own-work", "From the repo", marker="own work")
        own = gitberth.wait_preview(app.id, pr=own_n, timeout=90)
        own_deploy = wait_deployed(gitberth, own["Id"], own_sha)

        said = save_for_next_deploy(page, gitberth, app, "GPUs", "all")
        assert "next deploy" in said, said
        until(lambda: runtime_extras(gitberth.db.app(app.slug)).get("gpus"), 15, "the parent's GPUs to be saved",
              seen=lambda: runtime_extras(gitberth.db.app(app.slug)))
        parent_extras = runtime_extras(gitberth.db.app(app.slug))
        fork = repo.fork(FORKER)
        fork_n, fork_sha = open_pull(repo, "from-fork", "From a fork", marker="from a fork", fork=fork)
        forked = gitberth.wait_preview(app.id, pr=fork_n, timeout=90)
        fork_deploy = wait_deployed(gitberth, forked["Id"], fork_sha)
        yield SimpleNamespace(repo=repo, app=app, parent_extras=parent_extras, own_n=own_n, own=own, own_deploy=own_deploy,
                              fork_n=fork_n, forked=forked, fork_deploy=fork_deploy)


class TestWhatAForksPreviewRunsWith:
    def test_a_forks_preview_runs_without_added_capabilities_security_options_or_gpus(self, reach, page, gitberth, engine):
        w = reach
        assert sorted(w.parent_extras.get("capAdd") or []) == ["NET_ADMIN", "SYS_PTRACE"], w.parent_extras
        assert w.parent_extras.get("securityOpt") == ["no-new-privileges", "seccomp=unconfined"], w.parent_extras
        assert w.parent_extras.get("gpus") == "all", w.parent_extras

        # Its container: no added capability, no GPU, and of the security options only no-new-privileges.
        inspect = engine.inspect(f"gb-{w.forked['Slug']}")
        assert inspect and inspect["State"]["Running"], (inspect or {}).get("State")
        host = inspect["HostConfig"]
        assert capabilities(inspect) == [], f"the fork's preview runs with added capabilities: {host.get('CapAdd')}"
        options = host.get("SecurityOpt") or []
        assert options and all(no_new_privileges(o) for o in options), f"the fork's preview runs with the security options {options}"
        assert not host.get("DeviceRequests"), f"the fork's preview was given GPUs: {host.get('DeviceRequests')}"
        # Its own settings, copied from the parent's, say the same.
        extras = runtime_extras(gitberth.db.app(w.forked["Slug"]))
        assert not extras.get("capAdd") and not extras.get("gpus") and extras.get("securityOpt") == ["no-new-privileges"], extras

        # Its deploy log says what was left out, and why.
        log = gitberth.deploy_log(w.fork_deploy["Id"])
        line = next((x for x in log.splitlines() if "Runs without" in x), None)
        assert line, f"the fork's deploy log doesn't say what it runs without:\n{log[:3000]}"
        said = line.split(" ", 1)[1]
        m = re.fullmatch(r"Runs without added capabilities \(([^)]*)\), security options \(([^)]*)\) or GPUs \(([^)]*)\), "
                         r"because the pull request comes from a fork and its code mustn't reach the host\.", said)
        assert m, said
        assert (sorted(c.strip() for c in m.group(1).split(",")), m.group(2), m.group(3)) == (["NET_ADMIN", "SYS_PTRACE"], "seccomp=unconfined", "all"), said

        # And its deploy log page shows that line, under what the preview is.
        gitberth.open(page, f"/apps/{w.forked['Id']}/deployments/{w.fork_deploy['Id']}")
        until(lambda: said in log_lines(page), 30, "the deploy log page to show what the fork's preview runs without",
              seen=lambda: log_lines(page)[:15])
        assert f"A preview of {w.app.name} (PR #{w.fork_n}): its settings are {w.app.name}'s, and so are its variables for previews" in log_lines(page)
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_a_pull_request_from_the_repo_keeps_the_capabilities(self, reach, gitberth, engine):
        w = reach
        inspect = engine.inspect(f"gb-{w.own['Slug']}")
        assert inspect and inspect["State"]["Running"], (inspect or {}).get("State")
        assert capabilities(inspect) == ["NET_ADMIN", "SYS_PTRACE"], inspect["HostConfig"].get("CapAdd")
        options = inspect["HostConfig"].get("SecurityOpt") or []
        assert any(no_new_privileges(o) for o in options) and "seccomp=unconfined" in options, options
        log = gitberth.deploy_log(w.own_deploy["Id"])
        assert f"==> A preview of {w.app.name} (PR #{w.own_n})" in log, log[:3000]
        assert not [x for x in log.splitlines() if "Runs without" in x], "the repo's own pull request's preview was left without something"
