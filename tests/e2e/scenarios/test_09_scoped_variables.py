"""Scenario 9: variables for every deploy, for production, or for previews.

A Git app's Environment tab gives each variable a scope with its picker (All deploys, Production, Previews), and tags the
Production and Preview ones; one name can have a value in each. The app here gets E2E_SHARED for every deploy,
E2E_API_URL in all three scopes, E2E_ONLY_PRODUCTION for production and E2E_ONLY_PREVIEWS for previews, and its repo a
GitHub variable. Then a pull request is previewed.
- The tab lists each variable with its picker set to its scope and the scoped ones tagged (each tag saying what it
  means), and its line says what scopes are.
- Production gets the All and Production values, and the preview its parent's All and Preview values: a Production or
  Preview value wins over the All one of the same name, and the repo's GitHub variable goes to both. VERCEL_ENV is
  production for production and preview for the preview.
- A preview has no Environment tab (its page leaves it out, and its /environment shows its Overview) and no variables
  of its own; its banner, its deploy log (the file and its page) and the Previews tab say its variables for previews are
  its parent's.
- A name twice in one scope is refused with its message, under the table and in the apply bar (Apply off), and by
  Gitberth when it's saved anyway (the bar's Save for next deploy, which it still offers): added for production or for
  every deploy, or made by a row's picker. The same name in another scope is fine.
- A change to Preview values only doesn't restart production, and the apply bar says so: Apply saves it without a
  deploy, production's container, deploys and pending state stay as they were, and the preview's next deploy (Previews
  tab › Redeploy) takes it.

The Environment tab's helpers here serve scenario 10 too.
"""
import contextlib
import re
import time
from types import SimpleNamespace

import pytest
from playwright.sync_api import TimeoutError as PlaywrightTimeout

from e2e.wait import until
from test_02_pull_request_previews import log_lines, new_app, open_pull, own_apps, previews_card, text_of, unique, wait_finished
from test_04_preview_limit import choose_from_menu, text_of_said
from test_06_copying import close_snackbars
from test_19_declared_volumes import apply_bar, wait_final

# The scopes as the picker's values have them (EnvScope), and its options as it shows them.
ALL, PRODUCTION, PREVIEW = "All", "Production", "Preview"
PICKER = ["All deploys", "Production", "Previews"]
SECTION_LINE = ("Passed to the container when it starts; build-time ones to the build too. Each is for every deploy, for production, "
                "or for previews.")
TAG_TITLES = {
    PRODUCTION: "Only production gets it: the app's own container, not its previews. It wins over a value for every deploy with the same name.",
    PREVIEW: "Only previews get it, not production. It wins over a value for every deploy with the same name.",
}
# What the apply bar says of a change only previews take (AppSettingsService.PlanAsync).
PREVIEWS_ONLY = "Production doesn't restart: previews take it with their next deploy."
# What it says of one production takes, on a Git app that runs.
PRODUCTION_TAKES = "Applies with a deploy without a build"

API_ALL = "https://api.example.org/all"
API_PRODUCTION = "https://api.example.org/production"
API_PREVIEW = "https://api.example.org/preview"
# The parent's variables, as the Environment tab gets them: (name, scope, value).
VARIABLES = [
    ("E2E_SHARED", ALL, "shared by every deploy"),
    ("E2E_API_URL", ALL, API_ALL),
    ("E2E_API_URL", PRODUCTION, API_PRODUCTION),
    ("E2E_API_URL", PREVIEW, API_PREVIEW),
    ("E2E_ONLY_PRODUCTION", PRODUCTION, "production only"),
    ("E2E_ONLY_PREVIEWS", PREVIEW, "previews only"),
]
# Set on GitHub, in the repo's variables: every deploy gets it.
FROM_GITHUB = ("E2E_FROM_GITHUB", "set in the repo's GitHub variables")

PRODUCTION_GETS = {"E2E_SHARED": "shared by every deploy", "E2E_API_URL": API_PRODUCTION, "E2E_ONLY_PRODUCTION": "production only",
                   FROM_GITHUB[0]: FROM_GITHUB[1], "VERCEL_ENV": "production"}
