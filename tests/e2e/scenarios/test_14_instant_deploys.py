"""Scenario 14: instant deploys.

GitHub tells Gitberth about a push the moment it happens, through Cloudflare's edge and the tunnel, and Gitberth looks
at that repo at once instead of at its next check (the rig's watcher looks every 10 seconds).

- Settings › GitHub integration › Instant deploys: without domains it says what it needs and how often Gitberth looks
  meanwhile. With them it takes a host name of its own: one that isn't a host name, or that someone else's routes are
  on, is refused and nothing changes; one in no zone the token can see is routed, and the page says its DNS record
  can't be made; one Cloudflare's free certificate doesn't cover (two levels below the zone) is said, since GitHub
  checks the certificate.
- Turned on, the tunnel routes only /hooks/github of the host to Gitberth (and /hooks/deploy/<token>, deploy hooks', in
  the same path rule) plus a 404 rule for the rest of it, never behind Access, and the host gets its DNS record. The keeper adds Gitberth's webhook to each deployed repo (the payload
  URL, the secret Settings shows, push, pull_request and delete) and records it; GitHub's ping for it is answered. The
  page shows the payload URL, content type, secret (Show, Hide, copy) and events, and each repo's webhook.
- A signed push deploys at once, with the trigger "Push on GitHub" (on its deploy page, Activity and the app page):
  measured from the delivery to the deploy's start, against the look before it and the 10-second poll.
- A pull request delivery makes its preview at once, and a delete delivery removes a branch's preview at once; a push
  to a branch makes its preview at once too.
- The host only ever answers with a status code: ping 200; 204 for another event or for a repo Gitberth doesn't deploy
  (nothing deploys, and Gitberth never looks at it); a bare 401 for a bad or missing signature; a bare 405 for anything
  but POST; 413 past 1 MB (exactly 1 MB is taken). Every other address of the host is the tunnel's 404, never
  Gitberth's pages.
- Ten bad signatures from one sender: its next bad ones get 429 (a CF-Connecting-IP of its own making changes nothing),
  while its signed ones, and another sender's bad one, are answered as before.
- Changing the host moves the route, the DNS record and Gitberth's recorded webhook (PATCHed: never a second one).
  Turning it off takes the route and DNS record away (it asks first); the webhooks stay on GitHub, as the confirm
  says, and are forgotten, and polling deploys. Another service's webhook and route on another host, with the same
  path, are never touched.
- A webhook on an earlier host name that Gitberth never recorded (another Gitberth's) is left alone, and Gitberth adds
  its own. A repo no app deploys any more is forgotten: its webhook stays where it was, and is never moved later.
- A webhook added by hand with Gitberth's payload URL is taken up and recorded, and a push through it with GitHub's
  default content type (form data) deploys at once too.
- A repo deployed after instant deploys were turned on gets its webhook at the keeper's own next look, with no change
  in Settings (slow: up to 10 minutes).
"""
import contextlib
import json
import re
import secrets
import time
from datetime import datetime, timezone

import pytest

from e2e.wait import stays, until
# Settings › Domains on with a Cloudflare token, and off again afterwards: scenario 12's, shared rather than copied.
from test_12_commit_statuses import domains_turned_on

# The tunnel's rule for GitHub's deliveries (and deploy hooks' calls): exactly these paths of the webhook's host, to
# Gitberth's published port, as the fake cloudflared (on the host's network, like the live install's) reaches it.
PATH_RULE = "^/hooks/(github|deploy/[A-Za-z0-9_-]{20,100})$"
# The rule another service has for its own webhooks on a host of its own: the path Gitberth's rule had before deploy hooks.
THEIR_PATH = "^/hooks/github$"
OWN_SERVICE = "http://localhost:8080"
NOT_FOUND = "http_status:404"
EVENTS = ["push", "pull_request", "delete"]
SECTION = "#github-integration"
DOTS = "\u2022" * 16
MB = 1024 * 1024
TURNED_ON = "GitHub's webhooks reach Gitberth at {} now."
TURNED_OFF = "Instant deploys are off."
# A route of another service's, as someone made it in Cloudflare's dashboard: nothing listens there, so it answers 502 at once.
THEIR_SERVICE = "http://localhost:9"


# ---- small things ----

def payload_url(host):
    """The payload URL Gitberth's webhook has: the host's /hooks/github, over https."""
    return f"https://{host}/hooks/github"


def webhook_rules(host):
    """All the tunnel has for the webhook's host: its one path to Gitberth, then a 404 for every other address of it."""
    return [{"hostname": host, "path": PATH_RULE, "service": OWN_SERVICE}, {"hostname": host, "service": NOT_FOUND}]


def flat(text):
    return " ".join((text or "").split())


def header(headers, name):
    return next((v for k, v in (headers or {}).items() if k.lower() == name.lower()), None)


def epoch(text):
    """A time Gitberth keeps (UTC, as EF Core writes it to SQLite: 2026-11-10 12:34:56.1234567), in seconds since 1970. The
    rig's containers share one clock, so it compares with the stand-ins' times."""
    m = re.match(r"^(\d{4}-\d\d-\d\d)[ T](\d\d:\d\d:\d\d)(?:\.(\d+))?", text or "")
    assert m, f"not one of Gitberth's times: {text!r}"
    fraction = (m[3] or "")[:6].ljust(6, "0")
    return datetime.strptime(f"{m[1]} {m[2]}.{fraction}", "%Y-%m-%d %H:%M:%S.%f").replace(tzinfo=timezone.utc).timestamp()


def docker_time(stamp):
    """A time Docker stamps on a log line (2026-11-10T12:34:56.123456789Z), in seconds since 1970."""
    m = re.match(r"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d+))?Z$", stamp or "")
    assert m, f"not one of Docker's log times: {stamp!r}"
    fraction = (m[2] or "")[:6].ljust(6, "0")
    return datetime.strptime(f"{m[1]}.{fraction}", "%Y-%m-%dT%H:%M:%S.%f").replace(tzinfo=timezone.utc).timestamp()


def cloudflared_ran(engine, host, since):
    """When the fake cloudflared began running a configuration that routes the host: the time Docker stamped on its
    "Updated to new configuration" line (the one Gitberth's own wait for the tunnel reads), in seconds since 1970.
    None when it hasn't yet."""
    for line in engine.logs("cloudflare-tunnel", since=int(since) - 1, timestamps=True).splitlines():
        stamp, _, text = line.partition(" ")
        if "Updated to new configuration" in text and host in text:
            return docker_time(stamp)
    return None


def json_of_size(size):
    """A JSON body of exactly this many bytes."""
    head, tail = '{"zen":"Keep it logically awesome.","padding":"', '"}'
    return head + "x" * (size - len(head) - len(tail)) + tail


def last_deployment_id(gitberth):
    return (gitberth.db.one("SELECT MAX(Id) AS Id FROM Deployments") or {}).get("Id") or 0


def report(request, text):
    """A measurement, in the run's output and its log: pytest shows a passing test's own output nowhere, so it's written
    with output capture suspended, as capsys.disabled() does."""
    plugins = request.config.pluginmanager
    reporter, capture = plugins.get_plugin("terminalreporter"), plugins.get_plugin("capturemanager")
    if reporter is None:
        return
    with capture.global_and_fixture_disabled() if capture is not None else contextlib.nullcontext():
        reporter.write_line(f"[e2e] {request.node.name}: {text}")


# ---- what GitHub has, and what Gitberth asked of it ----

def hook_with(repo, url, made_by=("api",)):
    """The repo's webhook with this payload URL, made by one of made_by ("api": through GitHub's API, as Gitberth makes them)."""
    return next((h for h in repo.hooks() if h["config"].get("url") == url and h.get("made_by") in made_by), None)


def wait_hook(repo, host, timeout=60):
    """Waits for Gitberth's webhook with the host's payload URL on the repo, and returns it as GitHub has it."""
    url = payload_url(host)
    return until(lambda: hook_with(repo, url), timeout, f"Gitberth's webhook for {url} on {repo.full_name}",
                 seen=lambda: [(h["id"], h.get("made_by"), h["config"].get("url")) for h in repo.hooks()])


def kept_by_github(hook):
    """What GitHub keeps of a webhook: a change to any of it is a change to the webhook."""
    return {k: hook.get(k) for k in ("id", "name", "config", "events", "active", "updated_at", "made_by")}


def unchanged(repo, hook):
    now = next((h for h in repo.hooks() if h["id"] == hook["id"]), None)
    return now is not None and kept_by_github(now) == kept_by_github(hook)


def hook_calls(repo, since):
    """Gitberth's calls about the repo's webhooks after the marker, however they were answered: (method, the path after
    the repo, status)."""
    prefix = f"/repos/{repo.full_name}/".lower()
    return [(c["method"], c["path"][len(prefix):] if c["path"].lower().startswith(prefix) else c["path"], c["status"])
            for c in repo.api_calls(since, path=r"/hooks(/\d+)?$")]


