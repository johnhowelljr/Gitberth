"""Scenario 27: databases from the app page.

An app gets a Postgres database or a Redis of its own from its Settings tab, and Gitberth puts its connection string in a
variable the app reads; each preview gets its own.

- Settings › Databases creates a Postgres server (the rig has none): a normal app from postgres:18 called postgres, on the
  shared network as postgres, nothing published, its superuser's password a secret variable, updates that only tell. It's
  the default for new databases by itself.
- A Git app's Settings › Databases adds a Postgres database: a database and a role of the app's own on the server, which
  only the role may connect to, with a password that works; the app's container gets DATABASE_URL by a deploy without a
  build. A Redis too: a container of the app's own (hidden from the Apps list) with a password, and REDIS_URL. The
  Environment tab says Gitberth sets them, the Overview has a Databases card, and the delete dialog lists them.
- Previews on, a pull request opened: its preview gets a database of its own (and a Redis of its own), production keeps
  its own; closing the pull request drops the preview's.
- Remove with "keep the data": the database is still on the server, the variable goes from the app. Remove with "delete
  the data": the database and its role are gone (a Redis's container and volume too).
- An app that isn't a Git app (the server's own, from a registry) takes a Redis through the change engine: its container
  is recreated with REDIS_URL, which its Environment tab shows as Gitberth's, and without it once it's removed.
- No password is ever on a page, in a deploy log, in Gitberth's own log or in the audit log; every change is recorded
  there, and none of it as a change made outside Gitberth.
"""
import json
import re
from types import SimpleNamespace
from urllib.parse import unquote, urlsplit

from e2e.audit import last_id, no_network_change_outside, since
from e2e.engine import DockerError, docker
from e2e.wait import until
from test_02_pull_request_previews import PULLS_EVERY, open_pull, wait_deployed
from test_14_instant_deploys import COPY_SPY

SERVER = "postgres"
# What setting up a server ends with: ready, or why not.
SERVER_SAID = r"Postgres server|couldn't|failed|Nothing was created|refused"


def flat(text):
    return " ".join((text or "").split())


def psql(engine, sql, database="postgres"):
    """Runs SQL in the server's container as its superuser (the image trusts local connections), its rows as lines."""
    return [line for line in engine.exec(SERVER, "psql", "-X", "-q", "-t", "-A", "-U", "postgres", "-d", database, "-c", sql).splitlines() if line]


def databases_on_server(engine):
    return set(psql(engine, "SELECT datname FROM pg_database"))


def roles_on_server(engine):
    return set(psql(engine, "SELECT rolname FROM pg_roles"))


def managed(gitberth, app_id):
    return gitberth.db.query("SELECT * FROM ManagedDatabases WHERE AppId = ? ORDER BY Id", app_id)


def url_parts(url):
    parts = urlsplit(url)
    return parts.scheme, unquote(parts.username or ""), unquote(parts.password or ""), parts.hostname, parts.port, parts.path.lstrip("/")


def open_section(gitberth, page, app):
    gitberth.open(page, f"/apps/{app.key}/settings")
    section = page.locator("#sec-databases")
    section.wait_for()
    until(lambda: section.locator(".gb-skel").count() == 0, 30, "the Databases section to load", seen=section.inner_text)
    return section


def add_database(gitberth, page, app, kind="Postgres", variable=None):
    """Settings › Databases › Add database: the kind, the variable when given; returns the snackbar's text."""
    section = open_section(gitberth, page, app)
    section.get_by_role("radio", name=kind, exact=True).click()
    box = section.locator("#db-variable")
    until(lambda: box.input_value() == ("REDIS_URL" if kind == "Redis" else "DATABASE_URL") or variable, 15, "the variable to follow the kind",
          seen=box.input_value)
    if variable:
        box.fill(variable)
        box.blur()
    button = section.get_by_role("button", name="Add database")
    until(button.is_enabled, 30, "Add database to be offered", seen=section.inner_text)
    button.click()
    return flat(gitberth.snackbar(page, r"^Added|didn't|couldn't|can't|refused", timeout=120_000))


