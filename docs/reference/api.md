# Tools for AI agents and the HTTP API

Gitberth gives AI agents (over MCP, at `/mcp`) and scripts (over plain HTTP, at `/api/v1`) the same tools: what each one
does, the access a token needs for it, and its arguments. Each tool calls the same service the UI's button or form does,
with the same checks, so an agent can't do anything a signed-in person couldn't, and every change it makes is in the
[audit log](../guides/audit-log.md) under the token's name. To make a token and connect a client, see
[AI agents and the API](../guides/ai-agents.md).

## Calling a tool

Every request carries a token: `Authorization: Bearer gbt_…`. Requests from a browser page (with an `Origin` header) are
refused with 403.

| | |
|---|---|
| `POST /mcp` | The MCP server: stateless Streamable HTTP. `initialize`, `tools/list` (only the tools the token may call) and `tools/call`. |
| `GET /api/v1/tools` | Every tool, whatever the token's access: name, title, description, the access it needs, whether this token may call it (`allowed`: `false` for one above its access, which answers 403), its hints and its JSON input schema. |
| `POST /api/v1/tools/<name>` | Calls a tool. The body is a JSON object of its arguments (`{}` for none), each named once; an argument the tool doesn't take is refused. The answer is the tool's JSON result. |
| `GET /api/v1/openapi.json` | An OpenAPI 3.1 document of the tools, for agent frameworks and API clients that read one. |

```bash
curl -s -X POST http://192.168.50.10:8080/api/v1/tools/deploy_app \
  -H "Authorization: Bearer gbt_…" -H "Content-Type: application/json" \
  -d '{"app": "orders-api", "wait": true}'
```

Name an app by its key from `list_apps` (`gb-orders-api`), its name (`orders-api`) or its number. Results are compact JSON with
snake_case names; a secret variable comes back by name only, and no tool returns a token, a password, a connection
string or a deploy hook's URL (except the new hook's, once, from `create_deploy_hook`).

Deploys and updates run in the background: a tool returns at once with the `deployment_id`, which `get_deployment`
follows. Pass `wait: true` to have it follow the deploy for up to 10 minutes and return how it ended, with the last lines
of its log; one still going after 10 minutes comes back as an ordinary answer with `still_running: true` and its number.

Logs come back bounded: a line longer than 8 KB is cut (it ends with `…`), and at most about 1 MB of lines is returned,
the oldest dropped first; `truncated: true` says when either happened. A `grep` is plain text (case-insensitive), or a
regular expression between slashes (`/timeout|refused/`) of up to 200 characters, which runs without backtracking: no
backreferences or lookarounds. `get_deployment` greps the last 5000 lines of a deploy's log.

Tools that stop, delete or roll back (`stop_app`, `rollback_app`, `delete_app`, `remove_database` with its data,
`remove_domain`, and `set_previews` turning previews off while the app has some) need `confirm` set to the app's name
(or the domain), the way the UI asks for it to be typed.

### Errors

Over MCP a refusal or failure is a tool error whose text is the plain sentence the UI shows. Over HTTP it's
`{"error": "…"}` with a status code:

| Status | When |
|---|---|
| 400 | The body isn't a JSON object (or names an argument twice), the arguments aren't what the tool takes, or it refused them (a missing `confirm`, say). |
| 401 | No token, or one that's unknown, revoked or expired; the body says which. `WWW-Authenticate` is `Bearer realm="Gitberth"`, with `error="invalid_token"` and a fixed `error_description` for a token that was sent but isn't accepted. |
| 403 | The token's access doesn't allow the tool, or the request came from a browser page. |
| 404 | No such tool, or what it names (an app, a deploy, a domain) doesn't exist. |
| 409 | It can't be done now, as the UI would say (an app being changed, a deploy that didn't succeed). |
| 429 | Too many requests with a bad token from this address: wait a few minutes. A good token always gets in. |
| 500 | Something failed on Gitberth's side; its log says what. Still `{"error": "…"}`. |

## Access levels

