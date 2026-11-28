"""Scenario 45: health alerts, the longer history and unhealthy restarts (src/Gitberth/Health/ResourceAlerts.cs,
HealthRules.cs, StatsStore.cs's roll-ups, ChartRange.cs; src/Gitberth/Keeper/UnhealthyRestarts.cs).

- Settings lists Health alerts right after Uptime and status page; Keep apps running has Restart apps that turn
  unhealthy, on; Notifications has An app's CPU or memory stays high and Gitberth restarts an unhealthy app, both on.
- A container of the scenario's own that keeps a CPU busy, limited to half of one (--cpus 0.5): with the rig's alert
  minute of a second (alertMinute=1), the default CPU above 90% for 10 minutes takes 10 s. The dashboard says so and its
  Activity has it; once it rests, the banner goes after 10 more and its Activity says it's back under.
- A container whose own health check a file makes fail (its restarts keep the file): Gitberth restarts it 3 times, then
  stops, says so on the dashboard and restarts it no more; the file gone, it's healthy and the banner goes.
- Hourly roll-ups seeded into stats.db show on the Health page's 7-day range, in its readings as a table.

About 3 minutes: no app is built; two containers of the scenario's own, which go at the end with what it seeded.
"""
import secrets
import sqlite3
import time
from contextlib import closing
from datetime import datetime, timedelta, timezone
from types import SimpleNamespace

import pytest

from e2e import config
from e2e.engine import docker
from e2e.wait import until

ALPINE = "alpine:3.21"
# .NET's ticks, as stats.db keeps its times: 100 ns since 0001-01-01.
EPOCH_TICKS = 621_355_968_000_000_000


def flat(text):
    return " ".join((text or "").split())


def ticks(at):
    return EPOCH_TICKS + int(at.timestamp() * 10_000_000)


def stats_db():
    return closing(sqlite3.connect(f"{config.GBDATA}/stats.db", timeout=30, isolation_level=None))


def dashboard_says(gitberth, page, text):
    """Whether the dashboard's banners say it now (the page opened afresh)."""
    gitberth.open(page, "/")
    return text in flat(page.locator("main").inner_text())


def started_at(engine, name):
    return (engine.inspect(name) or {}).get("State", {}).get("StartedAt")


@pytest.fixture(scope="module")
def world(engine):
    tag = secrets.token_hex(2)
    w = SimpleNamespace(hot=f"gbe2e-hot-{tag}", sick=f"gbe2e-sick-{tag}")
    if not engine.image_exists(ALPINE):
        docker("pull", "-q", ALPINE, timeout=300)
    try:
        yield w
    finally:
        for c in (w.hot, w.sick):
            docker("rm", "-fv", c, check=False)
        try:
            with stats_db() as db:
                db.execute("DELETE FROM points_1h WHERE container = ?", (w.hot,))
        except sqlite3.Error as e:
            print(f"[e2e] couldn't take the seeded roll-ups out of stats.db after group 45: {e}")


def test_settings_have_health_alerts_and_the_new_switches(page, gitberth):
    gitberth.open(page, "/settings")
    links = page.locator(".gb-toc a")
    links.first.wait_for()
    names = [t.strip() for t in links.all_inner_texts()]
    assert names.index("Health alerts") == names.index("Uptime and status page") + 1, names
    section = page.locator("section#health-alerts")
    assert section.get_by_role("switch", name="Alert on high CPU and memory").get_attribute("aria-checked") == "true"
    assert section.locator("#ha-cpu").input_value() == "90" and section.locator("#ha-cpu-min").input_value() == "10"
    assert section.locator("#ha-mem").input_value() == "90" and section.locator("#ha-mem-min").input_value() == "5"
    keep = page.locator("section#keep").get_by_role("switch", name="Restart apps that turn unhealthy", exact=True)
    assert keep.get_attribute("aria-checked") == "true"
    notifications = page.locator("#notifications")
    for name in ("An app's CPU or memory stays high", "Gitberth restarts an unhealthy app"):
        assert notifications.get_by_role("switch", name=name, exact=True).get_attribute("aria-checked") == "true", name