def kept_hooks(gitberth):
    """The webhooks Gitberth recorded as its own (github.webhookIds): {owner/repo: (id, the payload URL it set on it)}."""
    raw = gitberth.db.setting("github.webhookIds")
    return {k: (v["Id"], v["Url"]) for k, v in json.loads(raw).items()} if raw else {}


def wait_kept(gitberth, expected, timeout=30):
    until(lambda: kept_hooks(gitberth) == expected, timeout, f"Gitberth to record {expected} as its webhooks",
          seen=lambda: kept_hooks(gitberth))


def delivery_through(repo, since, hook_id, event):
    """GitHub's delivery of the event through the hook after the marker, with the answer it got."""
    return until(lambda: next((d for d in repo.deliveries(since, event=event) if d["hook_id"] == hook_id), None), 30,
                 f"GitHub's {event} delivery through hook {hook_id}",
                 seen=lambda: [(d["event"], d["hook_id"], d["status"], d["error"]) for d in repo.deliveries(since)])


def reached_gitberth(record, host):
    """Whether the delivery went through Cloudflare by the tunnel's rule for the webhook's path, to Gitberth."""
    cf = ((record.get("route") or {}).get("cloudflared")) or {}
    return bool(record.get("via_tunnel")) and cf.get("rule_host") == host and cf.get("rule_path") == PATH_RULE and cf.get("service") == OWN_SERVICE


def look_at(repo, standins, timeout=60):
    """Waits for the watcher's next look at the repo (its ls-remote), and returns that call: from then on the next poll
    is a whole interval away."""
    return repo.wait_watcher_passes(1, since=standins.seq(), timeout=timeout)


def first_look_after(repo, seq, timeout=20):
    return until(lambda: (repo.ls_remote_attempts(seq) or [None])[0], timeout, f"the watcher's next look at {repo.full_name}")


def at_once(request, repo, looked, record, at, interval, what):
    """Checks `at` (when something happened) came right after the delivery, well before the poll after the look before
    it could have; and that the watcher looked at the repo right after the delivery, and not between that look and the
    delivery. Reports the timings, and returns them said."""
    delivered = record["at"]
    first = first_look_after(repo, record["seq"])
    between = [c for c in repo.ls_remote_attempts(looked["seq"]) if c["seq"] < record["seq"]]
    timing = (f"the delivery came {delivered - looked['at']:.2f}s after the watcher's look; it looked again {first['at'] - delivered:.2f}s "
              f"after the delivery; {what} {at - delivered:.2f}s after it; the next poll was due {looked['at'] + interval - delivered:.2f}s after it")
    report(request, timing)
    assert not between, f"the watcher looked at {repo.full_name} between the push and its delivery, so a poll may have found it: {between}"
    assert first["at"] - delivered < 2, f"the watcher didn't look at the repo as the delivery came: {timing}"
    assert at - delivered < 3, f"not at once: {timing}"
    assert at < looked["at"] + interval, f"no sooner than the next poll would have: {timing}"
    return timing


# ---- Settings › GitHub integration › Instant deploys ----

def open_section(gitberth, page):
    gitberth.open(page, "/settings")
    section = page.locator(SECTION)
    section.locator(".gb-h3", has_text="Instant deploys").wait_for()
    return section


def set_hook_host(gitberth, page, host):
    """Types the host name in and clicks Turn on (or Save, when there's one already). Returns what the snackbar said."""
    section = open_section(gitberth, page)
    section.locator("#hook-host").fill(host)
    section.get_by_role("button", name=re.compile(r"^(Turn on|Save)$")).click()
    return flat(gitberth.snackbar(page, "."))


def kv(section, term):
    """The value the section's list shows for a term (Payload URL, Content type, Secret, Events)."""
    return section.locator(f"xpath=.//dl[contains(@class, 'gb-kv')]/dt[normalize-space(.)='{term}']/following-sibling::dd[1]")


def shown_secret(page):
    """The webhook's secret as Settings shows it: hidden, Show reveals it, Hide hides it again."""
    dd = kv(page.locator(SECTION), "Secret")
    code = dd.locator("code")
    assert flat(code.inner_text()) == DOTS, f"the secret wasn't hidden: {code.inner_text()!r}"
    dd.get_by_role("button", name="Show", exact=True).click()
    secret = until(lambda: (lambda t: t if re.fullmatch(r"[0-9a-f]{64}", t) else None)(flat(code.inner_text())), 10,
                   "Show to reveal the secret", seen=code.inner_text)
    dd.get_by_role("button", name="Hide", exact=True).click()
    until(lambda: flat(code.inner_text()) == DOTS, 10, "Hide to hide the secret again", seen=code.inner_text)
    return secret


def notes(page):
    """The lines under the section: what the last change did, and each repo's webhook."""
    return [flat(t) for t in page.locator(f"{SECTION} ul.gb-dnotes li").all_inner_texts()]


def hook_line(page, repo):
    return next((n for n in notes(page) if n.startswith(f"{repo.full_name}:")), None)


def wait_hook_line(page, repo, text, timeout=30):
    until(lambda: hook_line(page, repo) == f"{repo.full_name}: {text}", timeout, f"Settings to say {repo.full_name}: {text}",
          seen=lambda: notes(page))


@contextlib.contextmanager
def instant_deploys(gitberth, new_page, host):
    """Instant deploys turned on with the host name (domains must be on); off again afterwards, its confirm answered,
    whatever happened. Yields the Settings page it was turned on in, and what the snackbar said."""
    page = new_page()
    try:
        yield page, set_hook_host(gitberth, page, host)
    finally:
        restore = new_page()
        section = open_section(gitberth, restore)
        off = section.get_by_role("button", name="Turn off", exact=True)
        if off.count() > 0:
            off.click()
            gitberth.confirm(restore, "Turn off")
            gitberth.snackbar(restore, re.escape(TURNED_OFF))


@contextlib.contextmanager
def someone_elses_route(cloudflare, zone, host, path=None):
    """Another service's route through the tunnel, made in Cloudflare's dashboard: its rule for the host (and path) ahead
    of the rest, and the host's DNS record. Both are taken out again afterwards."""
    tunnel = cloudflare.state()["tunnel_id"]
    rule = {"hostname": host, "service": THEIR_SERVICE} | ({"path": path} if path else {})
    record = cloudflare.add_dns_record(zone, "CNAME", host, f"{tunnel}.cfargotunnel.com", proxied=True, comment="the CI service's")
    config = cloudflare.tunnel()["config"]
    config["ingress"] = [rule] + list(config.get("ingress") or [])
    cloudflare.wait_runs(cloudflare.set_tunnel_config(config)["version"])
    try:
        yield rule, record
    finally:
        config = cloudflare.tunnel()["config"]
        config["ingress"] = [r for r in config.get("ingress") or [] if r != rule]
        cloudflare.set_tunnel_config(config)
        with contextlib.suppress(Exception):
            cloudflare.delete_dns_record(record["id"])


# On plain http (as Gitberth's own pages are here) the browser has no clipboard API, so Gitberth copies through a hidden
# text box and document.execCommand("copy"). This records what each copy would have put on the clipboard, and lets it work.
COPY_SPY = """() => {
    window.__gbCopied = [];
    const real = document.execCommand.bind(document);
    document.execCommand = (command, ...rest) => {
        if (command !== 'copy') return real(command, ...rest);
        const boxes = document.querySelectorAll('textarea');
        window.__gbCopied.push(boxes.length ? boxes[boxes.length - 1].value : null);
        return true;
    };
}"""


# ---- the scenarios ----

