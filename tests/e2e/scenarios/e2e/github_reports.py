"""What GitHub hears from Gitberth about deploys (GitHubReporter): commit statuses and pull request comments as the GitHub
stand-in keeps them, the calls Gitberth made to write them (each with its body, however it was answered), a comment's
parts as Gitberth writes them (GitHubReports.CommentBody), and every request Gitberth sent to GitHub's API, for the
checks that nothing private gets there. Also the two flows those checks need: Settings › GitHub integration's switches,
and the Environment tab giving an app variables."""
import json
import re
from datetime import datetime, timezone

from .gitberth import FINAL
from .wait import until

# The first line of every comment Gitberth keeps on a pull request.
COMMENT_MARKER = "<!-- gitberth:preview -->"
# What a comment says under a failed deploy.
FAILED_NOTE = "The deploy failed. The previous version of the preview, if there was one, still runs."
# Settings › GitHub integration's switches, and what each says when it's turned off and on again.
STATUSES = "Commit statuses"
COMMENTS = "Comments on pull requests"
SWITCHED = {
    (STATUSES, False): "Deploys don't set commit statuses any more.",
    (STATUSES, True): "Deploys set commit statuses on GitHub again.",
    (COMMENTS, False): "Previews don't comment on pull requests any more.",
    (COMMENTS, True): "Previews comment on their pull requests again.",
}
SETTING_KEYS = {STATUSES: "github.statuses", COMMENTS: "github.comments"}

# The scenarios' own calls to the stand-ins say who they are (conftest.RUNNER_AGENT).
RUNNER_AGENT = "gbe2e-runner"


def context(app_name, preview=False):
    """A deploy's status context: one per app ("Gitberth: <app>"), and a preview's under its parent's name."""
    return f"Gitberth preview: {app_name}" if preview else f"Gitberth: {app_name}"


# ---- commit statuses ----

def statuses(repo, sha, ctx=None):
    """The commit's statuses as GitHub keeps them (under one context, when given), oldest first."""
    return sorted(repo.statuses(sha=sha, context=ctx), key=lambda s: s["id"])


def wait_status(repo, sha, ctx, state, after=0, timeout=120):
    """Waits for a status of this state on the commit under the context, newer than the status `after` (an id), and
    returns it."""
    return until(lambda: next((s for s in statuses(repo, sha, ctx) if s["state"] == state and s["id"] > after), None), timeout,
                 f"a {state} status on {sha[:7]} under {ctx!r}", interval=0.5,
                 seen=lambda: [(s["context"], s["state"], s["description"], s["target_url"]) for s in statuses(repo, sha)])


def status_writes(repo, since):
    """Gitberth's POSTs of commit statuses on the repo after the marker, however they were answered."""
    return repo.api_calls(since, path=r"/statuses/[^/]+$", method="POST")


# ---- pull request comments ----

def comment_writes(repo, since):
    """Gitberth's comment writes on the repo after the marker, in order, however they were answered: a new comment (POST
    .../issues/<n>/comments) or an edit (PATCH .../issues/comments/<id>)."""
    return [c for c in repo.api_calls(since, path=r"/issues/(\d+/comments|comments/\d+)$") if c["method"] in ("POST", "PATCH")]


def written(call):
    """The comment a write sent, parsed (parse_comment)."""
    return parse_comment(((call.get("body") or {}).get("body")) or "")


HEAD = re.compile(r"^\*\*Gitberth preview of (?P<parent>.+): (?P<state>[A-Za-z]+)\*\*$", re.M)
ROW = re.compile(r"^\| (Preview|Commit|Updated) \| (.*) \|$", re.M)
LINK = re.compile(r"^\[(?P<text>[^\]]+)\]\((?P<url>[^)\s]+)\)$")
LOG = re.compile(r"^\[Deploy log\]\((?P<url>[^)\s]+)\) \(on your network\)$", re.M)


def parse_comment(body):
    """A preview's comment as Gitberth writes it: {marker (it starts with Gitberth's marker line), parent, state
    (Building, Ready, Failed or Cancelled), address (the preview's link, or None), address_text, commit (the short SHA,
    or None), updated (its time, a UTC datetime to the minute), log (the deploy log's link, or None), failed_note}."""
    head = HEAD.search(body)
    rows = dict(ROW.findall(body))
    preview = rows.get("Preview", "")
    link = LINK.match(preview)
    commit = re.fullmatch(r"`([0-9a-f]{7})`", rows.get("Commit", ""))
    updated = rows.get("Updated")
    log = LOG.search(body)
    return {
        "marker": body.startswith(COMMENT_MARKER + "\n"),
        "parent": head["parent"] if head else None,
        "state": head["state"] if head else None,
        "address": link["url"] if link else None,
        "address_text": link["text"] if link else preview,
        "commit": commit[1] if commit else None,
        "updated": datetime.strptime(updated, "%b %d, %Y %H:%M UTC").replace(tzinfo=timezone.utc) if updated else None,
        "log": log["url"] if log else None,
        "failed_note": FAILED_NOTE in body,
    }


