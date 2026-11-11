"""Scenario 15: refusals.

GitHub turning Gitberth away never fails a deploy, and stops only what it's about:
- A token that doesn't select one repo (GitHub's 403 on writes to a public repo it doesn't select): that repo's commit
  status is refused once, Settings › GitHub integration names the repo and the permission, and its next deploys don't
  ask again. The other repo's deploys still get their statuses, its pull request's preview its comment, and with
  instant deploys on, its webhook; the refused repo's line says why it has none.
- A 401 (GitHub refusing the token itself): the feature rests everywhere, with Settings' banner saying to update the
  token; deploys of any repo make no call. Saving the token again takes the banner away, and the next deploy reports.
- A rate limit, a 502 and an answer that doesn't come in time: each deploy still succeeds, nothing rests, nothing is
  said in Settings, and the next call is made as usual.
"""
import contextlib
import re

from e2e.github_reports import comment_writes, context, status_writes, wait_comment, wait_status
from e2e.wait import stays, until
from test_12_commit_statuses import domains_turned_on
from test_14_instant_deploys import hook_line, instant_deploys, notes, wait_hook, wait_hook_line

SECTION = "#github-integration"
TOKEN_REFUSED = "GitHub says the token is invalid or expired. Update it in Settings."


def flat(text):
    return " ".join((text or "").split())


def banners(gitberth, page):
    """Settings › GitHub integration's warnings, as (title, text)."""
    gitberth.open(page, "/settings")
    section = page.locator(SECTION)
    section.wait_for()
    return [(flat(b.locator(".gb-banner-title").inner_text()), flat(b.locator(".gb-banner-sub").inner_text()))
            for b in section.locator(".gb-banner").all()]


def wait_banners(gitberth, page, check, what, timeout=30):
    return until(lambda: (lambda b: b if check(b) else None)(banners(gitberth, page)), timeout, what,
                 seen=lambda: banners(gitberth, page))


@contextlib.contextmanager
def token_saved(gitberth, new_page, token, baseline):
    """This GitHub token saved in Settings; the session's own saved again afterwards, whatever happened."""
    page = new_page()
    said = gitberth.save_github_token(page, token)
    assert not re.search(r"rejected|invalid|expired", said), said
    try:
        yield page
    finally:
        gitberth.save_github_token(new_page(), baseline["token"])


def commit_and_deploy(gitberth, repo, app, text, after):
    """A commit on the repo's main, and the deploy the watcher makes of it, which must succeed. Returns (sha, deploy)."""
    sha = repo.commit({"marker.txt": text}, message=text)["sha"]
    d = gitberth.wait_deploy(app.id, after=after, timeout=300)
    assert d["CommitSha"] == sha, d
    return sha, d


def test_a_repo_github_refuses_rests_alone_and_the_others_still_report(page, new_page, gitberth, github, cloudflare, standins, apps,
                                                                        uniq, baseline):
    good = github.create_repo(uniq("rg"), files={"marker.txt": "good v1"})
    rested = github.create_repo(uniq("rr"), files={"marker.txt": "rested v1"})
    good_app = apps.track(gitberth.create_git_app(page, good.url))
    rested_app = apps.track(gitberth.create_git_app(page, rested.url))
    last = {a.id: gitberth.wait_deploy(a.id, timeout=420)["Id"] for a in (good_app, rested_app)}
    good_ctx, rested_ctx = context(good_app.name), context(rested_app.name)

    # A token that selects only the good repo: the other is public, so it's still cloned and watched, but GitHub refuses
    # every write to it with a 403.
    limited = github.token(login="e2e-bot", repos=[good.full_name])
    with token_saved(gitberth, new_page, limited, baseline) as settings:
        mark = standins.seq()
        g2, d = commit_and_deploy(gitberth, good, good_app, "good v2", last[good_app.id])
        last[good_app.id] = d["Id"]
        r2, d = commit_and_deploy(gitberth, rested, rested_app, "rested v2", last[rested_app.id])
        last[rested_app.id] = d["Id"]

        # The good repo's deploy reported as always.
        wait_status(good, g2, good_ctx, "success", timeout=60)
        # The other's: one try (as its deploy started), refused; its ending wasn't sent.
        stays(lambda: [(c["status"]) for c in status_writes(rested, mark)] == [403], 5, "one refused status on the rested repo")
        assert not rested.statuses(sha=r2, context=rested_ctx)

        # Settings names the repo and what the token needs there; nothing about the good repo, nothing about the token.
        title = f"Commit statuses aren't set on {rested.full_name}"
        shown = wait_banners(gitberth, settings, lambda b: any(t == title for t, _ in b), f"Settings to say {title}")
        assert len(shown) == 1, shown
        text = shown[0][1]
        assert f"The GitHub token isn't allowed to set a commit status on {rested.full_name}." in text, text
        assert "Repository permissions > Commit statuses: Read and write" in text and "for an hour, or until the token is saved again" in text, text

        # The rested repo's next deploy doesn't ask GitHub at all; the good one's still reports.
        since = standins.seq()
        r3, d = commit_and_deploy(gitberth, rested, rested_app, "rested v3", last[rested_app.id])
        last[rested_app.id] = d["Id"]
        g3, d = commit_and_deploy(gitberth, good, good_app, "good v3", last[good_app.id])
        last[good_app.id] = d["Id"]
        wait_status(good, g3, good_ctx, "success", timeout=60)
        stays(lambda: status_writes(rested, since) == [], 5, "no status call on the rested repo")

        # The good repo's pull request preview gets its comment.
        before = standins.seq()
        assert "Saved" in gitberth.set_previews(settings, good_app, mode="Pull requests")
        good.wait_pulls_listed(since=before, timeout=90)
        good.create_branch("feature")
        head = good.commit({"marker.txt": "feature"}, branch="feature", message="Feature")["sha"]
        cmark = standins.seq()
        n = good.open_pull("feature", title="Try it")["number"]
        preview = gitberth.wait_preview(good_app.id, pr=n, timeout=120)
        gitberth.wait_deploy(preview["Id"], timeout=420)
        wait_comment(good, n, "Ready", head[:7])
        assert all(c["status"] in (200, 201) for c in comment_writes(good, cmark)), comment_writes(good, cmark)

        # Instant deploys: the good repo gets Gitberth's webhook, the rested one's line says why it has none.
        zone = f"{uniq('rf')}.test"
        host = f"hooks.{zone}"
        cloudflare.zone(zone)
        with domains_turned_on(gitberth, new_page, cloudflare):
            with instant_deploys(gitberth, new_page, host) as (hooks_page, _):
                wait_hook(good, host)
                wait_hook_line(hooks_page, good, "Webhook added")
                line = until(lambda: hook_line(hooks_page, rested), 60, f"Settings' line for {rested.full_name}", seen=lambda: notes(hooks_page))
                assert f"isn't allowed to read {rested.full_name}'s webhooks" in line and "Webhooks: Read and write" in line, line
                assert not [h for h in rested.hooks() if host in (h["config"].get("url") or "")], rested.hooks()
                # Webhooks are said per repo, not as a banner.
                assert not [t for t, _ in banners(gitberth, hooks_page) if "webhook" in t.lower()], banners(gitberth, hooks_page)

    # Saved again (the session's token), everything is tried again: the rested repo's next deploy reports.
    assert banners(gitberth, page) == [], banners(gitberth, page)
    r4, _ = commit_and_deploy(gitberth, rested, rested_app, "rested v4", last[rested_app.id])
    wait_status(rested, r4, rested_ctx, "success", timeout=60)