| Access | What a token with it may call |
|---|---|
| **Read** | Every read tool: apps, logs, stats, deploys, previews, health, domains, uptime, the audit log, databases, updates, settings. |
| **Operate** | Read, plus deploy, restart, start, stop, roll back, cancel a deploy, promote a preview, check for and apply updates, run a cron job now, and previews on or off. |
| **Admin** | Every tool below. It's full control of this host, like signing in: it can create apps whose containers reach the whole host, so treat an Admin token like the admin password. |

No tool makes, lists or revokes tokens, changes the admin password, or stops the audit log recording Docker changes made
outside Gitberth. That's a promise about the tools: only for Read and Operate tokens is it the whole story, since an app
an Admin token creates can reach the whole host.

<!-- tools:start (made from the tools themselves: a unit test checks it) -->

## Read tools

### `list_apps`

Every app on this Gitberth host (Git apps, registry and local images, Compose apps, containers made by hand), as the Apps page lists them: its key (use it to name the app in other tools), name, source, state, image or commit, address, uptime and whether an update is waiting. Previews aren't listed here: list_previews lists an app's.

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `filter` | string | no | Only apps whose name, key, image or repo contains this text (case-insensitive). Leave out for every app. |

### `get_app`

Everything the app's page shows: state, source (repo, branch and commit, or image), addresses and domains, ports, environment variables (secret ones by name only: their values are never returned), volumes, resources, restart policy, health check, build filters, previews, databases, deploy hooks (names only), Compose services, cron jobs, update status, uptime, the down page, and the latest deploys.

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key from list_apps (gb-web), its name, or its number. |

### `get_app_logs`

The app's container logs (stdout and stderr, with timestamps), newest last, as its Logs tab shows them. Filter with grep, limit with tail and since. A Compose app's logs are its web service's unless you name another service. A line longer than 8 KB is cut, and at most about 1 MB comes back (the oldest lines go first): truncated says when.

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `tail` | integer | no | How many of the last lines to read (1 to 5000). Default 200. |
| `since` | string | no | Only lines since then: a duration like 10m, 2h or 1d, or a UTC time like 2026-11-15T10:00:00Z. |
| `grep` | string | no | Only lines that contain this text (case-insensitive), or match it as a regular expression when it starts and ends with / (at most 200 characters; no backreferences or lookarounds). |
| `service` | string | no | A Compose app's service whose logs to read; leave out for its web service. |

### `get_app_stats`

The app's CPU, memory and network now, and its last hour in 5-minute averages, as its Overview's charts show them. CPU is a percentage of one core (200 = two cores busy).

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |

### `list_deployments`

A Git or Compose app's deploys, newest first: number, status, trigger, commit, who asked, and when. get_deployment reads one with its log; rollback_app goes back to one that succeeded.

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `limit` | integer | no | How many (1 to 100). Default 20. |

### `get_deployment`

One deploy: its status, what it built, how it ended, and its log (secrets are masked in it). The log is its last lines; grep picks lines from its last 5000. A deploy still running shows its log so far. A line longer than 8 KB is cut, and at most about 1 MB comes back: truncated says when.

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `deployment_id` | integer | yes | The deploy's number (deployment_id from deploy_app or list_deployments). |
| `tail` | integer | no | How many of the log's last lines (1 to 5000). Default 100. |
| `grep` | string | no | Only log lines that contain this text (case-insensitive), or match it as a regular expression when it starts and ends with / (at most 200 characters; no backreferences or lookarounds). |

### `list_previews`

A Git app's preview deployments (one per branch or pull request, as its Previews tab lists them): key, branch or pull request, state, commit, address, when it expires, and whether it can be promoted to production.

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The parent app: its key, name or number. |

### `get_health`

How this host is doing: the Docker engine (version, CPUs, memory), the disk Docker uses, Gitberth's version, and the problems worth knowing about now: apps crashed, unhealthy, missing or down, deploys failing, a full disk.

Access: **Read** · read-only

No arguments.

### `list_domains`

The domains apps are served on through the Cloudflare Tunnel: which app, main or not, protected by Cloudflare Access or not. Name an app to have each of its domains checked as its Domains card does (working or not, and why).

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | no | Only this app's domains, checked live: its key, name or number. Leave out for every domain. |

