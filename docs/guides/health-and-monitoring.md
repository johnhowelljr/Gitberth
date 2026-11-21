# Health and monitoring

Gitberth records the CPU, memory and network traffic of every container on the host every 5 seconds, including the ones it didn't start, such as cloudflared and Gitberth itself. It keeps 6 hours of readings, across its own restarts, and marks deploys, restarts, crashes and health changes on the charts. This page covers the **Health** page, an app's own charts, and health checks: how to add one and what Gitberth does with it.

## The Health page

Click **Health** in the sidebar. The page lists every container that runs on the host, one row each, the busiest first.

![The Health page](../images/health.webp)

- **The header** shows the host's name, its CPUs, its memory and how many containers run. **Live** with the time of the last reading means the page is current. **Not updating** means Gitberth can't read Docker right now, and the last readings stay on screen.
- **1 hour** shows every reading. **6 hours** shows 30-second averages.
- **Find a container** searches by name, title or image. **Busiest first** and **A to Z** set the order.
- The twelve busiest show first. **Show the other …** lists the rest.
- **Each row** shows the container's name (and its image), its state, a CPU sparkline with the latest reading, its memory, and what happened to it in the time shown, like `1 deploy · 2 restarts`. An app that's [checked from outside](uptime-and-status-page.md) also shows how that stands, like **Uptime: up**.
- **Stopped** collects the containers that stopped but have readings or events from the last 6 hours. Their state says whether they were stopped or crashed, like **Exited (1)**.

### Open a container's charts

Click the arrow at the end of a row. It opens to three charts: **CPU**, **Memory** and **Network** (in and out).