PRODUCTION_LACKS = ["E2E_ONLY_PREVIEWS"]
PREVIEW_GETS = {"E2E_SHARED": "shared by every deploy", "E2E_API_URL": API_PREVIEW, "E2E_ONLY_PREVIEWS": "previews only",
                FROM_GITHUB[0]: FROM_GITHUB[1], "VERCEL_ENV": "preview"}
PREVIEW_LACKS = ["E2E_ONLY_PRODUCTION"]

# The change to Preview values only.
API_PREVIEW_2 = "https://api.example.org/preview-2"
ONLY_PREVIEWS_2 = "previews only, changed"


# ---- the Environment tab (scenario 10 uses these too) ----

def open_environment(page, gitberth, app):
    """The app's Environment tab, once its table of variables shows."""
    gitberth.open(page, f"/apps/{app.key}/environment")
    page.locator("#sec-env .gb-envtbl").wait_for()
    return page.locator("#sec-env")


def env_rows(page):
    """The Environment tab's variables as it lists them, each with its row (a locator), name, value, picker (its value:
    All, Production or Preview; and its options), scope tag and that tag's title, every tag's text, and its classes."""
    rows = page.locator("#sec-env .gb-envrow:not(.gb-envrow--head):not(.gb-envrow--more)")
    out = []
    for i in range(rows.count()):
        row = rows.nth(i)
        key_cell = row.locator(".gb-envkey")
        if key_cell.count() == 0:
            continue
        name = key_cell.locator("input[aria-label='Variable name']")
        key = name.input_value() if name.count() else text_of(key_cell.locator(":scope > span:not(.gb-tag):not(.gb-default-tag)").first)
        value = row.locator(".gb-envval input")
        picker = row.locator("select")
        tag = key_cell.locator(".gb-tag--outline")
        out.append(SimpleNamespace(
            row=row, key=key, value=value.input_value() if value.count() else None,
            value_type=value.get_attribute("type") if value.count() else None,
            scope=picker.input_value() if picker.count() else None,
            options=picker.locator("option").evaluate_all("os => os.map(o => o.textContent.trim())") if picker.count() else [],
            tag=text_of(tag) if tag.count() else None, tag_title=tag.get_attribute("title") if tag.count() else None,
            tags=[text_of(t) for t in key_cell.locator(".gb-tag").all()], classes=row.get_attribute("class") or ""))
    return out


def shown_rows(page, prefix="E2E_"):
    """The variables the tab lists with this prefix, as (name, scope, value, scope tag), sorted."""
    return sorted((r.key, r.scope, r.value, r.tag) for r in env_rows(page) if r.key.startswith(prefix))


def env_row(page, key, scope):
    """The tab's saved row of the variable in that scope (its row locator)."""
    for r in env_rows(page):
        if r.key == key and r.scope == scope and "gb-envrow--new" not in r.classes:
            return r.row
    raise AssertionError(f"the Environment tab has no row for {key} in {scope}: {[(r.key, r.scope) for r in env_rows(page)]}")


def add_variable(page, key, value, scope=ALL, build=False):
    """Add variable: a new row, its picker set first (so no step on the way names a variable twice), then its name and
    value, and Build time when asked. Returns the row."""
    section = page.locator("#sec-env")
    added = section.locator(".gb-envrow--new")
    before = added.count()
    section.get_by_role("button", name="Add variable").click()
    until(lambda: added.count() == before + 1, 15, "a new row after Add variable", seen=lambda: text_of(section))
    row = added.nth(before)
    picker = row.locator("select")
    if scope != ALL:
        picker.select_option(scope)
        until(lambda: picker.input_value() == scope, 15, f"the new row's picker to say {scope}", seen=picker.input_value)
    row.locator("input[aria-label='Variable name']").fill(key)
    row.locator(".gb-envval input").fill(value)
    if build:
        button = row.locator("button.gb-buildbtn")
        button.click()
        until(lambda: button.get_attribute("aria-pressed") == "true", 15, f"Build time to be on for {key}",
              seen=lambda: button.get_attribute("aria-pressed"))
    until(lambda: (row.locator("input[aria-label='Variable name']").input_value(), row.locator(".gb-envval input").input_value(),
                   picker.input_value()) == (key, value, scope), 15, f"the new row to hold {key} ({scope})",
          seen=lambda: text_of(row))
    return row


