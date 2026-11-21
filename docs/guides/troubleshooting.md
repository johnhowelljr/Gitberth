# Troubleshooting

This page lists the messages and states Gitberth shows when something goes wrong, what causes each, and how to fix it. Find the message you see, grouped by where it shows up. In the messages, `…` stands for a name, an address or a detail that varies.

When you need more detail than the page shows, read Gitberth's own log on the host:

```bash
cd /opt/gitberth
docker compose logs --tail 200 gitberth
```

## Signing in

| Message or symptom | Cause | Fix |
|---|---|---|
| "Wrong username or password." | The user name is always `admin`. The password is the one you set in `GITBERTH_ADMIN_PASSWORD` before the first start, the one Gitberth generated then, or the one you changed it to in **Settings › Admin password**. | Find a generated password with `docker compose logs gitberth \| grep "generated password"`. `GITBERTH_ADMIN_PASSWORD` is only read on the first start: changing it later does nothing. If you've lost the password, see [Forgot the admin password](#forgot-the-admin-password). |
| "Too many failed attempts. Try again in a few minutes." | 5 failed sign-ins within 15 minutes from the same address lock that address out until 15 minutes after the first one. A correct password is refused too while it lasts. | Wait, or restart Gitberth (`docker compose restart gitberth`), which clears the count. |
| Settings › Admin password: "The current password is wrong.", "Use at least 10 characters." or "The new passwords don't match." | What it says. | Type the current password again, and a new one of at least 10 characters twice. |
| A browser that was signed in goes to the sign-in page | The admin password was changed in another browser. That signs every other browser out: on its next request, or within a minute for a page that's left open. | Sign in with the new password. |

### Forgot the admin password

Gitberth has no reset link. You can make it take a new password from `.env`, as on the first start, by removing the saved one from its database:

1. Stop Gitberth. Your apps keep running.

   ```bash
   cd /opt/gitberth
   docker compose stop gitberth
   ```

2. Remove the saved password:

   ```bash
   docker run --rm -i -v gitberth_gitberth-data:/data alpine sh -c \
     'apk add --no-cache sqlite >/dev/null && sqlite3 /data/gitberth.db' <<'SQL'
   DELETE FROM Settings WHERE Key = 'admin.passwordHash';
   SQL
   ```

3. Set `GITBERTH_ADMIN_PASSWORD` in `.env`, or leave it empty to have one generated and printed in the log.
4. Start Gitberth with `docker compose up -d`, and sign in as `admin`.

Nothing else is lost: apps, tokens and secrets stay as they were.

## Gitberth's page

| Message or symptom | Cause | Fix |
|---|---|---|
| The page doesn't load at `http://192.168.50.10:8080` | Gitberth's container isn't running, or the port is mapped differently. | Run `docker compose ps` in `/opt/gitberth`, and read the log. Check the `ports:` line in `docker-compose.yml` (see [Configuration](../reference/configuration.md#port)). |
| "Rejoining the server..." or "Failed to rejoin." | The page lost its live connection to Gitberth, usually because Gitberth restarted, for example during an update. | Wait for it to reconnect, or reload the page. |
| "Something went wrong" with "Gitberth hit an unexpected error." | An error Gitberth didn't expect. | The details are in its log. The page shows a request ID you can look for there. |
| "There's no app called …" | No container on this host has that name, and Gitberth has no app by it. It was removed or renamed. | Click **All apps** and find it there. |
| Docker in the LXC fails with `open sysctl net.ipv4.ip_unprivileged_port_start … permission denied` | A runc security update in late 2025 broke Docker inside LXCs on older Proxmox versions. | Update the Proxmox host (see [Install](../getting-started/install.md)). |

## Docker

| Message or symptom | Cause | Fix |
|---|---|---|
| **Docker offline** in the top bar; "Docker can't be read" on the Health or Network page; "Not reachable: …" in **Settings › System** | Gitberth can't reach the Docker engine. On the Health page: "Can't reach the Docker engine at …". | Check that Docker runs on the host, and that `docker-compose.yml` mounts `/var/run/docker.sock:/var/run/docker.sock`. The Health banner asks "Is /var/run/docker.sock mounted into the Gitberth container?" The log says "Docker isn't reachable (…). Is /var/run/docker.sock mounted into this container?" at start. |
| **Not updating** next to the time on the Health or Network page | The last read of Docker failed. The page keeps the last readings and tries again. | As above, if it doesn't clear by itself. |
| "Gitberth talks to Docker over unix:// or tcp://, not …" | `DOCKER_HOST` is set to another kind of address. | Use `unix:///var/run/docker.sock` (the default) or `tcp://host:port`. |
| "Changing ports needs Docker 25 or later (API 1.44). This host has Docker …" | The Docker engine is too old for Gitberth to recreate containers. | Update Docker on the host. |
| "Couldn't join the 'gitberth' network (…); health URL checks may fail." in the log | Gitberth's own container couldn't join the shared network at start. | Check that the network exists (`docker network ls`) and restart Gitberth. |

