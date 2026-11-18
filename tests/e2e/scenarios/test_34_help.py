"""Scenario 34: the built-in Help and the tips, in the image as built (no internet needed: the rig's Gitberth reaches none).

- The top bar's Help menu: "Help for this page" from Settings opens Gitberth's settings' help, at the page the "?" by the
  title opens too; "Help topics" is the docs' index under a search; "Search help" puts the cursor in it; every item has
  a tip; the sidebar has Help at its foot.
- Search: page titles first, every word needed; the top bar's search has a Help group whose Enter opens the page.
- A page with a flow chart and pictures: the chart is drawn as SVG by Gitberth's own copy of mermaid, the pictures load
  from Gitberth (signed in only), one opens larger and Esc puts it away; "On this page" and previous/next are there.
- A tip on hover (its title and text, and "Learn more" to the right page and heading, kept open while the pointer
  moves onto it), and on keyboard focus; Esc closes it.
- MudBlazor's controls hand data-tip to an element of their own choosing: on a menu item, a menu's button, an icon
  button and a button, the element under the pointer carries the tip and its card opens. (A checkbox, in the Delete
  dialog of a Git or Compose app, carries its tip on its words, a Tip like any other.)
- No browser errors on any of these pages.
"""
import os
import re
from urllib.parse import urlsplit

from e2e import config
from e2e.net import call
from e2e.wait import until

TIP = "#gb-tipcard"


def shot(page, name):
    """A picture of what the scenario checked, beside the report (the help, a tip, the menu as a person sees them), once
    what just opened has faded in."""
    try:
        os.makedirs(config.OUT, exist_ok=True)
        page.wait_for_timeout(400)
        page.screenshot(path=os.path.join(config.OUT, f"34-{name}.png"))
    except Exception as e:  # noqa: BLE001 - a picture never fails the scenario
        print(f"[e2e] no screenshot {name}: {e}")


def card(page):
    return page.locator(TIP)


def card_open(page):
    """The tip card, once it's open and faded in."""
    loc = card(page)
    until(lambda: loc.count() == 1 and loc.is_visible() and "on" in (loc.get_attribute("class") or ""), 10,
          "the tip card to open", seen=lambda: loc.evaluate("e => e.outerHTML") if loc.count() else "(no card yet)")
    return loc


def open_help_menu(page):
    page.locator("button.gb-topbar-help").click()
    menu = page.locator(".mud-popover-open .mud-menu-item", has_text="Help topics")
    menu.wait_for()
    return page.locator(".mud-popover-open")


def test_the_help_menu_opens_help_for_this_page_and_the_topics(page, gitberth):
    gitberth.open(page, "/settings")
    # The "?" by the page's title leads where the menu's "Help for this page" does.
    by_title = page.locator(".gb-phead .gb-pagehelp")
    by_title.wait_for()
    target = by_title.get_attribute("href")
    assert target == "help/reference/gitberth-settings", target

    menu = open_help_menu(page)
    shot(page, "help-menu")
    items = [t.strip() for t in menu.locator(".mud-menu-item").all_inner_texts()]
    assert items == ["Help for this page", "Help topics", "Search help", "Getting started", "Troubleshooting", "FAQ"], items
    tips = menu.locator(".mud-menu-item").evaluate_all(
        "els => els.map(e => (e.closest('[data-tip]') || e.querySelector('[data-tip]'))?.getAttribute('data-tip'))")
    assert all(t and t.startswith("help.menu.") for t in tips), f"the Help menu's items have no tips: {tips}"

    menu.locator(".mud-menu-item", has_text="Help for this page").click()
    page.wait_for_url(re.compile(r"/help/reference/gitberth-settings$"))
    # The address changes first; the help page replaces Settings a moment later.
    title = page.locator(".gb-helpdoc .gb-phead h1")
    title.wait_for()
    assert title.inner_text().strip(), "the help page has no title"
    crumbs = page.locator(".gb-helpdoc .gb-crumbs")
    assert crumbs.inner_text().startswith("Help"), crumbs.inner_text()
    current = page.locator(".gb-helpnav-item[aria-current=page]")
    assert current.get_attribute("href") == "help/reference/gitberth-settings"
    assert page.locator(".gb-doc h2").count() > 3, "the page has no sections"
    assert page.locator(".gb-doc-pager a").count() >= 1, "no previous or next page"
    shot(page, "help-for-settings")

    # Help topics: the docs' own index, under the search box.
    open_help_menu(page).locator(".mud-menu-item", has_text="Help topics").click()
    page.wait_for_url(re.compile(r"/help$"))
    index = page.locator(".gb-doc--index")
    index.wait_for()
    assert index.locator("a[href^='help/']").count() > 20, "the index lists no pages"
    assert page.locator("#gb-help-search").is_visible()

    # Search help: the cursor goes to the box.
    gitberth.open(page, "/apps")
    open_help_menu(page).locator(".mud-menu-item", has_text="Search help").click()
    page.wait_for_url(re.compile(r"/help\?focus=search$"))
    until(lambda: page.evaluate("document.activeElement && document.activeElement.id") == "gb-help-search", 15,
          "the help's search box to have the cursor", seen=lambda: page.evaluate("document.activeElement && document.activeElement.outerHTML"))

    # The sidebar's Help, at its foot.
    assert page.locator(".gb-side-bottom a.gb-nav-item[href='help']").is_visible()
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