def bar_line(page):
    """The apply bar's line (what applying does, or what's wrong), or None without a bar."""
    sub = apply_bar(page).locator(".gb-applybar-sub")
    return text_of(sub) if sub.count() else None


def wait_bar(page, wanted, what, timeout=30):
    """Waits for the apply bar's line to be what `wanted` accepts (a text, or a test on it) and returns (its title, its
    line). The line follows the edits a moment after the last one, so it's waited for, never read once."""
    test = wanted if callable(wanted) else (lambda line: line == wanted)

    def settled():
        line = bar_line(page)
        return (text_of(apply_bar(page).locator(".gb-applybar-title")), line) if line and test(line) else None

    return until(settled, timeout, what, interval=0.5, seen=lambda: text_of(apply_bar(page)) if apply_bar(page).count() else "(no apply bar)")


def apply_button(page):
    return apply_bar(page).get_by_role("button", name=re.compile(r"^(Apply|Review and apply)$"))


def click_apply(page):
    button = apply_button(page)
    until(button.is_enabled, 30, "Apply to be offered", seen=lambda: text_of(apply_bar(page)))
    button.click()


def discard(page):
    """The apply bar's Discard, with the snackbars out of its way: the edits go, and so does the bar."""
    close_snackbars(page)
    apply_bar(page).get_by_role("button", name="Discard").click()
    until(lambda: apply_bar(page).count() == 0, 15, "Discard to drop the edits", seen=lambda: text_of(apply_bar(page)))


def saved_variables(gitberth, app_id):
    """The app's variables as Gitberth saved them (values are encrypted): (name, scope, secret, build time)."""
    return sorted((v["Key"], v["Scope"], v["IsSecret"], v["BuildTime"]) for v in gitberth.db.env_vars(app_id))


def production_now(gitberth, engine, app):
    """Production as it runs now: its container (id, start), its newest deploy, and whether Gitberth has saved settings
    that aren't live yet."""
    c = engine.inspect(app.container) or {}
    row = gitberth.db.app(app.slug) or {}
    return {"container": c.get("Id"), "started": (c.get("State") or {}).get("StartedAt"), "last deploy": gitberth.db.last_deployment_id(app.id),
            "ConfigChangePending": row.get("ConfigChangePending"), "RebuildPending": row.get("RebuildPending")}


def watch_production(gitberth, engine, app, before, seconds=5):
    """Production for a few seconds after a save: the first state that isn't `before`, else `before`."""
    end = time.monotonic() + seconds
    while True:
        now = production_now(gitberth, engine, app)
        if now != before or time.monotonic() >= end:
            return now
        time.sleep(0.5)


def picked(env, keys):
    return {k: env.get(k) for k in keys}


# ---- the world: an app with scoped variables, and a pull request's preview ----

