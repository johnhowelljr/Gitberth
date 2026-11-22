"""Scenario 39: New app's Docker Hub search and the tag picker, Settings › Image's Choose tag…, and the agents' tools.

Against the Docker Hub stand-in (hub.docker.com's search and tags API, index.docker.io's basic search) and the registry
registry.e2e.test (design 8.3 with D3): Gitberth never reaches the real Docker Hub here. Docker Hub's registry itself
(registry-1.docker.io) isn't the stand-in's, so no scenario goes on past the picker with a Docker Hub image that isn't on
the rig's engine; the registry's image goes on to Configure, its ports read from the stand-in.
- The search: badges (Official, Verified Publisher, Sponsored OSS), stars, pulls and Updated; text typed fast asks
  Docker Hub at most twice (debounced, no token, Gitberth's User-Agent); an image with no build for this host says so;
  ↓ and Enter pick a row.
- The tag picker on nginx: newest stable first by version (stable's rebuild, pushed last, is third), Recommended with
  its follow chips, the field preselected one part shorter (1.31); variants; pre-releases only with the switch; a tag
  with no build for this host marked; Windows and signature tags never listed; Docker Hub's two pages read once each.
- An app made by the API's create_app from redis:8-alpine (on the rig's engine, pulled once per rig volume, as group
  28's cache is; one app for the next two scenarios): Settings › Image › Choose tag… opens on its variant with the row it
  runs marked Running now; following 8.2-alpine and applying recreates it on it.
- Agents: search_images and list_image_tags with a Read token, which is refused update_app_settings; with an Admin
  token, update_app_settings' image moves the app to its other tag.
- When Docker Hub won't answer, typing still works: its basic search after a 500, the wait line with its countdown
  after a 429 (and no call while it holds, then a search by itself), the can't-be-reached line when both searches drop
  the connection; a reference typed for registry.e2e.test lists its tags from tags/list across its pages (inferred
  aliases, no dates) and goes on to Configure with the port its config declares.

That last scenario runs last, and waits out what it made Gitberth remember (Docker Hub left alone for the rig's hubWait,
a minute as installed) before it ends, so no scenario after it finds Docker Hub held.
"""
import hashlib
import json
import re
import secrets
import time

import pytest

from e2e import config
from e2e.engine import docker
from e2e.gitberth import App
from e2e.wait import until
from test_29_mcp import make_token, tool

HUB = "hub.docker.com"
REDIS = "redis:8-alpine"
REDIS_NEXT = "redis:8.2-alpine"
REDIS_SEARCH = {"badge": "official", "stars": 13302, "pulls": 1_009_000_000, "architectures": ["amd64", "arm64", "arm"], "updated_days": 3,
                "description": "Redis is the world's fastest data platform for caching, vector search, and NoSQL databases."}


def flat(text):
    return " ".join((text or "").split())


def new_app(gitberth, page):
    """New app, on its Registry image source (the default)."""
    gitberth.open(page, "/apps/new")
    box = page.locator("#gb-reg-search")
    box.wait_for()
    return box


def results(page):
    return page.locator("#gb-reg-results [role=option]")


def result_names(page):
    return [t.strip() for t in results(page).locator(".gb-resrow-t").all_inner_texts()]


def wait_results(page, first, count=None, timeout=30):
    """The search's rows, once the first is `first` (and there are `count` of them)."""
    return until(lambda: (lambda names: names if names and names[0] == first and (count is None or len(names) == count) else None)(result_names(page)),
                 timeout, f"the search to list {first} first", seen=lambda: result_names(page) or page.locator(".gb-dh-search").inner_text())


def tag_rows(page, field="gb-reg-tag"):
    return page.locator(f"#{field}-list [role=option]")


def row_tags(page, field="gb-reg-tag"):
    return [t.strip() for t in tag_rows(page, field).locator(".gb-tp-tag").all_inner_texts()]


def tag_row(page, tag, field="gb-reg-tag"):
    return tag_rows(page, field).filter(has=page.locator(".gb-tp-tag", has_text=re.compile(f"^{re.escape(tag)}$")))


def chips(row):
    return [flat(t) for t in row.locator(".gb-tp-follows button").all_inner_texts()]