def test_search_finds_page_titles_first_and_the_top_bar_has_a_help_group(page, gitberth):
    gitberth.open(page, "/help")
    box = page.locator("#gb-help-search")
    box.fill("troubleshooting")
    hits = page.locator(".gb-help-hit")
    hits.first.wait_for()
    shot(page, "search")
    # A page whose title has the word comes first, opened at its top.
    assert hits.first.get_attribute("href") == "help/guides/troubleshooting", hits.first.get_attribute("href")
    assert "result" in page.locator(".gb-help-count").inner_text()

    # Every word must be there.
    box.fill("troubleshooting zqxjkv")
    until(lambda: "Nothing in the help has every word" in page.locator(".gb-help-count").inner_text(), 15,
          "no result for a word no page has", seen=lambda: page.locator(".gb-help-results").inner_text())

    box.fill("troubleshooting")
    hits.first.wait_for()
    hits.first.click()
    page.wait_for_url(re.compile(r"/help/guides/troubleshooting$"))

    # The top bar's search: a Help group after the apps, whose Enter opens the highlighted page.
    search = page.locator("#gb-search-input")
    search.click()
    search.fill("troubleshooting")
    group = page.locator("#gb-search-results .gb-search-group", has_text="Help")
    group.wait_for()
    docs = page.locator("#gb-search-results .gb-search-hit--doc .gb-search-doc-t")
    assert "Troubleshooting" in docs.all_inner_texts(), docs.all_inner_texts()
    assert page.locator("#gb-search-results .gb-search-hit-t").count() == 0, "an app matched: the next check needs another word"
    shot(page, "top-search")
    gitberth.open(page, "/")
    search.click()
    search.fill("troubleshooting")
    group.wait_for()
    page.keyboard.press("Enter")
    page.wait_for_url(re.compile(r"/help/guides/troubleshooting$"))
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


# A page with a flow chart and pictures (the unit tests hold the docs to having both on it).
DRAWN_PAGE = "/help/guides/domains"