@pytest.fixture(scope="module")
def scoped(gitberth, github, engine, standins):
    """An app (its repo with a GitHub variable), previews of pull requests on, its variables added on the Environment tab
    and applied (a deploy without a build), then a pull request opened: its preview made and deployed."""
    with own_apps(gitberth, standins, "scoped-variables") as (page, made):
        repo, app, first = new_app(page, gitberth, github, made.track, unique("scv"))
        repo.set_variables(**{FROM_GITHUB[0]: FROM_GITHUB[1]})
        said = gitberth.set_previews(page, app, mode="Pull requests")
        assert said.startswith("Saved"), said

        open_environment(page, gitberth, app)
        for key, scope, value in VARIABLES:
            add_variable(page, key, value, scope)
        drafted = shown_rows(page)
        bar = wait_bar(page, lambda line: line.startswith(PRODUCTION_TAKES), "the apply bar to say production takes the change")
        click_apply(page)
        page.wait_for_url(re.compile(r"/apps/\d+/deployments/\d+$"), timeout=60_000)
        applied = wait_final(gitberth, int(gitberth.path_of(page).rsplit("/", 1)[-1]))
        url = gitberth.lan_url(app.id)
        production_env = gitberth.wait_answers(url, "/env") if applied["Status"] == "Succeeded" else None

        n, sha = open_pull(repo, "feature", "Scoped variables", marker="feature")
        preview = gitberth.wait_preview(app.id, pr=n, timeout=90)
        preview_deploy = wait_finished(gitberth, preview["Id"], sha)
        preview_env = gitberth.wait_answers(gitberth.lan_url(preview["Id"]), "/env") if preview_deploy["Status"] == "Succeeded" else None
        yield SimpleNamespace(page=page, repo=repo, app=app, first=first, drafted=drafted, bar=bar, applied=applied, url=url,
                              production_env=production_env, n=n, sha=sha, preview=preview, preview_deploy=preview_deploy,
                              preview_container=f"gb-{preview['Slug']}", preview_env=preview_env)


def succeeded(gitberth, deploy, what):
    assert deploy["Status"] == "Succeeded", (f"{what} (deploy #{deploy['Id']}, {deploy['Trigger']}) ended {deploy['Status']}: {deploy['Error']}\n"
                                             f"--- the end of its log ---\n{gitberth.deploy_log(deploy['Id'], tail=40)}")


