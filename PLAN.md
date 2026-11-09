# Gitberth plan

Gitberth is a small, single-container tool that deploys private GitHub repos as Docker containers on a Proxmox LXC. It's deliberately smaller than Coolify or Dokploy: no Postgres, Redis or Traefik.

## Decisions

| | Choice |
|---|---|
| Stack | .NET 10, Blazor Server with MudBlazor (dark theme), EF Core with SQLite, one project |
| Docker | The docker CLI (BuildKit through buildx), using the host's mounted socket |
| Repo access | One fine-grained personal access token with Contents: read-only; plain git for everything |
| Change detection | Poll `git ls-remote`, one request per repo, every 30 seconds by default |
| Dockerfiles | The repo's own Dockerfile wins. Otherwise built-in templates, regenerated on each deploy, or a custom Dockerfile per app |
| Secrets | ASP.NET Core Data Protection, keys in `/data/keys` |
| Auth | Single admin, cookie login, lockout after 5 failed attempts |
| Networking | Docker port bindings on the host's LAN address, each port LAN, Host only or Internal; optional own LAN addresses; domains through Cloudflare Tunnel. No SSH tunnels |
| Out of scope for now | Reverse proxy/TLS, databases as "image apps", multiple hosts |

## M1: deploy by hand (done)

- [x] Gitberth container, compose file and login
- [x] GitHub token, validated with its expiry shown, and URL parsing including `/tree/<branch>/<subdir>`
- [x] Detection and templates for:
  - a repo Dockerfile
  - ASP.NET Core, .NET worker/console and Blazor WebAssembly
  - Node server, Next.js, Nuxt and SvelteKit
  - Vite, CRA, Angular and Astro static builds
  - plain static sites
- [x] Environment variable suggestions from `.env.example`, the code and `appsettings.json`
- [x] Sub-folders the build script works in (`cd admin && npm run build`, `--prefix`, `--cwd`, `-C`) get their own dependency install
- [x] Repos with several apps: the wizard lists every runnable folder, Dockerfile variant and .NET project, ranked, with the best match selected
- [x] Sub-folder Node apps build from the repo root (workspaces, `file:` dependencies, `cd ..`), and a folder's `vercel.json` install and build commands are used as-is
- [x] Build with BuildKit cache mounts, then run with ports, env vars, volumes, limits and a restart policy
- [x] Container swap with a 15-second stability check, an optional health URL and automatic rollback
- [x] Start, stop, restart and delete; live build and container logs; deploy history; manual rollback; keep the last 3 images
- [x] Verified on the Proxmox LXC with a static site (`orb`)
- [ ] Verify a Node app and an ASP.NET Core app on the LXC
- [x] Professional dark UI (MudBlazor), splash screen, console-style logs, toasts and confirm dialogs

## M2: watch and self-heal