def test_a_busy_container_alerts_and_the_alert_ends_once_it_rests(page, gitberth, engine, world):
    # Busy while /tmp/hot is there; at rest without it.
    docker("run", "-d", "--name", world.hot, "--cpus", "0.5", "--init", ALPINE, "sh", "-c",
           "touch /tmp/hot; while :; do if [ -f /tmp/hot ]; then :; else sleep 1; fi; done")
    engine.wait_running(world.hot)
    banner = f"{world.hot}'s CPU is high"
    until(lambda: dashboard_says(gitberth, page, banner), 90, "the dashboard to say the CPU is high", interval=3)

    engine.exec(world.hot, "rm", "-f", "/tmp/hot")
    until(lambda: not dashboard_says(gitberth, page, banner), 90, "the banner to go once it rests", interval=3)
    gitberth.open(page, f"/apps/{world.hot}/activity")
    until(lambda: "It stayed under 90% of its 0.5-CPU limit for 10 minutes." in flat(page.locator("main").inner_text()), 30,
          "its Activity to say it's back under", seen=lambda: flat(page.locator("main").inner_text())[:600])
    assert "It used" in flat(page.locator("main").inner_text())


def test_an_unhealthy_container_is_restarted_three_times_then_left(page, gitberth, engine, world):
    docker("run", "-d", "--name", world.sick, "--init", "--stop-timeout", "2", "--health-cmd", "test ! -f /tmp/sick",
           "--health-interval", "2s", "--health-timeout", "2s", "--health-retries", "2", ALPINE, "sleep", "36000")
    until(lambda: "(healthy)" in docker("ps", "--filter", f"name={world.sick}", "--format", "{{.Status}}"), 60, "it to be healthy")
    starts = {started_at(engine, world.sick)}

    engine.exec(world.sick, "touch", "/tmp/sick")
    banner = f"{world.sick} stays unhealthy"

    def gave_up():
        starts.add(started_at(engine, world.sick))
        return dashboard_says(gitberth, page, banner)

    until(gave_up, 150, "Gitberth to stop restarting it", interval=2, seen=lambda: len(starts))
    # Its first start and 3 restarts; none after it gave up.
    starts.add(started_at(engine, world.sick))
    assert len(starts) == 4, starts
    last = started_at(engine, world.sick)
    time.sleep(12)
    assert started_at(engine, world.sick) == last, "restarted again after Gitberth gave up"

    engine.exec(world.sick, "rm", "-f", "/tmp/sick")
    until(lambda: not dashboard_says(gitberth, page, banner), 60, "the banner to go once it's healthy", interval=3)
    gitberth.open(page, f"/apps/{world.sick}/activity")
    assert "Restarted by Gitberth: Docker said it was unhealthy (3 of at most 3 in an hour)." in flat(page.locator("main").inner_text())


def test_the_7_day_range_shows_the_hourly_roll_ups(page, gitberth, engine, world):
    if not engine.running(world.hot):
        pytest.skip("needs the busy container from the scenario before")
    hour = datetime.now(timezone.utc).replace(minute=0, second=0, microsecond=0)
    rows = [(world.hot, ticks(hour - timedelta(hours=6 * i)), 42.0, 55.0, 64.0 * 2**20, 80.0 * 2**20, 1000.0, 2000.0) for i in range(1, 13)]
    with stats_db() as db:
        db.executemany("INSERT OR REPLACE INTO points_1h (container, t, cpu, cpu_max, mem, mem_max, rx, tx) VALUES (?, ?, ?, ?, ?, ?, ?, ?)", rows)

    gitberth.open(page, f"/health#hc-title-{world.hot}")
    page.get_by_role("radio", name="7 days").click()
    row = page.locator(".gb-hitem", has=page.locator(f"#hc-title-{world.hot}"))
    if row.locator(f"#hc-body-{world.hot}").count() == 0:
        row.get_by_role("button", name=f"Show {world.hot}'s charts").click()
    row.get_by_role("button", name="As a table").click()
    table = row.locator(".gb-mini-table").last
    until(lambda: sum(1 for t in table.locator("tbody tr").all_inner_texts() if "42%" in t) == 12, 30, "the 12 seeded hours in the table",
          seen=lambda: table.locator("tbody tr").all_inner_texts()[:4])
    assert "averaged over an hour" in table.locator("caption").inner_text()