def test_instant_deploys_need_domains_and_a_host_name_of_their_own(page, new_page, gitberth, cloudflare, uniq, baseline):
    """Without domains the section says what instant deploys need, and how often Gitberth looks meanwhile. With them it
    takes a host name: one that isn't a host name, or that has someone else's routes, is refused and nothing changes; one
    in no zone the token can see is routed, and the page says why it has no DNS record."""
    section = open_section(gitberth, page)
    text = flat(section.inner_text())
    assert ("GitHub can tell Gitberth about a push the moment it happens, through your Cloudflare tunnel. That needs domains turned on. "
            f"Until then Gitberth checks for new commits every {baseline['watch_seconds']} seconds.") in text, text
    assert section.get_by_role("link", name="domains", exact=True).get_attribute("href") == "settings#domains"
    assert section.locator("#hook-host").count() == 0, "the host name field shows while domains are off"

    zone = f"{uniq('wn')}.test"
    cloudflare.zone(zone)
    with domains_turned_on(gitberth, new_page, cloudflare):
        section = open_section(gitberth, page)
        box = section.locator("#hook-host")
        box.wait_for()
        assert flat(section.locator("label[for='hook-host']").inner_text()) == "Host name for GitHub's webhooks"
        assert box.get_attribute("placeholder") == "hooks.example.com"
        field = section.locator(".gb-field").filter(has=page.locator("#hook-host"))
        assert flat(field.locator(".gb-help").inner_text()) == ("Only its addresses /hooks/github and /hooks/deploy/… (deploy hooks) go to Gitberth, "
                                                                 "never behind Access; every other address on it answers 404. Use a name nothing else uses.")
        turn_on = section.get_by_role("button", name="Turn on", exact=True)
        assert turn_on.is_disabled(), "Turn on works without a host name"
        assert section.get_by_role("button", name="Turn off", exact=True).count() == 0
        assert section.locator("dl.gb-kv").count() == 0, "the webhook's values show before it's turned on"
        box.fill(f"hooks.{zone}")
        until(turn_on.is_enabled, 10, "Turn on to take the name typed")
        box.fill("")
        until(turn_on.is_disabled, 10, "Turn on to wait for a name again")

        # Not a host name: refused, saying why, and nothing is saved.
        said = set_hook_host(gitberth, page, "localhost")
        assert said == '"localhost" isn\'t a domain Gitberth can route. Use a name like app.example.com.', said
        assert gitberth.db.setting("github.webhookHost") is None

        # A name someone else's routes are on: refused, and the tunnel stays as it was.
        taken = f"taken.{zone}"
        with someone_elses_route(cloudflare, zone, taken) as (_, record):
            before = cloudflare.tunnel()
            said = set_hook_host(gitberth, page, taken)
            assert said == f"{taken} already has routes in your tunnel. Pick a name nothing uses yet, like hooks.example.com.", said
            assert gitberth.db.setting("github.webhookHost") is None
            assert cloudflare.tunnel() == before, "refusing the name changed the tunnel"
            assert [r["id"] for r in cloudflare.dns_records(taken)] == [record["id"]]

        # A name in no zone the token can see: routed, but it can't have its DNS record, and the page says so.
        lost = f"hooks.{uniq('nz')}.test"
        with instant_deploys(gitberth, new_page, lost) as (settings, said):
            assert said == f"Saved. {lost} needs attention; see below.", said
            problem = (f"{lost}: Gitberth's token can't see a Cloudflare zone for it in the tunnel's account. Add the domain there, or give "
                       "the token access to its zone.")
            assert problem in notes(settings), notes(settings)
            line = settings.locator(f"{SECTION} ul.gb-dnotes li", has_text="can't see a Cloudflare zone")
            assert "#ffd98a" in (line.get_attribute("style") or ""), "the problem isn't shown as one"
            assert cloudflare.rules_for(lost) == webhook_rules(lost), cloudflare.ingress()
            assert cloudflare.dns_records(lost) == []
        assert cloudflare.rules_for(lost) == [], cloudflare.ingress()
        assert gitberth.db.setting("github.webhookHost") is None
    assert not gitberth.errors(page), gitberth.errors(page)


def test_turning_on_routes_only_the_hook_path_and_adds_the_webhook_to_each_repo(page, new_page, gitberth, github, cloudflare, standins,
                                                                                apps, uniq, scenario, baseline):
    zone = f"{uniq('wh')}.test"
    host = f"hooks.{zone}"
    url = payload_url(host)
    cloudflare.zone(zone)
    tunnel = cloudflare.state()["tunnel_id"]
    repos = [github.create_repo(uniq("ia"), files={"marker.txt": "instant a"}), github.create_repo(uniq("ib"), files={"marker.txt": "instant b"})]
    # Another service's webhook on each, on another host of the zone, with the same path.
    foreign = {r.full_name: r.add_foreign_hook(f"https://ci.{zone}/hooks/github", events=("push", "pull_request"),
                                               secret="the-ci-services-own-secret") for r in repos}
    made = [apps.track(gitberth.create_git_app(page, r.url)) for r in repos]
    for app in made:
        gitberth.wait_deploy(app.id, timeout=420)

    with domains_turned_on(gitberth, new_page, cloudflare):
        mark = standins.seq()
        with instant_deploys(gitberth, new_page, host) as (settings, said):
            assert said == TURNED_ON.format(host), said

            # ---- Cloudflare: only the webhook's path goes to Gitberth (never behind Access), the rest of the host is a
            # 404 of the tunnel's own; and the host leads to the tunnel.
            assert cloudflare.rules_for(host) == webhook_rules(host), cloudflare.ingress()
            ingress = cloudflare.ingress()
            at = ingress.index(webhook_rules(host)[0])
            assert ingress[at + 1] == webhook_rules(host)[1] and ingress[-1].get("hostname") is None, ingress
            assert [(r["type"], r["content"], r["proxied"], r["comment"]) for r in cloudflare.dns_records(host)] == \
                [("CNAME", f"{tunnel}.cfargotunnel.com", True, "Added by Gitberth")], cloudflare.dns_records(host)

            # ---- Settings: what the change did, and the values to paste by hand.
            shown = notes(settings)
            for note in (f"Routed https://{host}/hooks/github and /hooks/deploy/… to Gitberth, for GitHub's webhooks and deploy hooks", f"Added a DNS record sending {host} to the tunnel"):
                assert note in shown, shown
            assert all(not (li.get_attribute("style") or "") for li in settings.locator(f"{SECTION} ul.gb-dnotes li").all()), \
                "a line shows as a problem"
            section = settings.locator(SECTION)
            assert section.locator("#hook-host").input_value() == host
            assert section.get_by_role("button", name="Save", exact=True).is_disabled(), "Save works for the name already saved"
            assert section.get_by_role("button", name="Turn on", exact=True).count() == 0
            assert section.get_by_role("button", name="Turn off", exact=True).is_visible()
            assert flat(kv(section, "Payload URL").locator("code").inner_text()) == url
            assert flat(kv(section, "Content type").inner_text()) == "application/json"
            assert flat(kv(section, "Events").inner_text()) == "push, pull_request, delete"
            assert ("Gitberth adds this webhook to every repo it deploys, which needs Webhooks: Read and write on the token. Without it, add it "
                    "by hand on each repo: Settings › Webhooks › Add webhook, with the values above and \"Let me select individual events\".") \
                in flat(section.inner_text()), section.inner_text()
            secret = shown_secret(settings)
            # The copy buttons copy the payload URL and the secret (the secret while it's hidden too).
            settings.evaluate(COPY_SPY)
            kv(section, "Payload URL").get_by_role("button", name="Copy the payload URL").click()
            gitberth.snackbar(settings, "Copied to the clipboard")
            copy = kv(section, "Secret").get_by_role("button", name="Copy the webhook secret")
            copy.click()
            # It says so for a moment (1.4 s).
            until(lambda: copy.get_attribute("title") == "Copied", 10, "the secret's copy button to say Copied", interval=0.1)
            assert settings.evaluate("() => window.__gbCopied") == [url, secret]

            # ---- GitHub: Gitberth's webhook on each deployed repo, with the payload URL, Settings' secret and its three
            # events, made through the API with the saved token; the other service's stays as it was.
            hooks = {r.full_name: wait_hook(r, host) for r in repos}
            for r in repos:
                hook = hooks[r.full_name]
                assert (hook["name"], hook["active"], sorted(hook["events"])) == ("web", True, sorted(EVENTS)), hook
                assert hook["config"] == {"url": url, "content_type": "json", "insecure_ssl": "0", "secret": secret}, hook["config"]
                assert hook_calls(r, mark) == [("GET", "hooks", 200), ("POST", "hooks", 201)], r.api_calls(mark)
                made_it = r.api_calls(mark, path=r"/hooks$", method="POST")[0]
                assert made_it["body"] == {"name": "web", "active": True, "events": EVENTS,
                                           "config": {"url": url, "content_type": "json", "secret": secret, "insecure_ssl": "0"}}, made_it["body"]
                assert made_it["token"] == baseline["token"]
                theirs = foreign[r.full_name]
                assert unchanged(r, theirs), f"the other service's webhook changed: {theirs} -> {r.hooks()}"
                assert not r.api_calls(scenario.seq, path=rf"/hooks/{theirs['id']}$"), "Gitberth called the other service's webhook"
                assert len(r.hooks()) == 2, r.hooks()
            # Gitberth records them, so they follow the host name when it changes.
            wait_kept(gitberth, {r.full_name: (hooks[r.full_name]["id"], url) for r in repos})
            for r in repos:
                wait_hook_line(settings, r, "Webhook added")

            # ---- Settings again, after a reload: the same, and the repos' webhooks.
            settings.reload()
            gitberth.wait_ready(settings)
            section = settings.locator(SECTION)
            section.locator("#hook-host").wait_for()
            assert section.locator("#hook-host").input_value() == host
            assert flat(kv(section, "Payload URL").locator("code").inner_text()) == url
            assert flat(kv(section, "Secret").locator("code").inner_text()) == DOTS
            for r in repos:
                assert hook_line(settings, r) == f"{r.full_name}: Webhook added", notes(settings)
            assert not gitberth.errors(settings), gitberth.errors(settings)