- [x] Watcher: one `git ls-remote` per repo every 30 seconds (configurable), a per-app auto-deploy switch, and "Check now". A commit that was already tried is never retried automatically, so a broken commit doesn't loop and a rollback isn't undone.
- [x] Health page: CPU, memory, and network in and out for every container on the host, sampled every 5 seconds, with a per-container switch to stop gathering
- [x] Every container on the host, not just Gitberth's apps: counted on the dashboard, listed on a Containers page with start, stop, restart, remove, logs and details, and warnings about what depends on each. Each shows its addresses on the LAN and inside Docker, with connection details for databases
- [x] Any container reached straight from the LAN, at the host's address: each port is LAN, Host only or Internal, as a real Docker binding. A standalone container is published through a recreate that keeps everything else and rolls back by itself, and a Compose one gets the change for its Compose file. Containers join the shared network `gitberth` and stay on it after a recreate. A Network page lists every host port and Docker network, and a container can have its own LAN address
- [x] 6 hours of Health history kept on disk across restarts of Gitberth, with deploys, restarts, stops, crashes and health changes marked on the charts (from Docker's events), and stopped containers kept on the page
- [x] Bounded automatic retries for failed builds: once 15 minutes later and once after each Gitberth update; rolled-back deploys and manual rollbacks are left alone
- [ ] Reconciler every 30 seconds that restarts apps that should be running (today this only happens at startup)
- [ ] Weekly `docker builder prune` and disk usage on the dashboard
- [ ] Token-expiry warning banner (the expiry date is already shown in Settings)
- [ ] Notifications (ntfy or Discord) when a deploy fails
- [x] Scheduled restarts for any container (every N minutes, hours or days), without downtime for apps with a domain

## M3: a home for apps coming from Vercel

- [x] Vercel's system variables (VERCEL_URL and friends, framework copies such as NEXT_PUBLIC_VERCEL_URL, AUTH_URL for Auth.js), with a main domain per app
- [x] Deploys without downtime for apps with a domain: the new version starts next to the old one and the tunnel moves to it first
- [x] Cron jobs from vercel.json, called the way Vercel calls them
- [x] SvelteKit and Astro apps set up for Vercel build with the Node adapter instead
- [ ] Import a project from Vercel: settings, environment variables, domains
- [ ] Preview deployments for pull requests

## M4: backends in every common language

Each language lives in its own folder under `src/Gitberth/Detection/` (`ILanguage`, see Languages.cs): detection, the generated Dockerfile, the environment variables its code reads, and the files that mark its projects for the app finder. A web framework beats a package.json that only builds front-end assets, so Django, Rails and Laravel apps with Vite or Tailwind are built as what they are. Every framework has a sample project in `tests/samples`, detected in the tests and built and started on Docker by `tests/samples/check-on-docker.sh`.

- [x] Python: Django, FastAPI, Flask, Starlette, Litestar, Quart, Sanic, Falcon, Bottle, aiohttp, Tornado, Streamlit, Gradio, Dash, and Procfile apps; pip, uv, Poetry, PDM and Pipenv
- [x] Node.js backends: Express, Fastify, NestJS, Koa, Hapi, Hono, AdonisJS, Strapi and plain TypeScript servers, and Bun
- [x] Go (Gin, Echo, Fiber, Chi, net/http), Rust (Axum, Actix, Rocket) and Deno (Fresh, Hono, Oak)
- [x] Ruby (Rails, Sinatra, Rack), PHP (Laravel, Symfony, Slim, plain PHP) and the JVM (Spring Boot, Quarkus, Micronaut, Ktor)

## M5: save and restore

One file with everything this host runs, to bring it all back on another machine: install, sign in, **Restore**, pick the file, and everything is running again once the repos are cloned and built.

- [ ] **What's saved:**
  - Gitberth's settings: the admin login, the GitHub and Cloudflare tokens, Health choices (titles, gathering, scheduled restarts) and every other setting.
  - Every app: repo, branch, folder, build settings, a custom Dockerfile, ports, environment variables with their secrets, volumes, limits, restart policy, health URL, domains, and the commit it runs.
  - Every other container on the host, such as Postgres or cloudflared: its image (with digest), command, environment, ports, volumes, networks and restart policy, as `docker inspect` has them.
  - The data in every named volume:
    - Postgres and MySQL are dumped with their own tools, which take a consistent snapshot while they keep serving.
    - Any other container with volume data is paused, not stopped, only for the moment its own copy takes.
    - Apps without volumes aren't touched at all: they're rebuilt from git, so their settings are their whole state.
  - **Stop everything while saving**, as an option: one consistent moment across all containers, at the cost of downtime for the whole save. Gitberth notes which containers are running, stops them, saves, and starts exactly those again. The list is written down first, so a restart of Gitberth in the middle still brings them back.
  - Saving changes nothing else on the host: it's a backup as much as a move.
  - The Health history, so the charts carry on.
  - A checkbox per container and volume, with its size, to leave things out (the sample containers, a cache).
- [ ] **One encrypted file.** It's encrypted with a passphrase chosen when saving (AES-256-GCM, key from PBKDF2), since it holds every secret. Gitberth never keeps the passphrase. The file streams to and from disk, so a large one doesn't go through the browser connection: it downloads from a signed-in endpoint and uploads the same way.
- [ ] **Restore onto a fresh install.** It reads the file, asks for the passphrase, and shows what it will bring back before doing anything. Then, with a live log like a deploy's:
  1. settings and secrets, re-encrypted for the new machine;
  2. the other containers, with their images pulled, volumes filled and databases loaded, started in an order that puts databases first;
  3. each app's volumes, then the app, deployed from GitHub at the commit it ran;
  4. the domains. With the same Cloudflare tunnel (cloudflared is restored with its token), they work as soon as the apps answer on their old host ports.
- [ ] When restoring onto another machine while the original still runs, warn that the original should stop once the new one is up: two machines on the same Cloudflare tunnel share its visitors. Restoring a backup onto the same machine (after a reinstall, say) needs no such step.
- [ ] Unit tests for the file format and every step, and a full save, wipe and restore on a test host as the end-to-end check.

## M6: apps from Docker images

Not everything worth running is in your GitHub: databases, admin tools, monitoring. **New app** gets a second way in, next to "From GitHub": **From a Docker image**.

- [ ] **Pick an image:**
  - A catalog of popular, well-kept images by kind:
    - databases: Postgres, MySQL, MariaDB, MongoDB, Redis, Valkey;
    - database tools: Adminer, pgAdmin;
    - automation and monitoring: Uptime Kuma, n8n, Grafana, Prometheus;
    - storage, search and mail: MinIO, Meilisearch, Mailpit.
    Each comes with Gitberth's defaults: its port, its volumes, and the settings it needs, with passwords generated.
  - A search of Docker Hub, showing official and verified images, stars and pulls.
  - Any image reference pasted in, from GitHub's registry (ghcr.io), Quay, LinuxServer or a private registry.
- [ ] **Before installing:**
  - Pick the version from the registry's tags, newest stable first.
  - Gitberth reads the image to fill in its ports, volumes and default settings.
  - You set the same things as for a GitHub app: environment, ports, volumes, limits, restart policy and domains.
  - Ports use the GitHub apps' Ports table. Every port the image exposes is a row, on the LAN by default, databases included, with the catalog's names and web port.
- [ ] **Installed as an app:** on the dashboard and in the menu with the GitHub apps, with logs, Health, domains, scheduled restarts and save and restore. Deploying one pulls the image instead of building.
  - Its network name never takes one a container already answers to on `gitberth`: Postgres from the catalog becomes `postgres-2` while the shared postgres is there.
- [ ] **Updates:**
  - Gitberth checks the tag for a new image the way it checks GitHub for new commits, and shows **Update available**.
  - With automatic updates on, it pulls the new image and swaps it in, without downtime when the app has a domain.
  - A database never moves to a new major version by itself.
- [ ] **Adopt a standalone container:** turn one, like the shared Postgres, into an image app that keeps its volumes, so Gitberth manages, updates, saves and restores it like everything else.
  - Its port bindings become the app's ports, and its networks the app's networks.
  - Its volumes keep their names exactly, and its anonymous volumes are carried over, as a port change carries them.
  - Gitberth itself, the tunnel, Gitberth's helpers, and Compose, Swarm and Kubernetes containers can't be adopted.
- [ ] Catalog entries are installed and checked on Docker by the sample suite, like the GitHub samples.

## Later

- A GitHub webhook through the Cloudflare Tunnel for instant deploys
- Private npm and NuGet feeds through BuildKit secrets
- Repos that use compose files, GitLab and Gitea, and Railpack for more languages