class TestScopes:
    def test_the_environment_tab_lists_each_scope_with_its_picker_and_tag(self, scoped, page, gitberth):
        w = scoped
        expected = sorted((k, s, v, None if s == ALL else s) for k, s, v in VARIABLES)
        # What was typed in, before it was applied: each new row with its picker and tag.
        assert w.drafted == expected, f"the new rows held {w.drafted}"
        # Saved: in Gitberth's database with their scopes, and listed again by the tab after a fresh load.
        assert [(k, s) for k, s, _, _ in saved_variables(gitberth, w.app.id) if k.startswith("E2E_")] == sorted((k, s) for k, s, _ in VARIABLES)
        section = open_environment(page, gitberth, w.app)
        assert text_of(section.locator(".gb-card-sub")) == SECTION_LINE
        assert shown_rows(page) == expected, f"the Environment tab lists {shown_rows(page)}"
        for r in [r for r in env_rows(page) if r.key.startswith("E2E_")]:
            # Every variable's picker offers the three scopes; a scoped one's tag says what it means, an All one has none.
            assert r.options == PICKER, f"{r.key} ({r.scope})'s picker offers {r.options}"
            assert r.tag_title == TAG_TITLES.get(r.scope), f"{r.key} ({r.scope})'s tag: {r.tag!r} titled {r.tag_title!r}"
            assert "New" not in r.tags and "gb-envrow--new" not in r.classes and "gb-envrow--changed" not in r.classes, (r.key, r.tags, r.classes)
            picker = r.row.locator("select")
            assert (picker.get_attribute("aria-label"), picker.get_attribute("title")) == (f"Which deploys get {r.key}", "Which deploys get it"), \
                (picker.get_attribute("aria-label"), picker.get_attribute("title"))
        # Nothing is pending: what's listed is what's saved.
        assert apply_bar(page).count() == 0, text_of(apply_bar(page))
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_production_gets_all_and_production_values(self, scoped, gitberth, engine):
        w = scoped
        # Adding them said what applying does, and Apply ran a deploy without a build.
        title, line = w.bar
        assert title == "1 unsaved change" and line.startswith(PRODUCTION_TAKES), w.bar
        succeeded(gitberth, w.applied, "applying the variables")
        assert w.applied["Trigger"] == "ConfigChange", w.applied
        # What the app sees, and what Docker started it with.
        for where, env in (("its /env", w.production_env), ("its container", engine.env(w.app.container))):
            assert picked(env, PRODUCTION_GETS) == PRODUCTION_GETS, f"production's {where}: {picked(env, list(PRODUCTION_GETS) + PRODUCTION_LACKS)}"
            assert not [k for k in PRODUCTION_LACKS if k in env], f"production's {where} has {picked(env, PRODUCTION_LACKS)}"

    def test_a_preview_gets_its_parents_all_and_preview_values(self, scoped, gitberth, engine):
        w = scoped
        succeeded(gitberth, w.preview_deploy, f"PR #{w.n}'s preview")
        for where, env in (("its /env", w.preview_env), ("its container", engine.env(w.preview_container))):
            assert picked(env, PREVIEW_GETS) == PREVIEW_GETS, f"the preview's {where}: {picked(env, list(PREVIEW_GETS) + PREVIEW_LACKS)}"
            assert not [k for k in PREVIEW_LACKS if k in env], f"the preview's {where} has {picked(env, PREVIEW_LACKS)}"
        assert w.preview_env.get("VERCEL_GIT_PULL_REQUEST_ID") == str(w.n)
        # It has no variables of its own: its deploys read its parent's.
        assert gitberth.db.env_vars(w.preview["Id"]) == [], f"the preview has variables of its own: {gitberth.db.env_vars(w.preview['Id'])}"
        texts = [x for x in gitberth.deploy_log(w.preview_deploy["Id"]).splitlines() if "variables for previews" in x]
        assert texts and texts[0].endswith(f"==> A preview of {w.app.name} (PR #{w.n}): its settings are {w.app.name}'s, and so are its variables for previews"), texts

    def test_a_preview_has_no_environment_tab_of_its_own(self, scoped, page, gitberth):
        w = scoped
        gitberth.open(page, f"/apps/{w.preview_container}")
        banner = page.locator(".gb-banner", has_text="Preview of")
        banner.wait_for()
        assert f"Its settings come from {w.app.name}, and so do its variables for previews." in text_of(banner.locator(".gb-banner-sub"))
        # The parent's name in it links to the parent's Settings tab.
        assert banner.locator(".gb-banner-sub a").get_attribute("href") == f"apps/{w.app.key}/settings"
        tabs = gitberth.tabs(page)
        assert "Environment" not in tabs and tabs == ["Overview", "Network", "Logs", "Activity"], tabs
        # Its /environment shows its Overview: no variables to edit there.
        gitberth.open(page, f"/apps/{w.preview_container}/environment")
        page.locator(".gb-banner", has_text="Preview of").wait_for()
        active = page.locator(".gb-tabbar .gb-tabbar-item[aria-current='page']")
        until(lambda: text_of(active) == "Overview", 15, "its Overview to be the tab shown", seen=lambda: text_of(active))
        assert page.locator("#sec-env").count() == 0, "a preview's page shows an Environment section"
        assert page.locator("input[aria-label^='Value of ']").count() == 0, "a preview's page has variables to edit"
        # Its deploy log page says whose variables it got, as the step the file has (a step shows without its "==> ").
        gitberth.open(page, f"/apps/{w.preview['Id']}/deployments/{w.preview_deploy['Id']}")
        step = f"A preview of {w.app.name} (PR #{w.n}): its settings are {w.app.name}'s, and so are its variables for previews"
        until(lambda: step in log_lines(page), 30, "the preview's deploy log page to say whose variables it got", seen=lambda: log_lines(page)[:12])
        # The parent's Previews tab says where its variables come from.
        gitberth.open(page, f"/apps/{w.app.key}/previews")
        foot = text_of(previews_card(page).locator(".gb-card-foot"))
        assert f"Each preview is an app of its own, built with {w.app.name}'s settings and its variables for previews (see Environment)." in foot, foot
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"

    def test_a_name_twice_in_one_scope_is_refused(self, scoped, page, gitberth, engine):
        w = scoped
        saved_before = saved_variables(gitberth, w.app.id)
        production_before = production_now(gitberth, engine, w.app)
        section = open_environment(page, gitberth, w.app)
        errors = section.locator(".gb-field-error")

        def refused(wanted):
            """The tab's message under the table, the same in the apply bar, and Apply off."""
            until(lambda: [text_of(e) for e in errors.all()] == [wanted], 15, f"the tab to say {wanted!r}",
                  seen=lambda: [text_of(e) for e in errors.all()])
            assert errors.first.get_attribute("role") == "alert"
            title, line = wait_bar(page, wanted, f"the apply bar to say {wanted!r}")
            assert apply_button(page).is_disabled(), f"Apply can be clicked while {wanted}"
            return title, line

        # E2E_API_URL for production again.
        add_variable(page, "E2E_API_URL", "https://api.example.org/again", PRODUCTION)
        refused("E2E_API_URL (Production) is set twice.")
        # The bar still offers Save for next deploy (Apply is what it turns off). Saved that way, Gitberth refuses it with
        # its own message, and keeps nothing of it.
        save = apply_bar(page).get_by_role("button", name="Save for next deploy")
        try:
            save.wait_for(timeout=30_000)
        except PlaywrightTimeout:
            raise AssertionError(f"the apply bar doesn't offer Save for next deploy, so a save can't be tried: {text_of(apply_bar(page))}") from None
        save.click()
        said = text_of_said(gitberth.snackbar(page, "twice"))
        assert said == "E2E_API_URL (Production) is listed twice.", said
        discard(page)
        assert errors.count() == 0, [text_of(e) for e in errors.all()]

        # E2E_SHARED for every deploy again.
        add_variable(page, "E2E_SHARED", "again", ALL)
        refused("E2E_SHARED is set twice.")
        discard(page)

        # The picker can make one too: E2E_API_URL's row for every deploy, moved to production.
        row = env_row(page, "E2E_API_URL", ALL)
        row.locator("select").select_option(PRODUCTION)
        refused("E2E_API_URL (Production) is set twice.")
        assert text_of(row.locator(".gb-tag--outline")) == "Production", text_of(row)
        discard(page)
        assert shown_rows(page) == sorted((k, s, v, None if s == ALL else s) for k, s, v in VARIABLES), shown_rows(page)

        # The same name in another scope is no problem: E2E_ONLY_PREVIEWS for production too.
        add_variable(page, "E2E_ONLY_PREVIEWS", "for production too", PRODUCTION)
        wait_bar(page, lambda line: line.startswith(PRODUCTION_TAKES), "the apply bar to say what applying does")
        assert errors.count() == 0, [text_of(e) for e in errors.all()]
        assert apply_button(page).is_enabled()
        discard(page)

        # Nothing was saved or deployed.
        assert saved_variables(gitberth, w.app.id) == saved_before, saved_variables(gitberth, w.app.id)
        assert production_now(gitberth, engine, w.app) == production_before, production_now(gitberth, engine, w.app)
        assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