def test_githubs_ping_for_a_webhook_gitberth_adds_is_answered(request, page, new_page, gitberth, github, cloudflare, standins, engine, apps,
                                                             uniq, baseline):
    """GitHub pings a webhook the moment it's made, and shows the answer next to it (a green tick, or the webhook marked
    failing until a later delivery gets through); Gitberth's guide promises the tick. Gitberth adds its webhooks right
    after it routes the host name, so the ping must find that route running in the tunnel, not just written to it.

    Whether a ping sent too soon finds the route anyway depends on when cloudflared next takes a new configuration: the
    fake one looks every second (a real one has it pushed by Cloudflare's edge, which takes about as long). So it's
    checked on three turn-ons in a row, each with a host name of its own and so a webhook (and a ping) of its own. A
    Gitberth that waits for cloudflared passes every one; one that doesn't meets the fake's look in time only by chance
    (when it comes in the moment between Gitberth's write and its webhook), and almost never three times."""
    zone = f"{uniq('wg')}.test"
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("ig"), files={"marker.txt": "ping"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    gitberth.wait_deploy(app.id, timeout=420)

    with domains_turned_on(gitberth, new_page, cloudflare):
        for n in (1, 2, 3):
            host = f"hooks{n}.{zone}"
            mark = standins.seq()
            with instant_deploys(gitberth, new_page, host) as (_, said):
                assert said == TURNED_ON.format(host), said
                hook = wait_hook(repo, host)
                ping = delivery_through(repo, mark, hook["id"], "ping")
                puts = [c for c in standins.calls(mark, host="api.cloudflare.com", method="PUT") if c["path"].endswith("/configurations")]
                # The tunnel's configuration Gitberth wrote with the host's route in it.
                routed = next((c for c in puts if host in json.dumps(c.get("body") or {})), None)
                assert routed, f"Gitberth didn't write {host}'s route to the tunnel: {[c.get('body') for c in puts]}"
                made = repo.api_calls(mark, path=r"/hooks$", method="POST")[0]
                # When cloudflared began running the route; then GitHub's ping again (its Redeliver, or Gitberth pinging).
                cloudflare.wait_runs()
                ran = until(lambda: cloudflared_ran(engine, host, routed["at"]), 30, f"cloudflared's log to say it runs {host}'s route")
                again_mark = standins.seq()
                status, _, _ = github.rest("POST", f"/repos/{repo.full_name}/hooks/{hook['id']}/pings", baseline["token"])
                assert status == 204, status
                again = delivery_through(repo, again_mark, hook["id"], "ping")
                cf = (ping.get("route") or {}).get("cloudflared") or {}
                what = (f"turn-on {n} of 3 ({host}): GitHub's ping got {ping['status'] or ping['error']} from rule {cf.get('rule')} "
                        f"({cf.get('rule_host')} {cf.get('service')}) of the tunnel's version {cf.get('version')}, which cloudflared ran; "
                        f"Gitberth wrote its route to the tunnel {made['at'] - routed['at']:.2f}s before it made the webhook, the ping came "
                        f"{ping['at'] - made['at']:.2f}s after that, and cloudflared began running the route {ran - routed['at']:.2f}s after "
                        f"Gitberth wrote it; pinged again then, the webhook got {again['status'] or again['error']}")
                report(request, what)
                # The route itself works once cloudflared runs it ...
                assert again["status"] == 200 and reached_gitberth(again, host), f"{what}: {again}"
                # ... but GitHub's own ping, sent the moment the webhook was made, has to find it too.
                assert ping["status"] == 200 and reached_gitberth(ping, host), what
                assert ping["response_body"] == "", ping


def test_a_signed_push_deploys_at_once(request, page, new_page, gitberth, github, cloudflare, standins, apps, uniq, baseline):
    zone = f"{uniq('ip')}.test"
    host = f"hooks.{zone}"
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("ip"), files={"marker.txt": "push v1"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    lan = gitberth.lan_url(app.id)

    with domains_turned_on(gitberth, new_page, cloudflare), instant_deploys(gitberth, new_page, host) as (_, said):
        assert said == TURNED_ON.format(host), said
        hook = wait_hook(repo, host)
        cloudflare.wait_runs()

        # Pushed right after a look at the repo, so the next poll is a whole interval away.
        looked = look_at(repo, standins)
        pushed = repo.commit({"marker.txt": "push v2"}, message="Pushed for an instant deploy")
        record = delivery_through(repo, looked["seq"], hook["id"], "push")
        assert (record["status"], record["response_body"], record["error"]) == (202, "", None), record
        assert reached_gitberth(record, host), record["route"]
        assert record["payload"]["after"] == pushed["sha"]

        two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=300)
        # "Push on GitHub" (Webhook): only the watcher's pass that took the delivery's poke starts one so, never a poll.
        assert (two["CommitSha"], two["Trigger"]) == (pushed["sha"], "Webhook"), two
        # Timed from the delivery to the deploy's start, against the look before the push and the poll that look made due.
        at_once(request, repo, looked, record, epoch(two["StartedAt"]), baseline["watch_seconds"], "the deploy started")
        assert gitberth.wait_answers(lan, "/")["marker"] == "push v2"

    # ---- What the pages say: "Push on GitHub".
    gitberth.open(page, f"/apps/{app.id}/deployments/{two['Id']}")
    meta = page.locator(".gb-hero-meta")
    meta.wait_for()
    tags = [flat(t) for t in meta.locator(".gb-tag").all_inner_texts()]
    assert "Push on GitHub" in tags, tags
    assert pushed["sha"][:7] in meta.inner_text(), meta.inner_text()
    log = page.locator(".gb-terminal-body")
    until(lambda: "Deploy succeeded" in log.inner_text(), 30, "the deploy log to show the deploy succeeded", seen=log.inner_text)

    gitberth.open(page, f"/apps/{app.key}/activity")
    entry = page.locator("ol.gb-tl > li").filter(has_text=f"Deploy #{two['Id']} (Push on GitHub) succeeded")
    entry.wait_for()
    assert "Push on GitHub · took" in flat(entry.inner_text()), entry.inner_text()

    gitberth.open(page, f"/apps/{app.key}")
    hero = page.locator(".gb-hero")
    until(lambda: f"#{two['Id']}" in hero.inner_text() and "· push on github" in flat(hero.inner_text()), 30,
          "the app page's last deploy to be the push", seen=hero.inner_text)
    assert not gitberth.errors(page), gitberth.errors(page)


def test_the_webhook_host_answers_only_with_status_codes(new_page, gitberth, github, cloudflare, standins, uniq):
    """Endpoint and host checks through Cloudflare and the tunnel, as GitHub and visitors meet them. A repo Gitberth doesn't
    deploy is enough: the secret comes from Settings."""
    zone = f"{uniq('wa')}.test"
    host = f"hooks.{zone}"
    url = payload_url(host)
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("na"))
    sender = f"203.0.113.{2 + secrets.randbelow(250)}"

    with domains_turned_on(gitberth, new_page, cloudflare), instant_deploys(gitberth, new_page, host) as (settings, said):
        assert said == TURNED_ON.format(host), said
        cloudflare.wait_runs()
        secret = shown_secret(settings)
        mark = standins.seq()
        last = last_deployment_id(gitberth)

        def send(**kw):
            record = repo.send_delivery(url=url, cf_connecting_ip=sender, **kw)
            assert reached_gitberth(record, host), f"it didn't reach Gitberth by its route: {record}"
            return record

        def bare(record):
            """(status, body, whether it's HTML)."""
            return record["status"], record["response_body"], "html" in (header(record["response_headers"], "Content-Type") or "").lower()

        # Signed: ping 200; another event, or a repo Gitberth doesn't deploy, 204.
        assert bare(send(event="ping", secret=secret)) == (200, "", False)
        assert bare(send(event="push", secret=secret)) == (204, "", False)
        assert bare(send(event="star", secret=secret)) == (204, "", False)
        # A bad signature, or none: a bare 401.
        assert bare(send(event="push", secret=secret, signature="bad")) == (401, "", False)
        assert bare(send(event="push", secret=secret, signature="none")) == (401, "", False)
        # Anything but POST: a bare 405 that says POST, never a page.
        got = send(event="push", secret=secret, method="GET")
        assert bare(got) == (405, "", False) and header(got["response_headers"], "Allow") == "POST", got
        # Exactly 1 MB is taken; one byte more is refused.
        exact = send(event="ping", secret=secret, raw_body=json_of_size(MB))
        assert (exact["body_size"],) + bare(exact) == (MB, 200, "", False), {k: exact[k] for k in ("status", "body_size", "error", "response_body")}
        over = send(event="ping", secret=secret, raw_body=json_of_size(MB + 1))
        assert (over["body_size"],) + bare(over) == (MB + 1, 413, "", False), {k: over[k] for k in ("status", "body_size", "error", "response_body")}

        # A visitor: the webhook's path answers with a bare 405 too, and every other address of the host is the tunnel's
        # own 404, never one of Gitberth's pages.
        for method, path, status in (("GET", "/hooks/github", 405), ("PUT", "/hooks/github", 405), ("GET", "/", 404), ("GET", "/login", 404),
                                     ("GET", "/settings", 404), ("POST", "/", 404), ("GET", "/hooks/github/", 404), ("GET", "/hooks/githubx", 404),
                                     ("GET", "/_blazor", 404), ("GET", "/hooks", 404)):
            visit = cloudflare.visit(f"https://{host}{path}", method=method)
            cf = (visit.get("route") or {}).get("cloudflared") or {}
            assert (visit["status"], visit["body"]) == (status, ""), f"{method} {path}: {visit}"
            if status == 404:
                assert (cf.get("rule_host"), cf.get("service")) == (host, NOT_FOUND), f"{method} {path} wasn't the tunnel's 404: {visit['route']}"
            else:
                assert (cf.get("rule_path"), cf.get("service")) == (PATH_RULE, OWN_SERVICE), f"{method} {path}: {visit['route']}"

        # Nothing deployed, and Gitberth never looked at the repo it doesn't deploy.
        stays(lambda: last_deployment_id(gitberth) == last and not repo.ls_remote_attempts(mark), 5,
              "nothing deployed and the repo not looked at")