def variant(page, name):
    return page.get_by_role("radiogroup", name="Variant").get_by_role("radio", name=re.compile(f"^{re.escape(name)}( |$)"))


def hub_calls(dockerhub, since, path):
    return dockerhub.calls(since, host=HUB, path=path)


def hub_wait(engine):
    """How long Gitberth leaves a Docker Hub it couldn't reach alone: the rig's hubWait (its test timings), else a minute."""
    timings = engine.env(config.GITBERTH_CONTAINER).get("GITBERTH_TEST_TIMINGS") or ""
    found = re.search(r"(?:^|,)\s*hubWait=([0-9.]+)", timings)
    return float(found.group(1)) if found else 60.0


def container_ports(page):
    """Configure's ports table: each row's container port, as a field or as text (a port the image declares)."""
    fields = page.locator(".p-port input").evaluate_all("els => els.map(e => e.value)")
    return fields + [t.strip() for t in page.locator(".p-port .gb-pport b").all_inner_texts()]


@pytest.fixture(scope="module")
def tokens(gitberth):
    """An Admin token and a Read one, made once in Settings › AI agents and API."""
    page = gitberth.new_page()
    try:
        admin, _ = make_token(gitberth, page, f"dh-admin-{secrets.token_hex(2)}", "Admin")
        read, _ = make_token(gitberth, page, f"dh-read-{secrets.token_hex(2)}", "Read")
    finally:
        page.context.close()
    return {"admin": admin, "read": read}


def redis_on_rig(engine):
    """redis:8-alpine in the rig's engine, pulled once per rig volume, also tagged redis:8.2-alpine (an image with two names
    is never removed with an app). Returns the digest its pull recorded: the index's, as Docker Hub's tags API gives it."""
    if not engine.image_exists(REDIS):
        docker("pull", "-q", REDIS, timeout=300)
    docker("tag", REDIS, REDIS_NEXT)
    digests = json.loads(docker("image", "inspect", "--format", "{{json .RepoDigests}}", REDIS))
    found = [d.split("@", 1)[1] for d in digests if d.startswith("redis@")]
    assert found, f"{REDIS} has no digest of Docker Hub's: {digests}"
    return found[0]


def seed_redis(dockerhub, digest):
    """library/redis on the stand-in with the image the rig runs: 8.2.3-alpine (also 8.2-alpine, 8-alpine and alpine) is its
    digest; the plain line and the older ones are other images."""
    def fake(label):
        return "sha256:" + hashlib.sha256(label.encode()).hexdigest()

    tags = [{"name": n, "digest": digest, "pushed": 3} for n in ("8.2.3-alpine", "8.2-alpine", "8-alpine", "alpine")]
    tags += [{"name": n, "digest": fake("redis-8.2.3"), "pushed": 3} for n in ("8.2.3", "8.2", "8", "latest")]
    tags += [{"name": n, "digest": fake("redis-8.0.4-alpine"), "pushed": 40} for n in ("8.0.4-alpine", "8.0-alpine")]
    tags += [{"name": n, "digest": fake("redis-7.4.6-alpine"), "pushed": 60} for n in ("7.4.6-alpine", "7.4-alpine", "7-alpine")]
    return dockerhub.seed("library/redis", tags, search=REDIS_SEARCH)


@pytest.fixture(scope="module")
def redis_app(gitberth, engine, tokens):
    """One app made by the API's create_app from redis:8-alpine, for Settings › Image's Choose tag… and the agents' tools;
    removed at the end through delete_app, its images kept (the rig's engine keeps redis once pulled). Yields the App,
    its key, and the digest the stand-in's tags are seeded with."""
    digest = redis_on_rig(engine)
    error, made = tool(gitberth, tokens["admin"], "create_app", {"source": "image", "image": REDIS, "name": f"rd-{secrets.token_hex(2)}", "wait": True},
                       timeout=300)
    assert not error, made
    row = until(lambda: gitberth.db.app_by_id(made["id"]), 30, "the redis app in Gitberth's database")
    app = App(row["Id"], row["Slug"], row["Name"])
    yield {"app": app, "key": made["key"], "digest": digest}
    if not config.KEEP:
        error, gone = tool(gitberth, tokens["admin"], "delete_app", {"app": made["key"], "confirm": app.name, "remove_volumes": True}, timeout=300)
        if error:
            print(f"[e2e] couldn't remove {app.name} after group 39: {gone}")