def remove_database(gitberth, page, app, name, delete_data):
    """Settings › Databases › Remove, keeping its data or deleting it (its name typed in); returns the snackbar's text."""
    section = open_section(gitberth, page, app)
    section.get_by_role("button", name=f"Remove the database {name}").click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    assert "stops getting" in flat(dialog.inner_text()), dialog.inner_text()
    if delete_data:
        dialog.get_by_role("radio", name=re.compile("^Delete the data")).click()
        button = dialog.get_by_role("button", name="Delete it")
        assert button.is_disabled(), "Delete it works before the name is typed"
        dialog.locator("#gb-remove-db").fill(name)
        until(button.is_enabled, 10, "Delete it to take the name")
        button.click()
    else:
        dialog.get_by_role("button", name="Remove it").click()
    dialog.wait_for(state="detached")
    # Any other app's change waits for the change engine's change of it under way (the add's recreate) to end first.
    return flat(gitberth.snackbar(page, r"^(Deleted|Removed)", timeout=300_000))


def create_server(gitberth, page, engine):
    """Settings › Databases › Create a Postgres server, unless the rig's engine has one Gitberth made already."""
    gitberth.open(page, "/settings")
    section = page.locator("#databases")
    section.wait_for()
    until(lambda: section.locator(".gb-skel").count() == 0, 30, "Settings › Databases to load", seen=section.inner_text)
    if engine.exists(SERVER):
        return None
    assert "No Postgres server yet" in flat(section.inner_text()), section.inner_text()
    section.get_by_role("button", name="Create a Postgres server").click()
    # Any ending: ready, or why not ("Nothing was created: Docker refused it …"), so a failure fails at once.
    said = flat(gitberth.snackbar(page, SERVER_SAID, timeout=600_000))
    assert said == f"The Postgres server {SERVER} is ready: new databases go on it.", said
    return said


def remove_server(gitberth, page, engine):
    """The server app's Remove, with its volume (its name typed in)."""
    gitberth.open(page, f"/apps/{SERVER}/settings")
    page.locator("#sec-remove").get_by_role("button", name=re.compile("^Remove")).click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    dialog.get_by_role("checkbox", name=re.compile("Also delete its volumes")).click()
    dialog.locator("#gb-remove-name").fill(SERVER)
    dialog.get_by_role("button", name=re.compile("^Remove")).click()
    engine.wait_gone(SERVER, timeout=120)


def wait_live(gitberth, app, after):
    """The deploy a database's change starts (without a build) is over and worked; returns it."""
    deploy = gitberth.wait_deploy(app.id, after=after, timeout=300)
    assert deploy["Trigger"] == "ConfigChange" and deploy["ImageTag"], deploy
    return deploy