def test_bad_signatures_from_one_sender_are_throttled_but_its_signed_ones_get_through(new_page, gitberth, github, cloudflare, uniq):
    zone = f"{uniq('wt')}.test"
    host = f"hooks.{zone}"
    url = payload_url(host)
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("nt"))
    n = 2 + secrets.randbelow(250)
    sender, other = f"198.51.100.{n}", f"198.51.100.{n + 1}"

    with domains_turned_on(gitberth, new_page, cloudflare), instant_deploys(gitberth, new_page, host) as (settings, said):
        assert said == TURNED_ON.format(host), said
        cloudflare.wait_runs()
        secret = shown_secret(settings)

        def send(ip, **kw):
            record = repo.send_delivery(url=url, secret=secret, cf_connecting_ip=ip, **kw)
            assert reached_gitberth(record, host), f"it didn't reach Gitberth by its route: {record}"
            assert record["response_body"] == "", record
            return record["status"]

        # Ten bad signatures from one sender (as Cloudflare passes it on): each a 401.
        assert [send(sender, signature="bad") for _ in range(10)] == [401] * 10
        # Its next bad ones: 429, signed badly or not at all, and a CF-Connecting-IP of its own making doesn't make it
        # another sender (Cloudflare sets that header itself).
        assert send(sender, signature="bad") == 429
        assert send(sender, signature="none") == 429
        assert send(sender, signature="bad", headers={"CF-Connecting-IP": other}) == 429
        # Signed with the secret, from that same sender: answered as ever.
        assert send(sender, event="ping") == 200
        assert send(sender, event="push") == 204
        # Another sender's bad signature: still only a 401.
        assert send(other, signature="bad") == 401


