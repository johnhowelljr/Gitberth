# Back up and restore

Everything Gitberth knows lives in one Docker volume. Your apps' data lives in their own volumes and folders. This page says what to back up, how to copy it with plain Docker commands, and how to bring everything back on a new machine. Gitberth has no backup button of its own yet: these steps use the shell on the host.

## What to back up

| What | Where | Why |
|---|---|---|
| Gitberth's data | The `gitberth-data` volume, which Docker names `gitberth_gitberth-data`, mounted at `/data` | Everything Gitberth knows: settings, apps, secrets, deploy history |
| Your apps' data | Their volumes and host folders | Gitberth doesn't copy them |
| Gitberth's `docker-compose.yml` and `.env` | `/opt/gitberth` | How Gitberth is started |
| Containers started outside Gitberth | Their Compose files or `docker run` commands | Gitberth doesn't create them again on a new machine |

You don't need to back up images. A Git app's image is rebuilt from Git, and a registry image is pulled again. An image built on the host, like `backup-agent:1.0`, is in no registry: keep what you built it from, or save it with `docker save`.

### What's in Gitberth's volume

| File or folder | What it holds |
|---|---|
| `gitberth.db` (with `gitberth.db-wal` and `gitberth.db-shm` while it runs) | Settings, apps, ports, domains, deploy history and every secret, encrypted |
| `keys/` | The keys those secrets are encrypted with |
| `logs/` | Each deploy's log and the Dockerfile it used |
| `stats.db` | The Health page's last 6 hours of readings and events |
| `work/` | Clones and build folders while a deploy runs. Emptied when Gitberth starts; no need to keep it. |

[Configuration](../reference/configuration.md#the-data-volume) has the details.

> **Note**
> The keys sit next to the database, so anyone with the backup can read every secret in it: tokens, registry passwords and app variables. Keep backups private. Restore the keys and the database together: without the keys, Gitberth can't read the secrets, and each registry sign-in in **Settings › Registries** says **needs its password again: enter it below**.

### Your apps' volumes

List the volumes on the host with `docker volume ls`. A named volume of a Git app gets the app's name in front of it: a volume called `data` on the app `website` is `gb-website-data`. Apps from images keep the names you gave their volumes.

An app's **Settings › Volumes** section and the **Container** card on its **Overview** show which volumes and host folders it uses.

## Back up Gitberth's data

Gitberth writes its database while it runs. For a copy that's consistent, stop Gitberth for the moment the copy takes. Your apps keep running.

```bash
cd /opt/gitberth
docker compose stop gitberth
docker run --rm -v gitberth_gitberth-data:/data:ro -v "$PWD":/backup alpine \
  tar czf /backup/gitberth-data-$(date +%F).tgz -C /data .
docker compose start gitberth
```

While Gitberth is stopped, nothing deploys, no update is checked, and scheduled jobs and restarts don't run. Job runs that come due meanwhile aren't caught up later. The Health charts show a gap.

Copy the `.tgz` file off the host, with `docker-compose.yml` and `.env`.

## Back up an app's volume

The same command works for any named volume. Stop the app first (**Stop** on its page) so its files don't change during the copy, then start it again:

```bash
docker run --rm -v gb-website-data:/data:ro -v "$PWD":/backup alpine \
  tar czf /backup/gb-website-data.tgz -C /data .
```

For a database you'd rather not stop, use its own dump tool, which takes a consistent copy while it serves. For Postgres, with the user your database uses:

```bash
docker exec postgres pg_dumpall -U postgres > postgres.sql
```

Copy host folders (bind mounts) with your usual tools.

## Restore onto a new machine

### Before you start

- Set up the new host as in [Install](../getting-started/install.md), but stop before Gitberth starts: Gitberth's data has to go into its volume before its first start.
- Copy your backup files and your old `.env` to `/opt/gitberth` on the new host.
- If the old machine still runs cloudflared, stop it before you start cloudflared on the new one. Two cloudflared containers with the same tunnel token share the tunnel's visitors.

### Steps

1. Set Gitberth up without starting it. The installer keeps the `.env` you copied, and with `GITBERTH_START=no` it installs Docker if needed, checks the machine, and creates Gitberth's container and volume but doesn't start them:

   ```bash
   curl -fsSL https://raw.githubusercontent.com/johnhowelljr/Gitberth/main/install.sh | GITBERTH_START=no sh
   cd /opt/gitberth
   ```

   By hand instead: put `docker-compose.yml` in `/opt/gitberth` (its project name, `gitberth`, gives the volume its name, `gitberth_gitberth-data`) and run `docker compose create` there.

2. Unpack Gitberth's data into the volume:

   ```bash
   docker run --rm -v gitberth_gitberth-data:/data -v "$PWD":/backup alpine \
     tar xzf /backup/gitberth-data-2026-11-08.tgz -C /data
   ```

3. Restore each app volume under the same name:

   ```bash
   docker volume create gb-website-data
   docker run --rm -v gb-website-data:/data -v "$PWD":/backup alpine \
     tar xzf /backup/gb-website-data.tgz -C /data
   ```

   Copy host folders back to the same paths. Load a database dump into the database once it runs.
4. Pull the images your registry apps run, with the same names and tags, for example `docker pull postgres:18`. Load images built on the host with `docker load`.
5. Start Gitberth, with `docker compose up -d` or by running the installer again, which checks that it answers:

   ```bash
   docker compose up -d
   ```

6. Sign in with your usual admin password. The database you restored has it, so `GITBERTH_ADMIN_PASSWORD` isn't used.

### What happens when Gitberth starts

- **Apps from a registry, a local image or by hand** that were running are created again from their saved settings, as soon as their image is on the host. One whose image is missing shows **Its container is gone** on its page: pull its image, then click **Create it again**.
- **Git apps** have no image on the new host yet, so Gitberth builds each one that was running again from its branch and deploys it, by itself, when it starts. The dashboard's **Recent deploys** shows them as they run. An app that was stopped stays stopped: **Start** on its page builds it when you want it.
- **Containers started outside Gitberth**, such as a Compose stack or cloudflared, aren't created by Gitberth. Start them the way you did on the old machine. Gitberth keeps their records by name, and when each one starts it joins it again to the networks it kept it on, such as `gitberth`. Start them soon after Gitberth: it forgets those networks for a container that's still missing about 10 minutes after it starts.
- **Domains** work again once the apps answer on their host ports, as long as cloudflared runs with the same tunnel token.
- **The LAN address.** If the new machine has another address, **Settings › Network** says the saved one isn't the host's any more and offers the detected one with **Use it**.
- **Own LAN addresses.** `gitberth-lan` isn't on the new host. **Settings › Own LAN addresses** says it isn't there any more and offers to turn it on again with the same block.
- **Health history** carries on from `stats.db`, as far as its readings are still within the last 6 hours.

## Related

- [Configuration](../reference/configuration.md)
- [Updating Gitberth](updating-gitberth.md)
- [Security](../concepts/security.md#how-secrets-are-stored)
- [Install](../getting-started/install.md)
