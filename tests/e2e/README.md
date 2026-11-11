# End-to-end tests

The unit tests use stand-ins inside the test process. These tests run the real Gitberth image, built from this tree, in
a Docker engine of its own, against stand-ins for GitHub (git and its API) and Cloudflare (its API and cloudflared).
A browser drives the UI, and each scenario checks what really happened: containers, images, volumes and environments in
the engine, and the calls the stand-ins recorded.

Gitberth and the scenario runner never reach the real GitHub or Cloudflare: those names lead to the stand-ins, whose
test CA they trust. The rig's engine itself resolves names as usual, though. So image pulls go to the real registries
(Docker Hub, and ghcr.io for the uv image in Gitberth's Python Dockerfiles), and a build step or an app that fetched
something from github.com would reach the real one. The test app does neither, and an app a scenario deploys mustn't
either.

## Running it

On a Docker host that allows privileged containers:

```sh
sh tests/e2e/run.sh test                      # bring the rig up, start from a fresh Gitberth, run every scenario
sh tests/e2e/run.sh test -k branch_previews   # only the scenarios matching a pytest -k expression
sh tests/e2e/run.sh up                        # only start the rig, or bring it up to date
sh tests/e2e/run.sh logs [gitberth|standin|cloudflared|<container>] [lines]
sh tests/e2e/run.sh password                  # the rig's admin password, to look at its UI on port 18092
sh tests/e2e/run.sh down                      # remove everything the rig made
```

`E2E_KEEP=1` leaves the apps the scenarios made in place, so you can look at them afterwards.

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
                                       │                  api.cloudflare.com, and Cloudflare's edge; control API on :8099
                                       ├─ gitberth        Gitberth, as docker-compose.yml starts it, plus rig/compose.e2e.yml
                                       ├─ cloudflare-tunnel  the fake cloudflared, on the host's network (edge port 7844)
                                       ├─ gbe2e-runner    pytest + Playwright, on gbe2e (only while tests run)
                                       └─ gb-*            the apps Gitberth deploys
```

- **`run.sh`** runs on the host. It starts the engine container, copies Gitberth's sources and this folder into it, and
  runs **`rig/inner.sh`** there to build and start everything else.
- **Gitberth** is the image built from the repo's `Dockerfile`. It's started with `docker-compose.yml` plus
  `rig/compose.e2e.yml`, which adds only test settings:
  - `extra_hosts` send GitHub's and Cloudflare's names to the stand-in;
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
    404, a refused git request. A call no route answers gets GitHub's 404 (or Cloudflare's 7003), is logged as `no
    stand-in route`, and is marked `unrouted`: Gitberth would read that answer as a missing permission or "not set up",
    so a scenario during which Gitberth made one fails, and so does the run.
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
  - Gitberth finding the fake cloudflared.

  The shared fixtures are in `conftest.py`, and the helpers in `scenarios/e2e/`.
- The runner resolves GitHub's and Cloudflare's names to the stand-in and trusts the rig's CA, just as Gitberth does.
  So `github.rest(...)` and `cloudflare.rest(...)` get the answers Gitberth gets.

### Deliveries

A webhook delivery reaches Gitberth only the way it would in real life:

1. A proxied CNAME for the hook's host leads to the tunnel.
2. The first tunnel rule matching the host and path (`^/hooks/github$`) goes to Gitberth's service. With cloudflared on
   the host's network that's `http://localhost:8080`, Gitberth's published port.

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
- A call Gitberth makes that no stand-in route answers fails the scenario (and the run): add the route to the stand-in,
  in GitHub's or Cloudflare's real shape. The scenarios' own `rest()` calls are left out, so a check may ask for a
  missing route on purpose.
- A scenario that changes a global setting (domains, tokens, instant deploys) puts it back in a `finally`. Its injected
  failures are cleared for it, before its apps are removed.
- A scenario that fails because of Gitberth itself stays failing. Report it with its evidence; never weaken the check.

A failing scenario's report carries its assertion and Gitberth's warnings and errors from its run. Screenshots of its
pages are saved in the rig at `/e2e/out` (`docker cp gitberth-e2e:/e2e/out .` fetches them).

Run one `run.sh test` at a time on each rig: every run starts by resetting it. To run two at once, give the second its
own rig with `E2E_INSTANCE=b` (or `c`): `gitberth-e2e-b` on `gbe2e-lan-b` (192.168.71.0/24, UI on port 18093), or
`gitberth-e2e-c` (192.168.72.0/24, port 18094), each with its own engine, volume and Gitberth. `run.sh down` with the
same `E2E_INSTANCE` removes it.