def test_changing_the_host_name_moves_gitberths_webhook_and_route(page, new_page, gitberth, github, cloudflare, standins, apps, uniq,
                                                                  scenario):
    zone = f"{uniq('wm')}.test"
    first, second, other = f"hooks.{zone}", f"gh.{zone}", f"ci.{zone}"
    cloudflare.zone(zone)
    tunnel = cloudflare.state()["tunnel_id"]
    repo = github.create_repo(uniq("im"), files={"marker.txt": "move v1"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    # Another service's webhook and route, on another host of the zone, with the same path.
    foreign = repo.add_foreign_hook(payload_url(other), events=("push",), secret="the-ci-services-own-secret")

    with domains_turned_on(gitberth, new_page, cloudflare), someone_elses_route(cloudflare, zone, other, path=THEIR_PATH) as (their_rule, their_record):
        with instant_deploys(gitberth, new_page, first) as (settings, said):
            assert said == TURNED_ON.format(first), said
            hook = wait_hook(repo, first)
            secret = shown_secret(settings)
            wait_kept(gitberth, {repo.full_name: (hook["id"], payload_url(first))})

            mark = standins.seq()
            said = set_hook_host(gitberth, settings, second)
            assert said == TURNED_ON.format(second), said

            # ---- Cloudflare: the first name's route and record are gone, the second's in their place; theirs stay.
            assert cloudflare.rules_for(first) == [] and cloudflare.rules_for(second) == webhook_rules(second), cloudflare.ingress()
            assert their_rule in cloudflare.ingress(), cloudflare.ingress()
            assert cloudflare.dns_records(first) == [], cloudflare.dns_records(first)
            assert [(r["type"], r["content"], r["proxied"], r["comment"]) for r in cloudflare.dns_records(second)] == \
                [("CNAME", f"{tunnel}.cfargotunnel.com", True, "Added by Gitberth")], cloudflare.dns_records(second)
            assert [r["id"] for r in cloudflare.dns_records(other)] == [their_record["id"]]
            assert first in (gitberth.db.setting("github.webhookHostsBefore") or "").split("\n")
            shown = notes(settings)
            for note in (f"Removed the route GitHub's webhook and deploy hooks took through {first}", f"Routed https://{second}/hooks/github and /hooks/deploy/… to Gitberth, for GitHub's webhooks and deploy hooks",
                         f"Added a DNS record sending {second} to the tunnel", f"Removed the DNS record Gitberth added for {first}"):
                assert note in shown, shown

            # ---- GitHub: the webhook Gitberth recorded moved (PATCHed), with its secret and events; no second one.
            moved = until(lambda: hook_with(repo, payload_url(second)), 30, "the webhook to move to the new name",
                          seen=lambda: [(h["id"], h["config"].get("url")) for h in repo.hooks()])
            assert moved["id"] == hook["id"], f"Gitberth made another webhook rather than move its own: {repo.hooks()}"
            assert moved["config"] == {"url": payload_url(second), "content_type": "json", "insecure_ssl": "0", "secret": secret}, moved["config"]
            assert sorted(moved["events"]) == sorted(EVENTS) and moved["active"] is True, moved
            assert hook_calls(repo, mark) == [("GET", "hooks", 200), ("PATCH", f"hooks/{hook['id']}", 200)], repo.api_calls(mark)
            assert [h["id"] for h in repo.hooks() if h.get("made_by") == "api"] == [hook["id"]], repo.hooks()
            assert unchanged(repo, foreign) and not repo.api_calls(scenario.seq, path=rf"/hooks/{foreign['id']}$"), repo.hooks()
            wait_kept(gitberth, {repo.full_name: (hook["id"], payload_url(second))})
            wait_hook_line(settings, repo, "Webhook set up")
            assert flat(kv(settings.locator(SECTION), "Payload URL").locator("code").inner_text()) == payload_url(second)

            # ---- A push goes to the new name and deploys at once; theirs still answer for their host.
            cloudflare.wait_runs()
            looked = look_at(repo, standins)
            pushed = repo.commit({"marker.txt": "move v2"}, message="Pushed after the move")
            record = delivery_through(repo, looked["seq"], hook["id"], "push")
            assert (record["url"], record["status"]) == (payload_url(second), 202) and reached_gitberth(record, second), record
            two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=300)
            assert (two["CommitSha"], two["Trigger"]) == (pushed["sha"], "Webhook"), two
            theirs = delivery_through(repo, looked["seq"], foreign["id"], "push")
            cf = (theirs.get("route") or {}).get("cloudflared") or {}
            assert (theirs["status"], cf.get("rule_host"), cf.get("rule_path"), cf.get("service")) == (502, other, THEIR_PATH, THEIR_SERVICE), theirs
            # The first name leads nowhere now.
            gone = repo.send_delivery(url=payload_url(first), secret=secret, event="ping")
            assert gone["status"] is None and "resolve" in (gone["error"] or ""), gone
        assert their_rule in cloudflare.ingress() and cloudflare.rules_for(second) == [], cloudflare.ingress()


def test_turning_off_takes_the_route_away_and_leaves_the_webhooks(page, new_page, gitberth, github, cloudflare, standins, apps, uniq, baseline):
    zone = f"{uniq('wo')}.test"
    host, other = f"hooks.{zone}", f"ci.{zone}"
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("io"), files={"marker.txt": "off v1"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    foreign = repo.add_foreign_hook(payload_url(other), events=("push",), secret="the-ci-services-own-secret")

    with domains_turned_on(gitberth, new_page, cloudflare), someone_elses_route(cloudflare, zone, other, path=THEIR_PATH) as (their_rule, their_record):
        with instant_deploys(gitberth, new_page, host) as (settings, said):
            assert said == TURNED_ON.format(host), said
            hook = wait_hook(repo, host)
            wait_kept(gitberth, {repo.full_name: (hook["id"], payload_url(host))})
            cloudflare.wait_runs()

            # Turn off asks first, saying what happens; Cancel changes nothing.
            section = open_section(gitberth, settings)
            section.get_by_role("button", name="Turn off", exact=True).click()
            dialog = settings.locator(".mud-dialog")
            dialog.wait_for()
            asked = flat(dialog.inner_text())
            assert "Turn off instant deploys?" in asked, asked
            assert (f"Gitberth takes {host}'s route out of your tunnel, and checks for new commits every {baseline['watch_seconds']} seconds again. "
                    "The webhooks on your repos stay; remove them on GitHub if you like.") in asked, asked
            dialog.get_by_role("button", name="Cancel", exact=True).click()
            dialog.wait_for(state="detached")
            stays(lambda: gitberth.db.setting("github.webhookHost") == host and cloudflare.rules_for(host) == webhook_rules(host), 2,
                  "Cancel to leave instant deploys on")

            # Turned off.
            mark = standins.seq()
            section.get_by_role("button", name="Turn off", exact=True).click()
            gitberth.confirm(settings, "Turn off")
            said = flat(gitberth.snackbar(settings, re.escape(TURNED_OFF)))
            assert said == TURNED_OFF, said

            # ---- Cloudflare: the route and the record Gitberth added are gone; theirs stay.
            assert cloudflare.rules_for(host) == [] and their_rule in cloudflare.ingress(), cloudflare.ingress()
            assert cloudflare.dns_records(host) == [], cloudflare.dns_records(host)
            assert [r["id"] for r in cloudflare.dns_records(other)] == [their_record["id"]]
            assert gitberth.db.setting("github.webhookHost") is None
            # ---- The page says so, and offers to turn it on again.
            shown = notes(settings)
            for note in (f"Removed the route GitHub's webhook and deploy hooks took through {host}", f"Removed the DNS record Gitberth added for {host}"):
                assert note in shown, shown
            section = settings.locator(SECTION)
            assert section.locator("#hook-host").input_value() == ""
            assert section.get_by_role("button", name="Turn on", exact=True).is_visible()
            assert section.get_by_role("button", name="Turn off", exact=True).count() == 0 and section.locator("dl.gb-kv").count() == 0
            # ---- GitHub: the webhooks stay as they were, and Gitberth no longer counts its own as its own.
            wait_kept(gitberth, {})
            assert unchanged(repo, hook) and unchanged(repo, foreign), repo.hooks()
            assert hook_calls(repo, mark) == [], repo.api_calls(mark)

            # ---- A push: GitHub's delivery to the old name goes nowhere, and the poll deploys it.
            cloudflare.wait_runs()
            mark = standins.seq()
            pushed = repo.commit({"marker.txt": "off v2"}, message="Pushed with instant deploys off")
            lost = delivery_through(repo, mark, hook["id"], "push")
            assert lost["status"] is None and "resolve" in (lost["error"] or ""), lost
            theirs = delivery_through(repo, mark, foreign["id"], "push")
            assert (theirs["status"], ((theirs.get("route") or {}).get("cloudflared") or {}).get("rule_host")) == (502, other), theirs
            two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=120)
            assert (two["CommitSha"], two["Trigger"]) == (pushed["sha"], "Poll"), two
    assert not gitberth.errors(settings), gitberth.errors(settings)


def test_a_pull_request_delivery_makes_its_preview_at_once(request, page, new_page, gitberth, github, cloudflare, standins, apps, uniq,
                                                           baseline):
    zone = f"{uniq('wp')}.test"
    host = f"hooks.{zone}"
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("ir"), files={"marker.txt": "production"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    gitberth.wait_deploy(app.id, timeout=420)
    before = standins.seq()
    assert "Saved" in gitberth.set_previews(page, app, mode="Pull requests")
    repo.wait_pulls_listed(since=before, timeout=90)
    repo.create_branch("feature")
    sha = repo.commit({"marker.txt": "feature"}, branch="feature", message="A feature to preview")["sha"]

    with domains_turned_on(gitberth, new_page, cloudflare), instant_deploys(gitberth, new_page, host) as (_, said):
        assert said == TURNED_ON.format(host), said
        hook = wait_hook(repo, host)
        cloudflare.wait_runs()

        looked = look_at(repo, standins)
        number = repo.open_pull("feature", title="Preview me at once")["number"]
        record = delivery_through(repo, looked["seq"], hook["id"], "pull_request")
        assert (record["action"], record["status"], record["response_body"]) == ("opened", 202, ""), record
        assert reached_gitberth(record, host), record["route"]
        preview = gitberth.wait_preview(app.id, pr=number, timeout=30)
        assert preview["Slug"] == f"{app.slug}-pr-{number}", preview
        at_once(request, repo, looked, record, epoch(preview["PreviewCreatedAt"]), baseline["watch_seconds"], "the preview was made")
        listed = until(lambda: repo.api_calls(record["seq"], path=r"/pulls$", method="GET"), 20, "Gitberth to ask GitHub for the pull requests")[0]
        assert listed["at"] - record["at"] < 2, f"the pull requests were asked for {listed['at'] - record['at']:.2f}s after the delivery"

        deploy = gitberth.wait_deploy(preview["Id"], timeout=420)
        assert (deploy["CommitSha"], deploy["Trigger"]) == (sha, "Webhook"), deploy
        started = epoch(deploy["StartedAt"])
        report(request, f"the preview's deploy started {started - record['at']:.2f}s after the delivery")
        assert started - record["at"] < 5 and started < looked["at"] + baseline["watch_seconds"], \
            f"its deploy started {started - record['at']:.2f}s after the delivery, the poll was due {looked['at'] + baseline['watch_seconds'] - record['at']:.2f}s after it"

    gitberth.open(page, f"/apps/{preview['Id']}/deployments/{deploy['Id']}")
    meta = page.locator(".gb-hero-meta")
    meta.wait_for()
    tags = [flat(t) for t in meta.locator(".gb-tag").all_inner_texts()]
    assert "Push on GitHub" in tags, tags
    assert not gitberth.errors(page), gitberth.errors(page)


def test_a_delete_delivery_removes_the_branch_preview_at_once(request, page, new_page, gitberth, github, cloudflare, standins, engine, apps,
                                                              uniq, baseline):
    zone = f"{uniq('wb')}.test"
    host = f"hooks.{zone}"
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("iq"), files={"marker.txt": "production"})
    repo.create_branch("doomed")
    app = apps.track(gitberth.create_git_app(page, repo.url))
    gitberth.wait_deploy(app.id, timeout=420)
    before = standins.seq()
    assert "Saved" in gitberth.set_previews(page, app, mode="Pull requests and branches")
    listed = repo.wait_pulls_listed(since=before, timeout=90)
    # Two more looks, so the branch is known as it was before previews: only a new commit on it makes its preview.
    repo.wait_watcher_passes(2, since=listed["seq"], timeout=90)

    with domains_turned_on(gitberth, new_page, cloudflare), instant_deploys(gitberth, new_page, host) as (_, said):
        assert said == TURNED_ON.format(host), said
        hook = wait_hook(repo, host)
        cloudflare.wait_runs()

        # ---- A push to the branch: its preview, at once.
        looked = look_at(repo, standins)
        sha = repo.commit({"marker.txt": "doomed work"}, branch="doomed", message="Work on a branch that goes")["sha"]
        record = delivery_through(repo, looked["seq"], hook["id"], "push")
        assert (record["status"], record["payload"]["ref"]) == (202, "refs/heads/doomed"), record
        preview = gitberth.wait_preview(app.id, branch="doomed", timeout=30)
        at_once(request, repo, looked, record, epoch(preview["PreviewCreatedAt"]), baseline["watch_seconds"], "the preview was made")
        deploy = gitberth.wait_deploy(preview["Id"], timeout=420)
        assert (deploy["CommitSha"], deploy["Trigger"]) == (sha, "Webhook"), deploy
        container = f"gb-{preview['Slug']}"
        assert engine.running(container)

        # ---- The branch deleted: the preview goes, at once. GitHub sends delete, and a push saying the branch is gone.
        looked = look_at(repo, standins)
        sent = repo.delete_branch("doomed")["deliveries"]
        assert sorted((d["event"], d["status"]) for d in sent if d["hook_id"] == hook["id"]) == [("delete", 202), ("push", 202)], sent
        record = delivery_through(repo, looked["seq"], hook["id"], "delete")
        assert (record["payload"]["ref"], record["payload"]["ref_type"]) == ("doomed", "branch") and reached_gitberth(record, host), record
        gone = until(lambda: time.time() if not engine.exists(container) else None, 30, f"the preview's container {container} to go",
                     interval=0.2, seen=lambda: (engine.inspect(container) or {}).get("State"))
        # Timed from the first of the two to reach Gitberth: either wakes the watcher.
        earliest = min(record, delivery_through(repo, looked["seq"], hook["id"], "push"), key=lambda d: d["seq"])
        at_once(request, repo, looked, earliest, gone, baseline["watch_seconds"], "its container was gone")
        gitberth.wait_no_preview(app.id, branch="doomed", timeout=30)
        entry = until(lambda: next((a for a in gitberth.db.activity(app.id) if a["Text"].startswith("Preview doomed removed")), None), 30,
                      "the parent's Activity to say the preview went", seen=lambda: [a["Text"] for a in gitberth.db.activity(app.id)][-5:])
        assert entry["Text"] == "Preview doomed removed: the branch was deleted.", entry


def test_a_webhook_on_an_earlier_host_name_that_gitberth_never_recorded_is_left_alone(page, new_page, gitberth, github, cloudflare, standins,
                                                                                    apps, uniq, baseline):
    zone = f"{uniq('we')}.test"
    first, second = f"hooks.{zone}", f"gh.{zone}"
    cloudflare.zone(zone)
    one = github.create_repo(uniq("ea"), files={"marker.txt": "earlier a"})
    two = github.create_repo(uniq("eb"), files={"marker.txt": "earlier b"})
    app_one = apps.track(gitberth.create_git_app(page, one.url))
    deployed = gitberth.wait_deploy(app_one.id, timeout=420)

    with domains_turned_on(gitberth, new_page, cloudflare):
        # This Gitberth's webhooks were on the first name once: it added one to the repo it deployed, and recorded it.
        with instant_deploys(gitberth, new_page, first) as (_, said):
            assert said == TURNED_ON.format(first), said
            old = wait_hook(one, first)
            wait_kept(gitberth, {one.full_name: (old["id"], payload_url(first))})
        # Turned off: the webhook stays on GitHub, and Gitberth forgets it was its own; the name is an earlier one now.
        wait_kept(gitberth, {})
        assert unchanged(one, old)
        assert first in (gitberth.db.setting("github.webhookHostsBefore") or "").split("\n")

        # Meanwhile another Gitberth takes that name over. On the first repo its webhook takes the old one's place
        # (deleted on GitHub, then added by hand with the same payload URL); it put one on another repo too, which this
        # Gitberth starts deploying only now.
        status, _, _ = github.rest("DELETE", f"/repos/{one.full_name}/hooks/{old['id']}", baseline["token"])
        assert status == 204
        theirs = {r.full_name: r.add_pasted_hook(payload_url(first), secret="another-gitberths-secret-" + secrets.token_hex(8)) for r in (one, two)}
        app_two = apps.track(gitberth.create_git_app(page, two.url))
        gitberth.wait_deploy(app_two.id, timeout=420)

        # On again, with another name: Gitberth adds its own to each repo, and leaves the other Gitberth's as they are.
        mark = standins.seq()
        with instant_deploys(gitberth, new_page, second) as (settings, said):
            assert said == TURNED_ON.format(second), said
            ours = {r.full_name: wait_hook(r, second) for r in (one, two)}
            for r in (one, two):
                other = theirs[r.full_name]
                assert ours[r.full_name]["id"] != other["id"]
                assert hook_calls(r, mark) == [("GET", "hooks", 200), ("POST", "hooks", 201)], r.api_calls(mark)
                assert unchanged(r, other), f"another Gitberth's webhook on {r.full_name} changed: {other} -> {r.hooks()}"
                assert not r.api_calls(mark, path=rf"/hooks/{other['id']}$")
            wait_kept(gitberth, {r.full_name: (ours[r.full_name]["id"], payload_url(second)) for r in (one, two)})
            for r in (one, two):
                wait_hook_line(settings, r, "Webhook added")

            # A push: the other Gitberth's webhook goes to the old name (which leads nowhere here now); Gitberth's own
            # reaches it, and it deploys at once.
            cloudflare.wait_runs()
            looked = look_at(one, standins)
            pushed = one.commit({"marker.txt": "earlier a, again"}, message="Pushed past another Gitberth's webhook")
            mine = delivery_through(one, looked["seq"], ours[one.full_name]["id"], "push")
            assert mine["status"] == 202 and reached_gitberth(mine, second), mine
            elsewhere = delivery_through(one, looked["seq"], theirs[one.full_name]["id"], "push")
            assert elsewhere["status"] is None and "resolve" in (elsewhere["error"] or ""), elsewhere
            again = gitberth.wait_deploy(app_one.id, after=deployed["Id"], timeout=300)
            assert (again["CommitSha"], again["Trigger"]) == (pushed["sha"], "Webhook"), again


def test_a_repo_no_app_deploys_any_more_is_forgotten_and_its_webhook_never_moved(page, new_page, gitberth, github, cloudflare, standins,
                                                                                apps, uniq, scenario):
    zone = f"{uniq('wf')}.test"
    first, second, third = f"hooks.{zone}", f"gh.{zone}", f"wh.{zone}"
    cloudflare.zone(zone)
    kept_repo = github.create_repo(uniq("fa"), files={"marker.txt": "kept"})
    left_repo = github.create_repo(uniq("fb"), files={"marker.txt": "left"})
    app_kept = apps.track(gitberth.create_git_app(page, kept_repo.url))
    app_left = apps.track(gitberth.create_git_app(page, left_repo.url))
    gitberth.wait_deploy(app_kept.id, timeout=420)
    gitberth.wait_deploy(app_left.id, timeout=420)

    with domains_turned_on(gitberth, new_page, cloudflare), instant_deploys(gitberth, new_page, first) as (settings, said):
        assert said == TURNED_ON.format(first), said
        ours = {r.full_name: wait_hook(r, first) for r in (kept_repo, left_repo)}
        wait_kept(gitberth, {r.full_name: (ours[r.full_name]["id"], payload_url(first)) for r in (kept_repo, left_repo)})
        left = ours[left_repo.full_name]

        # No app deploys the second repo any more.
        gitberth.delete_app(page, app_left)
        gitberth.wait_app(app_left.slug, gone=True)

        # The name changes: the deployed repo's webhook follows it; the other repo's is forgotten and stays where it was.
        mark = standins.seq()
        assert set_hook_host(gitberth, settings, second) == TURNED_ON.format(second)
        moved = until(lambda: hook_with(kept_repo, payload_url(second)), 30, "the deployed repo's webhook to move")
        assert moved["id"] == ours[kept_repo.full_name]["id"], kept_repo.hooks()
        wait_kept(gitberth, {kept_repo.full_name: (moved["id"], payload_url(second))})
        assert hook_calls(left_repo, mark) == [], f"Gitberth touched the webhooks of a repo no app deploys: {left_repo.api_calls(mark)}"
        assert unchanged(left_repo, left), left_repo.hooks()

        # Deployed again, and the name changes once more: that repo gets a webhook of its own, and the old one stays on
        # the first name, never moved.
        app_again = apps.track(gitberth.create_git_app(page, left_repo.url))
        gitberth.wait_deploy(app_again.id, timeout=420)
        mark = standins.seq()
        assert set_hook_host(gitberth, settings, third) == TURNED_ON.format(third)
        fresh = wait_hook(left_repo, third)
        assert fresh["id"] != left["id"], left_repo.hooks()
        assert hook_calls(left_repo, mark) == [("GET", "hooks", 200), ("POST", "hooks", 201)], left_repo.api_calls(mark)
        assert unchanged(left_repo, left), f"the forgotten webhook changed: {left} -> {left_repo.hooks()}"
        assert not left_repo.api_calls(scenario.seq, path=rf"/hooks/{left['id']}$"), "Gitberth called the forgotten webhook"
        moved = until(lambda: hook_with(kept_repo, payload_url(third)), 30, "the deployed repo's webhook to move again")
        assert moved["id"] == ours[kept_repo.full_name]["id"], kept_repo.hooks()
        wait_kept(gitberth, {kept_repo.full_name: (moved["id"], payload_url(third)), left_repo.full_name: (fresh["id"], payload_url(third))})
        wait_hook_line(settings, left_repo, "Webhook added")
        wait_hook_line(settings, kept_repo, "Webhook set up")


def test_a_host_name_cloudflares_free_certificate_doesnt_cover_is_said(new_page, gitberth, github, cloudflare, uniq):
    """GitHub checks the webhook host's certificate (Gitberth's webhooks say insecure_ssl "0"), and Cloudflare's free one
    covers the zone and one level below it. A name two levels down never gets a delivery through, so Settings says so,
    as the Domains card does for an app's domain (or refuses the name), rather than that the webhooks reach Gitberth."""
    zone = f"{uniq('wd')}.test"
    deep = f"hooks.gitberth.{zone}"
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("nd"))

    with domains_turned_on(gitberth, new_page, cloudflare), instant_deploys(gitberth, new_page, deep) as (settings, said):
        told = flat(" ".join([said] + notes(settings)))
        met = "(not routed)"
        if gitberth.db.setting("github.webhookHost") == deep:
            # What GitHub meets there: its deliveries never get through.
            cloudflare.wait_runs()
            secret = shown_secret(settings)
            ping = repo.send_delivery(url=payload_url(deep), secret=secret, event="ping")
            assert ping["status"] is None and "SSL certificate problem" in (ping["error"] or ""), ping
            met = ping["error"]
        assert said != TURNED_ON.format(deep) and "certificate" in told.lower(), \
            f"Settings took {deep} without a word about its certificate, though GitHub's ping there fails ({met}). Settings said: {told}"


