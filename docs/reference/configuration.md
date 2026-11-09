# Configuration

This page covers how Gitberth itself is set up on the host: the environment variables it reads, its volume and port, the files in its data volume, and the names and labels it gives the things it makes in Docker. Most settings live in the UI instead (see [Gitberth's settings](gitberth-settings.md)); these are the few that have to be known before Gitberth starts.

## The compose file

The `docker-compose.yml` from the install looks like this:

```yaml
name: gitberth  # fixes the data volume's name (gitberth_gitberth-data), whatever the folder is called
services:
  gitberth:
    image: ghcr.io/johnhowelljr/gitberth:latest
    container_name: gitberth
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      GITBERTH_ADMIN_PASSWORD: ${GITBERTH_ADMIN_PASSWORD:-}  # used on first start only; change it in the UI later
      GITBERTH_PUBLIC_HOST: ${GITBERTH_PUBLIC_HOST:-}        # this machine's LAN address, used for app links
      TZ: ${TZ:-UTC}
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - gitberth-data:/data

volumes:
  gitberth-data:
```

The three variables come from the `.env` file next to it:

```bash
GITBERTH_ADMIN_PASSWORD=
GITBERTH_PUBLIC_HOST=192.168.50.10
TZ=UTC
```

After you change `.env` or `docker-compose.yml`, run `docker compose up -d` in `/opt/gitberth` to recreate Gitberth with the change. Your apps keep running.

## Environment variables

| Variable | What it does | Default |
|---|---|---|
| `GITBERTH_ADMIN_PASSWORD` | The admin password, read on the first start only, when no password is saved yet. Later, change it in **Settings › Admin password**; changing this variable does nothing. | Empty: Gitberth generates a 20-character password and prints it once in its log: `docker compose logs gitberth`. |
| `GITBERTH_PUBLIC_HOST` | The LAN address of this host, used for every LAN address and connection string Gitberth shows. An IP or a name your PC resolves. **LAN address of this host** in **Settings › Network** wins over it when set. | Empty: the address detected on the host's default route, else the address your browser used. |
| `TZ` | Gitberth's time zone: every time its pages show, its log, and the window for automatic updates. A name like `UTC` or `America/New_York`. | `UTC` (from the compose file) |
| `GITBERTH_DATA` | The folder Gitberth keeps its data in. The image sets it to `/data`, where the volume is mounted. | `/data` in a container; `App_Data` next to Gitberth when it runs outside one |
| `GITBERTH_NETWORK` | The name of the shared Docker network every app is on, which Gitberth creates if it's missing. | `gitberth` |
| `DOCKER_HOST` | The Docker engine's address, read the way the docker CLI reads it. `unix://` addresses and plain `tcp://host:port` addresses (without TLS) work. | `unix:///var/run/docker.sock` |
| `GITBERTH_DOCKER` | The docker CLI Gitberth runs. The image includes one. | `docker` |
| `GITBERTH_GIT` | The git Gitberth runs. The image includes one. | `git` |
| `ASPNETCORE_HTTP_PORTS` | The port Gitberth listens on inside its container. The image sets it. | `8080` |
| `DOTNET_RUNNING_IN_CONTAINER` | Set by the .NET base image. Gitberth reads it to know it runs in a container: it then keeps its data in `/data`, finds its own container, and can run its helper containers. | `true` in the image |

Only the first three belong in `.env` for a normal install. To change one of the others, add it under `environment:` in `docker-compose.yml`.

A `GITBERTH_*` variable or `DOCKER_HOST` that's set but empty counts as unset, so it gets its default: an empty `GITBERTH_DATA` still means `/data` in a container.

Gitberth is an ASP.NET Core app, so the framework's own variables work too, such as `Logging__LogLevel__Default=Debug` for a more detailed log.

## Port

Gitberth listens on port 8080 inside its container, published on port 8080 of the host. To open it on another host port, change the left side of the `ports:` line and run `docker compose up -d`:

```yaml
    ports:
      - "8081:8080"
```

Gitberth's own container isn't recreated from its page: its **Network** tab shows its ports as they are and says they're set in its Compose file.

## Volumes

| Mount | What it's for |
|---|---|
| `/var/run/docker.sock` | The host's Docker engine. Gitberth builds, runs and watches containers through it. Without it, Gitberth can't see Docker. See [Security](../concepts/security.md#the-docker-socket). |
| `gitberth-data` at `/data` | Everything Gitberth knows. Docker names the volume `gitberth_gitberth-data`, because the compose project is called `gitberth`. |

## The data volume

| File or folder | What it holds |
|---|---|
| `gitberth.db` | Gitberth's SQLite database: settings, apps, ports, networks, domains, deploy history, activity, update checks, and every secret, encrypted. While Gitberth runs, `gitberth.db-wal` and `gitberth.db-shm` sit next to it. |
| `keys/` | The keys Gitberth encrypts secrets with (ASP.NET Core Data Protection). They also sign the sign-in cookie. |
| `logs/deploy-<number>.log` | Each deploy's log. `logs/deploy-<number>.Dockerfile` is the Dockerfile it built with. They go when the app is deleted. |
| `stats.db` | The Health page's readings and Docker's events for the last few hours, so the charts carry on after a restart. Nothing in it is needed to run. |
| `work/` | Clones and build folders while a deploy runs. Emptied when Gitberth starts. |

Gitberth upgrades its database when a new version starts. See [Back up and restore](../guides/backups-and-restore.md) for what to copy.

## What Gitberth makes in Docker

| Kind | Name | What it is |
|---|---|---|
| Network | `gitberth` (or `GITBERTH_NETWORK`) | The shared network every app is on, where containers reach apps by name. Gitberth's own container joins it at start. |
| Network | `gitberth-lan` | Only while own LAN addresses are on: an ipvlan network on the host's interface. |
| Container | `gb-<app>` | A Git app's container, like `gb-website`. |
| Container | `gb-<app>_next`, `gb-<app>_prev` | A deploy's temporary containers: the new version starting next to the old one, and the old one moved aside. Gitberth removes them when the deploy is done. |
| Container | `<name>_new_<id>`, `<name>_old_<id>` | The new and old container while Gitberth recreates a container for a change or an update. |
| Container | Labelled `gitberth.helper` | Short-lived helpers that read the host's network and test LAN addresses. They run Gitberth's own image, read-only, with no capabilities and 32 MiB of memory. |
| Image | `gitberth/<app>:<commit>` | A Git app's image, tagged with the first 7 characters of its commit. The last 3 good ones are kept for rollbacks. |
| Volume | `gb-<app>-<name>` | A Git app's named volume: a volume called `data` on `website` is `gb-website-data`. |

### Labels

Gitberth labels the containers and images it makes. Don't remove these labels: Gitberth uses them to recognize its own.

| Label | On | Meaning |
|---|---|---|
| `gitberth.managed` | Containers, networks | Made by Gitberth. |
| `gitberth.app` | Containers, images | The app it belongs to. |
| `gitberth.source` | Containers | How the app came to Gitberth: `registry`, `local`, `manual`, or `git`. |
| `gitberth.deployment`, `gitberth.trigger` | Containers | The deploy that started it, and what started that deploy. The Health page's marks come from these. |
| `gitberth.bridge` | Containers | A deploy's temporary container. |
| `gitberth.helper` | Containers, networks | A helper, or a network a test made for a moment. |
| `gitberth.commit` | Images | The commit a Git app's image was built from. |
| `gitberth.port`, `gitberth.crons` and other `gitberth.*` labels | Images | What a build found out: the web port, the scheduled jobs from `vercel.json`, and what Vercel's variables need. A rollback to that image uses them. |

## Related

- [Install](../getting-started/install.md)
- [Gitberth's settings](gitberth-settings.md)
- [Back up and restore](../guides/backups-and-restore.md)
- [Updating Gitberth](../guides/updating-gitberth.md)