# ---- a change to Preview values only ----

@pytest.fixture(scope="module")
def preview_only(scoped, gitberth, engine):
    """The parent's two Preview values changed on its Environment tab and applied; production watched for a few seconds;
    then the preview's Redeploy, from the Previews tab."""
    w = scoped
    page = w.page
    before = production_now(gitberth, engine, w.app)
    open_environment(page, gitberth, w.app)
    env_row(page, "E2E_API_URL", PREVIEW).locator(".gb-envval input").fill(API_PREVIEW_2)
    env_row(page, "E2E_ONLY_PREVIEWS", PREVIEW).locator(".gb-envval input").fill(ONLY_PREVIEWS_2)
    try:
        bar = wait_bar(page, PREVIEWS_ONLY, "the apply bar to say production doesn't restart")
        waited = None
    except AssertionError as e:
        bar, waited = None, str(e)
    button = apply_button(page)
    offered = (text_of(button), button.is_enabled()) if button.count() else None
    click_apply(page)
    said = text_of_said(gitberth.snackbar(page, "."))
    with contextlib.suppress(PlaywrightTimeout):
        apply_bar(page).wait_for(state="detached", timeout=15_000)
    landed = gitberth.path_of(page)
    after = watch_production(gitberth, engine, w.app, before)
    saved_rows = None
    with contextlib.suppress(Exception):
        open_environment(page, gitberth, w.app)
        saved_rows = shown_rows(page)
    pending_banner = page.locator(".gb-banner", has_text="Saved, not live yet").count()
    production_env = gitberth.wait_answers(w.url, "/env")

    # The preview's next deploy: Redeploy, on the parent's Previews tab.
    gitberth.open(page, f"/apps/{w.app.key}/previews")
    choose_from_menu(page, f"PR #{w.n}", "Redeploy")
    page.wait_for_url(re.compile(rf"/apps/{w.preview['Id']}/deployments/\d+$"), timeout=60_000)
    redeployed = wait_final(gitberth, int(gitberth.path_of(page).rsplit("/", 1)[-1]))
    preview_env = gitberth.wait_answers(gitberth.lan_url(w.preview["Id"]), "/env") if redeployed["Status"] == "Succeeded" else None
    yield SimpleNamespace(before=before, after=after, bar=bar, waited=waited, offered=offered, said=said, landed=landed, saved_rows=saved_rows,
                          pending_banner=pending_banner, production_env=production_env, redeployed=redeployed, preview_env=preview_env,
                          production_after_redeploy=production_now(gitberth, engine, w.app),
                          production_env_after_redeploy=gitberth.wait_answers(w.url, "/env"))


