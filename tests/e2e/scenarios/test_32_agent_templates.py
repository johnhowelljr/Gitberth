"""Scenario 32: an AI agent installs an app template (1.3).

An Admin token, over MCP:
- list_templates lists the catalog as the gallery has it, each template with its inputs and what it needs;
- a template that needs Postgres, with no server yet, is refused with what to do: create_postgres_server, which sets one
  up as Settings › Databases does (recorded against the token) and, asked again, says there's one already;
- create_app with source template installs Miniflux with an input of the agent's: its database and its generated password
  are made as the gallery makes them, wait follows its first deploy to the end, and it answers on its LAN port;
- no tool's answer carries its password or its database's, and the audit log has the app made from its template by the
  token, over MCP;
- delete_app refuses without the app's name in confirm; with it, and its volumes, images and database's data, nothing is
  left behind.
"""
import json
from urllib.parse import unquote, urlsplit

from e2e import audit
from e2e.net import get
from e2e.wait import until
from test_27_databases import SERVER, databases_on_server, remove_server, roles_on_server
from test_29_mcp import make_token, tool
from test_30_templates import TemplateApp, leftovers


def test_an_agent_installs_a_template_with_its_database_and_deletes_it(page, gitberth, engine, apps, uniq):
    first = audit.last_id()
    name = uniq("installer")
    token, _ = make_token(gitberth, page, name, "Admin")
    answers = []

    def call(tool_name, args=None, timeout=60):
        error, result = tool(gitberth, token, tool_name, args, timeout=timeout)
        answers.append(result if isinstance(result, str) else json.dumps(result))
        return error, result

    # ---- The catalog, as the gallery has it.
    error, listed = call("list_templates")
    assert not error, listed
    templates = {t["id"]: t for t in listed["templates"]}
    assert len(templates) >= 20 and {"miniflux", "memos", "grafana"} <= set(templates), sorted(templates)
    flux = templates["miniflux"]
    assert flux["needs"]["postgres_server"] is True and flux["web"]["port"] == 8080, flux
    assert [i["name"] for i in flux["inputs"]] == ["ADMIN_USERNAME"] and flux["generated_secrets"] == ["ADMIN_PASSWORD"], flux

    # ---- No Postgres server yet: refused with what to do, then set up by the agent.
    made_server = False
    if not engine.exists(SERVER):
        error, why = call("create_app", {"source": "template", "template": "miniflux"})
        assert error and "create_postgres_server" in why, why
        assert gitberth.db.query("SELECT Id FROM Apps WHERE TemplateId = 'miniflux'") == []
        error, made = call("create_postgres_server", {"wait": True}, timeout=660)
        made_server = not error
        assert not error, made
        assert made["created"] is True and made["server"] == SERVER, made
        until(lambda: audit.since(first, "action = 'settings.databases.server.created' AND actor_kind = 'ApiToken' AND actor = ?", name), 30,
              "the server's setup in the audit log, against the token")
    try:
        error, again = call("create_postgres_server")
        assert not error and again["created"] is False and again["server"] == SERVER, again

        # ---- Install: its database and password made, its first deploy followed to the end.
        error, installed = call("create_app", {"source": "template", "template": "miniflux", "inputs": {"ADMIN_USERNAME": "boss"}, "wait": True},
                                timeout=900)
        assert not error, installed
        assert installed["template"] == "miniflux" and installed["deploy"]["status"] == "Succeeded", installed
        assert installed["deploy"]["requested_by"] == f"the token {name}", installed
        assert any("ADMIN_PASSWORD" in n for n in installed["notes"]), installed
        row = gitberth.db.app_by_id(installed["id"])
        app = apps.track(TemplateApp(row["Id"], row["Slug"], row["Name"], row["WebService"]))
        assert (row["Source"], row["TemplateId"], installed["key"]) == ("Compose", "miniflux", app.key), (row, installed["key"])
        dbs = gitberth.db.query("SELECT * FROM ManagedDatabases WHERE AppId = ?", app.id)
        assert [(d["Kind"], d["Server"], d["Variable"]) for d in dbs] == [("Postgres", SERVER, "DATABASE_URL")], dbs
        database, role = dbs[0]["Name"], dbs[0]["Role"]
        env = engine.env(app.container)
        assert env["ADMIN_USERNAME"] == "boss"
        password, db_password = env["ADMIN_PASSWORD"], unquote(urlsplit(env["DATABASE_URL"]).password or "")
        lan = gitberth.lan_url(app.id)
        until(lambda: get(lan + "/healthcheck")[0] == 200, 120, "Miniflux on its LAN port", seen=lambda: get(lan + "/healthcheck"))

        # ---- What an agent reads about it never carries a secret; the audit log has it made by the token.
        for tool_name, args in (("get_app", {"app": app.key}), ("list_databases", {"app": app.key}),
                                ("get_deployment", {"deployment_id": installed["deploy"]["deployment_id"]})):
            error, _ = call(tool_name, args)
            assert not error, tool_name
        leaked = [s for s in (password, db_password) if s and any(s in a for a in answers)]
        assert leaked == [], f"{len(leaked)} secret(s) in a tool's answer"
        created = until(lambda: audit.since(first, "action = 'app.created' AND app_id = ?", app.id), 30, "the app's creation in the audit log")
        assert (created[0]["actor_kind"], created[0]["actor"], created[0]["via"]) == ("ApiToken", name, "Mcp"), created
        assert "from the template miniflux" in created[0]["summary"], created

        # ---- Delete, with its name to confirm: nothing is left, its database and role neither.
        error, why = call("delete_app", {"app": app.key, "remove_volumes": True, "remove_images": True, "remove_databases": True})
        assert error and "confirm" in why, why
        error, gone = call("delete_app", {"app": app.key, "confirm": app.name, "remove_volumes": True, "remove_images": True,
                                          "remove_databases": True}, timeout=300)
        assert not error, gone
        until(lambda: not any(leftovers(engine, app).values()), 120, "the app to leave nothing behind", seen=lambda: leftovers(engine, app))
        assert database not in databases_on_server(engine) and role not in roles_on_server(engine)
        assert gitberth.db.query("SELECT * FROM ManagedDatabases WHERE AppId = ?", app.id) == []
    finally:
        if made_server:
            remove_server(gitberth, page, engine)