def test_a_page_draws_its_flow_chart_and_loads_its_pictures(page, gitberth):
    gitberth.open(page, DRAWN_PAGE)
    chart = page.locator(".gb-mermaid").first
    chart.wait_for()
    until(lambda: page.locator(".gb-mermaid[data-drawn] svg").count() > 0 or page.locator(".gb-mermaid[data-failed]").count() > 0, 60,
          "the flow chart to be drawn", seen=lambda: chart.evaluate("e => e.outerHTML.slice(0, 600)"))
    assert page.locator(".gb-mermaid[data-failed]").count() == 0, page.locator(".gb-mermaid[data-failed]").first.inner_text()
    assert page.locator(".gb-mermaid[data-drawn] svg").first.bounding_box()["height"] > 40, "the chart is drawn, but empty"
    assert page.evaluate("typeof window.mermaid") == "object", "mermaid isn't Gitberth's own copy, loaded on this page"
    drawn = page.locator(".gb-mermaid[data-drawn]").first
    drawn.scroll_into_view_if_needed()
    shot(page, "flow-chart")
    # A click opens the chart larger; Esc puts it back on the page.
    drawn.click()
    page.locator(".gb-lightbox .gb-lightbox-chart svg").wait_for()
    shot(page, "flow-chart-larger")
    page.keyboard.press("Escape")
    page.locator(".gb-lightbox").wait_for(state="detached")
    assert drawn.locator(".gb-mermaid-svg svg").count() == 1, "the chart didn't come back to the page"

    # Every picture loads, from Gitberth (they load lazily, so each is brought into view first).
    pictures = page.locator(".gb-doc img")
    assert pictures.count() > 0, "the page has no pictures"
    for i in range(pictures.count()):
        img = pictures.nth(i)
        img.scroll_into_view_if_needed()
        until(lambda: img.evaluate("i => i.complete && i.naturalWidth > 0"), 30, f"picture {i + 1} to load",
              seen=lambda: img.evaluate("i => ({src: i.currentSrc, complete: i.complete, width: i.naturalWidth})"))
        assert img.get_attribute("width") and img.get_attribute("height"), "a picture without its size"
    src = pictures.first.get_attribute("src")
    assert src.startswith("help-media/images/") and "?v=" in src, src
    # The pictures are for signed-in people only: without the sign-in, the address ends on the sign-in page.
    status, headers, body = call("GET", gitberth.url(src))
    kind = {k.lower(): v for k, v in headers.items()}.get("content-type", "")
    assert not kind.startswith("image/"), f"the picture opened without signing in ({status}, {kind})"

    # A click opens it larger; Esc puts it away.
    pictures.first.click()
    viewer = page.locator(".gb-lightbox img")
    viewer.wait_for()
    until(lambda: viewer.evaluate("i => i.complete && i.naturalWidth > 0"), 15, "the larger picture to load")
    shot(page, "picture-larger")
    page.keyboard.press("Escape")
    page.locator(".gb-lightbox").wait_for(state="detached")
    assert urlsplit(page.url).path == DRAWN_PAGE, "opening the picture left the page"

    assert page.locator(".gb-helptoc a").count() > 3, "no On this page"
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


def test_a_tip_opens_on_hover_and_keyboard_focus_and_learn_more_leads_to_its_heading(page, gitberth):
    gitberth.open(page, "/")
    trigger = page.locator("nav[aria-label=Pages] a.gb-nav-item[data-tip='nav.apps']")
    trigger.hover()
    tip = card_open(page)
    assert tip.get_attribute("role") == "tooltip"
    assert tip.locator(".gb-tipcard-title").inner_text().strip() == "Apps"
    assert "Every container on this host" in tip.locator(".gb-tipcard-text").inner_text()
    assert trigger.get_attribute("aria-describedby") == "gb-tipcard"
    shot(page, "tip-hover")
    more = tip.locator("a.gb-tipcard-more")
    href = more.get_attribute("href")
    assert href.startswith("help/"), href
    page_path, _, heading = href.partition("#")

    # The card stays while the pointer moves onto it, so its link can be clicked; it goes to that page and heading.
    more.hover()
    more.click()
    page.wait_for_url(lambda u: urlsplit(u).path == "/" + page_path)
    assert urlsplit(page.url).fragment == heading, page.url
    target = page.locator(f"[id='{heading}']")
    target.wait_for()
    until(lambda: 0 <= target.bounding_box()["y"] < page.viewport_size["height"], 15, f"the heading #{heading} to be in view",
          seen=lambda: target.bounding_box())
    until(lambda: not card(page).is_visible(), 5, "the card to close with the navigation")

    # Keyboard: Tab to the first control with a tip, and its card opens at once; Esc closes it.
    gitberth.open(page, "/")
    focused = None
    for _ in range(12):
        page.keyboard.press("Tab")
        focused = page.evaluate("(() => { const t = document.activeElement?.closest('[data-tip]'); return t ? t.getAttribute('data-tip') : null; })()")
        if focused:
            break
    assert focused, "no control with a tip took the keyboard's focus"
    tip = card_open(page)
    assert tip.locator(".gb-tipcard-title").inner_text().strip(), "the card has no title"
    shot(page, "tip-focus")
    page.keyboard.press("Escape")
    until(lambda: not card(page).is_visible(), 5, "Esc to close the card")
    assert page.evaluate("document.activeElement?.closest('[data-tip]')?.getAttribute('aria-describedby')") is None
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"