class TestAPreviewOnlyChange:
    def test_the_apply_bar_says_production_doesnt_restart(self, scoped, preview_only):
        p = preview_only
        assert p.waited is None, p.waited
        assert p.bar == ("1 unsaved change", PREVIEWS_ONLY), p.bar
        assert p.offered == ("Apply", True), f"the apply bar offered {p.offered}"
        # Apply saved it, said so, and stayed on the Environment tab: no deploy to go to.
        assert p.said == "Saved.", p.said
        assert p.landed == f"/apps/{scoped.app.key}/environment", p.landed
        expected = sorted((k, s, {API_PREVIEW: API_PREVIEW_2, "previews only": ONLY_PREVIEWS_2}.get(v, v), None if s == ALL else s)
                          for k, s, v in VARIABLES)
        assert p.saved_rows == expected, f"after the save the Environment tab lists {p.saved_rows}"

    def test_production_isnt_restarted_or_deployed(self, scoped, preview_only):
        p = preview_only
        assert p.before["ConfigChangePending"] == 0 and p.before["RebuildPending"] == 0, p.before
        assert p.after == p.before, f"saving a change for previews changed production: {p.before} -> {p.after}"
        assert p.pending_banner == 0, "production's page says saved settings aren't live yet"
        assert picked(p.production_env, PRODUCTION_GETS) == PRODUCTION_GETS, picked(p.production_env, PRODUCTION_GETS)
        # The preview's redeploy didn't touch it either.
        assert p.production_after_redeploy == p.before, p.production_after_redeploy
        assert picked(p.production_env_after_redeploy, PRODUCTION_GETS) == PRODUCTION_GETS

    def test_the_previews_next_deploy_takes_it(self, scoped, preview_only, gitberth):
        p = preview_only
        succeeded(gitberth, p.redeployed, "the preview's redeploy")
        assert p.redeployed["Trigger"] == "Manual", p.redeployed
        wanted = {**PREVIEW_GETS, "E2E_API_URL": API_PREVIEW_2, "E2E_ONLY_PREVIEWS": ONLY_PREVIEWS_2}
        assert picked(p.preview_env, wanted) == wanted, picked(p.preview_env, list(wanted) + PREVIEW_LACKS)
        assert not [k for k in PREVIEW_LACKS if k in p.preview_env], picked(p.preview_env, PREVIEW_LACKS)
