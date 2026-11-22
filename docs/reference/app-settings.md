# App settings

Every setting on an app's **Settings**, **Environment** and **Network** tabs, field by field, in the order the page shows
them, and the uptime check on its **Overview**. Each entry says what the setting does, its default, and when you'd change
it. To learn how to edit and apply settings, see [Change settings](../guides/manage-apps.md#change-settings).

Which sections an app has depends on where it comes from: a Git app has **Source** and **Previews**, an app that runs an
image has **Image**, a [Compose app](../guides/compose-apps.md) has **Compose** in place of the container's own sections,
and a [preview](../guides/previews.md) has no **Settings** tab at all (its parent's settings are its own).

![An app's Settings tab, with the section list on the left](../images/app-settings.webp)

## How changes apply

Nothing changes until you apply it with the bar at the bottom of the page. The **Applies** column below says what
applying does:

| Applies | Meaning |
|---|---|
| At once | Gitberth's own data. Saved when you apply; nothing restarts. |
| Live | Changed on the running container with `docker update`. No restart. |
| Recreate | The container is recreated with the new setting. For a Git app, a deploy without a build does it. |
| Build | Git apps: saved, and the next deploy builds the app with it. |

A container that Docker Compose made takes **Live** changes in place; anything else gives you the lines for its Compose
file. Gitberth's own container shows its settings, and every change gives the lines for its Compose file.

## Settings tab

The list on the left jumps to each section, with a dot by any section that has unsaved changes. **Environment** opens the
Environment tab, and **Ports and networks** opens the Network tab.

### General

How the app shows up in Gitberth. Nothing here restarts it.

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Name** | What Gitberth calls the app everywhere. The container keeps its own name, so other containers still reach it by it. | The name it was created with, or the container's name | At once |
| **Health URL** | A path, starting with `/`, that a new container must answer (with a status below 400) before it takes over from the old one, after a deploy or a change. Shown for Git apps and apps with a web port. Set it when "it started" isn't enough to know the app works. | None | At once |
| **Pinned** | Shows the app in the sidebar and on the dashboard. | Off | At once |
| **Gather stats** | Reads its CPU, memory and network every 5 seconds and keeps 6 hours, for the Health page and the Overview charts. Turn it off for a container you don't care about. | On | At once |
| **Notifications** | Whether [notifications](../guides/notifications.md) tell about this app: its deploys, crashes and health check. Off leaves it out, wherever Settings › Notifications sends them. | On | At once |

### Source

Git apps only: where the code comes from and how it's built. The section names the repository and what Gitberth last
detected. The app's image is built from the repository, so it has no **Image** field.

![An app's Settings tab, Source section: its repository, branch and subfolder, Deploy on push, and Build's three ways, Detect automatically chosen, Railpack and Custom Dockerfile](../images/app-settings-build.webp)

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Branch** | The branch Gitberth builds and watches. | The branch you picked when you added the app | Build |
| **Subfolder** | The app's folder in the repository. Leave it empty for the repository's root. | Empty | Build |
| **Deploy on push** | A new commit on the branch deploys by itself. Turn it off to deploy only with **Deploy**. | On | At once |
| **Vercel's variables** | Sets `VERCEL_URL`, `VERCEL_GIT_COMMIT_SHA` and the rest. See [Variables Gitberth sets](../guides/environment-variables.md#variables-gitberth-sets-like-vercel). | On | Build |
| **Build** | **Detect automatically** generates the Dockerfile on every deploy (or uses the repository's own); when it finds nothing it knows, Railpack builds the app. **Railpack** always builds with [Railpack](railpack.md), from the repository's `railpack.json` and the app's `RAILPACK_` variables; the repository's Dockerfile isn't used. **Custom Dockerfile** uses the one you write here. | **Detect automatically** | Build |
| **Dockerfile** | With **Custom Dockerfile**: your Dockerfile. Keep the `# gitberth:build-args` and `# gitberth:volumes` lines; Gitberth fills them in. | The generated one | Build |
| **Project to run** | .NET only: which project of the solution runs, such as `src/MyApp/MyApp.csproj`. | The one Gitberth picked | Build |
| **Watch paths** | Build filters, one pattern per line, like a `.gitignore`: a new commit deploys by itself only when it changes a file one of these matches. An app in a subfolder offers **Watch website/** to fill in its folder. | Empty: any file | At once |
| **Ignored paths** | A new commit whose changed files all match these doesn't deploy by itself, such as `*.md` and `docs/`. | Empty | At once |

When Railpack builds the app (its **Build** is **Railpack**, or it's **Detect automatically** and Railpack built it last
time), the section says **Railpack 0.43.0 builds it, from the repository's railpack.json and the app's RAILPACK_
variables.**, with a link to [How Gitberth runs Railpack](railpack.md#how-gitberth-runs-it), and has no **Dockerfile** or
**Project to run**.

A commit the build filters leave out, or whose message says `[skip ci]`, ends **Skipped** and the app keeps running
its version; **Deploy** still builds it. Patterns starting with `!` aren't supported, and each field takes at most 50
patterns of at most 200 characters. See [Skip commits that don't need a deploy](../guides/deploys-and-rollbacks.md#skip-commits-that-dont-need-a-deploy).

### Compose

[Compose apps](../guides/compose-apps.md) only: a Git app deployed with Compose has this section after **Source** (its
**Build** field is the Compose file's then), and an inline Compose app has it in place of **Image**. Saved on its own with
**Save and deploy**, which says what changes first (**New: …**, **Removed, with their containers (their volumes stay):
…**) and then deploys; **Undo** drops the edits. A Compose app has no **Command**, **Volumes**, **Resources**, **Health
check**, **Labels**, **Logging** or **Advanced** section, and **Restart** has only the scheduled restart: those are its
Compose file's, service by service.

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Compose file** | A Git app's: the file's path in the app's folder, such as `compose.yaml`. An inline app's: the whole file, as `docker compose` reads it; `${NAME}` takes the app's variables. Saving shows the services that come and go. | The file it was created from | **Save and deploy** |
| **Web service** | The service whose container is the app's: its domains, LAN port, health URL, logs and uptime checks. The others stay inside the app. | The one that publishes a web port | **Save and deploy** |
| **Put every service on Gitberth's network** | Every service joins Gitberth's shared network too, with no name of its own there, so a worker or a cron service reaches the app's database server and other apps by name. Off: only the web service, as the app's name. Its previews follow it. | Off | **Save and deploy** |

### Previews

Git apps only: a preview of each pull request, or branch, as an app of its own. Saved on its own with **Save previews**;
nothing here restarts the app. See [Preview deployments](../guides/previews.md).

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Previews** | **Off**, **Pull requests**, or **Pull requests and branches** (a branch once it gets a new commit). Turning them off removes the previews, after asking. | **Off** | At once |
| **At most running** | How many previews run at once (1 to 20). More wait, and start when one goes. | 3 | At once |
| **Remove after (days without a new commit)** | A preview with no new commit for this long is removed (1 to 365). | 7 | At once |
| **Also pull requests from forks** | Previews pull requests from other people's copies of the repo. Their code is built and run on this server, without added capabilities, security options or GPUs (no preview gets the host's devices or privileged mode). Turning it off removes their previews. | Off | At once |
| **Protect previews' domains** | Visitors sign in through Cloudflare Access before they reach a preview's domain. | On | At once |
| **Each preview's database** | **None of its own**, **An empty one** or **A copy of production's**: a database and a role of the preview's own on the same Postgres server, made before its first build and dropped with it, for the app's own database and for a variable whose production value is a `postgres://` URL to a server on this host Gitberth can use; and a Redis of its own for an app with one. A copy holds production's personal data too. See [Databases](../guides/databases.md#previews). | **An empty one** | Each preview's next deploy |

**Undo** drops the section's edits. Turning previews off asks first (**Turn previews off?**, **Turn off and remove**),
since it removes them.

### Image

Apps that run an image: what it runs. The section says where the image came from, its ID, age and size, and where its
updates come from.

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Image** | A name and tag, such as `postgres:18`, or a digest. Changing it pulls the new image first, then recreates the app on it; what the old image set is left behind. If the image isn't on the host, **Pull it** appears in the bar. **Choose tag…** beside it lists the image's tags, newest stable first, with the one the app runs marked **Running now**, and puts the one you use in the field (see [Go back to an older version](../guides/updates.md#go-back-to-an-older-version)). | The image it was created from | Recreate |
| **Updates** | **When I ask**: nothing's checked until you ask, and no badge shows. **Tell me**: a badge appears on the app when its registry has a newer image. **Automatically**: Gitberth pulls and recreates it when the tag moves, in the hours set in **Settings** › **Updates**. For an image no registry is known to have, the lines speak of a newer build of its tag on this host instead. Never automatic for a database. See [Image updates](../guides/updates.md). | For an app Gitberth created, **New apps from an image** in **Settings** › **Updates** (**Tell me** unless changed); **When I ask** for an image pinned by digest or ID; never **Automatically** for a database | At once |

### Deploy hooks

Git apps, Compose apps, and apps that run a registry's tag: secret URLs that deploy the app when something calls them,
such as a CMS, a scheduled job or CI. A Git app's hook builds its branch's newest commit again; a Compose app's deploys
its file again with its images pulled anew; an image app's pulls its tag and updates it when the image moved. Saved on
its own; nothing here restarts the app. See [Deploy hooks](../guides/deploy-hooks.md).

| Field or button | What it does | Default | Applies |
|---|---|---|---|
| **New hook's name** and **Create hook** | Makes a hook named for what will call it, like `Contentful`, up to 50 characters, one of each name per app and at most 10 hooks. The new hook shows a `curl` line to try it. | No hooks | At once |
| **On your network** | The hook's URL on Gitberth's own address, with a copy button. | | |
| **From the internet** | The hook's URL on the host name in **Settings** › **GitHub integration**, shown once instant deploys have one. | | |
| **Delete** (bin) | Deletes the hook, after asking. Its URL answers 404 from then on. | | At once |

Each hook also says when it was last used (**Last used 2 h ago**, or **Never used**).

### Databases

Every app but Gitberth itself, its tunnel and previews (whose follow their parent's): a Postgres database or a Redis of
the app's own, with its connection string in a variable Gitberth sets. Saved on its own; the app takes a change as any
settings change: a Git or Compose app with a deploy without a build, any other app recreated by the change engine. See
[Databases](../guides/databases.md).

| Field or button | What it does | Default | Applies |
|---|---|---|---|
| **Add** | **Postgres**: a database and a role of the app's own on a Postgres server, named after the app, that only it may connect to. **Redis**: a container of the app's own with a password and its data on a volume of its own. | **Postgres** | |
| **Server** | Postgres: the server it goes on, among those Gitberth can use (**· default** marks the default). With none, a **No Postgres server for it yet** box links to **Settings › Databases**. | The default in **Settings › Databases** | |
| **Container** | Redis: its container's name, the app's container with `_redis` added (`gb-website_redis`). Read-only. | | |
| **Variable** | The variable its connection string goes in. Says when it takes the place of the app's own value or a GitHub variable: Gitberth's value wins. | `DATABASE_URL`, `REDIS_URL` | |
| **Add database** | Makes it and sets the variable. | | At once, then the app takes it |
| **Copy connection string** | Copies the URL with its password; the page never shows it. | | |
| **Remove** (bin) | **Keep the data**: the variable goes, and the database stays where it is (a Postgres database on its server, a Redis's data in its volume); click **Remove it**. **Delete the data**: it's dropped too, once its name is typed in; click **Delete it**. A database Gitberth didn't make is only ever kept. | | At once, then the app takes it |

Each database shows its kind, name, server and size, its **Variable**, where it's **Reached at** (and as which role), and
when it was **Made** (**By Gitberth**, or **Taken over**: Gitberth never deletes a taken-over one's data).

### Command

Leave a field empty to use the image's own value, which shows greyed.

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Entrypoint** | The program the container runs. | The image's | Recreate |
| **Command** | The arguments it runs with, or the command when the image has no entrypoint. A secret in it, such as `cloudflared`'s `--token`, is shown masked; edit around it and it's kept. | The image's | Recreate |
| **Working directory** | The folder it starts in. | The image's | Recreate |
| **User** | The user it runs as. | The image's, else `root` | Recreate |

### Volumes

Storage that outlives the container. Recreating never deletes a volume.

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Volume or folder** | A named volume, a folder on the host (a full path such as `/srv/data`), or a temporary one in memory. An unnamed volume Docker made shows as **anonymous** and keeps its data. | None | Recreate |
| **Path in the container** | Where it's mounted, such as `/data`. | | Recreate |
| **read and write** / **read-only** | Click to switch. | **read and write** | Recreate |

**Named volume**, **Host folder** and **Temporary (tmpfs)** add a row. Add a volume for any data that must stay: without
one, what the app writes goes when the container is recreated.

A Git app's named volume is called `gb-<app>-<name>`, so one app's name can spell another's: `test-data` on `website`
and `data` on `website-test` are both `gb-website-test-data`. Gitberth refuses to save a named volume whose name another app has
already, or that one of the app's [previews](../guides/previews.md) would then share with another app.

### Restart

| Field | What it does | Default | Applies |
|---|---|---|---|
| **When it stops** | Docker's restart policy. **Never**: Docker doesn't start it again after a crash or a host reboot. **On failure**: it comes back after a crash, but not after a reboot or if you stop it. **Unless stopped**: it comes back after a crash or a host reboot, but stays down if you stop it. **Always**: like **Unless stopped**, and it also comes back after you stop it, once Docker restarts. With **Never** or **On failure**, a crash Docker doesn't start again is started by Gitberth, up to 4 times, and so is a stop with a host reboot (see [When an app stops by itself](../guides/health-and-monitoring.md#when-an-app-stops-by-itself)); an app you stopped stays down. | **Unless stopped** for an app Gitberth created | Live |
| **Tries before it gives up** | With **On failure**: how many times Docker restarts it. 0 tries for ever. | 0 | Live |
| **Scheduled restart** | **Off**, or **Every** so many **minutes**, **hours** or **days**, counted from when it last started. From 5 minutes to 365 days. The help under it says what a restart does to this app: a Git app is recreated with a deploy's checks, anything else gets `docker restart`. See [Scheduled restarts](../guides/scheduled-jobs-and-restarts.md#scheduled-restarts). | Off | At once |

### Resources

Limits stop one app from starving the others.

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Memory limit** | The most memory it may use, in **MiB** or **GiB**, at least 6 MiB. Empty for no limit. A Java app sizes its heap from it. | No limit | Live |
| **CPU limit** | How many of the host's CPUs it may use, such as `0.5` or `2`; the field says **of N CPUs**. Empty: it may use every core. | No limit | Live |
| **Shared memory** | The size of `/dev/shm`, in MiB. Databases and browsers use it. | Docker's 64 MiB | Recreate |
| **CPU priority** | **Low**, **Normal** or **High**. Only matters when the host is busy. | **Normal** | Live |
| **Process limit** | How many processes it may run. Empty for no limit. | No limit | Live |

### Health check

Docker runs the check inside the container and marks it healthy or not. Gitberth waits for healthy after a change or a
deploy, and the Health page marks when it starts failing.

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Check** | **Image default** (the image's own `HEALTHCHECK`, if it has one), **Command** (one of your own), or **None**. | **Image default** | Recreate |
| **Command** | With **Command**: run by the shell inside the container; it passes when it exits with 0. For example `curl -fsS http://localhost:3000/health \|\| exit 1`. | | Recreate |
| **Every** | Seconds between checks. | 30 | Recreate |
| **Timeout** | Seconds a check may take. | 30 | Recreate |
| **Retries** | Failed checks in a row before it's unhealthy. | 3 | Recreate |
| **Grace period** | Seconds after it starts during which failures don't count. | 0 | Recreate |

### Labels

Notes on the container that other tools read, such as Traefik, Watchtower or Homepage. Each label is a row with its value;
**Add label** adds one. Labels starting with `gitberth.` are Gitberth's own, and `com.docker.compose.` ones are Compose's:
both are shown locked. The image's own labels are listed underneath, read-only. Applies: Recreate.

### Logging

Where the container's output goes. The **Logs** tab reads it either way.

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Driver** | **The host's default**, `json-file`, `local`, `journald`, `syslog` or `none`. | **The host's default** | Recreate |
| **Options** | `key=value`, one per line, such as `max-size=10m` and `max-file=3` for `json-file`. | None | Recreate |

### Advanced

Everything else Docker can set. Most apps never need these. Every field here applies with a recreate.

| Field | What it does | Default |
|---|---|---|
| **Hostname** | The container's host name. | Docker's own |
| **Stop signal** | The signal Docker sends to stop it. | The image's, else `SIGTERM` |
| **Stop timeout** | Seconds Docker waits after the stop signal before it kills the container. | 10 |
| **Network mode** | **Its networks**, **The host's** (it uses the host's network directly, with no ports of its own) or **None**. A Git app always runs on its networks. | **Its networks** |
| **Init process** | Runs a small init process that reaps zombie processes and passes on signals. | Off |
| **Privileged** | Gives the container full access to the host's devices. A [preview](../guides/previews.md) never gets it, nor the app's **Devices**. | Off |
| **Read-only root** | Makes the container's own files read-only; only volumes are writable. | Off |
| **Terminal (TTY)** | Gives it a terminal. | Off |
| **Capabilities added** / **Capabilities dropped** | Linux capabilities, such as `NET_ADMIN`. | None |
| **DNS servers** | DNS servers it uses instead of the host's. | The host's |
| **GPUs** | `all`, a number, or `device=0,1`. | None |
| **Extra hosts** | `host:ip`, one per line, added to its `/etc/hosts`, such as `homelab:192.168.50.10`. | None |
| **Devices** | `host path:container path`, one per line, such as `/dev/dri:/dev/dri`. | None |
| **Ulimits** | `name=soft:hard`, one per line, such as `nofile=65536:65536`. | None |
| **Security options** | One per line, such as `no-new-privileges`. | None |
| **Sysctls** | `key=value`, one per line, such as `net.core.somaxconn=1024`. | None |

### Remove

**Remove …** removes the app. A Git app's container goes with its deploy history; its images and volumes go only if you
tick them. A Compose app's whole project goes (`docker compose down`), its named volumes and images only if you tick them. Any other app's volumes stay unless you tick them. See [Remove an app](../guides/manage-apps.md#remove-an-app).
Gitberth's own container has no **Remove** section.

## Environment tab

The app's environment variables. See [Environment variables](../guides/environment-variables.md) for how to use them.

| Part | What it does | Applies |
|---|---|---|
| **Name** / **Value** | Each variable the container gets. Change a value in place; a new row takes a name too. | Recreate |
| **Secret** (lock) | Masks the value wherever it's shown. Always on for names and values that look secret. Off, and can't be turned on, for a Git app's `RAILPACK_` variable, which Railpack writes into its plan and the image: one that holds a password isn't saved, and the secret goes in a variable of its own, written as `$NAME` in the setting (see [Railpack's settings](railpack.md#settings)). | At once |
| **Build time** (wrench) | Git apps: passes the variable to the image build too. Always on, and can't be turned off, for a `RAILPACK_` variable: it configures [Railpack's build](railpack.md#settings). | Build |
| **All deploys** / **Production** / **Previews** | Git apps: which deploys get the variable. One name can have a value for each; a **Production** or **Previews** one wins over **All deploys**. See [Environments](../guides/previews.md#environments). | Recreate (production) or the previews' next deploy |
| **N from the image** | The image's own variables, folded. Edit one to make it the app's own. | Recreate |
| **Add variable**, **Paste .env** | Add one variable, or several from `.env` lines. | Recreate |
| **Stored in GitHub**, **Stored in GitLab** | Git apps: the repository's GitHub variables, or the GitLab project's CI/CD variables with their scope (**Every deploy (\*)**, **Production (production)**, **Previews (preview/\*)**). **Save to GitHub** (**Save to GitLab**) and delete act there at once; the app gets them with its next deploy. **Open in GitHub** opens their page. | Next deploy |
| **Set by Gitberth, like on Vercel** | Git apps: the variables Gitberth adds, read-only. Turned off with **Vercel's variables** in **Source**. | |
| **Set by Gitberth from its databases** | The variables the app's [databases](../guides/databases.md) give it, with where each comes from, read-only. For a Git or Compose app they win over a value of the same name above (tagged **Replaced by Gitberth**) and over GitHub's; any other app's row of one says **Set by Gitberth**. A Compose app's come in parts too (`DATABASE_URL_HOST` …). | |
| **Set by Gitberth: where it's reached** | Apps installed from a [template](../guides/templates.md): `APP_URL`, `APP_HOST`, `APP_SCHEME` and `APP_HTTPS`, from its main domain or its LAN address, read-only. A variable of the same name above wins, tagged **Yours above**. | Each deploy |

A [Compose app](../guides/compose-apps.md)'s variables are what its Compose file's `${…}` are filled with: Gitberth writes
them to the project's `.env` too, so `env_file: .env` takes them. A service gets one only when the file passes it on
(`environment:` or `env_file:`). A change applies with a deploy without a build.

## Network tab

How the app is reached. The diagram at the top, **How … is reached**, draws every way in, as Docker and the tunnel have it
now. [Networking](../guides/networking.md) and [Domains](../guides/domains.md) explain these in depth.

![An app's Network tab, with how it's reached: from the internet through Cloudflare, from your LAN, and on the gitberth network](../images/app-network.webp)

### Ports

| Column | What it does | Default |
|---|---|---|
| **Web** | Marks the web port: the one **Open**, domains, the health URL and scheduled jobs use. Moving it restarts nothing. | The first web port Gitberth found |
| **Container port** | The port the app listens on inside the container. | From the image, the paste, or detection |
| **Protocol** | **TCP** or **UDP**. | **TCP** |
| **Access** | **Internal**: not published; containers on its networks reach it by name. **LAN**: every device on your network, at this host's address and the host port. **Host only**: programs on the host itself (`127.0.0.1`), like the Cloudflare tunnel. | **LAN** for a new app |
| **Host port** | The port on the host. It says as you type whether the port is free, and who has it if not. | The container port when it's free, else the next free one above it |
| **Address** | The address this gives, with a copy button. | |

**Add port** adds a row. The bar at the bottom applies a change: **Save** when nothing restarts, **Apply** for a Git app
(a deploy without a build), or **Review and apply** for any other app (a recreate, through the change dialog). A Git app
also offers **Save for next deploy**. A container Compose manages gets the lines for its Compose file instead.

### Domains

The app's domains, through the Cloudflare tunnel, once domains are turned on in **Settings** › **Domains (Cloudflare)**.
Type a domain such as `app.example.com` and click **Add domain** (or **Add domain and publish** when the web port is
**Internal**). Each domain shows **Live**, **Not working** or **Down page**, with **Make main**, **Serve every path**,
**Point it here** or **Fix** when they apply, a button that previews its down page, and a button to remove it. See
[Domains](../guides/domains.md).

#### Down page

At the foot of the **Domains** card, once the app has a domain.

| Field | What it does | Default | Applies |
|---|---|---|---|
| **Down page** | While the app can't be served (stopped, its container down, a deploy replacing it, or its protection not applied yet), its domains show a clean "temporarily unavailable" page instead of Cloudflare's error. See [The down page](../guides/uptime-and-status-page.md#the-down-page). | On | At once, saved on its own |
| **Message** | A sentence of yours on the down page, up to 280 characters, like "Back by 9:00 UTC." A protected domain's down page leaves it out. **Save message** saves it. | None | At once, saved on its own |

### Networks

The Docker networks the container is on, with the names other containers use to reach it there, its IP address, and
who else is on each. **Join a network…** with an optional extra name, then **Join**, adds one; it's instant and restarts
nothing. **Leave…** asks first. See [Networking](../guides/networking.md).

### Own LAN address

An address of the app's own on your LAN, once own LAN addresses are turned on in **Settings** › **Own LAN addresses**.
Choose **Off**, **Automatic** (the next free address of the block) or **Static** (one you type), and click **Apply**. It's
instant and restarts nothing. **Change** and **Remove…** change it later. See [Networking](../guides/networking.md).

![An app's Network tab with an own LAN address on](../images/own-lan-address.webp)

## Overview tab: Uptime

The **Uptime** card on the app's **Overview** checks the app from outside, the way a visitor reaches it. See
[Uptime checks](../guides/uptime-and-status-page.md).

| Field or button | What it does | Default | Applies |
|---|---|---|---|
| **Uptime check** | Checks the app every minute (as set in **Settings › Uptime and status page**): its main domain over HTTPS, else its web port on the LAN. Off, it isn't checked, sends no down or back-up notifications, and the status page leaves it out unless you picked it there. | On | At once, saved on its own |
| **Path** | The path the check asks for, like `/health`. | `/` | At once, saved on its own |
| **Check now** | Checks it at once. A stopped app isn't checked: it says why instead. | | |

## Related

- [Manage apps](../guides/manage-apps.md)
- [Environment variables](../guides/environment-variables.md)
- [Networking](../guides/networking.md)
- [Gitberth's settings](gitberth-settings.md)