def test_an_app_and_its_previews_get_databases_of_their_own(page, gitberth, github, engine, apps, uniq, scenario):
    first = last_id()
    secrets_seen = set()

    # ---- Settings › Databases: a server, set up as an app, the default by itself.
    create_server(gitberth, page, engine)
    server = engine.wait_running(SERVER)
    assert server["Config"]["Image"] == "postgres:18", server["Config"]["Image"]
    assert "postgres" in server["NetworkSettings"]["Networks"]["gitberth"]["Aliases"], server["NetworkSettings"]["Networks"]
    assert not any(server["HostConfig"].get("PortBindings") or {}), server["HostConfig"].get("PortBindings")
    labels = engine.labels(SERVER)
    assert (labels.get("gitberth.managed"), labels.get("gitberth.source"), labels.get("gitberth.app")) == ("true", "registry", "postgres"), labels
    row = gitberth.db.app("postgres")
    assert (row["Container"], row["Source"], row["UpdatePolicy"]) == ("postgres", "Registry", "Notify"), row
    assert "POSTGRES_PASSWORD" in json.loads(row["SecretKeys"] or "[]"), row["SecretKeys"]
    secrets_seen.add(engine.env(SERVER)["POSTGRES_PASSWORD"])
    until(lambda: gitberth.db.setting("databases.defaultServer") == SERVER, 30, "the server to be the default")
    gitberth.open(page, "/settings")
    servers = page.locator("#databases")
    until(lambda: "Gitberth uses it" in flat(servers.inner_text()) and "Default" in flat(servers.inner_text()), 60,
          "the server to show as used and the default", seen=servers.inner_text)

    # ---- A Git app gets a Postgres database of its own.
    repo = github.create_repo(uniq("db"), files={"marker.txt": "databases v1"})
    app = apps.track(gitberth.create_git_app(page, repo.url))
    one = gitberth.wait_deploy(app.id, timeout=420)
    said = add_database(gitberth, page, app)
    database = app.slug.replace("-", "_")
    assert said.startswith(f"Added the database {database} on {SERVER}."), said
    two = wait_live(gitberth, app, one["Id"])
    [pg] = managed(gitberth, app.id)
    assert (pg["Kind"], pg["Server"], pg["Name"], pg["Role"], pg["Variable"], pg["Created"]) == ("Postgres", SERVER, database, database, "DATABASE_URL", 1), pg
    assert database in databases_on_server(engine) and database in roles_on_server(engine)
    # Only its own role (and superusers) may connect: PUBLIC lost CONNECT.
    [acl] = psql(engine, f"SELECT datacl FROM pg_database WHERE datname = '{database}'")
    assert not any(entry.startswith("=") and "c" in entry.split("/")[0] for entry in acl.strip("{}").split(",")), acl
    url = engine.env(app.container)["DATABASE_URL"]
    scheme, user, password, host, port, name = url_parts(url)
    assert (scheme, user, host, port, name) == ("postgres", database, SERVER, 5432, database), url
    secrets_seen.add(password)
    # Its password works over the network (the image's scram-sha-256), and a wrong one doesn't.
    assert engine.exec(SERVER, "psql", "-X", "-t", "-A", url, "-c", "SELECT current_user").strip() == database
    try:
        engine.exec(SERVER, "psql", "-X", "-t", "-A", url.replace(f":{password}@", ":wrong@"), "-c", "SELECT 1")
        raise AssertionError("a wrong password signed in")
    except DockerError as e:
        assert "password authentication failed" in str(e), e
    # The section lists it; Copy connection string copies the URL (never shown on the page).
    section = open_section(gitberth, page, app)
    text = flat(section.inner_text())
    assert database in text and "DATABASE_URL" in text and f"on {SERVER}" in text and "By Gitberth" in text, text
    page.evaluate(COPY_SPY)
    section.get_by_role("button", name="Copy DATABASE_URL's connection string").click()
    gitberth.snackbar(page, "Copied DATABASE_URL's connection string")
    assert page.evaluate("() => window.__gbCopied") == [url]

    # ---- And a Redis of its own.
    said = add_database(gitberth, page, app, kind="Redis")
    alias = f"{app.slug}-redis"
    assert said.startswith(f"Added a Redis, {alias}."), said
    three = wait_live(gitberth, app, two["Id"])
    redis = f"{app.container}_redis"
    engine.wait_running(redis)
    redis_labels = engine.labels(redis)
    assert (redis_labels.get("gitberth.app"), redis_labels.get("gitberth.database")) == (app.slug, "redis"), redis_labels
    assert alias in engine.inspect(redis)["NetworkSettings"]["Networks"]["gitberth"]["Aliases"]
    assert f"{redis}-data" in [m.get("Name") for m in engine.mounts(redis)], engine.mounts(redis)
    redis_url = engine.env(app.container)["REDIS_URL"]
    redis_password = url_parts(redis_url)[2]
    assert redis_url == f"redis://:{redis_password}@{alias}:6379", redis_url
    secrets_seen.add(redis_password)
    assert engine.exec(redis, "redis-cli", "ping", env={"REDISCLI_AUTH": redis_password}).strip() == "PONG"
    refused = engine.exec(redis, "redis-cli", "ping", check=False)
    assert "NOAUTH" in refused or "WRONGPASS" in refused, refused
    # It's the app's: never an app of its own in the list.
    assert redis not in gitberth.app_list_keys(page)
    # Production's database stayed as it was.
    assert engine.env(app.container)["DATABASE_URL"] == url

    # ---- What the pages say.
    gitberth.open(page, f"/apps/{app.key}/environment")
    card = page.locator("#sec-env-databases")
    card.wait_for()
    said = flat(card.inner_text())
    assert f"From the Postgres database {database} on {SERVER}" in said and f"From its Redis, {alias}" in said and "DATABASE_URL" in said and "REDIS_URL" in said, said
    gitberth.open(page, f"/apps/{app.key}")
    overview = page.locator("section.gb-card").filter(has=page.locator(".gb-card-title", has_text="Databases"))
    overview.wait_for()
    assert database in flat(overview.inner_text()) and alias in flat(overview.inner_text()), overview.inner_text()

    # ---- Previews: each its own database and Redis; production keeps its own; closing the pull request drops them.
    said = gitberth.set_previews(page, app, mode="Pull requests")
    assert said.startswith("Saved"), said
    assert gitberth.db.app(app.slug)["PreviewDatabases"] == "Empty"
    number, sha = open_pull(repo, "feature", "Add a feature", marker="feature one")
    preview = gitberth.wait_preview(app.id, pr=number, timeout=120)
    deploy = wait_deployed(gitberth, preview["Id"], sha, timeout=420)
    preview_db = f"{database}_pr_{number}"
    log = gitberth.deploy_log(deploy["Id"])
    assert f"Making the preview's own database {preview_db} on {SERVER}, empty, for DATABASE_URL" in log, log
    preview_container = f"gb-{preview['Slug']}"
    preview_url = engine.env(preview_container)["DATABASE_URL"]
    scheme, user, preview_password, host, port, name = url_parts(preview_url)
    assert (user, host, name) == (preview_db, SERVER, preview_db), preview_url
    secrets_seen.add(preview_password)
    assert preview_db in databases_on_server(engine) and preview_db in roles_on_server(engine)
    assert engine.exec(SERVER, "psql", "-X", "-t", "-A", preview_url, "-c", "SELECT current_database()").strip() == preview_db
    preview_redis = f"{preview_container}_redis"
    engine.wait_running(preview_redis)
    preview_redis_url = engine.env(preview_container)["REDIS_URL"]
    assert preview_redis_url.endswith(f"@{preview['Slug']}-redis:6379"), preview_redis_url
    secrets_seen.add(url_parts(preview_redis_url)[2])
    assert engine.env(app.container)["DATABASE_URL"] == url and engine.env(app.container)["REDIS_URL"] == redis_url
    assert [r["Name"] for r in managed(gitberth, preview["Id"])] == [preview_db, f"{preview['Slug']}-redis"]
    # Closing a pull request moves no head: the watcher notices it at its regular look at GitHub's pull requests.
    repo.close_pull(number)
    gitberth.wait_no_preview(app.id, pr=number, timeout=PULLS_EVERY + 120)
    until(lambda: preview_db not in databases_on_server(engine) and preview_db not in roles_on_server(engine), 60,
          "the preview's database and role to be dropped", seen=lambda: sorted(databases_on_server(engine)))
    engine.wait_gone(preview_redis, timeout=60)
    assert database in databases_on_server(engine)

    # ---- The delete dialog lists the app's databases, their data kept unless ticked.
    gitberth.open(page, f"/apps/{app.key}/settings")
    page.locator("#sec-remove").get_by_role("button", name=re.compile("^Remove")).click()
    dialog = page.locator(".mud-dialog")
    dialog.wait_for()
    assert f"the Postgres database {database} on {SERVER}" in flat(dialog.inner_text()) and "Also delete their data" in flat(dialog.inner_text()), dialog.inner_text()
    dialog.get_by_role("button", name="Cancel").click()
    dialog.wait_for(state="detached")

    # ---- Remove, keeping the data: the database stays on the server; the app loses the variable.
    before = gitberth.db.last_deployment_id(app.id)
    said = remove_database(gitberth, page, app, database, delete_data=False)
    assert said.startswith(f"Removed the Postgres database {database}: its data stays on {SERVER}."), said
    wait_live(gitberth, app, before)
    assert "DATABASE_URL" not in engine.env(app.container)
    assert database in databases_on_server(engine) and database in roles_on_server(engine)

    # ---- Remove, deleting the data: another database, dropped with its role; the Redis, with its container and volume.
    before = gitberth.db.last_deployment_id(app.id)
    said = add_database(gitberth, page, app, variable="ANALYTICS_DATABASE_URL")
    second = f"{database}_2"
    assert said.startswith(f"Added the database {second} on {SERVER}."), said
    wait_live(gitberth, app, before)
    secrets_seen.add(url_parts(engine.env(app.container)["ANALYTICS_DATABASE_URL"])[2])
    assert second in databases_on_server(engine)
    said = remove_database(gitberth, page, app, second, delete_data=True)
    assert said.startswith(f"Deleted the Postgres database {second} and its data."), said
    assert second not in databases_on_server(engine) and second not in roles_on_server(engine)
    gitberth.wait_idle(app.id)
    said = remove_database(gitberth, page, app, alias, delete_data=True)
    assert said.startswith(f"Deleted its Redis ({alias}) and its data."), said
    engine.wait_gone(redis, timeout=60)
    assert f"{redis}-data" not in engine.volumes()
    assert managed(gitberth, app.id) == []
    gitberth.wait_idle(app.id)
    assert "REDIS_URL" not in engine.env(app.container)

    # ---- A create the server refuses halfway takes back what it made, and nothing else. CREATE DATABASE waits 5 s for
    # template1 to be free and fails while another session is on it: by then the database's role exists, and must go again
    # (psql says which statement failed only when it reads the script as a file).
    roles_before, databases_before = roles_on_server(engine), databases_on_server(engine)
    docker("exec", "-d", SERVER, "psql", "-X", "-U", "postgres", "-d", "template1", "-c", "SELECT pg_sleep(60)")
    until(lambda: psql(engine, "SELECT count(*) FROM pg_stat_activity WHERE datname = 'template1'") != ["0"], 30, "a session on template1")
    said = add_database(gitberth, page, app, variable="REFUSED_DATABASE_URL")
    assert said.startswith(f"{SERVER} refused the database {database}_2:") and "template1" in said, said
    assert (roles_on_server(engine), databases_on_server(engine)) == (roles_before, databases_before), \
        (sorted(roles_on_server(engine) ^ roles_before), sorted(databases_on_server(engine) ^ databases_before))
    assert managed(gitberth, app.id) == []
    psql(engine, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'template1' AND pid <> pg_backend_pid()")

    # ---- Any other app takes its database through the change engine: here the server's own app (from a registry) gets
    # a Redis, and its container is recreated with REDIS_URL, then without it.
    server_app = SimpleNamespace(key=SERVER)
    said = add_database(gitberth, page, server_app, kind="Redis")
    server_alias, server_redis = f"{SERVER}-redis", f"{SERVER}_redis"
    assert said.startswith(f"Added a Redis, {server_alias}. Postgres is recreated with REDIS_URL."), said
    engine.wait_running(server_redis)
    until(lambda: "REDIS_URL" in engine.env(SERVER) and engine.running(SERVER), 180, "the server to be recreated with REDIS_URL",
          seen=lambda: sorted(engine.env(SERVER)))
    secrets_seen.add(url_parts(engine.env(SERVER)["REDIS_URL"])[2])
    assert gitberth.db.app("postgres")["Source"] == "Registry"
    gitberth.open(page, f"/apps/{SERVER}/environment")
    env_row = page.locator("#sec-env .gb-envrow").filter(has_text="REDIS_URL").first
    env_row.wait_for()
    assert "Set by Gitberth" in flat(env_row.inner_text()) and env_row.locator("input").count() == 0, env_row.inner_text()
    said = remove_database(gitberth, page, server_app, server_alias, delete_data=False)
    assert said.startswith(f"Removed its Redis ({server_alias}): its data stays in the volume {server_redis}-data. Postgres is recreated without REDIS_URL."), said
    engine.wait_gone(server_redis, timeout=60)
    assert f"{server_redis}-data" in engine.volumes()
    until(lambda: "REDIS_URL" not in engine.env(SERVER) and engine.running(SERVER), 180, "the server to be recreated without REDIS_URL",
          seen=lambda: sorted(engine.env(SERVER)))
    docker("volume", "rm", f"{server_redis}-data")

    # ---- Nothing secret anywhere it's shown or kept; every change recorded, none of it as made outside Gitberth.
    secrets_seen.discard("")
    shown = []
    for path in (f"/apps/{app.key}/settings", f"/apps/{app.key}/environment", f"/apps/{app.key}", "/settings", f"/apps/{SERVER}"):
        gitberth.open(page, path)
        page.wait_for_timeout(1500)
        shown.append(page.content())
    logs = [gitberth.deploy_log(d["Id"]) for d in gitberth.db.deployments(app.id)] + [gitberth.deploy_log(deploy["Id"])]
    entries = since(first)
    kept = json.dumps(entries) + "\n".join(logs) + gitberth.logs(since=scenario.t0) + "\n".join(shown)
    leaked = [s for s in secrets_seen if s in kept]
    assert leaked == [], f"{len(leaked)} password(s) shown or kept"
    actions = [e["action"] for e in entries]
    for action, count in (("settings.databases.server.created", 1), ("app.database.added", 3), ("preview.database.created", 2),
                          ("preview.database.removed", 2), ("app.database.removed", 3)):
        assert actions.count(action) >= count, (action, [(e["action"], e["summary"]) for e in entries if "database" in e["action"]])
    added = [e for e in entries if e["action"] == "app.database.added"]
    assert added[0]["summary"] == f"Added the Postgres database {database} on {SERVER} to {app.name}: its connection string is in DATABASE_URL.", added[0]
    assert (added[0]["actor_kind"], added[0]["actor"], added[0]["via"]) == ("User", "admin", "UI"), added[0]
    for container in (SERVER, redis, preview_redis, server_redis):
        no_network_change_outside(container, first, wait=0 if container != SERVER else 35)
    outside = [(e["action"], e["container"], e["summary"]) for e in since(first, "actor_kind = 'Docker'")
               if (e["container"] or "") in (SERVER, redis, preview_redis, server_redis, app.container, preview_container)]
    assert outside == [], outside
    assert not gitberth.errors(page), gitberth.errors(page)

    remove_server(gitberth, page, engine)