# What's under the pointer at a point (a tip card already open there aside): the tip of the element there, its own or
# a parent's, or the element itself.
UNDER = """([x, y]) => {
    const e = document.elementsFromPoint(x, y).find(el => !el.closest('#gb-tipcard'));
    const t = e && e.closest('[data-tip], [data-tip-text]');
    return t ? (t.getAttribute('data-tip') || 'note: ' + t.getAttribute('data-tip-text')) : (e ? 'no tip: ' + e.outerHTML.slice(0, 200) : 'nothing');
}"""


def hover_tip(page, control, tip, title=None, words=None):
    """Points at the middle of the control, as a person would: the element there must carry the tip (a catalog key, or
    "note: <words>" for a note of its own), and its card must open with the tip's title, or the note's words."""
    control.wait_for()
    control.scroll_into_view_if_needed()
    box = control.bounding_box()
    assert box and box["width"] > 0 and box["height"] > 0, f"{tip}: the control isn't on the screen"
    x, y = box["x"] + box["width"] / 2, box["y"] + box["height"] / 2
    under = page.evaluate(UNDER, [x, y])
    assert under == tip, f"under the pointer, in the middle of the control for {tip}: {under}"
    page.mouse.move(x, y)
    shown = card_open(page)
    part, want = (".gb-tipcard-title", title) if title else (".gb-tipcard-text", words)
    until(lambda: shown.locator(part).count() == 1 and shown.locator(part).inner_text().strip() == want, 10,
          f"the card of {tip} to open", seen=lambda: shown.inner_text())


def test_mudblazors_controls_carry_their_tips_where_the_pointer_is(page, gitberth):
    # A menu item (MudMenuItem): the Help menu's.
    gitberth.open(page, "/apps")
    menu = open_help_menu(page)
    hover_tip(page, menu.get_by_role("menuitem", name="Help topics"), "help.menu.topics", title="Help topics")
    page.keyboard.press("Escape")
    until(lambda: not card(page).is_visible(), 5, "Esc to close the card")

    # A menu's button (MudMenu) and an icon button (MudIconButton): Gitberth's own Logs tab.
    gitberth.open(page, f"/apps/{config.GITBERTH_CONTAINER}/logs")
    hover_tip(page, page.get_by_role("button", name=re.compile(r"^Last \d+ lines$")), "app.logs.tail", title="Lines shown")
    shot(page, "tip-menu-button")
    hover_tip(page, page.get_by_role("button", name="Reconnect", exact=True), "note: Reconnect", words="Reconnect")

    # A button (MudButton): a scheduled restart on the Health page, with Gitberth's own charts open.
    gitberth.open(page, f"/health#hc-title-{config.GITBERTH_CONTAINER}")
    hover_tip(page, page.get_by_role("button", name=re.compile(r"^Scheduled restart of ")).first, "app.settings.scheduled-restart",
              title="Scheduled restart")
    page.keyboard.press("Escape")
    until(lambda: not card(page).is_visible(), 5, "Esc to close the card")
    assert not gitberth.errors(page), f"the browser reported errors: {gitberth.errors(page)}"