## Docker's disk is filling up

Images, build caches, containers and volumes all live on Docker's disk. Gitberth reads how much of it is free on the disk
that holds its own data volume, which is in Docker's storage, so it's the same disk. It goes by two lines:

- Under 5 GB free, or under 5% of the disk when that's more, Gitberth won't pull an image, so a pull never fills the disk
  halfway through: the disk is **almost full**.
- Under 15 GB free, or a quarter of a small disk, the disk is **getting low**; on a big disk the line is at twice the
  first one, so the warning always comes first. On a 64 GB disk that's 15 GB and 5 GB; on 1 TB, about 102 GB and 51 GB.
  **Settings › Disk** says where both lines are on this host.

It shows the disk in these places:

- **Disk** in the strip at the top of the **Apps** list: what's used of its size, and how much is free, with **getting
  low** or **too little to pull images**.
- **Disk** in the dashboard's **This host** card: how much is free, with a bar for the part in use, and **getting low**
  or **almost full**.
- A banner on the dashboard, **Docker's disk is getting low** or **Docker's disk is almost full** (then a pill in the
  top bar on every page too), with **See what takes the room**. [AI agents](ai-agents.md) hear it in `get_health`.
- A [notification](notifications.md), with **Docker's disk gets full** on: once when the disk is getting low (not again
  within a day), once when it's almost full, and once when there's room again.
- **Disk** on a new app's review: what's free of the disk's size, with **Getting low**, or **Too little to pull** when
  its image has to be pulled and the disk is almost full.
- **Settings › Disk**: the free space and its lines, the build cache, the images, and what Gitberth cleaned up.

Gitberth cleans up by itself (**Clean up by itself** in **Settings › Disk**, on unless you turn it off). Once a day it
removes the build cache no build used for 7 days (**Keep unused build cache for**) and keeps the cache under a tenth of
the disk, removes the untagged images of its own old builds and the leftover images of deleted apps, and each app's
images beyond the ones it keeps for **Roll back**. When the disk is getting low it also removes the build cache no build
used for a day, at most once an hour; when it's almost full, all the build cache no build is using, at most every 30
minutes. The next builds take longer, but they don't fail for want of room. It never removes an image an app runs or
rolls back to, one a container uses, an image that isn't from a build of Gitberth's (`postgres:18`, say), nor anything a
running build uses: a deploy that gets to its build while a cleanup runs waits for it, and its log says
"Waiting for the disk cleanup to finish…". Each cleanup that removed something is in the [audit log](audit-log.md).

| Message or symptom | Cause | Fix |
|---|---|---|
| **Not enough disk to pull it** on a new app's review, with "Only … free on Docker's disk. Free some space first.", and **Create and start** can't be clicked | The image isn't on the host yet, and the disk is below the limit. An app whose image is on the host already can still be created. | Make room (below), then click **Change settings** and **Review and create** again. |
| "Only … free on Docker's disk. Free some space first." when you update an app, click **Pull it**, or set up a Postgres server | The same limit, for every pull. An automatic update isn't made either: Gitberth tries again at its next look in the update window. | Make room, then try again. |
| "Only … free on Docker's disk." as a note on a new app's review | The disk is getting low: the pull still goes ahead. | Nothing yet, but make room soon. |
| "Docker's disk is full: free some space, then try again. Docker said: …" | The disk filled up while Docker was pulling. | Make room, then try again. |
| A build or a Compose app's deploy fails with Docker's `no space left on device` | Those aren't checked beforehand: the disk filled up on the way. A failed build leaves the running version as it was. | Make room, then click **Deploy**. |

To make room:

1. See what takes the space: **Settings › Disk** shows the build cache and the images, and `docker system df` on the
   host lists images, containers, local volumes and the build cache, with how much of each could be freed.