def paste_hook(repo, url, secret, events, content_type):
    """Gitberth's webhook added by hand on GitHub (Settings › Webhooks › Add webhook) from what Gitberth's Settings shows."""
    return repo.s.api("POST", f"/github/repos/{repo.full_name}/hooks", {"url": url, "events": list(events), "secret": secret, "kind": "pasted",
                                                                       "content_type": content_type})


def test_a_webhook_added_by_hand_is_taken_up_and_recorded(request, page, new_page, gitberth, github, cloudflare, standins, apps, uniq,
                                                          baseline):
    """Someone added Gitberth's webhook by hand, with only Pushes ticked and GitHub's own default content type (form data).
    When the keeper next looks (here: the token saved again in Settings › GitHub access) it takes that webhook up rather
    than add a second: Gitberth's values set on it, and its ID recorded. Its pushes then deploy at once."""
    zone = f"{uniq('wk')}.test"
    host = f"hooks.{zone}"
    url = payload_url(host)
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("ih"), files={"marker.txt": "by hand v1"})

    with domains_turned_on(gitberth, new_page, cloudflare), instant_deploys(gitberth, new_page, host) as (settings, said):
        assert said == TURNED_ON.format(host), said
        secret = shown_secret(settings)
        # Deployed after the keeper looked, so it won't look at the repo again for 10 minutes by itself.
        app = apps.track(gitberth.create_git_app(page, repo.url))
        one = gitberth.wait_deploy(app.id, timeout=420)
        pasted = paste_hook(repo, url, secret, ["push"], "form")

        mark = standins.seq()
        assert gitberth.save_github_token(new_page(), baseline["token"]).startswith("Token saved")
        until(lambda: hook_calls(repo, mark) == [("GET", "hooks", 200), ("PATCH", f"hooks/{pasted['id']}", 200)], 30,
              "Gitberth to take up the webhook added by hand", seen=lambda: hook_calls(repo, mark))
        taken = next(h for h in repo.hooks() if h["id"] == pasted["id"])
        assert taken["config"] == {"url": url, "content_type": "json", "insecure_ssl": "0", "secret": secret}, taken["config"]
        assert sorted(taken["events"]) == sorted(EVENTS) and taken["active"] is True, taken
        assert [h["id"] for h in repo.hooks()] == [pasted["id"]], repo.hooks()
        wait_kept(gitberth, {repo.full_name: (pasted["id"], url)})
        open_section(gitberth, settings)
        wait_hook_line(settings, repo, "Webhook set up")

        # Its pushes come as JSON now, and deploy at once.
        cloudflare.wait_runs()
        looked = look_at(repo, standins)
        pushed = repo.commit({"marker.txt": "by hand v2"}, message="Pushed through the webhook taken up")
        record = delivery_through(repo, looked["seq"], pasted["id"], "push")
        assert (record["headers"]["Content-Type"], record["status"]) == ("application/json", 202) and reached_gitberth(record, host), record
        two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=300)
        assert (two["CommitSha"], two["Trigger"]) == (pushed["sha"], "Webhook"), two
        at_once(request, repo, looked, record, epoch(two["StartedAt"]), baseline["watch_seconds"], "the deploy started")