![A container's charts on the Health page](../images/health-charts.webp)

Above the charts:

- **Gather stats** turns the readings for this container off or on (see [Stop gathering stats for a container](#stop-gathering-stats-for-a-container)).
- **No scheduled restart** (or **Restarts every …**) sets a scheduled restart. See [Scheduled jobs and restarts](scheduled-jobs-and-restarts.md).
- **Rename** gives the container another name in Gitberth (see [Rename a container](#rename-a-container)).
- **As a table** lists the readings and events instead, newest first. **Charts** goes back.

The app page's **More** menu has **On the Health page**, which opens the page at that app's charts.

### Reading the charts

- **CPU** uses the `docker stats` scale: 100% is one full core, so a busy container on a 6-core host can show up to 600%.
- CPU time and network bytes are running totals, so nothing between two readings is missed. Each point is the average over its 5 seconds: a 1-second burst still shows, at a fifth of its height.
- **Memory** is a reading taken at that moment, so a spike that comes and goes between readings isn't seen. With a memory limit set, the chart says how much of it is in use, like `of 512 MiB limit`.
- Containers that use the host's network (`--network host`, as cloudflared often does) have no network counters of their own: the chart says **Uses the host's network, so Docker can't count its traffic separately.**
- A new container shows **Collecting. The first point appears within 10 seconds.**

**Dashed lines** mark what happened, from Docker's own events, so they include containers Gitberth didn't start and restarts it didn't do. Point at a line for the details. The stretch a container was down is shaded.

| Mark | Meaning |
|---|---|
| **Deploy #12** | A deploy started this container |
| **Settings applied** | A deploy applied saved settings |
| **Rollback #12**, **Rolled back** | A rollback, or the previous version running again after a failed deploy |
| **Scheduled restart** | A scheduled restart |
| **Brought back** | Gitberth made the container again because it was gone ([When an app stops by itself](#when-an-app-stops-by-itself)) |
| **Recreated** | A new container took its place |
| **Started**, **Stopped**, **Restarted** | Started, stopped, or restarted by Docker or by hand |
| **Exited** | Its process ended with exit code 0 |
| **Crashed** | It exited with an error code |
| **Out of memory** | It ran out of memory |
| **Paused**, **Resumed** | Paused and resumed |
| **Unhealthy**, **Healthy** | Its health check started failing, and passes again |

After Gitberth itself was down, it picks up whatever events Docker still remembers.

### Rename a container

Containers named by Compose or by hand can be hard to tell apart. **Rename** on the Health page gives one another name:

- **An app's** name changes everywhere in Gitberth, as it would in its **Settings › General**. The container keeps its own name, so nothing is redeployed.
- **Any other container** gets a title that only Gitberth shows. Leave it empty to show the container's own name again.

Either way, the container's own name stays visible under the title.

### Stop gathering stats for a container

Turn off **Gather stats** above a container's charts. It moves to **Not gathering stats** at the bottom of the page, where its switch turns it back on. Its last 6 hours are kept. Every app has the same switch in its **Settings › General**.

## An app's own charts

An app's **Overview** tab has a **Resources** card with its **CPU** and **Memory** charts. **1 h** shows the last hour, every 5 seconds; **6 h** shows the last 6 hours, every 30 seconds. The card says how long the app was last down, and **Open in Health** opens its row on the Health page.

![An app's Overview tab](../images/app-overview.webp)

When stats aren't gathered for the app, the card says **Stats aren't gathered for it**, with **Turn it on in Settings**.

The app's header also shows its uptime, CPU (with a sparkline) and memory, against its limit when it has one.

## Health checks

There are two kinds, and an app can have both:

- **A Docker health check** is a command Docker runs inside the container every so often. The container then reports **Starting**, **Healthy** or **Unhealthy**.
- **A health URL** is a path on the app's web port, like `/health`, that Gitberth checks after a deploy or a change, before the new container takes over.

### Add a Docker health check

1. Open the app's **Settings** tab and go to **Health check**.
2. Under **Check**, choose one:
   - **Image default**: the image's own check, if it has one. The section shows it, or says the image has none.
   - **Command**: a command of your own.
   - **None**: no health check.
3. For **Command**, type the **Command**, for example `curl -fsS http://localhost:3000/health || exit 1`. It runs in the container's shell and passes when it exits with 0. Optionally set **Every**, **Timeout**, **Retries** and **Grace period**; empty fields use Docker's defaults (30 s, 30 s, 3 and 0 s).
4. Click **Review and apply**, then **Recreate …** in the change dialog.
   The container is recreated with the new check. For a Git app the button is **Apply**, and the change goes live with a deploy without a build.

The command needs the tool it calls (`curl` here) to be in the image.

### Set a health URL

1. Open the app's **Settings** tab and go to **General**.
2. Type a path in **Health URL**, like `/health`.
3. Click **Apply** in the bar at the bottom. Nothing restarts.

The field shows for Git apps and for apps with a web port.

### What Gitberth does with them

- **Shows them.** The app's state reads **Healthy**, **Unhealthy** or **Starting** wherever it's shown. The **Container** card on the **Overview** shows the check's state and command, or **None.** with **Add one**.
- **Counts unhealthy apps.** An unhealthy app is counted under **Needs attention** on the dashboard and the **Apps** list.
- **Marks the charts** when a check starts failing and when it passes again.
- **Tells you.** With **A health check fails** on in **Settings › Notifications**, a [notification](notifications.md) goes
  out when the app turns unhealthy, and again once it passes.
- **Waits for healthy before a new container takes over.** After a deploy, an update or a change that recreates the container, a container with a Docker health check has to report healthy, instead of only staying up for 15 seconds. A deploy waits up to 60 seconds after those 15 seconds. A change or an update waits for the check's grace period plus its interval times (retries + 1), plus 10 seconds.
- **Checks the health URL.** After the container is up, Gitberth requests the health URL on the web port. Any answer below 400 passes; redirects aren't followed. A deploy tries every 2 seconds for up to 60 seconds.
- **Rolls back when they fail.** If the new container reports unhealthy, never reports healthy in time, or the health URL doesn't answer, the old container keeps running (or comes back), and the deploy log or change dialog says which check failed.

Gitberth doesn't restart a container that turns unhealthy later. To restart an app now and then, give it a scheduled restart (see [Scheduled jobs and restarts](scheduled-jobs-and-restarts.md)).

## When an app stops by itself

Docker starts a crashed container again only as its restart policy says (**When it stops** on the app's Settings tab,
under **Restart**): never with **Never**, a few times with **On failure**. Every 30 seconds Gitberth looks at every app
that's meant to run, and brings one back that stopped by itself:

- **It crashed and Docker won't start it again**: it exited with an error, or ran out of memory, whatever its restart
  policy (**Never** included).
- **Docker couldn't start it**: a port was taken, or a folder it mounts wasn't there yet when the host booted.
- **It stopped when Docker or the host restarted**, and its restart policy didn't start it again.
- **A deploy stopped it and couldn't start it again.** Not a `docker stop` someone ran while the deploy was building:
  Gitberth heard that one, knew it wasn't its own, and leaves it.
- **Its container is gone**: Gitberth makes it again. A Git or Compose app gets a deploy without a build from its last
  image (or its current release); with that image gone too (Docker says so), the commit it ran is built again on its
  branch, never a newer one. An app from a registry, a local image or by hand is made again from its saved settings. A
  Compose service that Compose never creates (behind a profile, or with no replicas) isn't missing.

It waits until the app has been down for 30 seconds (a missing container: two looks in a row), so Docker's own restarts
and a `docker restart` are over first. It never starts an app while a deploy, a change, a deletion or a Forget has it,
nor when Docker doesn't answer, and a Stop that comes while it brings an app back wins.

### What it leaves alone

- **An app you stopped**: with **Stop** in Gitberth, or with `docker stop` or `docker kill` outside it. Gitberth reads the
  stop from Docker's events, or from its exit code (130, 137 and 143) when it wasn't watching. It remembers a stop it
  left, so the app stays stopped after Gitberth restarts, even once Docker's events of it are gone.
- **A program that ended by itself with exit code 0**, as a one-shot job does.
- **An app whose deploy to bring it back you cancelled**: Gitberth leaves it as it is, without trying every 6 hours,
  until you start, stop or deploy it.
- **A container someone adopted that's gone**: Gitberth doesn't make it again by itself. Start it to make it again from
  its saved settings, or forget the app.
- **Gitberth itself.**

The app's Activity says once why it was left, and its page shows **It stopped without a crash** (or **Gitberth leaves it
as it is**, after a cancelled deploy).

### When it keeps stopping

Gitberth tries up to 4 times in a row: at once, then 1, 5 and 15 minutes after the try before. An app that then stays up
for 10 minutes is fine again, and its next stop counts from the first try. A build counts as all 4 tries: if building it
again doesn't work, Gitberth stops at once.

After the 4th try, Gitberth stops bringing it back and tells you. It still tries every 6 hours, quietly, and tells you
only if the app comes back. Start or deploy the app once it's fixed: from then on a stop counts from the first try again.
A deploy that changed nothing that runs (a commit the build filters skip, or one cancelled before it ran) doesn't count
as one. A try counts even when Gitberth can't write it down (a full disk, say), so the back-off holds.

### Where it says so

- **The app's page**: a banner while a try is coming (**Gitberth brings it back in a moment**, with **Start now**), and once
  Gitberth gave up (**Gitberth stopped bringing it back**, with **Start** and **Logs**).
- **The dashboard and the top bar**: an app Gitberth gave up on shows as a banner, and as a pill on every page.
  [AI agents](../reference/api.md) see it in `get_health`'s problems.
- **Activity**: each try, and the give-up (the **Starts and stops** filter).
- **The audit log**: `app.recovered` for each try and `app.recovery.stopped` for the give-up, by Gitberth through the
  keep-running check (see [Audit log](audit-log.md)).
- **Notifications**: with **Gitberth brings an app back** on in **Settings › Notifications**, the first try of each
  incident (once the app runs), one notice for all the apps brought back after Docker or the host restarted, and the
  give-up (see [Notifications](notifications.md)). Its own deploys aren't told as deploys, however they end, nor posted
  to GitHub or GitLab as commit statuses: the commit they build ran already.
- **Settings › Keep apps running**: the apps Gitberth gave up on, with **Start**, and what it brought back in the last 24
  hours. Turn **Bring apps back by themselves** off there to have Gitberth leave every stopped app as it is.

Two things to know: a `docker stop` of an app in the same minute as Gitberth's own container stops looks like Docker
restarting, so that app comes back with Gitberth; and Gitberth running outside a container (while developing it) can't
tell a Docker restart from a stop.

## Related

- [Manage apps](manage-apps.md)
- [App settings](../reference/app-settings.md)
- [How deploys and changes work](../concepts/how-deploys-work.md)
- [Troubleshooting](troubleshooting.md)