def follow(gitberth, tokens, redis_app, image):
    """The redis app's image changed through update_app_settings (the agents' way), followed to the end."""
    error, changed = tool(gitberth, tokens["admin"], "update_app_settings", {"app": redis_app["key"], "image": image, "wait": True}, timeout=300)
    assert not error, changed
    until(lambda: gitberth.db.app_by_id(redis_app["app"].id)["ImageRef"] == image, 30, f"the app to follow {image}",
          seen=lambda: gitberth.db.app_by_id(redis_app["app"].id)["ImageRef"])
    return changed


def test_search_shows_badges_stars_pulls_and_updated_and_the_keyboard_picks(page, gitberth, dockerhub, scenario):
    box = new_app(gitberth, page)
    box.fill("postg")
    wait_results(page, "postgres", 4)
    first = results(page).nth(0)
    assert flat(first.locator(".gb-dh-badge").inner_text()) == "Official", first.inner_text()
    assert [flat(t) for t in results(page).locator(".gb-dh-badge--verified-publisher").all_inner_texts()] == ["Verified Publisher"] * 3

    # Typed fast: the debounce asks Docker Hub once or twice, never per letter, anonymously and as Gitberth.
    box.fill("")
    mark = dockerhub.s.seq()
    box.press_sequentially("ngin", delay=40)
    wait_results(page, "nginx", 4)
    asked = hub_calls(dockerhub, mark, r"^/api/search/v4")
    assert 1 <= len(asked) <= 2, [(c["query"], c["status"]) for c in asked]
    last = asked[-1]
    assert last["query"]["query"].startswith("ngin") and last["query"]["type"] == "image", last["query"]
    assert last["token"] is None and last["headers"]["User-Agent"].startswith("Gitberth/"), last["headers"]
    assert result_names(page) == ["nginx", "linuxserver/nginx", "arm64v8/nginx", "dtagdevsec/nginx"]

    nginx = flat(results(page).nth(0).inner_text())
    assert re.search(r"★ 21\.4k", nginx) and "1B+ pulls" in nginx and "Updated 2 days ago" in nginx, nginx
    assert flat(results(page).nth(1).locator(".gb-dh-badge").inner_text()) == "Sponsored OSS"
    assert "No amd64 image" in flat(results(page).nth(2).inner_text()) and "No amd64 image" not in nginx

    # The first row is highlighted as results arrive; ↓ moves to the second, and Enter picks it.
    assert box.get_attribute("aria-activedescendant") == "gb-reg-opt-0"
    box.press("ArrowDown")
    until(lambda: box.get_attribute("aria-activedescendant") == "gb-reg-opt-1", 10, "↓ to highlight the second row")
    box.press("Enter")
    picked = page.locator(".gb-dh-picked .gb-resrow-t")
    picked.wait_for()
    assert picked.inner_text().strip() == "linuxserver/nginx"
    assert results(page).count() == 0 and page.get_by_role("button", name="Change image").is_visible()
    page.locator("#gb-reg-tag").wait_for()
    assert not gitberth.errors(page), gitberth.errors(page)


