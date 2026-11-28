# Manage apps

In Gitberth every container on the host is an app, whoever started it: the ones it builds from GitHub, the images you run
from it, and containers started some other way, such as a database you ran by hand or `cloudflared`. You find them all on
the **Apps** page, and each one has a page where you start, stop and restart it, change any of its settings, and remove it.
This page covers the list, the app page, and what each action does.

## The Apps list

Click **Apps** in the sidebar. The strip at the top shows how many apps are running, the host's CPU, the memory the apps
use, and the disk. If any app is in trouble (crashed, unhealthy, or missing its container), a box says so; click
**Show them** to list only those. When Docker's disk is getting low the strip says so, and the dashboard has a banner
that leads to **Settings › Disk**.

![The Apps list with apps from GitHub, registries and by hand](../images/apps-list.webp)

Each row shows:

| Column | What it shows |
|---|---|
| **App** | The app's name, and its repository, or its image. **Update** means a newer image is available (never shown for an app set to **When I ask**); **System** marks Gitberth itself and the Cloudflare tunnel. |
| **Status** | **Running**, **Healthy**, **Stopped**, **Exited with an error**, **Deploying** and so on, with how long it has run. A Compose app says how many of its services run: **Running 3/3**, or **Degraded 2/3**. |
| **Address** | Where you reach it. A web address opens in a new tab; a lock means it's reachable from the host only. **Internal only** means no port is published; **No ports** means it has none. |
| **Ports** | Its ports, Docker's way round (`5432:5432`). Click a web port to open it, or any other published port to copy its address. A lock marks a host-only port, and a faint one, like `5432/tcp`, isn't published. |
| **CPU · Memory** | The latest reading, with a small CPU chart. 100% CPU is one full core. **Not gathered** when stats are off for it. |
| **Source** | Where it came from: **Git** (with GitHub's or GitLab's mark), **Registry**, **Local image**, **By hand**, **Compose** for a Compose file Gitberth keeps, or **Existing** for a container started outside Gitberth. |

The list refreshes every 5 seconds.

### Search, filter, sort and group