def test_a_push_through_a_webhook_added_by_hand_as_form_data_deploys_at_once(request, page, new_page, gitberth, github, cloudflare, standins,
                                                                             apps, uniq, baseline):
    """Gitberth's webhook added by hand on GitHub from what Settings shows, but with GitHub's own default content type:
    form data, the JSON in a payload= field, signed as it's sent. A push through it is a signed push to a repo Gitberth
    deploys, so it deploys at once (202), as one sent as JSON does, rather than being taken (204) and left to the poll."""
    zone = f"{uniq('wj')}.test"
    host = f"hooks.{zone}"
    url = payload_url(host)
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("ij"), files={"marker.txt": "form v1"})

    with domains_turned_on(gitberth, new_page, cloudflare), instant_deploys(gitberth, new_page, host) as (settings, said):
        assert said == TURNED_ON.format(host), said
        secret = shown_secret(settings)
        # Deployed after the keeper looked, so the webhook stays as it was added for the next 10 minutes.
        app = apps.track(gitberth.create_git_app(page, repo.url))
        one = gitberth.wait_deploy(app.id, timeout=420)
        pasted = paste_hook(repo, url, secret, EVENTS, "form")
        cloudflare.wait_runs()

        looked = look_at(repo, standins)
        pushed = repo.commit({"marker.txt": "form v2"}, message="Pushed through a webhook sending form data")
        record = delivery_through(repo, looked["seq"], pasted["id"], "push")
        assert record["headers"]["Content-Type"] == "application/x-www-form-urlencoded" and reached_gitberth(record, host), record
        assert record["error"] is None and record["response_body"] == "", record
        two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=300)
        assert two["CommitSha"] == pushed["sha"], two
        what = (f"Gitberth answered the signed push {record['status']}, and '{two['Trigger']}' started its deploy "
                f"{epoch(two['StartedAt']) - record['at']:.1f}s after the delivery (the poll after the push was due "
                f"{looked['at'] + baseline['watch_seconds'] - record['at']:.1f}s after it)")
        report(request, what)
        assert (record["status"], two["Trigger"]) == (202, "Webhook"), what
        at_once(request, repo, looked, record, epoch(two["StartedAt"]), baseline["watch_seconds"], "the deploy started")
        assert [h["id"] for h in repo.hooks()] == [pasted["id"]], repo.hooks()


@pytest.mark.slow
def test_a_repo_deployed_later_gets_the_webhook_at_the_keepers_own_next_look(request, page, new_page, gitberth, github, cloudflare, standins,
                                                                             apps, uniq, baseline):
    """Nothing in Settings changes after the app is made: the keeper's own look, every 10 minutes, adds the webhook to a
    repo Gitberth starts deploying after instant deploys were turned on. This waits for it, up to 10 minutes."""
    zone = f"{uniq('wl')}.test"
    host = f"hooks.{zone}"
    cloudflare.zone(zone)
    repo = github.create_repo(uniq("il"), files={"marker.txt": "later v1"})

    with domains_turned_on(gitberth, new_page, cloudflare), instant_deploys(gitberth, new_page, host) as (settings, said):
        assert said == TURNED_ON.format(host), said
        turned_on = time.time()
        mark = standins.seq()
        app = apps.track(gitberth.create_git_app(page, repo.url))
        one = gitberth.wait_deploy(app.id, timeout=420)
        hook = until(lambda: hook_with(repo, payload_url(host)), max(30, 11.5 * 60 - (time.time() - turned_on)),
                     "the keeper's own look to add the webhook", interval=5, seen=lambda: repo.hooks())
        made = repo.api_calls(mark, path=r"/hooks$", method="POST")[0]
        waited = made["at"] - turned_on
        report(request, f"the keeper added the webhook {waited:.0f}s after instant deploys were turned on "
                        f"({made['at'] - epoch(one['QueuedAt']):.0f}s after the app was made)")
        assert waited <= 11 * 60, f"the webhook came {waited:.0f}s after instant deploys were turned on"
        open_section(gitberth, settings)
        assert hook["config"]["secret"] == shown_secret(settings) and sorted(hook["events"]) == sorted(EVENTS), hook
        wait_kept(gitberth, {repo.full_name: (hook["id"], payload_url(host))})
        # GitHub's ping for it is answered: the route was in place long before.
        ping = delivery_through(repo, mark, hook["id"], "ping")
        assert ping["status"] == 200 and reached_gitberth(ping, host), ping

        # And a push deploys at once.
        looked = look_at(repo, standins)
        pushed = repo.commit({"marker.txt": "later v2"}, message="Pushed once the keeper added the webhook")
        record = delivery_through(repo, looked["seq"], hook["id"], "push")
        assert record["status"] == 202, record
        two = gitberth.wait_deploy(app.id, after=one["Id"], timeout=300)
        assert (two["CommitSha"], two["Trigger"]) == (pushed["sha"], "Webhook"), two
        at_once(request, repo, looked, record, epoch(two["StartedAt"]), baseline["watch_seconds"], "the deploy started")