### `get_uptime`

Uptime checks, as the app's Uptime card shows them: up or down now, the last check, uptime over 24 hours, 7 and 30 days, and incidents. Without an app: every app's state now and the public status page's data (when it's on).

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | no | The app: its key, name or number. Leave out for every app and the status page. |

### `query_audit_log`

The audit log: every change to this Gitberth, who made it (a person, a token, a hook, GitHub or GitLab, Gitberth itself, or something outside Gitberth), how and with what outcome, newest first, with the same filters as its page. Page with before_id (the last id you got).

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `range` | string | no | How far back: day, week (default), month or all. |
| `who` | string | no | Who made the changes, as kind:name, like User:admin, ApiToken:my-agent, DeployHook:cms, GitHub:GitHub, GitLab:GitLab, Docker:outside Gitberth. |
| `app` | string | no | Only this app's entries: its key, name or number. |
| `area` | string | no | Only one part: apps, deploys, domains, settings, signins or outside (Docker changes made outside Gitberth). |
| `outcome` | string | no | Only entries that ended so: succeeded, failed or denied. |
| `text` | string | no | Words in the summary, the target, the actor, the app or the action. |
| `limit` | integer | no | How many entries (1 to 500). Default 50. |
| `before_id` | integer | no | For the next page: only entries numbered below this one. |

### `list_databases`

Apps' databases (Postgres databases and Redis that Gitberth made or took over): kind, server, name, the variable their connection string is set in, and size. Never a connection string or a password. Without an app: the Postgres servers on this host and every app's databases.

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | no | Only this app's databases: its key, name or number. |

### `list_updates`

Apps that run a registry's image, and whether a newer image is waiting for each, as Gitberth last checked (check_updates asks the registry again; apply_update updates one).

Access: **Read** · read-only

No arguments.

### `list_images`

The images on this host with a name, newest first, as New app's local images list them: tags, size, age, how many containers use each, and whether Gitberth built it (a Git app's build).

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `filter` | string | no | Only images whose tag contains this text (case-insensitive). |

### `list_templates`

Gitberth's app templates, as New app's Templates gallery shows them: what each one is, what it needs (a Postgres server, Redis, memory), and the inputs create_app takes for it (source template). The secrets it gets are made when it's installed, and never shown.

Access: **Read** · read-only

| Argument | Type | Required | What it is |
|---|---|---|---|
| `query` | string | no | Only templates with every word of this in their name, description, category or id (any case). |
| `category` | string | no | Only this category, like Monitoring (categories lists them). |

### `get_settings`

Gitberth's own settings, as its Settings page shows them, without a secret: notifications (where to by name and kind only, and about what), updates, domains, uptime checks and the status page, deploys, the audit log, and the version.

Access: **Read** · read-only

No arguments.

## Operate tools

### `deploy_app`

Deploys a Git app (clone, build, start, zero-downtime where it can) or a Compose app (its images pulled anew), like the Deploy button: production always builds the app's own branch, its newest commit, or with commit one earlier commit of that branch (or one production ran before), to go back or forward to it. A preview's code reaches production only through promote_preview. Returns at once with the deployment_id; with wait it follows the deploy (up to 10 minutes) and returns how it ended with the log's last lines. An app that runs a registry's image isn't deployed: use apply_update.

Access: **Operate**

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `no_cache` | boolean | no | Build without the build cache: every step runs again and base images are pulled. |
| `commit` | string | no | A Git app: build exactly this commit of the app's own branch (its SHA, 7 to 40 hex characters) instead of its newest. Only a commit on the branch, or one production ran before, is built: any other fails when it's fetched. |
| `wait` | boolean | no | Wait for the deploy to end (up to 10 minutes) and return its outcome. |

### `restart_app`

Restarts the app's container (a Compose app: every service), like Restart on its page. Nothing is rebuilt or recreated.

Access: **Operate**

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |

### `start_app`

Starts a stopped app, like Start on its page. A Git app whose settings changed while it was stopped is recreated with them; one whose container is gone is made again (from its last image, or built).