def test_the_tag_picker_puts_the_newest_stable_first_by_variant(page, gitberth, dockerhub, scenario):
    box = new_app(gitberth, page)
    mark = dockerhub.s.seq()
    box.fill("nginx")
    wait_results(page, "nginx")
    box.press("Enter")
    field = page.locator("#gb-reg-tag")
    until(lambda: field.input_value() == "1.31", 30, "the tag picker to preselect 1.31", seen=lambda: (field.input_value(), page.locator(".gb-tp").inner_text()))
    status = page.locator(".gb-tp-status")
    assert flat(status.inner_text()) == "Follows 1.31, now 1.31.2: patch releases arrive as updates.", status.inner_text()

    # Newest stable first by version: stable's rebuild (1.30.7) was pushed last, and comes third.
    assert row_tags(page)[:3] == ["1.31.2", "1.31.1", "1.30.7"], row_tags(page)
    first = tag_row(page, "1.31.2")
    assert "Recommended" in flat(first.inner_text()) and chips(first) == ["1.31.2", "1.31", "1", "latest", "mainline"], chips(first)
    assert first.get_attribute("aria-selected") == "true", "the row the field follows is the selected one"
    assert "1.31" == flat(first.locator(".gb-tp-chip--on").inner_text())
    assert "2 days ago" in flat(first.inner_text()), first.inner_text()
    assert variant(page, "Standard").get_attribute("aria-checked") == "true"
    assert variant(page, "alpine").count() == 1 and variant(page, "All").count() == 1

    # Pre-releases only with the switch: 1.32.0-rc.1 is then first, marked.
    assert "1.32.0-rc.1" not in row_tags(page)
    pre = page.get_by_role("switch", name="Show pre-releases")
    pre.click()
    until(lambda: row_tags(page)[:1] == ["1.32.0-rc.1"], 15, "the pre-release to show first", seen=lambda: row_tags(page))
    assert "pre-release" in flat(tag_row(page, "1.32.0-rc.1").inner_text())
    pre.click()
    until(lambda: "1.32.0-rc.1" not in row_tags(page), 15, "the pre-release to go again", seen=lambda: row_tags(page))

    variant(page, "alpine").click()
    until(lambda: row_tags(page)[:3] == ["1.31.2-alpine", "1.31.1-alpine", "1.30.7-alpine"], 15, "alpine's rows", seen=lambda: row_tags(page))
    # Every variant: the one tag with no build for this host is still listed, and marked.
    variant(page, "All").click()
    perl = tag_row(page, "1.31.2-perl")
    until(lambda: perl.count() == 1, 15, "1.31.2-perl under All", seen=lambda: row_tags(page))
    assert "No amd64 image" in flat(perl.inner_text()), perl.inner_text()
    shown = flat(page.locator(".gb-tp").inner_text())
    assert "windowsservercore" not in shown and ".sig" not in shown, "Windows and signature tags are never listed"

    # Nothing changed the selection; Docker Hub's two pages of nginx's 130 tags were read once each.
    assert field.input_value() == "1.31" and page.get_by_role("button", name="Continue", exact=True).is_enabled()
    pages = sorted(int(c["query"].get("page", "1")) for c in hub_calls(dockerhub, mark, r"^/v2/namespaces/library/repositories/nginx/tags"))
    assert pages == [1, 2], pages
    assert not gitberth.errors(page), gitberth.errors(page)


def test_an_app_chooses_another_tag_through_the_change_dialog(page, gitberth, dockerhub, engine, tokens, redis_app):
    app = redis_app["app"]
    if gitberth.db.app_by_id(app.id)["ImageRef"] != REDIS:
        follow(gitberth, tokens, redis_app, REDIS)  # the agents' scenario ran first: back on redis:8-alpine
    seed_redis(dockerhub, redis_app["digest"])
    try:
        gitberth.open(page, f"/apps/{redis_app['key']}/settings")
        section = page.locator("#sec-source")
        section.get_by_role("button", name="Choose tag…").click()
        dialog = page.locator(".mud-dialog")
        field = dialog.locator("#gb-dlg-tag")
        field.wait_for()
        # It opens on the app's own tag, on its variant, with the row it runs marked.
        assert field.input_value() == "8-alpine"
        running = tag_rows(page, "gb-dlg-tag").filter(has_text="Running now")
        until(lambda: running.count() == 1, 30, "the row the app runs", seen=lambda: dialog.inner_text())
        assert flat(running.locator(".gb-tp-tag").inner_text()) == "8.2.3-alpine" and chips(running) == ["8.2.3-alpine", "8.2-alpine", "8-alpine", "alpine"]
        assert dialog.get_by_role("radiogroup", name="Variant").get_by_role("radio", name=re.compile("^alpine( |$)")).get_attribute("aria-checked") == "true"
        assert row_tags(page, "gb-dlg-tag")[0] == "8.2.3-alpine", "nothing newer of alpine sits above it"
        assert flat(dialog.locator(".gb-tp-status").inner_text()) == "Follows 8-alpine, now 8.2.3-alpine: minor releases and patches arrive as updates, never a new major."

        running.locator(".gb-tp-follows").get_by_role("button", name="8.2-alpine", exact=True).click()
        until(lambda: field.input_value() == "8.2-alpine", 10, "the chip to be chosen")
        assert flat(dialog.locator(".gb-tp-status").inner_text()) == "Follows 8.2-alpine, now 8.2.3-alpine: patch releases arrive as updates."
        dialog.get_by_role("button", name="Use 8.2-alpine").click()
        dialog.wait_for(state="detached")
        assert page.locator("#set-image").input_value() == REDIS_NEXT

        # The tab's own path: Review and apply, the change dialog's Recreate.
        bar = page.get_by_role("region", name="Unsaved changes")
        apply = bar.get_by_role("button", name=re.compile(r"^(Review and apply|Apply)$"))
        until(apply.is_enabled, 30, "Review and apply to be offered", seen=bar.inner_text)
        apply.click()
        change = page.locator(".mud-dialog")
        change.get_by_role("button", name=re.compile(r"^Recreate ")).click()
        change.get_by_role("button", name="Done").click(timeout=180_000)
        until(lambda: gitberth.db.app_by_id(app.id)["ImageRef"] == REDIS_NEXT, 30, "the app to follow redis:8.2-alpine",
              seen=lambda: gitberth.db.app_by_id(app.id)["ImageRef"])
        assert engine.image_of(app.container) == REDIS_NEXT
        assert not gitberth.errors(page), gitberth.errors(page)
    finally:
        dockerhub.reset()