2. Click **Clean up now** in **Settings › Disk**: it runs the daily cleanup at once and says what it freed. For more,
   **Clear build cache** there deletes all of it; the next build of each app runs every step again. See
   [Gitberth settings](../reference/gitberth-settings.md#disk).
3. Remove the apps you don't use, with **Also delete its image** (**Also delete its images** for a Git or Compose app)
   ticked. See [Remove an app](manage-apps.md#remove-an-app). Images of apps you deleted with it unticked are listed
   under **Images of deleted apps** in **Settings › Disk**, with **Remove these images**.
4. Remove images on the host. `docker image prune` removes the ones with no name left, and `docker image prune -a` every
   one no container uses. Either also removes the image an app ran before its last update, so **Roll back** isn't
   offered for that app any more; `-a` also removes the images Gitberth keeps of each Git app's last 3 successful deploys,
   so it can't roll back to those.

Gitberth tidies up as it goes, too: a Git app keeps the images of its last 3 successful deploys and no more, and an
update keeps the image it replaced, for **Roll back**, and removes the one from the update before when nothing uses it.

## Tokens and sign-ins

Gitberth watches the tokens and sign-ins it uses: the GitHub token, the GitLab token, the Cloudflare API token (while
domains are on), registry sign-ins, and its own API tokens that something used in the last 30 days. It asks GitHub,
GitLab and Cloudflare about theirs every 6 hours, and hears what each service says of a token every time Gitberth calls
it anyway. A token about to expire gets a banner on the dashboard and in its section of Settings 3 weeks, a week and a
day before (an API token a week and a day before). A token that has expired, or that its service turned away, gets a red
banner, a pill in the top bar on every page, and an entry in the audit log. Each also sends a notice when **A token is
about to expire or is refused** is on in [Notifications](notifications.md), once per warning.

A refusal counts only when the service's own check refuses the token too, made at once: a single call turned away, say
during an outage of the service, never says the token is dead. A check that couldn't be made (no answer, a server error,
a rate limit) changes nothing.

| Banner | What stops | Fix |
|---|---|---|
| **The GitHub token expires in …**, **The GitHub token has expired** or **GitHub refused the token** | Private repos don't deploy, and commit statuses, pull request comments, webhooks and repo variables stop. | Make a new token at [github.com/settings/personal-access-tokens](https://github.com/settings/personal-access-tokens) with the same permissions, and save it in **Settings › GitHub access**. |
| **The GitLab token expires in …**, **The GitLab token has expired** or **GitLab refused the token** | Private projects don't deploy, and GitLab hears nothing from Gitberth. When GitLab said why, the banner has its word: "GitLab says it was revoked." | Make a new token in your GitLab's **Access tokens** (the banner's **Make a new token** opens it), and save it in **Settings › GitLab**. |
| **The Cloudflare API token expires in …**, **The Cloudflare API token has expired** or **Cloudflare refused Gitberth's API token** | Domains can't be added, moved or removed, and their routes can't change. The routes already in your tunnel keep working. | Make a new token at [dash.cloudflare.com/profile/api-tokens](https://dash.cloudflare.com/profile/api-tokens) with the permissions **Settings › Domains** lists, and save it there. |
| **ghcr.io refused the sign-in** (or another registry) | Apps on that registry's private images can't be pulled or checked for updates. Its row in **Settings › Registries** says **refused on** the day. | Enter the registry, user name and password or token again in **Settings › Registries**, then **Verify and save**. |
| **The API token … expires in …** or **The API token … has expired** | Whatever uses it can't reach Gitberth any more. The banner says when it was last used, and from which address. | Make a new token in **Settings › AI agents and API**, give it to that agent or script, and revoke the old one. |

Saving a new token, or removing the token, takes its banner away at once. A banner of a token that expires in a few
weeks or days can be dismissed: it stays hidden until the next warning, or until a new token is saved. The last day's
banner, an expired token's and a refused one's can't be dismissed. **Check the token** in **Settings › GitHub access**,
**GitLab** and **Domains** asks the service at once; what it says counts straight away.

## GitHub and deploys

A failed deploy's page shows **The deploy failed**, or **Rolled back: the old version kept running**, with the reason. The log below it shows the details, and **Last output from the new container:** when the container started and stopped.

| Message | Cause | Fix |
|---|---|---|
| "… needs a GitHub token. Add one in Settings (Contents: Read-only on this repo)." | The repo is private and no token is saved. | Add one in **Settings › GitHub access** (see [Deploy from GitHub](deploy-from-github.md)). |
| "GitHub rejected the token. Check that it hasn't expired and update it in Settings." | The token expired or was revoked. **Settings › GitHub access** shows its expiry, and **renew it soon** in its last 3 weeks; a refused token also has its banner (see [Tokens and sign-ins](#tokens-and-sign-ins)). | Create a new token and click **Replace token**. |
| "GitHub can't find …, or your token can't see it. Check the URL, and that the token has Contents: Read-only access to this repo." | A typo in the URL, or a fine-grained token that doesn't include this repo. | Edit the token on GitHub and add the repo under its repository access. |
| "Branch '…' doesn't exist in …" | The app's branch was deleted or renamed. | Set the branch in the app's **Settings › Source**. |
| "Can't reach github.com. Check the host's internet connection and DNS." | The host can't resolve or reach GitHub. | Check the host's network and DNS. |
| "The subfolder '…' doesn't exist on …" | The app's folder isn't on that branch. | Fix **Subfolder** in **Settings › Source**. |
| "Gitberth couldn't work out how to build this project. Add a Dockerfile to the repo, or set a custom Dockerfile in the app's settings." | Nothing Gitberth knows how to build is in the folder. | See [How Gitberth builds](../reference/build-detection.md), or add a Dockerfile. |
| "The image build failed; the log above shows why. The running container wasn't touched." | The build itself failed: a compile error, a missing file, a dependency that didn't install. | Read the build output in the log. The running version keeps running. |
| "This Docker host doesn't let builds use its network, so the build can't reach the other containers. Building without them…" | The Docker host refuses host networking for builds. | Nothing to do unless the build needs another container, such as a database for migrations. |
| "The new container exited (code …)." | The app stopped during its first 15 seconds. | Read **Last output from the new container:** in the log. Common causes are a missing environment variable and an app that listens only on `localhost`. |
| "The new container ran out of memory. Raise its memory limit." | It hit its memory limit. | Raise the limit in the app's **Settings › Resources**. |
| "The image's HEALTHCHECK reports unhealthy." or "The image's HEALTHCHECK never reported healthy." | The container's Docker health check failed. | See [Health checks](health-and-monitoring.md#health-checks). |
| "Health check failed for http://…: …" | The app's health URL didn't answer below 400 within 60 seconds. | Check the path in **Settings › General › Health URL**, or clear it. |
| "Image … no longer exists. Deploy to rebuild it." | A settings change, a scheduled restart or a rollback needed an image that isn't on the host any more: it was removed (only the last 3 good images are kept), or Gitberth was restored onto a new host. | Click **Deploy**, or **Start** when the page says **Its container is gone**: both build the branch again. |
| "Gitberth restarted during this deploy." | Gitberth stopped mid-deploy, for example for an update. | A new commit's build is tried again by itself; otherwise click **Deploy**. |
| "Superseded by a newer deploy" | A newer deploy of the same app was queued while this one waited. | Nothing: the newer one runs. |
| "Cancelled: the app was stopped." | The app was stopped while the deploy waited. | Start the app, then click **Deploy** if the new version should run. |
| "warning: the repo's GitHub variables couldn't be read, so the copy kept from the last read is used." | GitHub couldn't be reached, or the token can't read the repo's variables. The app gets the last variables Gitberth read. | Check the reason after it, often "The GitHub token isn't allowed to read …'s variables." The token needs **Variables: Read and write** (fine-grained) or the `repo` scope (classic). |

**A push doesn't deploy.** Gitberth checks each branch every 30 seconds by default. A push isn't deployed when **Deploy on push** is off in **Settings › Source**, when the app is stopped, or when the commit was already tried and failed: Gitberth retries a failed build only twice (15 minutes later, and after a Gitberth update). Click **Deploy** to try it again. See [Deploys and rollbacks](deploys-and-rollbacks.md).

## Changes to a container

These show in the change dialog, in the **Ports** card, or as a banner on the app's page.

| Message or banner | Cause | Fix |
|---|---|---|
| "Gitberth is already changing …. Try again when that's done." | Another change, deploy or update of the same app is running. | Wait for it to finish. |
| "Used by …" under a host port field, "Port … is used by …" when you apply, or "Port … is taken (…). Nothing changed." | Another container or program holds that host port. | Click **Use** with the free port offered, or pick another. [Check a port](networking.md#check-a-port) says who holds it. |
| "Not applied: … runs as before" with "The original container is back as it was." | The new container failed its checks, so Gitberth put the old one back. The line above says which check: "The new … stopped after … s (exit code …).", "reported unhealthy", "didn't report healthy within … s.", "didn't answer its health URL", or "doesn't answer on … (checked from the host), though it answered before the change." | Click **Its last output** to see what the new container printed. Fix the setting and apply again. |
| **A change is waiting for you**, with **Keep the new one** and **Put the original back** | Gitberth restarted in the middle of a change and can't tell whether it finished. Both containers are kept. | Check the new one (its logs, its address), then choose. Volumes stay either way. |
| **Both containers are kept**, with **Start the original** | A failed change couldn't put the original back by itself. | Click **Start the original**. |
| **Recreated while stopped**, with **Put the original back** | A stopped container was recreated with new settings. The original is kept until the new one's first start passes its checks. | Start the app, or put the original back. |
| "… isn't on this host yet." with **Pull it** | A new image or tag in **Settings › Image** isn't on the host. | Click **Pull it**, then apply. |
| **Its container is gone** | The container was removed outside Gitberth, or Gitberth was restored onto a new host. | A Git app: click **Start**. The banner says whether that makes the container again from its last image, without a build, or builds the branch again because the image isn't on the host any more. An app from an image: **Create it again**. One started outside Gitberth: start it again the way it was started, or **Forget…** the record. |
| **Renamed outside Gitberth** | The container now runs under another name. | Open the other name with **Open …**, rename it back, or **Forget…** this record. |
| **Saved, not live yet** | A Git app has saved settings its container doesn't run yet. | **Apply now**, or **Rebuild and deploy** when the change needs a build. |
| "Gitberth can't apply this: Compose would undo it." | Docker Compose manages the container. | Put the lines Gitberth gives into the Compose file and run the command it shows. |
| "… changed since you started editing. Discard to load it as it is now." | The container changed while you edited its settings. | Click **Discard** and edit again. |
| "Started with --rm: stopping it deletes it, so Gitberth can't recreate it." | The container was started with `--rm`. | Recreate it yourself without `--rm`, or create it as an app in Gitberth. |

## Image updates

| Message | Cause | Fix |
|---|---|---|
| "Can't check: sign-in failed." | The registry refused the saved sign-in. Gitberth tries again after 24 hours; when signing in again is refused too, **Settings › Registries** has a banner for it (see [Tokens and sign-ins](#tokens-and-sign-ins)). | Fix the sign-in in **Settings › Registries**. |
| "Only on this host: no registry has it." | No registry has this image, or it's private and there's no sign-in for it. | For a private image, add a sign-in in **Settings › Registries**. |
| "Not checked yet." | No check has run for this image. | Click **Check now** on the **Updates** card. |
| "It's pinned to a digest, so there's nothing to update. Give it a tag in Settings to follow one." | The app runs `name@sha256:…`. | Set a tag in **Settings › Image** if it should update. |
| "A GitHub app is updated by a new commit: use Deploy." | Git apps don't take image updates. | Click **Deploy**. |
| "… moved again while it was pulled: check again." | The tag changed in the registry during the pull. | Click **Update** again. |
| "… has no container to update: start it first." | The app has no container. | Start it, then update. |
| "… refused the sign-in, so nothing was saved. Docker said: …" | **Verify and save** failed in **Settings › Registries**. | Check the user name and password or token. For ghcr.io, use a token that can read packages. |
| A sign-in in **Settings › Registries** says "needs its password again: enter it below" | Gitberth can't read its saved password any more, for example because the `keys/` folder was lost in a restore. | Enter the registry, user name and password or token again, and click **Verify and save**: it replaces the old sign-in once it's verified. |
| Activity: "Automatic update to … failed and was rolled back: … Updates are back to Tell me until you choose Automatically again." | An automatic update failed its checks. The old container is back, and the app's choice is now **Tell me**. | Look at the reason, then choose **Automatically** again in **Settings › Image**. |
| Activity: "Automatic updates paused after a roll back: updates are back to Tell me until you choose Automatically again." | You rolled back an app set to **Automatically**, so the next window wouldn't update it again. | Choose **Automatically** again in **Settings › Image** when the newer image should run. |
| "There's no earlier image to go back to: Gitberth hasn't updated it." or "The image it ran before (…) isn't on this host any more." | **Roll back** needs the image the app ran before its last update, and it's gone or never existed. | Go back by tag instead: see [Go back to an older version](updates.md#go-back-to-an-older-version). |
| An app set to **When I ask** has no **Update** tag in the Apps list | That's the choice: no scheduled checks and no badge. | Click **Check for update** on its page, or choose **Tell me** in **Settings › Image**. |

## Domains

| Message | Cause | Fix |
|---|---|---|
| "No cloudflared container is running on this host. Gitberth reads which tunnel to use from it, so start the tunnel first." | No running container whose image is cloudflared. | Start cloudflared with the tunnel's token (see [Domains](domains.md#before-you-start)). |
| "More than one Cloudflare tunnel runs on this host (…). Stop the ones Gitberth shouldn't manage, then save the token again." | Gitberth manages one tunnel. | Stop the others and click **Save token** again. |
| "The cloudflared container … doesn't run its tunnel from a token Gitberth can read (a credentials file, say), so Gitberth can't tell which tunnel it is." | cloudflared runs a tunnel from a credentials file. | Run it with `--token`, `TUNNEL_TOKEN` or a token file. |
| "The tunnel Gitberth's Cloudflare token was saved for isn't running on this host." | The cloudflared container stopped, or now runs another tunnel. | Start it, or save the token again for the tunnel that runs. |
| "cloudflared (…) can't reach Gitberth's apps: it's on neither the host's network nor the shared network gitberth, and joining gitberth failed (…)." | Gitberth couldn't join cloudflared to its network. | Run cloudflared with `--network host`, or join it to `gitberth` on its Network tab. |
| "Cloudflare didn't let Gitberth's token …. The token needs Account › Cloudflare Tunnel › Edit, Zone › DNS › Edit and Zone › Zone › Read." | The token lacks a permission. | Edit the token in Cloudflare, then **Replace token**. |
| "Cloudflare refused Gitberth's API token itself, so it couldn't …: the token has expired, or was disabled or deleted. Save a new one in Settings › Domains." | Cloudflare doesn't take the token at all any more. | Make a new token (see [Tokens and sign-ins](#tokens-and-sign-ins)), then **Replace token**. |
| "Cloudflare's API rate limit was reached while trying to …. It clears within minutes: Gitberth tries again on the app's next deploy, and every 15 minutes for a domain it's removing." | Too many requests to Cloudflare at once. | Wait a few minutes, then click **Fix** on the domain, or leave a removal to the retry. |
| "…: … Gitberth tries again every 15 minutes until it's done." | A removed domain's route or DNS record couldn't be taken out of Cloudflare, for example because Cloudflare couldn't be reached. The domain is gone from the app, and no other app takes its route meanwhile. | Nothing, usually: Gitberth tries again every 15 minutes while domains are on. If it keeps failing, fix what the message says. |
| "Enter a domain, like app.example.com." or "\"…\" isn't a domain Gitberth can route. Use a name like app.example.com." | What you typed isn't a host name. | Type the name only, like `app.example.com`. |
| "… already belongs to …" | Another app has that domain. | Remove it there first. |
| "… needs the web port on the host. Set it to LAN or Host only." | The web port is **Internal** and cloudflared reaches apps through host ports. | Use **Add domain and publish**, or set the web port's **Access**. |
| "Mark a web port in Ports first: domains go to the web port." | The app has no web port. | Mark one in the **Ports** card (see [The web port](networking.md#the-web-port)). |
| "Cloudflare didn't let Gitberth's token … Cloudflare Access …. The token needs …, plus Account › Access: Apps and Policies › Edit and Account › Access: Organizations, Identity Providers, and Groups › Read to protect domains." | A domain is protected, and the token can't manage Cloudflare Access, or can't read your team domain. | Add both **Account › Access: Apps and Policies › Edit** and **Account › Access: Organizations, Identity Providers, and Groups › Read** to the token in Cloudflare (see [Protect a domain](domains.md#protect-a-domain)). |
| "… isn't served: it's protected, and …" | A protected domain can't be put behind Cloudflare Access, so Gitberth keeps it out of the tunnel rather than serve it open. The rest says why: Zero Trust isn't set up, nobody is allowed in, Cloudflare refused, or Cloudflare was busy or couldn't be reached. | When it ends with "Gitberth tries again every 15 minutes.", Cloudflare only failed for a moment: nothing, usually, or click **Fix** to try at once. Otherwise fix what it says in **Settings › Domains (Cloudflare) › Protection**, or turn the domain's **Protected** switch off. |
| "… isn't behind Cloudflare Access yet, so anyone may still open it. …" | You turned **Protected** on (or previews' protection), but the domain is still served without Access; the rest says why. Either the tunnel couldn't be changed, so its old route may still be open, or the tunnel has rules for it that Gitberth didn't make. | If it ends with "Gitberth tries again every 15 minutes.", nothing, usually: click **Fix** on the domain to try at once. If it says the tunnel has rules Gitberth didn't make, delete them in the tunnel's settings in Cloudflare, or add the Access check to them yourself: Gitberth never changes them, so no retry helps. |
| "… isn't open to everyone yet: Gitberth couldn't change the tunnel. …" | You turned **Protected** off (or previews' protection), but the tunnel couldn't be changed; the rest says why. Its route still checks the sign-in, or it isn't served yet. | When it ends with "Gitberth tries again every 15 minutes.", nothing, usually: click **Fix** on the domain to try at once. Otherwise fix what it says. |
| "… still asks visitors to sign in: Gitberth couldn't remove its Cloudflare Access application. …" or "… may still ask visitors to sign in: Gitberth couldn't remove its Cloudflare Access application or see whether it's gone. …" | You turned **Protected** off (or previews' protection), or removed a protected domain or its app, but the Access application Gitberth made, **Gitberth: …**, couldn't be removed, so visitors still have to sign in there. The rest says why. **may still ask** means Gitberth couldn't see the account's Access applications, so it can't tell whether that one is still there. A domain still on its app shows **Still behind Cloudflare Access, though it isn't protected any more.** meanwhile, while the app runs. | When it ends with "Gitberth tries again every 15 minutes.", Cloudflare was busy or couldn't be reached: nothing, usually (**Fix** on the domain, where the card has it, tries at once). Otherwise Cloudflare refused, usually because the token lacks **Account › Access: Apps and Policies › Edit**: give it that. Then, when the note ends with "Click Fix to try again.", click **Fix** on the domain; otherwise (the domain is gone, or its app isn't running, so the card has no **Fix**) click **Check again** in **Settings › Domains (Cloudflare) › Protection**, and once Gitberth may use Cloudflare Access there, it tries again within 15 minutes. Gitberth doesn't ask every 15 minutes in vain meanwhile, but also tries the next time it updates your domains in Cloudflare, and **Replace token** tries at once. You can also delete **Gitberth: …** in the Zero Trust dashboard; Gitberth notices it's gone the next time its token may list the account's Access applications. A removed domain's application is tried until Gitberth sees it's gone, or sees another tunnel on your Cloudflare account serve that name with it (another Gitberth took the name over, say): then it's left to that one. Without Zero Trust on the tunnel's Cloudflare account there's no application left to remove, and Gitberth forgets it. |
| "… is protected, but the tunnel also has rules for it that Gitberth didn't make, and they skip Cloudflare Access. Remove them in Cloudflare." | Someone else's rules for the domain (a path rule made in the dashboard, say) send visitors on without a sign-in. Gitberth never changes them. | Delete those rules in the tunnel's settings in Cloudflare, or add the Access check to them yourself. |
| "GitHub's webhooks can't reach Gitberth through …: cloudflared (…) can't reach Gitberth's own container." | cloudflared is on the host's network and Gitberth's port isn't published, or it's on neither network Gitberth is on. | Publish Gitberth's port (the `GITBERTH_PORT` in its `.env`), or run cloudflared with `--network host`. |
| "… already has routes in your tunnel. Pick a name nothing uses yet, like hooks.example.com." | The host name chosen for GitHub's webhooks is used by other routes. | Pick another name in **Settings › GitHub integration**. |
| **Domains are off** on an app's Domains card | Domains are turned off. | Turn them on in **Settings › Domains (Cloudflare)**. |
| **No Cloudflare token yet** | No token is saved. | Save one in **Settings › Domains (Cloudflare)**. |
| **The tunnel needs a look** | Gitberth can't find or use the tunnel; the line says why. | Fix what it says, as above. |
| An address on the Overview says "domains are off in Settings", "no Cloudflare token in Settings" or "the tunnel can't reach its web port" | The domain doesn't answer for that reason. | Turn domains on, save a token, or open the app's **Network** tab and fix the domain. |

A domain marked **Not working** says why under its name; [When a domain isn't working](domains.md#when-a-domain-isnt-working) lists those lines and their fixes.

## Networking and own LAN addresses

| Message | Cause | Fix |
|---|---|---|
| "… isn't an address of this host any more", in **Settings › Network** | The host's address changed since you saved it. | Click **Use it** to save the detected one. |
| LAN addresses Gitberth shows use the wrong IP | Gitberth picked another of the host's addresses. | Set **LAN address of this host** in **Settings › Network**. |
| "Random: changes at every start." under a host port | Docker picked the host port, and picks another at every start. | Click **Keep …** to keep the port it has now, or **Use …** for the container's own port, then apply. |
| "Its Internal ports have no address: it isn't on a shared network. Join one in Networks below." | Other containers can't reach it by name. | Join `gitberth` in the **Networks** card. |
| "Gitberth tests addresses with a helper container, which needs Gitberth to run in one." | Gitberth runs outside a container, for example during development. | Own LAN addresses need Gitberth in its container. |
| "Gitberth couldn't read this host's network (…)." | The helper that reads the host's network failed. | Check the log; Docker has to be able to run Gitberth's image with the host's network. |
| "A device answers on …: pick a block no device uses." | Something on your LAN uses an address in the block. | Pick another block, or **Suggest a block**. |
| "The router … doesn't answer from the block. A firewall between this host and the router may drop addresses other than …: on Proxmox, the container's IP filter." | Traffic from the block's addresses is dropped before it reaches the router. | In Proxmox, check the LXC's firewall options: an IP filter allows only the container's own address. |
| "Every address of … is taken." | The block has no free address left. | Use a bigger block, or give an app a **Static** address. |
| "A device answers on …. Pick another address." | The static address you typed is in use. | Type another one. |
| "Nothing answers on … on … yet: it may listen on another port." | The address was given, but the app doesn't answer on its known ports yet. | Nothing, if the app listens on another port or is still starting. |
| "Own LAN addresses are off. Turn them on in Settings › Own LAN addresses." | Own LAN addresses aren't turned on. | Turn them on in **Settings › Own LAN addresses**. |
| "gitberth-lan isn't there any more, so own LAN addresses are off. Turn them on again to use …" | The `gitberth-lan` network was removed, or Gitberth moved to a new host. | Click **Turn on** with the same block. |
| An app can't reach another by name | They aren't on the same Docker network. | **Join gitberth** on the other app's **Network** tab (see [Networking](networking.md#reach-other-containers-by-name)). |

## GitLab

| Message | Cause | Fix |
|---|---|---|
| "… needs a GitLab token. Add one in Settings › GitLab (read_api and read_repository scopes)." | The project is private and no GitLab token is saved. | Save one in **Settings › GitLab** (see [GitLab](gitlab-integration.md#the-tokens-scopes)). |
| "GitLab rejected the token, or it can't read this project. …" or "GitLab says the token is invalid, expired or revoked. Update it in Settings › GitLab." | The token expired, was revoked, or lacks a scope. **Settings › GitLab** shows its expiry and scopes, with **renew it soon** in its last 3 weeks, and a refused token's banner (see [Tokens and sign-ins](#tokens-and-sign-ins)). | Make a new token and click **Replace token**, or **Check the token** after you changed its scopes. |
| "GitLab can't find …, or your token can't see it. …" | A typo in the URL, or the token's user isn't a member of the project. | Check the URL, and the user's role on the project. |
| "Gitberth deploys from github.com and from its GitLab, …. For another GitLab, set its address in Settings › GitLab." | The URL isn't on github.com or on the GitLab set in **Settings › GitLab**. | Set **GitLab address** in **Settings › GitLab** first. |
| "Can't reach …. Check its address in Settings › GitLab, and the host's network and DNS." | The host can't reach your GitLab. | Check the address, and the host's network. |
| A banner such as **Commit statuses aren't set on …** in **Settings › GitLab** | GitLab refused that on the project: the token lacks the **api** scope, or its user the role. | Give the token what the banner says. Deploys carry on meanwhile. |

## Previews

| Message | Cause | Fix |
|---|---|---|
| "… would reach this preview with production's value (…), so the preview's build and code would use production's database. …" | A variable that names a database would give the preview production's, and Gitberth can't make it one of its own. | Give the variable a **Previews** value on the app's **Environment** tab. See [Production's database](previews.md#productions-database). |
| **Waiting: 3 previews run already** | The app has as many previews running as **At most running** allows. | Remove one, raise the limit in **Settings › Previews**, or wait for one to expire. |
| **Pull requests couldn't be read** (or **Merge requests couldn't be read**) on the **Previews** tab | GitHub or GitLab couldn't be asked which requests are open: often a public repo's 60 questions an hour, or a token without access. | Save a token with **Pull requests** access (GitHub) or **read_api** (GitLab). Previews already there keep following their commits. |

## Databases

| Message | Cause | Fix |
|---|---|---|
| "There's no Postgres server for it yet. Create one in Settings › Databases, or choose one there." | No Postgres server Gitberth can use. | Click **Create a Postgres server** in **Settings › Databases**. See [Databases](databases.md#postgres-servers). |
| **Not usable** next to a server in **Settings › Databases**, with why | The server isn't running, isn't on Gitberth's network, or Gitberth can't sign in to it as its superuser. | Fix what it says, such as joining it to `gitberth` on its **Network** tab. |
| "Compose made … (project …): Compose would undo a change Gitberth made to it, so it can't get a database from here." | The container belongs to a Compose project Gitberth didn't deploy. | Add the variable in its Compose file, or deploy the file as a [Compose app](compose-apps.md). |
| "It replaces the … set on the Environment tab." under **Variable** | The app already has a value by that name. | Nothing: Gitberth's value wins. Use another **Variable** to keep both. |

## Compose apps

| Message | Cause | Fix |
|---|---|---|
| "The service … asks for … containers (deploy.replicas or scale). Gitberth runs one container a service: set it to 1." | The file asks for several copies of a service. | Set it to 1 in the file. |
| "There's no service … in the file …" | The **Web service** named isn't in the file. | Pick another **Web service**. |
| A deploy ends **Rolled back**, its log showing the failing service's last output | A service didn't stay up, or the web service didn't answer its health URL. | Read the service's output in the log, fix the file or its variables, and deploy again. |

## AI agents and the API

| Message | Cause | Fix |
|---|---|---|
| "That isn't a Gitberth token: they start with gbt_." or "The token isn't known: …" (401) | The header doesn't carry a token Gitberth made. | Copy the token again, or make a new one in **Settings › AI agents and API**. |
| "The token … was revoked." or "… expired on …" (401) | What it says. A token in use is warned about on the dashboard a week and a day before it expires. | Make a new token. |
| "The token … has Read access, and … needs Operate. …" (403) | The tool needs more access than the token has. | Make a token with that access, if the agent should have it. |
| "Requests from a browser page (with an Origin header) aren't accepted here. …" (403) | The call came from a web page. | Call it from an MCP client, a script or `curl`. |
| "Too many requests with a bad token from this address. …" (429) | 10 bad tokens from one address in 15 minutes. | Wait a few minutes, then use a good token. |
| "There are 50 tokens in use, the most there can be. Revoke one first." | The limit of tokens in use. | Revoke one you no longer need. |

Notifications that don't arrive are covered in [Notifications](notifications.md#when-a-message-doesnt-arrive), and an
app that's down for its uptime check in [Uptime checks](uptime-and-status-page.md#whats-checked).

## Health page

| Message | Cause | Fix |
|---|---|---|
| "Collecting. The first point appears within 10 seconds." | A container just started. | Wait. |
| **Stats aren't gathered for it** on an app's Overview | **Gather stats** is off for it. | **Turn it on in Settings**. |
| "Uses the host's network, so Docker can't count its traffic separately." | The container uses `--network host`. | Nothing: CPU and memory still show. |

## Related

- [Health and monitoring](health-and-monitoring.md)
- [Deploys and rollbacks](deploys-and-rollbacks.md)
- [Domains](domains.md)
- [FAQ](../faq.md)