def gitberths_comments(repo, pr):
    """The pull request's comments that Gitberth wrote (they start with its marker), oldest first."""
    return sorted((c for c in repo.comments(pr) if c["body"].startswith(COMMENT_MARKER)), key=lambda c: c["id"])


def wait_comment(repo, pr, state, commit, timeout=120):
    """Waits until Gitberth's newest comment on the pull request says this state for this commit (a short SHA), and
    returns every comment the pull request has."""
    def check():
        mine = gitberths_comments(repo, pr)
        last = parse_comment(mine[-1]["body"]) if mine else None
        return repo.comments(pr) if last and last["state"] == state and last["commit"] == commit else None

    return until(check, timeout, f"Gitberth's comment on PR #{pr} to say {state} for {commit}",
                 seen=lambda: [(c["id"], c["edits"], {k: v for k, v in parse_comment(c["body"]).items() if k in ("state", "commit", "address")})
                               for c in repo.comments(pr)])


# ---- everything GitHub received ----

def sent_to_github(standins, since):
    """Every request Gitberth made to GitHub's API after the marker (the scenarios' own left out), each as
    {"call": "METHOD path", "text": its path, query and body}."""
    out = []
    for c in standins.calls(since, host="api.github.com"):
        if (c.get("headers") or {}).get("User-Agent") == RUNNER_AGENT:
            continue
        body = json.dumps(c["body"], ensure_ascii=False) if c.get("body") is not None else (c.get("body_text") or "")
        out.append({"call": f"{c['method']} {c['path']}", "text": "\n".join([c["path"], json.dumps(c.get("query") or {}, ensure_ascii=False), body])})
    return out


def leaks(standins, since, needles):
    """The requests to GitHub's API after the marker that carry any of the needles: [(call, needle)]."""
    return [(s["call"], n) for s in sent_to_github(standins, since) for n in needles if n and n in s["text"]]


# ---- the flows these checks need ----

def set_github_switch(gitberth, page, title, on):
    """Settings › GitHub integration: turns the switch (STATUSES or COMMENTS) on or off, as it isn't already. Returns the
    snackbar's text, or None when it was that way already."""
    gitberth.open(page, "/settings")
    switch = page.locator("#github-integration").get_by_role("switch", name=title, exact=True)
    switch.wait_for()
    if (switch.get_attribute("aria-checked") == "true") == on:
        return None
    switch.click()
    said = gitberth.snackbar(page, re.escape(SWITCHED[(title, on)]))
    until(lambda: switch.get_attribute("aria-checked") == ("true" if on else "false"), 10, f"the {title} switch to show it's {'on' if on else 'off'}")
    return said


def wait_deployment(gitberth, deployment_id, ends=("Succeeded",), timeout=300):
    """Waits for this deployment to be over, and checks it ended in one of `ends`; returns its row."""
    d = until(lambda: (lambda x: x if x and x["Status"] in FINAL else None)(gitberth.db.deployment(deployment_id)), timeout,
              f"deploy #{deployment_id} to finish", interval=2, seen=lambda: gitberth.db.deployment(deployment_id))
    if d["Status"] not in ends:
        raise AssertionError(f"Deploy #{deployment_id} ended {d['Status']}, not {'/'.join(ends)}: {d['Error']}\n"
                             f"--- the end of its log ---\n{gitberth.deploy_log(deployment_id, tail=40)}")
    return d


def set_variables(gitberth, page, app, variables):
    """The Environment tab, as someone fills it in: each {"name", "value", "build_time"} added as a new variable for every
    deploy (a name that looks secret, like *_TOKEN, is secret by itself), then Apply. A Git app takes it with a deploy
    without a build, whose page opens: returns that deployment's id."""
    gitberth.open(page, f"/apps/{app.key}/environment")
    table = page.get_by_role("table", name="Environment variables")
    table.wait_for()
    names = table.get_by_label("Variable name", exact=True)
    for v in variables:
        # A new row goes last, once the server has drawn it: only then is it there to fill in.
        rows = names.count()
        page.get_by_role("button", name="Add variable").click()
        until(lambda: names.count() == rows + 1, 15, "a new variable's row", seen=table.inner_text)
        names.nth(rows).fill(v["name"])
        table.get_by_label(f"Value of {v['name']}", exact=True).fill(v["value"])
        if v.get("build_time"):
            mark = table.get_by_role("button", name=f"Build time for {v['name']}", exact=True)
            mark.click()
            until(lambda: mark.get_attribute("aria-pressed") == "true", 10, f"{v['name']} to be passed to the build")
    bar = page.get_by_role("region", name="Unsaved changes")
    until(lambda: "deploy without a build" in bar.inner_text(), 30, "the apply bar to say a deploy without a build applies it", seen=bar.inner_text)
    bar.get_by_role("button", name=re.compile(r"^(Review and apply|Apply)$")).click()
    page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"), timeout=60_000)
    return int(gitberth.path_of(page).rsplit("/", 1)[-1])
