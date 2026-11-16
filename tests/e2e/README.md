# End-to-end tests

The unit tests use stand-ins inside the test process. These tests run the real Gitberth image, built from this tree, in
a Docker engine of its own, against stand-ins for GitHub (git and its API), GitLab (git and its API) and Cloudflare (its
API and cloudflared). A browser drives the UI, and each scenario checks what really happened: containers, images,
volumes and environments in the engine, and the calls the stand-ins recorded.

Gitberth and the scenario runner never reach the real GitHub, GitLab or Cloudflare: those names (gitlab.com, and
gitlab.e2e.test for a self-managed GitLab) lead to the stand-ins, whose test CA they trust. The rig's engine itself
resolves names as usual, though. So image pulls go to the real registries
(Docker Hub, and ghcr.io for the uv image in Gitberth's Python Dockerfiles), and a build step or an app that fetched
something from github.com would reach the real one. The test app does neither, and an app a scenario deploys mustn't
either.

## Running it

**Testing a change takes 10 minutes in all**: the unit tests, the rigs' setup, and the scenario tests the change
touches. **The whole suite is on demand**, before a release: about 25 minutes on GitHub Actions, or about an hour on
CT 101's two rigs.

On a Docker host that allows privileged containers:

```sh
sh tests/e2e/run.sh impact --base origin/next --lanes 2   # the default way to test a change: 10 minutes in all
sh tests/e2e/run.sh plan --base origin/next --lanes 2     # what that would run, and why; touches nothing
sh tests/e2e/run.sh smoke --lanes 2                       # the smoke (6 tests)
sh tests/e2e/run.sh groups 29 12+slow @t28-paste          # the groups and aliases named (impact.toml)
sh tests/e2e/run.sh groups 31 --templates memos,miniflux  # the template smoke, for those templates only
sh tests/e2e/run.sh wide | full | release --lanes 2       # the hub set; every group (slow tests too); full + every template
sh tests/e2e/run.sh run-plan plan.tsv                     # a plan made elsewhere (impact.py plan --format tsv)
sh tests/e2e/run.sh unit                                  # the unit tests, in mcr.microsoft.com/dotnet/sdk:10.0
sh tests/e2e/run.sh test [pytest args]                    # one rig, pytest as given (no args: every group but 31)
sh tests/e2e/run.sh up | down | status                    # start or update the rig; remove everything it made
sh tests/e2e/run.sh logs [gitberth|standin|cloudflared|<container>] [lines]
sh tests/e2e/run.sh password                              # the rig's admin password, for its UI on port 18092
sh tests/e2e/run.sh help
```

A bare `run.sh` is `run.sh impact` (its base defaults to `origin/main`). Every command but `plan`, `test`, `unit` and
the rig's own ends in `run-plan`: it prints the plan, runs the unit tests beside the rigs' `up` (a unit failure stops
the scenarios), runs the lanes side by side, and ends with an `E2E SUMMARY` block and a one-line `in short:`. It exits 0
only when everything passed.

Options: `--base REF` (impact's base, with git) or `--base-tree DIR` (a copy of the base, for a copy of the tree
without git); `--lanes 1|2|3`; `--budget MIN` (impact's minutes in all, 10 by default; 0: no limit); `--no-unit`;
`--smoke` (add the smoke to groups); `--templates a,b`; `--timings test|production`; `--out FILE` (plan: keep the plan).

Variables: `E2E_KEEP=1` leaves the apps the scenarios made in place, so you can look at them afterwards.
`E2E_INSTANCE=b|c` picks the rig for one lane. `E2E_TIMINGS`, `E2E_TEST_TIMINGS` and `E2E_TEMPLATES`: see
"Timings" below. `E2E_DIND_IMAGE` (`docker:29-dind`) and `E2E_DIND_ARGS` (dockerd's flags, for example
`--registry-mirror=https://mirror.gcr.io`): a rig whose engine had others is started again, its volume kept.
`E2E_NUGET_VOLUME` is the unit tests' NuGet cache (`gbe2e-nuget`; CT 101 uses its warm `dotnet-nuget`).

From a PC without Docker, a wrapper such as `e2e-run.ps1 -Tree <tree>` does all of it on the Docker host: it plans
against a local copy of the base (or next as last pushed, on the host), uploads the tree, runs the plan on both rigs
and copies each lane's outputs back next to its log. Its `-Tier`, `-Groups`, `-Templates`, `-Budget`, `-Lanes`,
`-Timings`, `-NoUnit` and `-DryRun` match the options above.

The app templates' smoke, `run.sh release` or `run.sh groups 31 --templates memos,miniflux`, installs each template in
the gallery, waits for it to answer on its LAN port and deletes it. It always runs on its own (`-k
test_31_template_smoke`), with production timings, and on the first rig: it pulls every template's images (many GB the
first time), and they stay in that rig's engine, so the next run doesn't pull them again. A catalog change smokes just
its own templates.

## What a change runs

`tests/e2e/impact.py` reads the change and the map, `tests/e2e/impact.toml`, and picks a tier. The report names the
strongest that applies, with each file's reason:

| Tier | When | What runs |
|---|---|---|
| `none` | Every changed file maps to `@none`: docs, unit tests, the e2e tooling, workflows | the unit tests only |
| `scenarios` | Only scenario files (`scenarios/test_*.py`) changed | their groups, and the groups that import a name they changed |
| `impact` | Product or rig files with specific rules | the unit tests, their groups, the smoke |
| `wide` | A hub file with no narrowing word, or a file only the catch-all matches | `@wide`: the smoke and 00 01 06 12 19 20 22 23 24 25, and any other groups selected |
| `full` | A package or target change in `Gitberth.csproj`, or asked for | every group, slow tests included; no template smoke |
| `release` | Asked for | `full`, then every template with production timings |
| `smoke`, `groups` | Asked for | the smoke, or the groups named |

**The 10-minute budget.** An impact pass (the default: a bare `run.sh`, `run.sh impact`, `e2e-run.ps1 -Tree`) fits
10 minutes in all: about 3.5 for the unit tests beside the rigs' setup, then both lanes. It fills them by priority:

1. the core smoke, `@core`: sign in, and one app from GitHub deployed and answering. Always in;
2. one test of each thing the change points at, most specific first: refine hits and changed scenario files, then
   rules in the map's order (single files before folders), `@wide` last;
3. the rest of those groups' tests. A group that doesn't fit whole is taken test by test (a test class whole), by
   node ID;
4. the rest of the smoke (`@smoke`, 6 tests: sign in and out, a pull request's preview, a domain's commit status, the
   audit log, MCP, an agent installing a template).

Whatever doesn't fit is listed in the report as **left for the full suite**, and the tier line says the budget cut
it; nothing is dropped silently. `--budget 0` (`-Budget 0`) keeps everything. `full` and `release` have no budget:
they're the whole suite, on demand.

**How the map decides.** For each changed file (both sides of a rename), the first rule whose globs match decides.
A rule with `refine` entries looks at each changed hunk: its changed lines, 3 lines around them, and the declaration it
sits in (the method, constructor or type in a `.cs` file; in `.razor` markup the nearest `id="..."`, in `@code` the
method; a `.py` def or class). When an entry's regex matches, only the matching entries' groups count; otherwise the
rule's own groups do. A changed scenario file runs its own group, plus each group that imports a name it changed
(a fixture that calls a changed helper counts as changed too). Slow tests (`@pytest.mark.slow`) stay out unless a
rule says `NN+slow` or names the test. `notes` name what no scenario reaches, such as the engine tests
(`GITBERTH_ENGINE_IT=1`) or the UI tests, and the report prints them.

**Keeping the map right.** `python3 tests/e2e/impact.py check` checks it against the tree (the Build workflow runs it
on every push): a scenario file without its group, a test an alias names that doesn't exist, slow lists that don't
match the marks, a regex that doesn't compile. A new scenario file needs its group; moved code needs its globs. A file
only the catch-all matches runs `@wide` and the report warns: add a rule. The selector's own tests:
`python3 -m unittest discover -s tests/e2e -p impact_test.py`.

## Timings

Gitberth in the rig runs with shorter waits, `GITBERTH_TEST_TIMINGS`, whose list is in `rig/compose.e2e.yml`: a new
container's stay-up check (3 s instead of 15), the pull-request list (every 20 s instead of 5 min), and restart
schedules (a schedule minute lasts 20 s, looked at every 2 s). The log says which Gitberth has: `[rig] Gitberth's test
timings: ...`, or `none (production timings)`.

- `E2E_TIMINGS=production` (`--timings production`, `-Timings production`) runs with none: as an install runs.
  `E2E_TEST_TIMINGS`, when set, is passed on as the list itself.
- The template smoke always runs with production timings.
- The guard: only `rig/compose.e2e.yml` sets the variable (an install's compose file never passes it); unset, every wait
  is the production one; anything it doesn't understand, or a value that isn't shorter, stops Gitberth from starting.

The map's `minutes` are production timings'. With test timings, a plan estimates a group by its `test_minutes` when it
has them, else by its minutes times `[test_timings] factor` (0.5).

## Lanes

`--lanes 2` runs the first rig and `b` side by side (`--lanes 3` adds `c`), each with its own engine, volume and
Gitberth; the template smoke stays on the first rig. With one lane, `E2E_INSTANCE=b` (or `c`) picks the rig:
`gitberth-e2e-b` on `gbe2e-lan-b` (192.168.71.0/24, UI on port 18093), or `gitberth-e2e-c` (192.168.72.0/24, port
18094). `run.sh down` with the same `E2E_INSTANCE` removes it.

A rig is never shared: every run starts by resetting it. A run holds its rigs (a flock on `/tmp/gbe2e-lock-<rig>`,
which the runs it starts inherit through `E2E_LOCKED`), and also looks inside each rig for a run of an older `run.sh`.
A second run on a busy rig stops at once: `rig b (gitberth-e2e-b) is running scenarios; try again when it's done`.

## On GitHub Actions

The whole suite runs on GitHub's hosted runners, each run in a rig of its own: `.github/workflows/e2e.yml`, about 11
jobs and about 25 minutes, leaving CT 101 free. It's on demand; nothing starts it on a push. Two ways:

- **By hand:** Actions › E2E › Run workflow, or `gh workflow run e2e.yml --ref next -f suite=release`. `suite` is
  release, full, impact (against `base`), groups (with `groups`) or smoke; `timings` and `shards` too.
- **By a tag, with git alone:** push a tag named `e2e-*`, for example
  `git tag e2e-1.4-rc1 && git push origin e2e-1.4-rc1`. It runs the release suite with test timings on 8 runners, plus
  3 for the templates. It needs no API token, so a bot that pushes over SSH can start it.

The plan job's summary shows the plan; each job uploads its outputs (`junit.xml`, `report.txt`, screenshots, and
Gitberth's and the stand-ins' logs) as an `e2e-<id>` artifact for 14 days; the summary job puts every job's results in
one table. Docker Hub is reached through `mirror.gcr.io`. The repo is public, so the runners cost nothing.

The Build workflow's "E2E impact" job writes every push's and pull request's plan, with what it leaves for the full
suite, to the run's summary; nothing runs in a rig there.

## Measuring

Every run writes `junit.xml` (each test's time, its fixtures included) to `tests/e2e/out/<a|b|c>/`, next to
`report.txt` and the screenshots; a lane's further runs go to `out/<lane>-2` and so on. `python3 tests/e2e/impact.py
minutes tests/e2e/out` sets the measured minutes next to the map's and prints the lines to paste into `impact.toml`:
`test_minutes` from a run with test timings (the default), `minutes` with `--timings production`. `impact.py report
DIR...` turns `junit.xml` files into one Markdown table.

The rig is quick to run again. Its engine's volume keeps base images and build caches, and `up` rebuilds and restarts
only what changed. So a change to Gitberth rebuilds Gitberth alone, and with no changes nothing restarts. `test` starts
every run from a fresh Gitberth with no apps and empty stand-ins.

Everything the rig makes on the host is named `gitberth-e2e` or `gbe2e-*`. It never touches anything else, and `down`
removes only those names. (The `docker:29-dind` image stays, since other things may use it.)

## How it works

```
host ── gbe2e-lan 192.168.70.0/24 ── gitberth-e2e (docker:29-dind, privileged, 192.168.70.10, UI on host port 18092)
                                       │  its own engine, volume gitberth-e2e for /var/lib/docker
                                       ├─ gbe2e-standin   10.89.0.2 on gbe2e: HTTPS 443 for github.com, api.github.com,
                                       │                  gitlab.e2e.test, gitlab.com, api.cloudflare.com, and
                                       │                  Cloudflare's edge; control API on :8099
                                       ├─ gitberth        Gitberth, as docker-compose.yml starts it, plus rig/compose.e2e.yml
                                       ├─ cloudflare-tunnel  the fake cloudflared, on the host's network (edge port 7844)
                                       ├─ gbe2e-runner    pytest + Playwright, on gbe2e (only while tests run)
                                       └─ gb-*            the apps Gitberth deploys
```

- **`run.sh`** runs on the host. It starts the engine container, copies Gitberth's sources and this folder into it, and
  runs **`rig/inner.sh`** there to build and start everything else.
- **Gitberth** is the image built from the repo's `Dockerfile`. It's started with `docker-compose.yml` plus
  `rig/compose.e2e.yml`, which adds only test settings:
  - `extra_hosts` send GitHub's, GitLab's and Cloudflare's names to the stand-in;
  - `SSL_CERT_FILE` and `GIT_SSL_CAINFO` name a CA bundle made of the image's own CAs plus the rig's test CA;
  - Gitberth also joins the rig's network.

  Its admin password is made once per rig and only kept in the rig. Apps get `GITBERTH_PUBLIC_HOST=192.168.70.10`, so
  their LAN addresses are the engine container's.
- **`standins/`** hold the stand-ins (Python 3 standard library, plus git and openssl):
  - **The GitHub stand-in** serves bare repos over git's smart HTTP (`git http-backend`). Pull requests' heads are real
    `refs/pull/<n>/head` refs.
  - It answers the REST calls Gitberth makes: `/user`, repo variables, pulls, commit statuses, issue comments and repo
    hooks, with GitHub's shapes, status codes and pagination. Tokens act as fine-grained tokens: each permission is
    read and write, read-only or none, and a token may select only some repos. Any token still reads a public repo's
    public data (the repo, its pull requests, statuses and comments). Writes to such a repo, and its hooks and
    variables, get 403. A private repo the token doesn't select is a 404. A pull request's comments take either Issues
    or Pull requests, and one pull request reads with Contents too (its list only with Pull requests). Two hooks may
    share a URL only when no event is common to both. Comment and status IDs are past 2^31, as GitHub's are.
  - GitHub's primary rate limit holds: 60 requests an hour from an address without a token (or with one GitHub
    refuses), 5,000 for each user with one, counted down in the `x-ratelimit-*` headers. Past it comes GitHub's 403
    with `x-ratelimit-remaining: 0`, the window's reset time and the message naming the address or the user.
    `GET /rate_limit` doesn't count.
  - A push, a branch created or deleted, or a pull request opened, changed, closed or merged through the control API
    sends GitHub's delivery to each matching hook. The delivery is JSON in UTF-8 (unicode as it is, never escaped),
    signed (`X-Hub-Signature-256`) with the hook's secret, and goes where the hook's URL leads, as it would in real
    life (see "Deliveries" below).
  - Every call is recorded with the repo it's about, however it was answered: an injected failure, GitHub's 401 or
    404, a refused git request. A call no route answers gets GitHub's 404 (or Cloudflare's 7003, or GitLab's `{"error":
    "404 Not Found"}`), is logged as `no stand-in route`, and is marked `unrouted`: Gitberth would read that answer as a
    missing permission or "not set up", so a scenario during which Gitberth made one fails, and so does the run.
  - **The GitLab stand-in** (`gl.py`) is one GitLab answering as `https://gitlab.e2e.test` (a self-managed instance's
    address) and as `https://gitlab.com` (Gitberth's default instance); each answer names the host it was asked
    through. It answers what Gitberth calls of GitLab's REST API v4 (the GitLab contract): `/user` and
    `/personal_access_tokens/self`, projects (nested groups or a user's namespace; public, internal or private; by ID or
    URL-encoded path), branches, compare, merge requests from the same project or a fork, commit statuses, merge request
    notes, project hooks and CI/CD variables, with GitLab's shapes, its errors (401, 403 naming the scopes an endpoint
    takes, 404 Project Not Found, Grape's 400s, 409, 422) and offset paging (`X-Next-Page`, empty on the last page).
    - Tokens are personal, project or group access tokens with GitLab's scopes (`api`, `read_api`, `read_repository`,
      ...), an expiry date and revocation, sent as `PRIVATE-TOKEN`, `Authorization: Bearer` or `?private_token=`. They act
      as their user, whose role in the project or its groups (guest to owner) decides what it may do: a private project a
      user isn't in is a 404, a role too low a 403. A project or group token is a bot that's a member there; an "oauth"
      token gets 404 from `/personal_access_tokens/self`, as every token does on a GitLab older than 15.5
      (`gitlab.options(self_endpoint=False)`).
    - git over smart HTTP at `https://<host>/<path_with_namespace>.git`, with `oauth2:<token>` (or any username with an
      access token) as Basic credentials: no credentials for a private project is a 401, a token without a repository
      scope a 403, a user who isn't in it a 404. A merge request's head is `refs/merge-requests/<iid>/head` in its target
      project, a fork's commits fetched in.
    - Commit statuses follow GitLab's state machine: a pending or running status of the same name, ref and user moves
      on, and a move GitLab doesn't make (running to running) is its 400 `Cannot transition status ...`. GitLab adds its
      own system notes to a merge request (commits added, closed, merged, ...), and lists notes newest first. A hook's
      token is never shown, and is dropped when its URL changes without a new one; a hook URL on the local network
      (localhost, a private address, a name without a dot) is GitLab's 422 `Invalid url given`. A variable's key alone
      naming more than one environment scope is GitLab's 409.
    - GitLab.com's API rate limits hold (2,000 requests a minute for a user, 500 for an address without a token), with
      `RateLimit-*` headers on every answer; past them, or when a scenario asks (`gitlab.throttle()`), comes Rack::Attack's
      429: `Retry later`, `Retry-After` and `RateLimit-*`. An injected failure on a GitLab host without a body answers
      `{"message": "<status> <reason>"}` (a line of text for git).
    - A push, or a merge request opened, changed, closed, reopened, merged or approved through the control API, sends
      GitLab's Push Hook or Merge Request Hook to each hook that asked for it: JSON as Rails writes it, with
      `X-Gitlab-Event`, `X-Gitlab-Token` (the hook's token), `X-Gitlab-Event-UUID`, `X-Gitlab-Webhook-UUID`,
      `X-Gitlab-Instance` and `Idempotency-Key`. It goes where the hook's URL leads, as GitHub's deliveries do (see
      "Deliveries"), and GitLab waits 10 seconds for the answer. The stand-in sends each delivery once (GitLab's docs
      describe no automatic retry, but say to expect duplicates after a timeout: `resend_delivery` sends one again with
      its Idempotency-Key, as GitLab's Resend Request does), and it doesn't disable a failing hook as GitLab does after 4
      failures in a row.
    - Its calls are recorded as kind `gitlab` (the API) and `gitlab-git` (git), with the project they're about (`repo`:
      its path, also when asked for by ID) and the token they carried; a token sent in the query is `[FILTERED]` in the
      record's query. No token ever goes to the stand-ins' log.
  - **The Cloudflare stand-in** answers what `CloudflareApi.cs` calls in Cloudflare's envelope: token checks, zones,
    DNS records, the tunnel's configuration (each PUT bumps its version, which is kept across restarts), the Zero Trust
    organization, and Access applications. Its tokens have Cloudflare's permission groups. Access is two groups there:
    `access_apps` (Access: Apps and Policies) for the applications, and `access_org` (Access: Organizations, Identity
    Providers, and Groups) for the organization, which holds the team domain.
  - It's also **Cloudflare's edge**. A request to a host name (a webhook delivery, or a scenario's `visit`) gets through
    only when four things hold:
    1. The host's DNS record in one of the stand-in's active zones is proxied, and leads (directly or through CNAMEs)
       to `<tunnel>.cfargotunnel.com`.
    2. No self-hosted Access application covers the address, or the visitor signed in as an email it allows. Then
       Cloudflare adds the Access token cloudflared checks.
    3. A connector runs the tunnel. Otherwise the answer is 530, error 1033.
    4. The connector routes it.
  - **`cloudflared/`** is the fake cloudflared. Gitberth finds it as it finds a real one: the image name, `tunnel run
    --token`, and the network mode. It never contacts Cloudflare. It polls the stand-in for the tunnel's configuration
    and takes a new version as cloudflared does:
    - a version no newer than the one it runs is ignored;
    - one it can't decode, or whose ingress cloudflared would refuse, is logged as `ERR Failed to deserialize new
      configuration error=… version=N`, and the old one stays;
    - otherwise it logs `INF Updated to new configuration … version=N`, the line Gitberth's wait for the tunnel reads.

    Like cloudflared, it logs to stderr. `ingress.py` holds cloudflared's checks. Its edge port (7844) takes the
    tunnel's traffic from the stand-in and routes it by the ingress it runs, from its own network: `localhost` is the
    host it runs on, and names are its networks'. It applies the first rule whose hostname and path match. An
    `http_status` rule answers by itself. A rule with `originRequest.access` refuses a request without a valid Access
    token (403). An http(s) service gets the request with its Host header, and gives 502 when it can't be reached.
    Until it runs a configuration it answers 503. Off the host's network (`engine.recreate_fake_cloudflared(token,
    "gitberth")`), the stand-in joins the fake's network and the fake never joins the rig's, so it reaches only what a
    real one there would.
  - **`certs.py`** makes the test CA and the stand-ins' certificate the first time. Nothing secret is committed.
- **`apps/testapp`** is the template the scenarios' repos start from. It's a tiny Python server built from its own
  Dockerfile, which declares `VOLUME /data` and uses Gitberth's build-args marker. Its endpoints:
  - `/` shows its `marker.txt` and commit;
  - `/env` returns its environment;
  - `/build` shows what its build saw: Vercel variables in plain text, every other value only as a SHA-256;
  - `/file?path=` reads a file, and POST writes one;
  - `/cron` is what its `vercel.json` cron hits; each hit is logged to `/data/cron.log`.

  It listens on 8080, or on `PORT`. For another port, commit a Dockerfile with `EXPOSE <n>` and `ENV PORT=<n>`.
- **`scenarios/`** holds the pytest + Playwright scenarios, one file per scenario of the spec (`test_01_…`).
  `test_00_rig.py` checks the rig itself first:
  - pull requests from branches and forks, and merges;
  - the REST answers over TLS, GitHub's token and hook rules, and its rate limits;
  - injected failures, and Cloudflare's Access permissions;
  - calls found by their repo however they were answered, and calls without a route marked;
  - deliveries following DNS and the tunnel to Gitberth, signed over UTF-8, with GitHub's 10 seconds;
  - the fake cloudflared refusing a bad configuration, logging to stderr, and off the host's network;
  - Gitberth finding the fake cloudflared;
  - the GitLab stand-in (`TestGitLabStandIn`): its answer shapes and paging, tokens' scopes and users' roles, merge
    requests from branches and forks, statuses, notes, hooks and variables, git with `oauth2:<token>`, its deliveries
    and its 429s.

  The shared fixtures are in `conftest.py`, and the helpers in `scenarios/e2e/`.
- The runner resolves GitHub's, GitLab's and Cloudflare's names to the stand-in and trusts the rig's CA, just as Gitberth
  does. So `github.rest(...)`, `gitlab.rest(...)` and `cloudflare.rest(...)` get the answers Gitberth gets.

### Deliveries

A webhook delivery reaches Gitberth only the way it would in real life:

1. A proxied CNAME for the hook's host leads to the tunnel.
2. The first tunnel rule matching the host and path (`^/hooks/(github|gitlab|deploy/[A-Za-z0-9_-]{20,100})$`, which
   GitLab's project hooks and deploy hooks' calls take too) goes to Gitberth's service. With cloudflared on the host's
   network that's `http://localhost:8080`, Gitberth's published port.

Gitberth writes both itself once instant deploys have a host name. Until then, or when either is wrong (no record, a
missing or shadowed rule, a host that moved away), the delivery's record shows what happened instead:

- `error` (for example, "Couldn't resolve host") and no `status`; or
- the tunnel's answer, such as a 404 rule, 403 from Access, or 502 from an origin it couldn't reach.

GitHub waits 10 seconds for the answer, all told, through the tunnel or straight to Gitberth. Then it hangs up and
counts the delivery as failed: the record has no `status` and the error `timed out`, whatever the origin does after.

The record's `route` names the DNS chain, the Access application, the connector, and the rule and service cloudflared
used (`route["cloudflared"]`). Cloudflare passes the sender's address on as `CF-Connecting-IP`, and appends it to
`X-Forwarded-For`. A `CF-Connecting-IP` the sender sets itself never gets through.

Another service's hook goes where its own URL leads: nowhere, unless a scenario gives its host a DNS record. A hook
Gitberth wrongly took over (now with Gitberth's URL) is delivered to Gitberth, as GitHub would.

## Writing a scenario

The fixtures (see `conftest.py` and `scenarios/e2e/`):

| Fixture | What it gives |
|---|---|
| `gitberth` | Gitberth: `url()`, `new_page()`, `open(page, path)`, `sign_in`/`attempt_sign_in`/`sign_out`, `create_git_app(page, repo.url)`, `deploy`, `set_previews`, `delete_app`, `save_watch_interval`, `save_github_token`, `snackbar`, `confirm`, `banners`, `tabs`, `app_list_keys`, `app_list_total`, `dashboard_app_total`, `top_search`; waits: `wait_deploy(app_id, after=, statuses=)`, `wait_idle`, `wait_app`, `wait_preview`, `wait_no_preview`, `wait_answers(url, path)`; `lan_url(app_id)`, `deploy_log(id)`, `logs(since)`; `gitberth.db` reads its database (`app`, `deployments`, `last_deployment_id`, `previews`, `ports`, `web_port`, `volumes`, `env_vars`, `domains`, `activity`, `setting`, or `query(sql)`). |
| `github` | `token(login=, permissions=, repos=)` (a fine-grained token: `{"statuses": False}` or `"none"` takes a permission away, `"read"` makes it read-only), `change_token`, `rest(verb, path, token)`, `rate_limits()`, `reset_rate_limits()`, `create_repo(name, files=..., private=)` → a `Repo`: `commit`, `create_branch`, `delete_branch`, `fork`, `open_pull`, `update_pull`, `close_pull`, `merge_pull`, `set_variables`, `delete_comment`, `add_foreign_hook` (another service's), `add_pasted_hook` (Gitberth's, made by hand), `state`, `branch_sha`, `statuses`, `comments`, `hooks` (each with `made_by`: api, pasted or foreign), `deliveries`, `send_delivery(..., hook_id=, via_tunnel=, content_type=)`, `git_calls`, `ls_remotes`, `ls_remote_attempts`, `api_calls`, `wait_watcher_passes`, `wait_pulls_listed` (every one of these finds a call however it was answered). |
| `gitlab` | `token(username=, scopes=, expires_at=, kind=personal\|project\|group\|oauth, project=, group=, access_level=)`, `change_token`, `tokens`, `add_member(target, username, level)`, `rest(verb, path, token, how=header\|bearer\|query, host=)`, `options(self_endpoint=, webhook_timeout=, authenticated_limit=, unauthenticated_limit=)`, `reset_options`, `throttle(path, times=, retry_after=)` (a 429 on demand), `reset_rate_limits`, `calls`, `deliveries`, `create_project("group/sub/name", files=, visibility=)` → a `Project` (`url`, `clone_url`, `id`): `commit`, `create_branch`, `delete_branch`, `fork(namespace, owner)`, `open_mr(source_branch, source=<a fork>, draft=)`, `update_mr`, `close_mr`, `reopen_mr`, `merge_mr`, `approve_mr`, `unapprove_mr`, `add_note`, `delete_note`, `set_variables`/`set_variable`, `add_member`, `add_foreign_hook`, `add_pasted_hook`, `change(archived=, visibility=)`, `state`, `branch_sha`, `mr_refs`, `merge_requests`, `statuses`, `notes`, `hooks` (with their tokens), `variables`, `deliveries`, `send_delivery(event=push\|merge_request, token=, hook_id=, via_tunnel=)`, `resend_delivery(id)`, `git_calls`, `ls_remotes`, `ls_remote_attempts`, `api_calls`, `wait_watcher_passes`, `wait_mrs_listed`. `gl_token` is a token of e2e-bot with `api`; `e2e.gitlab.quote(path)` URL-encodes a project's path, `basic_auth(token)` is git's header. |
| `cloudflare` | `token(permissions)` (all of `Cloudflare.ALL` by default), `settings_token()` (exactly what Gitberth's Settings asks for: `Cloudflare.AS_SETTINGS_ASKS`), `change_token`, `rest(verb, path, token)`, `zone(name)`, `set_team(name)`, `tunnel()`, `ingress()`, `rules_for(host)`, `set_tunnel_config`, `connector()`, `wait_runs(version, network=)`, `visit(url, signed_in_as=)`, `dns_records`, `add_dns_record`, `delete_dns_record`, `access_apps`, `add_access_app`, `delete_access_app`, `options`, `cloudflared_token`. |
| `standins` | `seq()` (a marker), `calls(since, host=, method=, path=, kind=, repo=, unrouted=)`, `fail(path, status=, host=, rate_limit=, ...)` to inject a failure, `clear_failures()`. |
| `engine` | The rig's engine: `inspect`, `running`, `wait_running`, `wait_gone`, `containers`, `container_names`, `env`, `labels`, `image_of`, `mounts`, `host_ports`, `networks`, `address(name, network)`, `exec(name, *cmd, env=)`, `logs(name, since=, streams=)`, `images`, `image_labels`, `volumes`, `anonymous_volumes`, `recreate_fake_cloudflared(token, network)` (then `cloudflare.wait_runs(network=...)`). |
| `page`, `anon_page`, `new_page` | Browser pages (signed in, not signed in, or made on demand). `gitberth.errors(page)` lists the browser's errors on a page. |
| `apps` | `apps.track(app)`: the app is removed, with its images and volumes, when the scenario ends (unless `E2E_KEEP`). The scenario's injected failures are cleared first. |
| `uniq` | `uniq("bp")` → `bp-3fa9`: unique names, so scenarios never collide. |
| `scenario` | `scenario.seq` and `scenario.t0`: where this scenario started, for the stand-ins' calls and Gitberth's log. |
| `baseline` | The session's GitHub token (`baseline["token"]`) and watch interval (10 s). |

`e2e/github_reports.py` reads what GitHub heard from Gitberth about deploys: commit statuses (`statuses`, `wait_status`,
`status_writes`), pull request comments (`wait_comment`, `comment_writes`, `parse_comment`), and every request Gitberth
sent GitHub's API (`leaks` finds text that mustn't be there). It also turns Settings › GitHub integration's switches
(`set_github_switch`) and gives an app variables on its Environment tab (`set_variables`).

Rules of thumb:

- Check the real effect first: the engine, the app's `/env` or `/build`, or the stand-ins' records. Check the page as
  well when the feature has a screen element.
- Every wait polls with a timeout (`e2e.wait.until`). When it gives up, it says what it saw last. Never sleep longer
  than needed.
- To know the watcher has looked, use `repo.wait_watcher_passes(n, since=marker)`. It counts Gitberth's `ls-remote`s of
  the repo, failed ones too: a look refused at its first request (`GET …/info/refs`, answered 401, 404 or an injected
  failure) gets no further, and counts as one. A clone refused there would count as well. The rig's interval is 10
  seconds.
- Deliveries reach Gitberth only through the route Gitberth set up (see "Deliveries"). That's what proves instant
  deploys work. `send_delivery(via_tunnel=False)` goes straight to Gitberth's container, for checks of its endpoint
  alone: signatures, 405, the size limit, throttling.
- `send_delivery()` goes through Gitberth's own hook unless `hook_id` names another: the newest made through the API,
  else the newest pasted by hand, never another service's. With `url` and `secret` and no hook, it's JSON, as
  Gitberth's hooks are; `content_type="form"` sends form data instead.
- A delivery's sender (`CF-Connecting-IP`) is one of GitHub's hook addresses, and it changes from one second to the
  next. A throttling scenario passes `cf_connecting_ip=` to `send_delivery` for a sender of its own.
- After a change to the tunnel, `cloudflare.wait_runs()` waits for the fake cloudflared to run it, as Gitberth's own
  wait does.
- Scenarios about protection (8, 16, 17) save `cloudflare.settings_token()`, the token a user makes from what Settings
  lists. A token with more permissions hides what a real one would get.
- GitHub's rate limits: `standins.fail(path, host="api.github.com", rate_limit="primary")` or `"secondary"`. An injected
  failure without a body answers in the service's own shape, with the caller's own rate-limit headers (60 an hour
  without a token). The real limits hold too: a scenario that uses one up calls `github.reset_rate_limits()` after.
- GitLab's 429: `gitlab.throttle(path, times=1, retry_after=5)`, for the next calls whose path matches (git's too). A
  scenario that changes `gitlab.options(...)` puts them back with `gitlab.reset_options()` in a `finally`.
- A call Gitberth makes that no stand-in route answers fails the scenario (and the run): add the route to the stand-in,
  in GitHub's or Cloudflare's real shape. The scenarios' own `rest()` calls are left out, so a check may ask for a
  missing route on purpose.
- A scenario that changes a global setting (domains, tokens, instant deploys) puts it back in a `finally`. Its injected
  failures are cleared for it, before its apps are removed.
- A scenario that fails because of Gitberth itself stays failing. Report it with its evidence; never weaken the check.

- A new scenario file needs its group in `impact.toml`, and rules for the code it covers (`impact.py check` fails
  without the group).

A failing scenario's report carries its assertion and Gitberth's warnings and errors from its run. Screenshots of its
pages are saved in the rig at `/e2e/out`, and `run.sh test` copies them to `tests/e2e/out/<a|b|c>` with the report and
`junit.xml`. Before the selected tests run, every scenario file is collected: one that doesn't import stops the run
(`a scenario file doesn't import: ...`), since the others import each other's helpers.