def test_agents_search_list_tags_and_change_the_tag(gitberth, dockerhub, engine, tokens, redis_app):
    error, found = tool(gitberth, tokens["read"], "search_images", {"query": "redis"})
    assert not error, found
    first = found["images"][0]
    assert (first["name"], first["badge"], first["pulls_text"], found["basic"]) == ("redis", "official", "1B+", False), found
    error, listed = tool(gitberth, tokens["read"], "list_image_tags", {"image": "redis", "variant": "alpine"})
    assert not error, listed
    assert listed["registry"] == "Docker Hub" and listed["default_variant"] == "" and listed["default_tag"] == "8.2", listed
    top = listed["tags"][0]
    assert top["tag"] == "8.2.3-alpine" and {"8.2-alpine", "8-alpine", "alpine"} <= set(top["follows"]), top
    # A Read token reads; changing the tag takes an Admin token.
    error, refused = tool(gitberth, tokens["read"], "update_app_settings", {"app": redis_app["key"], "image": REDIS})
    assert error, refused

    # The app goes to the other of its two tags (redis:8.2-alpine after the Settings scenario, else redis:8-alpine's own).
    app = redis_app["app"]
    target = REDIS if gitberth.db.app_by_id(app.id)["ImageRef"] == REDIS_NEXT else REDIS_NEXT
    follow(gitberth, tokens, redis_app, target)
    assert engine.image_of(app.container) == target