def test_a_refused_token_rests_the_feature_everywhere_until_its_saved_again(page, new_page, gitberth, github, standins, apps, uniq, baseline):
    one = github.create_repo(uniq("t1"), files={"marker.txt": "one v1"})
    two = github.create_repo(uniq("t2"), files={"marker.txt": "two v1"})
    app_one = apps.track(gitberth.create_git_app(page, one.url))
    app_two = apps.track(gitberth.create_git_app(page, two.url))
    last = {a.id: gitberth.wait_deploy(a.id, timeout=420)["Id"] for a in (app_one, app_two)}

    # GitHub answers Gitberth's statuses with 401, as for a token it no longer takes.
    rule = standins.fail(r"/statuses/[^/]+$", status=401, host="api.github.com", method="POST")
    try:
        mark = standins.seq()
        s1, d = commit_and_deploy(gitberth, one, app_one, "one v2", last[app_one.id])
        last[app_one.id] = d["Id"]
        stays(lambda: [c["status"] for c in status_writes(one, mark)] == [401], 5, "one refused status call")
        title = "Commit statuses aren't set: GitHub refused the token"
        shown = wait_banners(gitberth, page, lambda b: any(t == title for t, _ in b), f"Settings to say {title}")
        assert (title, TOKEN_REFUSED) in shown, shown

        # Rested everywhere: the other repo's deploy makes no call either.
        since = standins.seq()
        _, d = commit_and_deploy(gitberth, two, app_two, "two v2", last[app_two.id])
        last[app_two.id] = d["Id"]
        stays(lambda: status_writes(two, since) == [] and status_writes(one, since) == [], 5, "no status calls while the token is refused")
    finally:
        standins.clear_failures(rule)

    # The token saved again: the banner goes, and the next deploy reports.
    gitberth.save_github_token(page, baseline["token"])
    assert banners(gitberth, page) == [], banners(gitberth, page)
    s3, _ = commit_and_deploy(gitberth, two, app_two, "two v3", last[app_two.id])
    wait_status(two, s3, context(app_two.name), "success", timeout=60)


def test_a_rate_limit_a_502_or_no_answer_in_time_fails_nothing_and_rests_nothing(page, gitberth, github, standins, apps, uniq):
    repo = github.create_repo(uniq("rl"), files={"marker.txt": "v1"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    last = gitberth.wait_deploy(app.id, timeout=420)["Id"]
    ctx = context(app.name)
    statuses_path = r"/statuses/[^/]+$"

    # The pending status hits the rate limit, the ending a 502: the deploy succeeds all the same.
    standins.fail(statuses_path, host="api.github.com", method="POST", rate_limit="primary", times=1)
    standins.fail(statuses_path, status=502, host="api.github.com", method="POST", times=1)
    mark = standins.seq()
    sha, d = commit_and_deploy(gitberth, repo, app, "v2", last)
    last = d["Id"]
    stays(lambda: [c["status"] for c in status_writes(repo, mark)] == [403, 502], 5, "two failed status calls")
    assert not repo.statuses(sha=sha, context=ctx)

    # The pending status gets no answer for 20 s: Gitberth gives up on it after 10, and the deploy goes on and reports
    # how it ended.
    standins.fail(statuses_path, status=502, delay_ms=20_000, host="api.github.com", method="POST", times=1)
    sha, d = commit_and_deploy(gitberth, repo, app, "v3", last)
    last = d["Id"]
    wait_status(repo, sha, ctx, "success", timeout=60)
    standins.clear_failures()

    # Nothing rests and nothing is said: the next deploy reports both ways.
    shown = banners(gitberth, page)
    assert not [b for b in shown if repo.full_name in b[0] or "refused the token" in b[0]], shown
    sha, _ = commit_and_deploy(gitberth, repo, app, "v4", last)
    wait_status(repo, sha, ctx, "success", timeout=60)
    assert [s["state"] for s in repo.statuses(sha=sha, context=ctx)] == ["pending", "success"]