- **Search**: type in **Search names, images, ports, addresses** (press **/** to jump there). It finds apps by name, image,
  repository, port or address. Several words narrow it down. When nothing matches, **New app from …** starts a new app
  from what you typed.
- **Show**: **All**, **Running**, **Stopped**, and **Needs attention** or **Updates** when there are any. Each has a count.
- **Source**: pick one or more sources; **Every source** clears it.
- **Sort by**: **Pinned, then name** (the default), **Name**, **Status**, **Address**, **Source**, **CPU** or **Memory**.
  You can also click a column's heading. Click it again, or the arrow next to **Sort by**, to reverse the order. CPU and
  memory start with the highest. The order stays in the page's address, so it's still there when you come back from an
  app.
- **Group by**: **Name** (the default) gathers apps whose names share a first word, like `sample-*`, once there are three
  or more; **Source** groups by where they came from; **Nothing** shows one list. A big group is split by runtime, and
  **Start N stopped** starts the apps in it that were stopped on purpose.

### The row's menu

Hover over a row for quick buttons (open, restart, stop or start, logs, settings), or click the **⋯** button at the end of
the row for its menu:

- **Open its page**
- **Open** and the app's address, when it has a web address and runs
- **Restart** and **Stop**, or **Start** when it's stopped
- **Logs**, **Settings** and **Network**, which open those tabs

![An Apps row's menu, open, with Open its page, Restart, Stop, Logs, Settings and Network](../images/apps-row-menu.webp)

**Paste docker run**, at the top of the page, starts a new app from a command. See
[Paste a docker run command or a Compose file](paste-docker-run-or-compose.md).

## The app page

Click an app's name to open its page.

### The header

The header shows the app's state, where it came from (its repository, branch, folder and commit, or its image; a
Compose app's **Compose: compose.yaml**, or **Template: …** for one installed from a template), its addresses (its
domain, **LAN**, **Own**, **Host** and **Internal**), and these figures: **Uptime**, **CPU**, **Memory**, **Last
deploy** for an app Gitberth deploys (or **Restart policy**), **Scheduled restart**, and **Health check** (or
**Restarts**). It's read again every 5 seconds.

- **The state**, after the name, with how long it has been that way (**Running** for 3 days), or when it stopped.
  **System** marks Gitberth itself and the Cloudflare tunnel.
- **The source line**:
  - A Git app: **Git** with GitHub's or GitLab's mark (a link to the repository), the repository, its branch, its folder
    when it has one, and the commit that runs with the first line of its message. A Git app deployed from a Compose file
    adds **Compose: …** with the file's name.
  - A Compose file Gitberth keeps: **A Compose file Gitberth keeps**, **Template: …** when a template installed it (a
    link back to the template), and when it was last deployed.
  - Any other app: its image and tag, where updates come from (**from Docker Hub**, **from its registry**, **local
    image**, **pinned to one image**, or **built by Gitberth**), and the start of its image's ID.
  - Then the container's own name when the app goes by another, and **Compose: …** with the project's name for a
    container Docker Compose made outside Gitberth.
- **The addresses**: its main domain (**+1 domain** when it has more, all of them on its **Network** tab), then **LAN**
  with a copy button, **Own** for its own LAN address, **Host** for a port published on the host only, and
  **Internal**, its name and port on its network. An app that has ports but publishes none says **Not published**, with
  **Publish on LAN**, which opens its **Network** tab. One on the host's network shows **Network**: the host's own, on any
  port it opens, or the host's own address (see [Host network mode](networking.md#host-network-mode)).

| Figure | What it shows |
|---|---|
| **Uptime** | How long the container has run since it last started, and since when; **Not running** while it's stopped. A restart or a deploy starts it again. |
| **CPU** | The latest reading, with a small chart of the last minutes. 100% is one core, so a busy app can show more on a host with several. |
| **Memory** | What it uses now, against its memory limit, or **no limit**. |
| **Last deploy** | For an app Gitberth deploys: the number of its latest deploy that wasn't skipped (a link to its log), how long ago it was queued, and what started it. |
| **Restart policy** | For any other app: what Docker does when it stops (**Never**, **On failure**, **Unless stopped** or **Always**). |
| **Scheduled restart** | **Every …** and the interval, counted from its start, or **Off**. See [Scheduled jobs and restarts](scheduled-jobs-and-restarts.md). |
| **Health check** | When it has one: **Healthy**, **Unhealthy** or **Starting**, with the command. See [Health checks](health-and-monitoring.md#health-checks). |
| **Restarts** | When it has no health check: how many times Docker restarted it by itself, as its restart policy says. A number that keeps growing means it keeps crashing: its **Logs** tab shows why. |

The buttons are:

- **Open**, when it has a web address; **Connect** for a database; otherwise **Logs**.
- **Deploy** for a Git or Compose app. For an app that runs an image, **Update** when a newer image is available, else
  **Check for update**.
- Restart, and stop or start.
- **⋯** (More): **Deploy without the build cache** (a Git or Compose app), **Pin to the menu** (or **Unpin from the
  menu**), **Rename…**, **Check for update** for an app that runs an image (**Check for a newer build** for one built on
  this host), **Roll back to …** after an update (see [Roll back an update](updates.md#roll-back-an-update)), **On the
  Health page**, and **Remove…** (or **Forget…** for a record with no container).

![The app page's More menu, open](../images/app-more-menu.webp)

### The tabs

| Tab | What's on it |
|---|---|
| **Overview** | **Addresses**, with copy buttons and **Connect** for a database; a Compose app's **Services**, each with its **Log** and **Restart**; **Resources**, its CPU and memory over the last hour or 6 hours (**1 h**, **6 h**); **Container** (a Compose app's **Web service's container**): image, command, restart policy, health check, limits, environment, volumes, networks and ID; **Recent activity**; **Uptime**, its checks from outside (see [Uptime checks](uptime-and-status-page.md)); **Databases**, when it has any; **Updates** for an app that runs an image, with **Check now** and, after an update, **Roll back to …**; **Scheduled jobs** for a Git app that has any; and **Scheduled commands** (see [Scheduled commands](scheduled-jobs-and-restarts.md#scheduled-commands)). When the image's tag names another image on the host than the one the container runs (after a new pull or build, or a rollback), the **Container** card still shows the image by its tag, and adds **the tag has moved on**. |
| **Environment** | Its variables. See [Environment variables](environment-variables.md). |
| **Network** | How it's reached, its ports, domains, networks and own LAN address. See [Networking](networking.md) and [Domains](domains.md). |
| **Previews** | A Git app's previews of its pull requests and branches. See [Preview deployments](previews.md#the-previews-tab). |
| **Settings** | Every other setting, in sections. See [Change settings](#change-settings) and [App settings](../reference/app-settings.md). |
| **Logs** | Its live output, with timestamps. The menu chooses how much history to show (**Last 100 lines**, **Last 500 lines** or **Last 2000 lines**); the button next to it reconnects. A Compose app's **Service** picker shows another service's log. See [Read an app's logs](#read-an-apps-logs). |
| **Terminal** | A shell in its running container, in the browser. See [The terminal](terminal-and-files.md#the-terminal). |
| **Files** | Its volumes and host folders: browse, download, upload, rename, delete, and edit small text files. See [The Files tab](terminal-and-files.md#the-files-tab). |
| **Activity** | What happened to it, newest first: deploys, settings changes, updates, starts, stops and crashes. See [Deploys and rollbacks](deploys-and-rollbacks.md#the-activity-tab). |

A [preview](previews.md)'s own page has no **Environment**, **Previews** or **Settings** tab: its settings are its
parent's.

![An app's Overview tab](../images/app-overview.webp)

![An app's Logs tab with its live output](../images/app-logs.webp)

### Read an app's logs

The **Logs** tab shows what the app's container writes, as `docker logs` would, and follows it live. **Logs** in the
header (when it has no **Open** or **Connect**), and in an Apps row's menu, open it too.

- **How much history**: it opens with the container's last 500 lines, then adds new ones as they come. The menu,
  **Last 500 lines**, picks **Last 100 lines**, **Last 500 lines** or **Last 2000 lines**; choosing one reads the log
  again from there.
- **Timestamps**: each line has its number and the time it was written, in Gitberth's time zone (the `TZ` in its
  `.env`). Lines that look like errors (`ERROR`, `error:`, an `Exception`) show in red, and warnings (`warn:`,
  `warning:`) in amber.
- **Live**, after the menu and the round arrow, while the log is followed. When the stream ends, it says why instead:
  **The container stopped**, or what Docker answered. The round arrow, **Reconnect**, reads the log again and follows it
  once more: use it after the container starts again.
- **Scrolling**: the view stays at the newest line while you're at the bottom. Scroll up to read, and it stays where you
  are; scroll back down and it follows again.
- **Long logs**: the tab keeps the last 5000 lines and shows the last 3000; a line at the top says how many earlier ones
  aren't shown.
- **Copy the log** copies every line the tab holds, and the wrap button turns line wrapping off or on.
- **A Compose app** shows a **Service** picker above the log when it has more than one service. Its web service, marked
  **(web)**, comes first and shows when the tab opens; pick another to read that service's log. The choice is in the
  page's address (`?service=worker`), and each service's **Log** on the **Overview**'s **Services** card opens it.

A Git app's build output isn't here: it's in each deploy's log (see
[The deploy page](deploys-and-rollbacks.md#the-deploy-page)).

### Banners

A banner under the header says when something needs you:

- **Preview of … for …** (a preview's page): what it previews, with **Pull request** (or **Merge request**, or
  **Branch**) and **All previews**.
- **Saved, not live yet** (Git and Compose apps): settings you saved wait for a deploy. Click **Apply now** for a deploy
  without a build, or **Rebuild and deploy**. When the change needs a new build, only **Rebuild and deploy** is offered.
- **A change is waiting for you**: a change was cut short, and Gitberth can't tell which container should stay. Click
  **Keep the new one** or **Put the original back**. **About the last change** says how a change that's over ended;
  **Dismiss** hides it.
- **Waiting for a change to settle**: the app can't start until a change of it is over.
- **Its container is gone**, **Not created yet** or **Not deployed yet**: Gitberth has the app's settings but no
  container. For an app from an image or by hand, **Start** (or **Create it again**) makes it from the saved settings, and
  **Forget…** drops the record. For a Git app, the banner says what **Start** will do: make the container again from its
  last image, without a build, or, when that image isn't on the host any more, build the branch again and deploy it. A
  container started outside Gitberth that was removed can only be forgotten here: start it again the way it was started.
  A sentence after it says whether Gitberth makes the container again by itself, and when.
- **Renamed outside Gitberth**: its container runs under another name now. Open that one, or forget this record.
- When an app that's meant to run is down (see
  [When an app stops by itself](health-and-monitoring.md#when-an-app-stops-by-itself)):
  - **Gitberth brings it back in a moment**: it crashed, or stopped when Docker or the host restarted, and Gitberth
    starts it again shortly, with which try it is. **Start now** doesn't wait.
  - **Gitberth stopped bringing it back**: it kept stopping after 4 tries. **Logs** shows why; **Start** it once it's
    fixed. The dashboard shows it too, and the top bar on every page.
  - **It stopped without a crash**: it was stopped from outside Gitberth, or its program ended by itself, so Gitberth
    leaves it stopped. **Start** starts it.
  - **Gitberth doesn't bring apps back by itself**: **Keep apps running** is off in Settings.

## Start, stop and restart

Use the buttons in the app's header, its row on the Apps list, or the dashboard.

- **Stop** always asks first. It says the app stops until you start it again and, for a Git app, that new commits aren't
  deployed while it's stopped.
- **Restart** and **Stop** say first what else they affect, such as the domains `cloudflared` serves, or the apps whose
  settings name this one (an app whose `DATABASE_URL` points at `postgres`).
- **Start** of a Git app whose saved settings aren't live yet recreates it with them. A Git app that was never deployed is
  deployed. One whose container is gone gets it back from its last image, without a build, or is built again from its
  branch when that image isn't on the host any more.
- While Gitberth deploys or changes an app, it can't be started, stopped, restarted or removed until that's done.
- A stopped database won't start while another running one uses the same volume: two servers on the same files can damage
  them. Stop the other one first.
- Gitberth won't stop or remove its own container. Use `docker compose` on the host for that.

## Rename and pin

- **Rename**: click **⋯** › **Rename…**, or go to **Settings** › **General**, and change **Name**. The container keeps its
  own name, so other containers still reach it by it. Nothing restarts.
- **Pin**: click **⋯** › **Pin to the menu**, or turn on **Pinned** in **Settings** › **General**. Pinned apps show in the
  sidebar and on the dashboard.

## Change settings

The **Settings** and **Environment** tabs, and the ports on the **Network** tab, are a draft until you apply them. A
changed field gets **Changed** next to its label and **was …** under it, with an undo button.

While there are changes, a bar at the bottom says how many (**3 unsaved changes**) and what applying does, such as
**No restart.** or how long the app will be offline. Click **Discard** to drop them, or **Apply** (**Review and apply**
when the container is recreated).

What a change does depends on the setting and the app:

| What you change | What happens |
|---|---|
| Name, pinned, gathering stats, health URL, update choice, scheduled restart | Saved at once. Nothing restarts. |
| Restart policy, memory limit, CPU limit, process limit | Changed in place with `docker update`. No restart. |
| Anything else, for an app that isn't from Git | The container is recreated with the new settings. The change dialog shows what happens first. |
| Anything else, for a Git app or a Compose app | Saved, then made live by a deploy without a build. A Git app's **Save for next deploy** keeps it for later instead. |
| Branch, folder, build, build-time variables or Vercel's variables, for a Git app | Saved. The next deploy builds the app with them. |
| A container that Docker Compose made | Limits change in place. Anything that needs a recreate shows **Change it in Compose**: the lines for its Compose file and the command to run. Gitberth doesn't recreate a Compose container, since Compose would undo it. |
| An app with no container | Saved. It applies when the container is created. |

Gitberth's own container shows its settings, and any change gives the lines for its Compose file.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 24}}}%%
flowchart TD
    edit["You change a setting"] --> bar["The bar: N unsaved changes,<br/>and what applying does"]
    bar --> kind{"What does<br/>the change<br/>touch?"}
    kind -->|"Gitberth's<br/>own data"| saved["Saved<br/>at once"]
    kind -->|"limits,<br/>restart<br/>policy"| live["Changed in<br/>place: no<br/>restart"]
    kind -->|"a Git or<br/>Compose app's<br/>container"| deploy["A deploy<br/>without<br/>a build"]
    kind -->|"anything<br/>else"| dialog["The change<br/>dialog: what<br/>changes, what's<br/>kept, the impact"]
    dialog --> create["New container created<br/>next to the old one"]
    create --> stop["Old one stopped,<br/>the names swapped"]
    stop --> start["New one started<br/>and checked"]
    start -->|"passes"| done["Old one removed;<br/>its volumes stay"]
    start -->|"fails"| back["Old one put back<br/>as it was"]
```

### The change dialog

When applying recreates the container, the change dialog opens first. It calls the app by its name in Gitberth, not by
its container's: **Apply 1 change to adminer?**, not `gb-adminer`. It shows:

- **What changes**: each setting, old and new.
- **What's kept**: its name, image, command, environment, volumes and the rest.
- **Impact**: about how long it's offline, who notices (the apps whose connections drop, the domains that pause), and
  what happens if the new container fails.

![The dialog that applies a settings change, with what changes, what's kept and the impact](../images/change-dialog.webp)

Click **Recreate adminer** to go ahead, or **Cancel**. A change that needs no restart shows **Apply, no restart** instead.

Gitberth creates the new container first, so a setting Docker refuses fails before anything stops. Then it stops the old
one, starts the new one and checks it stays up. The dialog shows each step as it runs, such as **Starting the new
container**, and says **adminer is back** when it's done. If the new container fails its checks, the original comes back
as it was, and the dialog says **Not applied: … runs as before**; **Its last output** shows why. You can close the dialog
while it runs; the change carries on. Your volumes are never removed by a recreate, and a stopped app is recreated
stopped.

## Remove an app

Click **⋯** › **Remove…** on the app's page, or **Remove …** at the bottom of its **Settings** tab.

![The dialog before removing an app, with the choice to delete its volumes and image](../images/remove-app-dialog.webp)

- **A Git app or a Compose app**: **Delete …?** stops and removes the container (a Compose app's whole project, with
  `docker compose down`) and deletes its deploy history and logs. **Also delete its images** is ticked; **Also delete
  its named volumes (their data is lost)** isn't. Its domains come out of the Cloudflare Tunnel. When it has
  [databases](databases.md), the dialog names them: unless you tick **Also delete their data**, their data stays where it
  is and only Gitberth forgets them; its previews' own databases always go with their previews. Click **Delete app**.
- **Any other app**: **Remove …?** lists what else it affects. The container is stopped and deleted; its volumes stay unless
  you tick **Also delete its volumes**, and then you type the container's name to confirm. **Also delete its image**
  removes the image unless another container uses it or another app keeps it for **Roll back**. **Also delete its
  databases' data** does the same for its databases, with the name typed in too. The message afterwards says why an image
  stays, like **Its image … stays: … keeps it for Roll back.** Its domains stop answering, as their routes come out of the
  tunnel. Click **Remove …**.

With domains turned off in **Settings**, either dialog says instead that the app's domains stay in your Cloudflare Tunnel
for now, and that Gitberth takes them out when domains are turned back on.

Removing can't be undone. An app with no container left shows **Forget…** instead, which deletes Gitberth's record and
touches no container, volume or image (a Redis of its own goes, its data kept in its volume). A preview's **Remove…**
removes it as its parent's **Previews** tab does. Every removal is recorded in the [audit log](audit-log.md).

## Related

- [App settings](../reference/app-settings.md)
- [Environment variables](environment-variables.md)
- [Networking](networking.md)
- [Health and monitoring](health-and-monitoring.md)