def test_when_docker_hub_wont_answer_typing_still_works(page, gitberth, dockerhub, engine, scenario):
    box = new_app(gitberth, page)
    problem = page.locator(".gb-dh-problem")
    unreachable_since = None
    try:
        # A 500 from the full search: the basic one answers, with official images marked only.
        mark = dockerhub.s.seq()
        dockerhub.answer(HUB, "/api/search/v4", status=500)
        box.fill("uptime")
        wait_results(page, "louislam/uptime-kuma")
        basic = page.locator(".gb-dh-basic")
        assert flat(basic.inner_text()) == "Docker Hub's full search isn't answering: this is its basic one, with official images marked only."
        row = flat(results(page).nth(0).inner_text())
        assert "Updated" not in row and results(page).locator(".gb-dh-badge").count() == 0, row
        assert [(c["host"], c["status"]) for c in dockerhub.calls(mark)] == [(HUB, 500), ("index.docker.io", 200)], dockerhub.calls(mark)

        # A 429: the wait line counts down, Docker Hub isn't asked while it holds (nor its basic search), then the same
        # text is searched again by itself.
        box.fill("")
        mark = dockerhub.s.seq()
        dockerhub.answer(HUB, "/api/search/v4", status=429, retry_after=10)
        box.fill("kuma")
        problem.wait_for()
        until(lambda: re.search(r"Docker Hub asked Gitberth to wait before searching again\..* · again in \d+ s", flat(problem.inner_text())), 10,
              "the wait line with its countdown", seen=problem.inner_text)
        box.fill("louislam")
        until(lambda: "again in" in problem.inner_text(), 10, "the line to stay while Docker Hub's wait holds", seen=problem.inner_text)
        page.wait_for_timeout(1500)
        assert [c["status"] for c in dockerhub.calls(mark)] == [429], "nothing more is asked while the wait holds"
        wait_results(page, "louislam/uptime-kuma", timeout=30)
        assert problem.count() == 0
        assert [c["status"] for c in dockerhub.calls(mark)] == [429, 200], [(c["host"], c["status"]) for c in dockerhub.calls(mark)]

        # Both searches drop the connection: the can't-be-reached line, and a full reference still goes on. Every try is
        # dropped: .NET sends a request again by itself when a kept-alive connection is dropped before any answer, once for
        # each such connection it holds, so one dropped connection alone isn't a host that can't be reached.
        box.fill("")
        mark = dockerhub.s.seq()
        for host, path in ((HUB, "/api/search/v4"), ("index.docker.io", "/v1/search")):
            dockerhub.answer(host, path, close=True, times=20)
        box.fill("kum")
        unreachable_since = time.monotonic()
        until(lambda: flat(problem.inner_text()).startswith("Docker Hub can't be reached from this host, so there's no search."), 30,
              "the can't-be-reached line", seen=lambda: page.locator(".gb-dh-search").inner_text())
        dockerhub.clear_answers()
        tried = dockerhub.calls(mark)
        assert {c["host"] for c in tried} == {HUB, "index.docker.io"} and all(c["status"] == 0 for c in tried), \
            [(c["host"], c["path"], c["status"]) for c in tried]

        # A registry of one's own, typed: its tags from tags/list across its pages, by version alone, aliases matched by name.
        mark = dockerhub.s.seq()
        box.fill("registry.e2e.test/team/api")
        wait_results(page, "registry.e2e.test/team/api", 1)
        assert "registry.e2e.test · as typed" in flat(results(page).nth(0).inner_text())
        box.press("Enter")
        field = page.locator("#gb-reg-tag")
        until(lambda: field.input_value() == "1.27", 30, "the picker to preselect 1.27", seen=lambda: page.locator(".gb-tp").inner_text())
        assert row_tags(page)[:3] == ["1.27.3", "1.27.2", "1.26.5"], row_tags(page)
        top = tag_row(page, "1.27.3")
        assert chips(top) == ["1.27.3", "≈ 1.27", "≈ 1"] or chips(top) == ["1.27.3", "≈1.27", "≈1"], chips(top)
        assert not re.search(r"\bago\b", flat(page.locator(".gb-tp-list").inner_text())), "tags/list gives no dates"
        assert flat(page.locator(".gb-tp-status").inner_text()) == "Follows 1.27, likely 1.27.3 now: patch releases arrive as updates."
        listing = [c for c in dockerhub.calls(mark, host="registry.e2e.test") if c["path"] == "/v2/team/api/tags/list"]
        assert [c["query"].get("last") for c in listing if c["status"] == 200] == [None, "1.27.2"], [(c["query"], c["status"]) for c in listing]

        # Continue reads the image's config from the registry: its port is in Configure before anything is pulled.
        page.get_by_role("button", name="Continue", exact=True).click()
        page.get_by_role("button", name="Review and create").wait_for(timeout=60_000)
        until(lambda: "8080" in container_ports(page), 30, "the image's port, 8080", seen=lambda: container_ports(page) or page.locator("main").inner_text()[:1500])
        assert "registry.e2e.test/team/api:1.27" in page.locator("main").inner_text()
        assert any(c["path"].startswith("/v2/team/api/blobs/") and c["status"] == 200 for c in dockerhub.calls(mark, host="registry.e2e.test"))
    finally:
        dockerhub.reset()
        # Gitberth leaves a Docker Hub it couldn't reach alone for a while (the rig's hubWait, a minute as installed): the
        # scenarios after this one find it answering again, whichever runs next.
        if unreachable_since is not None:
            left = hub_wait(engine) + 1 - (time.monotonic() - unreachable_since)
            if left > 0:
                time.sleep(left)