Access: **Operate** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |

### `stop_app`

Stops the app until it's started again (its domains show the down page meanwhile, and a Git app's new commits aren't deployed). Destructive for whoever uses it: pass confirm with the app's name, exactly as get_app or list_apps names it.

Access: **Operate** · destructive: needs `confirm`, idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `confirm` | string | no | The app's name, repeated, to show the stop is meant. |

### `rollback_app`

Rolls the app back. A Git or Compose app goes back to an earlier successful deploy whose image is still kept (list_deployments says can_roll_back): pass its deployment_id. An app that runs a registry's image goes back to the image it ran before its last update: leave deployment_id out. Pass confirm with the app's name. Returns at once (with the new deployment_id, for a Git app); with wait it follows it to the end.

Access: **Operate** · destructive: needs `confirm`

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `confirm` | string | no | The app's name, repeated, to show the rollback is meant. |
| `deployment_id` | integer | no | The earlier deploy to go back to (a Git or Compose app's). |
| `wait` | boolean | no | Wait for it to end (up to 10 minutes) and return the outcome. |

### `cancel_deploy`

Cancels the app's running deploy, like Cancel on the deploy's page. The app keeps running what it ran before.

Access: **Operate** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |

### `promote_preview`

Deploys a preview's commit to production, like Promote on the Previews tab: production runs that commit until its branch gets a newer one. Name the preview by its key from list_previews.

Access: **Operate**

| Argument | Type | Required | What it is |
|---|---|---|---|
| `preview` | string | yes | The preview: its key (from list_previews) or number. |
| `wait` | boolean | no | Wait for production's deploy to end (up to 10 minutes) and return the outcome. |

### `check_updates`

Asks the registry whether a newer image is waiting for an app that runs a registry's tag, like Check for update on its page. Nothing is pulled or changed.

Access: **Operate** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |

### `apply_update`

Updates an app that runs a registry's tag, like Update on its page: pulls the tag and recreates the app on the new image, putting the old one back if the new one doesn't stay up. Returns once it started; with wait it follows it to the end.

Access: **Operate**

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `wait` | boolean | no | Wait for the update to end (up to 10 minutes) and return the outcome. |

### `run_cron_now`

Runs one of the app's cron jobs (from its vercel.json) now, like Run now on its Scheduled jobs card, and returns how it went. get_app lists the jobs.

Access: **Operate**

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `path` | string | yes | The job's path, as get_app's cron_jobs gives it (/api/cron/cleanup). |

### `set_previews`

Turns a Git app's preview deployments on or off, as the choice at the top of its Settings tab's Previews section does: off, pull_requests, or pull_requests_and_branches. Nothing else about previews changes (set_preview_settings changes the rest, with Admin access). Turning previews off removes every preview the app has, with its containers, images, volumes, domains and databases: when it has any, pass confirm with the app's name.

Access: **Operate** · destructive: needs `confirm`, idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `mode` | string | yes | off, pull_requests, or pull_requests_and_branches. |
| `confirm` | string | no | When mode is off and the app has previews: the app's name, repeated, to show removing them is meant. |

## Admin tools

### `create_app`

Creates an app as New app does, checked the same way, and deploys or starts it. Sources: git (a GitHub repo, or a project on Gitberth's GitLab (Settings › GitLab: gitlab.com unless another is set), groups and subgroups too, built with what Gitberth detects, or with its Compose file when it has one: give repo, and maybe branch and subdirectory); image (a registry's image like nginx:1.27 or ghcr.io/owner/app:latest: give image); compose (a whole Compose file deployed as one app: give compose_yaml, and maybe web_service); template (one of Gitberth's app templates, as list_templates lists them: give template, its inputs, and maybe a domain; its databases and secrets are made as its page in New app makes them, and a template that needs Postgres needs a Postgres server first: create_postgres_server). Returns the app's key, and the deployment_id of a Git, Compose or template app's first deploy (wait follows it).

Access: **Admin**

| Argument | Type | Required | What it is |
|---|---|---|---|
| `source` | string | yes | git, image, compose or template. |
| `name` | string | no | The app's name; leave out to take the repo's, the image's or the Compose file's. |
| `repo` | string | no | git: the repository, like https://github.com/owner/repo or https://gitlab.com/group/subgroup/project (a link to a branch or folder works too). |
| `branch` | string | no | git: the branch to deploy; the repo's default when left out. |
| `subdirectory` | string | no | git: the folder the app is in, when it isn't the repo's root. |
| `use_compose` | boolean | no | git: true to deploy with the repo's Compose file, false to build one container; leave out to choose as New app does. |
| `image` | string | no | image: the image reference, like nginx:1.27. |
| `compose_yaml` | string | no | compose: the whole Compose file (YAML). |
| `web_service` | string | no | git with Compose, or compose: the service that's the app's web service (domains, LAN port, health check); leave out to pick as New app does. |
| `env` | array of objects | no | Variables to set (Git and Compose apps: the Compose file's variables too). |
| `template` | string | no | template: the template's id, as list_templates gives it (like uptime-kuma). |
| `inputs` | object | no | template: its inputs by name, as list_templates lists them, like {"ADMIN_USER": "admin"}. An input left out takes its default. |
| `domain` | string | no | template: a domain for it, like wiki.example.com, when domains are on (Settings › Domains). Its address variables (APP_URL) follow its main domain, else its LAN address. A template whose first visitor sets it up (open_until_setup in list_templates) takes a domain only behind Cloudflare Access (Settings › Protection), until a person turns that off. |
| `wait` | boolean | no | Wait for the first deploy or start to end (up to 10 minutes). |

### `delete_app`

Deletes an app as its page's Delete does: its container goes, its domains come out of the tunnel, and (when asked) its images, volumes and databases' data. A preview goes with everything of its own. Pass confirm with the app's name. This can't be undone.

Access: **Admin** · destructive: needs `confirm`

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `confirm` | string | no | The app's name, repeated, to show the delete is meant. |
| `remove_images` | boolean | no | Also delete its images. |
| `remove_volumes` | boolean | no | Also delete its volumes (their data is gone for good). |
| `remove_databases` | boolean | no | Also delete its databases' data (Settings › Databases). A preview's always go. |

### `set_env_vars`

Sets and removes an app's environment variables, as its Environment tab does, and applies them: a Git app is redeployed without a build (or rebuilt when a build-time variable changed), any other app's container is recreated with them. Secret values are written and never returned. Variables Gitberth sets from the app's databases aren't changed here.

Access: **Admin** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `set` | array of objects | no | Variables to add or change. |
| `remove` | array of strings | no | Names of variables to remove (every scope of the name, unless remove_scope says one). |
| `remove_scope` | string | no | Git apps: only remove the names' values for this scope: all, production or preview. |
| `next_deploy` | boolean | no | Git apps: save them for the next deploy instead of deploying now. |
| `wait` | boolean | no | Wait for the change (or its deploy) to end, up to 10 minutes. |

### `update_app_settings`

Changes an app's settings, as its Settings and Network tabs do, and applies them the way the page would (live where Docker can, else a redeploy or a recreate): name, health check path, memory and CPU limits, restart policy, updates policy, a Git app's auto-deploy and build filters, the down page and its message, the uptime check, or the whole list of ports (on its own: not with other container settings in the same call). Only what's given changes. Previews: set_previews and set_preview_settings.

Access: **Admin** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `name` | string | no | The app's name as Gitberth shows it. |
| `health_path` | string | no | The path the health check asks for, like /health; empty for none. |
| `memory_mb` | integer | no | Memory limit in MiB; 0 for none. |
| `cpus` | number | no | CPU limit in cores, like 0.5 or 2; 0 for none. |
| `restart_policy` | string | no | no, always, unless-stopped or on-failure. |
| `restart_retries` | integer | no | With on-failure only: how many times Docker restarts it before giving up (0: no limit). |
| `updates` | string | no | Image apps: off (when I ask), notify (tell me) or auto (update by itself). |
| `auto_deploy` | boolean | no | Git apps: deploy new commits by themselves. |
| `watch_paths` | string | no | Git apps: build filters, one pattern per line: a commit deploys by itself only when it changes a file these match. Empty for any file. |
| `ignored_paths` | string | no | Git apps: a commit whose changed files all match these patterns doesn't deploy by itself. Empty for none. |
| `down_page` | boolean | no | Show the down page on the app's domains while it can't be served. |
| `down_page_message` | string | no | The down page's message; empty for none. |
| `uptime_check` | boolean | no | Check the app from outside (its main domain, else its LAN address). |
| `uptime_path` | string | no | The path the uptime check asks for; empty for /. |
| `ports` | array of objects | no | The app's ports, as a whole new list. |
| `next_deploy` | boolean | no | Git apps: save container changes for the next deploy instead of deploying now. |
| `wait` | boolean | no | Wait for the change (or its deploy) to end, up to 10 minutes. |

### `set_preview_settings`

Changes a Git app's preview settings, as its Settings tab's Previews section does: how many previews run at most, the days without a new commit before one is removed, whether pull requests from forks get previews, whether previews' domains are behind Cloudflare Access, and each preview's database (none of its own, an empty one, or a copy of production's). Only what's given changes; previews on or off is set_previews. Forks: Whoever opens a pull request from a fork decides what its preview builds and runs here, on your network. Turn this on only if you trust everyone who can open one. A copy of production's database: A copy holds whatever production's database holds, people's personal data too, and a preview runs a pull request's code with it. Choose it only when everyone who can open a pull request may see that data.

Access: **Admin** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `limit` | integer | no | How many previews run at most (1 to 20); more wait for one to be removed. |
| `expiry_days` | integer | no | Days without a new commit before a preview is removed (1 to 365). |
| `forks` | boolean | no | Whether pull requests from forks get previews: anyone's code, built and run on this server. |
| `protect` | boolean | no | Whether previews' domains are behind Cloudflare Access (visitors sign in first). |
| `databases` | string | no | Each preview's database: none (none of its own), empty (an empty one, the default) or copy_of_production. |

### `add_database`

Gives the app a database of its own, as its Settings tab › Databases does: a Postgres database and role on a Postgres server, or a Redis of its own. Its connection string goes in the variable (DATABASE_URL or REDIS_URL by default), and the app is redeployed or recreated with it. The connection string is never returned.

Access: **Admin**

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `kind` | string | yes | postgres or redis. |
| `variable` | string | no | The variable the connection string goes in; DATABASE_URL for Postgres, REDIS_URL for Redis when left out. |
| `server` | string | no | Postgres: the server's container (list_databases lists them); the default server when left out. |

### `remove_database`

Takes a database off the app, as Remove on its Settings tab › Databases does: its variable goes, and the app is redeployed or recreated without it. With delete_data its data is deleted for good (only a database Gitberth made): then pass confirm with the app's name.

Access: **Admin** · destructive: needs `confirm`

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `database` | string | yes | The database: its id (list_databases) or its variable (DATABASE_URL). |
| `delete_data` | boolean | no | Delete its data too. Without it the data stays on the server. |
| `confirm` | string | no | With delete_data: the app's name, repeated, to show it's meant. |

### `create_deploy_hook`

Makes a deploy hook for the app, as its Settings tab › Deploy hooks does: a secret URL that deploys it (a Git or Compose app) or updates it (a registry image app) when called with POST. The URL is returned this once: keep it as you would a password.

Access: **Admin**

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `name` | string | yes | What will call it, like Contentful or Nightly build. |

### `delete_deploy_hook`

Deletes one of the app's deploy hooks: its URL answers 404 from then on.

Access: **Admin** · destructive: needs `confirm`, idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `hook` | string | yes | The hook: its name or id (get_app lists them). |

### `add_domain`

Serves the app on a domain through the Cloudflare Tunnel, as Add domain on its Domains card does: the tunnel's route and the DNS record are written in Cloudflare. Needs domains on in Settings, and a web port on the app. When the tunnel reaches apps through a host port, the app's web port is published on the host first (it may restart once).

Access: **Admin**

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `hostname` | string | yes | The domain, like shop.example.com. |

### `remove_domain`

Takes a domain off the app, as Remove on its Domains card does: its route leaves the tunnel and the DNS record Gitberth added goes. The app keeps running. Pass confirm with the domain.

Access: **Admin** · destructive: needs `confirm`, idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `hostname` | string | yes | The domain to remove. |
| `confirm` | string | no | The domain, repeated, to show it's meant. |

### `set_main_domain`

Makes one of the app's domains its main one: the address Gitberth gives the app (VERCEL_URL and friends) with its next build, and the one uptime checks ask.

Access: **Admin** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `hostname` | string | yes | One of the app's domains. |

### `protect_domain`

Puts one of the app's domains behind Cloudflare Access (visitors sign in with an emailed code; only Settings › Domains › Protection's addresses get in), or takes it out again. Anything that calls a protected domain without a browser can't any more.

Access: **Admin** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `app` | string | yes | The app: its key, name or number. |
| `hostname` | string | yes | One of the app's domains. |
| `on` | boolean | yes | true to protect it, false to open it to everyone again. |

### `set_notifications`

Chooses what Settings › Notifications sends to its destinations (where they go is set by a person, with their addresses and tokens). Only what's given changes.

Access: **Admin** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `deploy_failed` | boolean | no | When a deploy fails. |
| `deploy_succeeded` | boolean | no | When a deploy succeeds. |
| `crashed` | boolean | no | When an app crashes. |
| `unhealthy` | boolean | no | When a health check fails (and passes again). |
| `down` | boolean | no | When an uptime check finds an app down (and back up). |
| `certificate_expiring` | boolean | no | When a domain's certificate is about to expire. |
| `previews` | boolean | no | About previews' deploys too. |

### `set_update_settings`

Changes Settings › Updates: how often apps' images are checked for updates, when automatic updates run (in Gitberth's time zone), and the policy new image apps start with. Only what's given changes.

Access: **Admin** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `check_every_hours` | integer | no | Check every this many hours (1 to 168). |
| `automatic_window` | string | no | Automatic updates run between these times, like 03:00-05:00. |
| `default_policy` | string | no | New apps from an image: off (when I ask), notify (tell me) or auto. |

### `set_uptime_settings`

Changes how often every app is checked from outside (Settings › Uptime and status page). An app's own check is turned on or off with update_app_settings.

Access: **Admin** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `interval_seconds` | integer | yes | Check every this many seconds (30 to 900). |

### `set_status_page`

Changes the public status page (Settings › Uptime and status page): on or off, its title and description, which apps it lists, whether it shows response times, and the host name it's served on through the tunnel. Only what's given changes.

Access: **Admin** · idempotent

| Argument | Type | Required | What it is |
|---|---|---|---|
| `enabled` | boolean | no | Serve the status page (on the LAN at /status, and on its host name). |
| `title` | string | no | Its title. |
| `description` | string | no | A short description under the title; empty for none. |
| `apps` | array of strings | no | The apps it lists (keys, names or numbers); an empty list for every app with uptime checks. |
| `response_times` | boolean | no | Show each app's response time. |
| `host` | string | no | The host name it's served on through the tunnel, like status.example.com; empty for none. |

### `create_postgres_server`

Sets up a Postgres server for apps' databases, as Settings › Databases' Create a Postgres server does: an app called postgres (or a free variant) running postgres:18, on Gitberth's network with no published port, its password a secret variable. New databases go on it when no other server was chosen. Templates that need Postgres need one, and so does add_database's Postgres; list_databases says whether there is one.

Access: **Admin**

| Argument | Type | Required | What it is |
|---|---|---|---|
| `wait` | boolean | no | Wait for it to be set up (pulled, created and checked), up to 10 minutes. |

<!-- tools:end -->

## Related

- [AI agents and the API](../guides/ai-agents.md): making a token and connecting Claude Code, VS Code, Cursor, Claude Desktop or a script.
- [Gitberth settings](gitberth-settings.md#ai-agents-and-api): the **AI agents and API** section.
- [Audit log](../guides/audit-log.md): what every token did.
