"""Scenario 31: every app template installs and answers (1.3). Slow: it pulls every template's images.

Run it on its own with -k test_31_template_smoke (a run of every scenario skips it). For each template the gallery lists,
in turn: Install from its page (a
Postgres server is set up first when the rig has none), its first deploy succeeds, its web service answers its health
path on its LAN port (2xx, 3xx or 401), and Delete with its volumes and its databases' data takes its containers away.
Its images stay, so a second run doesn't pull them again. One template's failure doesn't stop the others: the run ends
with a line per template and fails if any didn't pass.
"""
import os
import re
import time

import pytest

from e2e.net import get
from e2e.wait import until
from test_27_databases import SERVER, create_server, remove_server
from test_30_templates import delete_everything, install

# A comma-separated list of template ids, to smoke only those (the gallery's every one when empty).
ONLY = [t for t in os.environ.get("E2E_TEMPLATES", "").split(",") if t]

FIRST_DEPLOY = 1200  # seconds: a first pull of a large image (Stirling-PDF, Nextcloud) takes a while
ANSWER = 300         # seconds for the web service to answer its health path after its deploy


def gallery_ids(gitberth, page):
    gitberth.open(page, "/apps/new?from=templates")
    cards = page.locator("a.gb-tpl-card")
    cards.first.wait_for()
    return [re.search(r"template=([a-z0-9-]+)", h).group(1) for h in (c.get_attribute("href") for c in cards.all())]


def health_of(gitberth, app_id):
    row = gitberth.db.app_by_id(app_id)
    return row["HealthCheckPath"] or "/"


@pytest.mark.slow
def test_31_template_smoke(request, page, gitberth, engine, apps):
    # Only when it's asked for by name: a whole run of the scenarios leaves it out.
    if "test_31" not in (request.config.getoption("keyword") or ""):
        pytest.skip("the template smoke run is slow: select it with -k test_31_template_smoke")
    ids = gallery_ids(gitberth, page)
    assert 15 <= len(ids) <= 20, ids
    if ONLY:
        ids = [t for t in ids if t in ONLY]
    made_server = not engine.exists(SERVER)
    create_server(gitberth, page, engine)
    results = {}
    try:
        for template in ids:
            started = time.monotonic()
            app = None
            try:
                app = apps.track(install(gitberth, page, template))
                deploy = gitberth.wait_deploy(app.id, timeout=FIRST_DEPLOY)
                assert deploy["Trigger"] == "Created", deploy
                url = gitberth.lan_url(app.id) + health_of(gitberth, app.id)
                status = until(lambda: (lambda s: s if 200 <= s < 400 or s == 401 else None)(get(url, timeout=20)[0]), ANSWER,
                               f"{template} to answer {url}", seen=lambda: get(url, timeout=20)[0])
                results[template] = f"ok ({status}, {time.monotonic() - started:.0f} s)"
            except Exception as e:  # noqa: BLE001 - one template's failure is recorded, and the others still run
                results[template] = f"FAILED: {str(e)[:1500]}"
            finally:
                if app is not None:
                    try:
                        gitberth.wait_idle(app.id, timeout=600)
                        delete_everything(gitberth, page, app, images=False)
                        until(lambda: not engine.containers(label=f"gitberth.app={app.slug}"), 180, f"{template}'s containers to go")
                    except Exception as e:  # noqa: BLE001
                        results[template] += f" (delete: {str(e)[:300]})"
            print(f"[e2e] template {template}: {results[template]}", flush=True)
    finally:
        if made_server:
            remove_server(gitberth, page, engine)
    failed = {t: r for t, r in results.items() if not r.startswith("ok")}
    summary = "\n".join(f"{t}: {r}" for t, r in results.items())
    print("[e2e] template smoke:\n" + summary, flush=True)
    assert not failed, f"{len(failed)} of {len(results)} template(s) didn't pass:\n" + "\n".join(f"{t}: {r}" for t, r in failed.items())
