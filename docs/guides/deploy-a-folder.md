# Deploy a folder

Deploy an app straight from a folder on your computer, without a Git repository: choose the folder, or a `.zip`, `.tar`
or `.tar.gz` of it, and Gitberth works out what it is, as it does for a repository, and brings it up. Each new upload is
a new version: it's deployed at once, and the version that runs keeps running until the new one works.

The examples use `site.zip`, a static website, and `todo-api`, a folder with a small Node.js server.

## Upload it

1. Click **New app** in the top bar, then the **Upload a folder** card.
2. Drop the folder (or the archive) on the box, or click **Choose a folder** or **Choose a file**.

   The upload goes straight to Gitberth, with how far it has got. Gitberth unpacks it, then looks at it the way it looks
   at a repository: what language and framework it is, how to build it, the port it listens on, and the variables it
   probably needs. When it holds several apps (a `web/` and an `api/` folder, say), it picks the most likely one, and
   the others are offered beside it.

3. Check what it found under **How it will run**, and the settings: its **Name** (from the folder's or the file's name:
   `site.zip` is `site`), the health URL, its ports (on the LAN, at a free host port), variables, volumes and resources.
   A folder with a Compose file can be deployed **With Compose**, as one app with all its services, or **As one
   container**: see [Compose apps](compose-apps.md).
4. Click **Deploy**.

   Gitberth makes the app and starts its first deploy: it unpacks the upload, builds its image and starts it, and the
   app's page opens on it. The deploy's log shows every step.

## What Gitberth does with it

An uploaded folder is built and run as a Git app's commit is: with its own Dockerfile when it has one, a Dockerfile
Gitberth writes for the language it found, or Railpack for a project Gitberth doesn't know (see
[How Gitberth builds each kind of project](../reference/build-detection.md) and [Railpack builds](../reference/railpack.md)). Its
variables, volumes, ports, domains, databases and the rest are set as for any other app. What it doesn't have is
anything of a repository's: nothing watches it, so it has no previews and nothing deploys on push, and it gets no
commit statuses, no Vercel variables and no repository variables.

Its page says where it came from: **Uploaded folder**, the file's name, its size and when it came, and, as its
"commit", the first 7 characters of the upload's SHA-256.

## What it leaves out

A folder chosen or dropped in the browser leaves out what the build makes again, and what isn't part of the app:
`.git`, `node_modules`, `.venv` and `venv`, `__pycache__`, `.next/cache`, and macOS's `.DS_Store`. The browser doesn't
send them at all. An archive is unpacked as it is, apart from macOS's `__MACOSX/` and `.DS_Store`.

When an archive (or a folder) has just one folder at the top, that folder is the app's root: `site.zip` holding
`site/index.html` deploys `index.html` at the root. A browser doesn't send its files' permissions, so a file in a chosen
folder isn't executable: a script that has to be, such as a `docker-entrypoint.sh` the Dockerfile runs as it is, keeps
its mode in a `.tar.gz`.

## What an upload can hold

Gitberth unpacks an upload only when it's safe to, and refuses the whole of it otherwise, saying why. Nothing of it is
ever written outside its own folder:

- **No paths that leave it**: an entry with an absolute path, a drive letter, or `..` in it is refused.
- **No links that leave it**: a link is followed the way the system would follow it, and one that ends outside the
  upload is refused. Nothing is ever written through a link.
- **No devices, FIFOs or sockets.**
- **Every file the size it says it is**: an entry that holds more (or less) than its archive says is refused.
- **Limits**: 2 GiB uploaded, 10 GiB unpacked, 200,000 entries, and no more than 200 times its own size once it
  unpacks to more than 1 GiB (as a zip bomb would).

## A new version

On the app's **Overview**, its **Source** card says what it runs and what its newest upload is. Drop a new version of the
folder (or an archive of it) on **Upload a new version**, or click **Choose a folder** or **Choose a file**.

The new version deploys at once, as a new commit of a Git app does: Gitberth builds it, starts it next to the running one
where it can, and the old one keeps running until the new one has passed its checks. When the build or the new
container fails, the version that ran keeps running, and the deploy's log says why.

**Deploy** in the app's header builds its newest upload again, for example once you've changed a build-time variable.

## From a script

The **Source** card has the same as a `curl` line, for a script or CI that makes the archive itself:

```sh
curl -X POST -H "Authorization: Bearer $GITBERTH_TOKEN" -F file=@site.zip http://192.168.50.10:8080/api/v1/apps/gb-site/upload
```

It needs an API token (**Settings** › **AI agents and API**): **Operate** to upload a new version of an app, **Admin**
to upload to a name no app has yet, which makes that app as New app would (its most likely app, with New app's
defaults). Add `?wait=true` to wait for the deploy to end. See [The upload endpoint](../reference/api.md#upload-a-folder-or-an-archive).

## Deploys and rolling back

Each upload is a deploy in the app's **Activity**, with **New upload** as what started it, the upload's short SHA-256 as
its commit, **Upload of site.zip** as its message and who uploaded it as its author. **Roll back to this** goes back to
an earlier deploy's image, as for a Git app (see [Deploys and rollbacks](deploys-and-rollbacks.md)). Gitberth keeps an
app's newest 5 uploads (always the one it runs and the one **Deploy** builds), and they go with the app when it's
deleted. Backups carry them: see [Back up and restore](backups-and-restore.md).

## If Gitberth can't tell what it is

When nothing in the upload says how to build it, and Railpack can't plan it either, New app says **Gitberth can't tell
what this is**, with what would make it work: a `Dockerfile`, or the file its language starts from, such as a
`package.json`, a `requirements.txt` or `pyproject.toml`, a `go.mod`, a `Gemfile`, a `composer.json`, a `Cargo.toml`, a
`.csproj`, or an `index.html` for a static site. **Customize the Dockerfile** lets you write one there instead; it's kept
with the app and used on every deploy.

## Related

- [Add an app](add-an-app.md)
- [Deploy an app from GitHub or GitLab](deploy-from-github.md)
- [How Gitberth builds each kind of project](../reference/build-detection.md)
- [Deploys and rollbacks](deploys-and-rollbacks.md)
