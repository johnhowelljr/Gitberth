"""Scenario 1: branch previews.

- Previews set to "Pull requests and branches": the branches that exist then are not built.
- A new commit on a branch makes a preview: <app>-<branch> as its slug, its own container, built from that branch's
  commit, told VERCEL_ENV=preview and the branch's VERCEL_GIT_*, and answering on its own LAN address.
- It's missing from the Apps list, its search, the top bar's search and the dashboard's count, and shown on the
  parent's Previews tab.
- Its own page has the preview banner, and no Settings or Environment tab.
- Production is untouched.
"""
from e2e.wait import stays, until


def test_branch_previews(page, gitberth, github, engine, apps, uniq, scenario, baseline):
    repo = github.create_repo(uniq("bp"), files={"marker.txt": "production"})
    # Branches that exist before previews are turned on, one of them with a commit of its own.
    repo.create_branch("old-work")
    repo.commit({"marker.txt": "old work"}, branch="old-work")
    repo.create_branch("idle")
    main_sha = repo.branch_sha("main")

    app = apps.track(gitberth.create_git_app(page, repo.url))
    production = gitberth.wait_deploy(app.id, timeout=420)
    assert production["CommitSha"] == main_sha

    # ---- Previews: pull requests and branches ----
    before = github.s.seq()
    said = gitberth.set_previews(page, app, mode="Pull requests and branches")
    assert "Saved" in said, said
    assert gitberth.db.app(app.slug)["PreviewMode"] == "PullRequestsAndBranches"
    # The first watcher pass with previews on asks GitHub for the open pull requests, and notes every branch's commit.
    listed = repo.wait_pulls_listed(since=before, timeout=90)
    # Two more passes after that one: the existing branches still have no preview.
    repo.wait_watcher_passes(2, since=listed["seq"], timeout=90)
    assert gitberth.db.previews(app.id) == [], "a branch that existed before previews were on was built"
    assert not [n for n in engine.container_names(prefix=app.container + "-")], "a preview container appeared for an old branch"

    # ---- A new commit on a branch makes its preview ----
    pushed = repo.commit({"marker.txt": "feature one"}, branch="old-work", message="Work on the old branch")
    sha = pushed["sha"]
    preview = gitberth.wait_preview(app.id, branch="old-work", timeout=90)
    assert preview["Slug"] == f"{app.slug}-old-work"
    assert preview["Kind"] == "Branch" and preview["Ref"] == "refs/heads/old-work"
    assert preview["ParentAppId"] == app.id
    assert preview["Name"] == f"{app.name} · old-work"
    deploy = gitberth.wait_deploy(preview["Id"], timeout=420)
    assert deploy["CommitSha"] == sha, "the preview wasn't built from the branch's new commit"
    # Only the branch with the new commit: the idle one still has none.
    assert gitberth.preview(app.id, branch="idle") is None

    # Its own container, from its own image repository, built from that commit.
    container = f"gb-{preview['Slug']}"
    assert engine.running(container)
    assert engine.image_of(container) == f"gitberth/{preview['Slug']}:{sha[:7]}"

    # It answers on its own LAN address, as a preview of that branch.
    url = gitberth.lan_url(preview["Id"])
    assert url != gitberth.lan_url(app.id)
    home = gitberth.wait_answers(url, "/")
    assert home["marker"] == "feature one"
    env = gitberth.wait_answers(url, "/env")
    expected = {"VERCEL_ENV": "preview", "VERCEL_TARGET_ENV": "preview", "VERCEL_GIT_COMMIT_REF": "old-work",
                "VERCEL_GIT_COMMIT_SHA": sha, "VERCEL_GIT_REPO_OWNER": repo.owner, "VERCEL_GIT_REPO_SLUG": repo.name,
                "VERCEL_GIT_PROVIDER": "github"}
    assert {k: env.get(k) for k in expected} == expected
    assert "VERCEL_GIT_PULL_REQUEST_ID" not in env, "a branch preview was told a pull request number"
    assert env.get("VERCEL_GIT_COMMIT_MESSAGE") == "Work on the old branch"

    # Production is untouched: same container, same commit, still production.
    prod_url = gitberth.lan_url(app.id)
    prod_env = gitberth.wait_answers(prod_url, "/env")
    assert prod_env.get("VERCEL_ENV") == "production" and prod_env.get("VERCEL_GIT_COMMIT_SHA") == main_sha
    assert gitberth.db.last_deployment_id(app.id) == production["Id"], "production was deployed again"

    # ---- Where it shows ----
    # Not in the Apps list, nor found by its search; the parent is.
    keys = gitberth.app_list_keys(page)
    assert app.key in keys and container not in keys, f"the Apps list shows {keys}"
    assert gitberth.app_list_keys(page, query=preview["Slug"]) == [], "searching the Apps list finds the preview"
    # Nor in the top bar's search: the parent's name finds the parent alone, the preview's slug finds nothing.
    gitberth.open(page, "/apps")
    titles = gitberth.top_search(page, app.slug, until_has=app.name)
    assert preview["Name"] not in titles, f"the top bar's search for {app.slug} offers {titles}"
    assert gitberth.top_search(page, preview["Slug"]) == [], "the top bar's search finds the preview"
    # Nor in the dashboard's count, which counts the apps the Apps list has.
    counts = until(lambda: (lambda d, a: (d, a) if d == a else None)(gitberth.dashboard_app_total(page), gitberth.app_list_total(page)),
                   45, "the dashboard to count the same apps as the Apps list",
                   seen=lambda: {"dashboard": gitberth.dashboard_app_total(page), "apps list": gitberth.app_list_total(page)})
    assert counts[0] >= 2

    # On the parent's Previews tab: the branch, Ready, its commit and its LAN address.
    gitberth.open(page, f"/apps/{app.key}/previews")
    row = page.locator(".gb-drow", has_text="old-work")
    row.wait_for()
    until(lambda: "Ready" in row.inner_text(), 60, "the preview's row to say Ready", seen=row.inner_text)
    text = row.inner_text()
    assert sha[:7] in text, text
    assert url.split("://", 1)[1] in text, f"the LAN address {url} isn't on the Previews tab: {text}"
    assert "Pull requests and branches" in page.locator("#gb-page").inner_text()

    # Its own page: the banner naming its parent and branch, and neither Settings nor Environment.
    gitberth.open(page, f"/apps/{container}")
    banner = page.locator(".gb-banner", has_text="Preview of")
    banner.wait_for()
    assert banner.locator(".gb-banner-title").inner_text().strip() == f"Preview of {app.name} for the branch old-work"
    tabs = gitberth.tabs(page)
    assert "Settings" not in tabs and "Environment" not in tabs, tabs
    assert "Overview" in tabs and "Logs" in tabs, tabs
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    # The production app's page still has both, and a Previews tab.
    gitberth.open(page, f"/apps/{app.key}")
    tabs = gitberth.tabs(page)
    assert {"Settings", "Environment", "Previews"} <= set(tabs), tabs

    # The idle branch stays without a preview through more passes (no commit since previews were turned on).
    mark = github.s.seq()
    repo.wait_watcher_passes(2, since=mark, timeout=90)
    stays(lambda: gitberth.preview(app.id, branch="idle") is None, 1, "the idle branch has no preview")
