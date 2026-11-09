# Updating Gitberth

Gitberth comes as a ready-made image, `ghcr.io/johnhowelljr/gitberth`, for Intel/AMD and ARM machines. Updating it replaces Gitberth's own container with one on a newer image; your apps keep running, and its data volume stays. Gitberth never updates itself, even when other apps update automatically, so you update it from the host's shell. This page also covers pinning a version, going back to an earlier one, and building Gitberth from source.

## Before you start

Back up Gitberth's data volume (see [Back up and restore](backups-and-restore.md#back-up-gitberths-data)). A new version may change Gitberth's database when it starts, and the backup is how you go back.

## Update to the newest version

The quickest way is to run the installer again. It keeps your settings and data, pulls the newest release, backs up
Gitberth's data to `/opt/gitberth/backups` before the new version starts (the five newest backups are kept), and checks
that Gitberth answers afterwards. For a Gitberth you built from source, it shows the command that updates it that way
instead.

```bash
curl -fsSL https://raw.githubusercontent.com/johnhowelljr/Gitberth/main/install.sh | sh
```

By hand:

1. On the host, go to Gitberth's folder:

   ```bash
   cd /opt/gitberth
   ```

2. Pull the newest image:

   ```bash
   docker compose pull
   ```

3. Recreate Gitberth's container on it:

   ```bash
   docker compose up -d
   ```

   Compose replaces the container. The `gitberth_gitberth-data` volume, and with it everything Gitberth knows, stays.
4. Go back to the browser. While Gitberth restarts, the page says **Rejoining the server...**, then carries on. If it says **Failed to rejoin.**, reload the page.

### What happens while Gitberth restarts

- **Apps keep running**, and domains keep working: cloudflared sends visitors straight to the apps, not through Gitberth.
- **A deploy that's running** is cut short and marked failed with **Gitberth restarted during this deploy.** The build of a new commit is tried again by itself; a rollback or a settings change isn't, so run it again.
- **A change to a container that was cut short**, such as a port change or an update, is finished or undone when Gitberth starts again. When Gitberth can't tell which, the app's page asks: **A change is waiting for you**.
- **A Git app whose last build failed** is tried once more after each Gitberth update, in case the new version builds it.
- **Health charts** carry on, with a gap for the restart.
- **Scheduled job runs** that came due while Gitberth was down aren't caught up.

## Image tags

| Tag | What it is |
|---|---|
| `latest` | The newest release. The `docker-compose.yml` from the install uses this. |
| `1.2.0` | One release, built when release `v1.2.0` is published on GitHub, or when the tag `v1.2.0` is pushed, which makes that release. |
| `1.2` | The newest release of 1.2. |
| `edge` | The newest build of the `main` branch: changes that aren't in a release yet. |
| `sha-<commit>` | The build of one commit. |

Every tag is published for `linux/amd64` and `linux/arm64`. A pre-release, like `1.3.0-rc.1`, gets only its own tag, so `latest` and `1.3` never move to it. The releases are listed on the repository's **Releases** page, and the tags that exist with the package, under **Packages**.

Each release's page shows its image and the `docker pull` command, and has the image attached as files, `gitberth-<version>-linux-amd64.tar.gz` (Intel/AMD) and `gitberth-<version>-linux-arm64.tar.gz` (ARM). On a machine that can't reach ghcr.io, download the one for it and load it with `docker load -i <file>`; it's tagged as the release, like `ghcr.io/johnhowelljr/gitberth:1.0.0`.

## Pin a version

Run the installer with the tag you want. It saves it in `.env` as `GITBERTH_IMAGE`, backs up Gitberth's data, and
switches to it:

```bash
curl -fsSL https://raw.githubusercontent.com/johnhowelljr/Gitberth/main/install.sh | GITBERTH_IMAGE=ghcr.io/johnhowelljr/gitberth:1.2 sh
```

By hand: add `GITBERTH_IMAGE=ghcr.io/johnhowelljr/gitberth:1.2` to `/opt/gitberth/.env`, then run
`docker compose pull && docker compose up -d` there.

With `1.2`, updates bring fixes released as 1.2.x. With `1.2.0` you stay on that one build. To follow the newest release
again, set it back to `ghcr.io/johnhowelljr/gitberth:latest`.

### See which image you run

In Gitberth, **Settings › System** shows the **Version**, with the commit a published image was built from, like `1.0.0 (3f9c2ab)`, and the sidebar shows `v1.0.0`. On the host:

```bash
docker compose images
```

The image's labels name the commit it was built from:

```bash
docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' ghcr.io/johnhowelljr/gitberth:latest
```

## Go back to an earlier version

Gitberth updates its database when a version starts, and an older version may not work with a database a newer one changed. Go back with the backup you made before the update.

1. Stop Gitberth:

   ```bash
   cd /opt/gitberth
   docker compose stop gitberth
   ```

2. Replace the volume's contents with the backup. This deletes what's in the volume first, so a newer database file can't mix with the older one:

   ```bash
   docker run --rm -v gitberth_gitberth-data:/data -v "$PWD":/backup alpine \
     sh -c 'rm -rf /data/* && tar xzf /backup/gitberth-data-2026-11-08.tgz -C /data'
   ```

3. Set the `image:` line in `docker-compose.yml` to the version you ran before, like `ghcr.io/johnhowelljr/gitberth:1.1`.
4. Start it:

   ```bash
   docker compose pull && docker compose up -d
   ```

Gitberth's records go back to the moment of the backup: deploys, settings and apps added since then aren't in them.

## Build Gitberth yourself

You can build the image from the source instead of pulling it. It's the same container, with the same settings and data volume; only where the image comes from changes.

### Before you start

- `git` on the host: `apt install -y git`.

### Steps

1. Clone the repository and copy your settings into it:

   ```bash
   git clone https://github.com/johnhowelljr/Gitberth.git /opt/gitberth-src
   cp /opt/gitberth/.env /opt/gitberth-src/.env
   cd /opt/gitberth-src
   ```

   On a new install, copy `.env.example` to `.env` instead and fill it in.
2. Build and start:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
   ```

   The image is tagged `gitberth:local`. The project name in `docker-compose.yml` is fixed to `gitberth`, so the container and the `gitberth_gitberth-data` volume are the same ones as before, whatever the folder is called.

To update a build of your own, run `git pull` in `/opt/gitberth-src` and the same command again. To go back to the published image, run `docker compose pull && docker compose up -d` with only `docker-compose.yml`.

## Related

- [Install](../getting-started/install.md)
- [Back up and restore](backups-and-restore.md)
- [Configuration](../reference/configuration.md)
- [Troubleshooting](troubleshooting.md)
