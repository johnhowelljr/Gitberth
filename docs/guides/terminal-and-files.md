# Terminal, files and config files

Three ways into an app without SSH on the host. The **Terminal** tab opens a shell in the app's running container, in the
browser. The **Files** tab browses the app's volumes and host folders, with downloads, uploads and a small editor. And
**config files**, on the **Settings** tab, are files Gitberth keeps for the app and mounts into its container, like an
nginx site or an app's settings file.

Every app has them, whatever its source, but Gitberth's own containers: Gitberth itself, its tunnel and its helpers.

## The terminal

### Open a terminal

1. Open the app's page and click the **Terminal** tab.
2. For a [Compose app](compose-apps.md), pick the **Service** whose container the shell runs in.
3. Choose **Run as**: **Its own user** (the user the image runs as) or **root**.
4. Click **Connect**.

The shell is `bash` when the image has it, else `sh`, with a terminal that's as wide as the tab (`TERM=xterm-256color`).
Type `exit`, or click **Disconnect**, to close it; leaving the tab closes it too. An image with no shell at all, such as
one built `FROM scratch`, has no terminal, and the tab says so. A stopped app has none either: start it first.

### What's recorded

Opening a terminal and closing it go in the [audit log](audit-log.md) and on the app's **Activity** tab: who opened it,
from which address, which shell and which user, and how long it was open. **What you type isn't recorded**, nor what the
shell prints.

A terminal nobody typed into for 30 minutes closes by itself. The tab opens its connection with a one-time ticket that's
good for 30 seconds, and only from Gitberth's own pages: another site can't open one, even in a browser that's signed in.

## The Files tab

### Browse a volume

The **Files** tab lists the app's volumes and host folders. Pick one under **Volume** (for a Compose app, its **Service**
first), then click a folder's name to go into it, and a name in the path above the list to go back up. Each entry shows its
size, when it changed, its mode (`0644`) and its owner (`uid:gid`).

Gitberth reads and changes the files through a short-lived helper container of its own image, with only that volume or
folder mounted, read-only unless a change needs more, and no network. Nothing runs in the app's container, so it works for
an image without any tools, and for a stopped app too.

### Download, upload and change files

- **Download** a file as it is, or a folder (the one you're in, or one in the list) as a `.tar.gz`.
- **Upload files**: drop them on the box under the list, or click **Choose files**. They go into the folder you're in,
  owned like that folder, and a file of the same name is replaced.
- **New folder**, **Rename** and **Delete**. Delete asks first, and a deleted file is gone: there's no bin.

A volume the app mounts read-only can be browsed and downloaded, but not changed here. A database's volume says so above
its list, and Gitberth asks before each change: a running database keeps its files open, so stop it first.

Uploads, downloads, new folders, renames, deletes and edits go in the [audit log](audit-log.md) with their paths and sizes,
never what's in them.

### Edit a text file

The pencil next to a text file of up to 1 MiB opens it below the list. Change it and click **Save**: the file keeps its
owner and mode. A larger file, or one that isn't UTF-8 text, can be downloaded instead.

### What's never offered

Docker's socket, Gitberth's own data, and the host's system folders (`/proc`, `/sys`, `/dev`) aren't in the list, even
when an app mounts them. A path is always one of the volume's: one that leads out of it, through a link, is refused.

## Config files

### Add a config file

1. Open the app's **Settings** tab and go to **Config files**.
2. Click **Add config file**.
3. Give its **Path in the container**, such as `/etc/nginx/conf.d/site.conf`, and its **Contents**.
4. Change its **Mode** if it needs another than `0644`. For a Compose app, pick its **Service**.
5. Turn on **Secret** for a file with passwords or keys in it: its contents are never shown again once it's saved.
6. Click **Add config file**.

Gitberth keeps the file's contents encrypted in its database, writes the file to its data folder, and mounts it read-only
at its path. For a Git, uploaded or Compose app, the app's next deploy mounts it. For any other app, the mount goes into
the bar at the bottom like any change: click **Review and apply**. The file's row says **Mounted** once its container
has it.

### Change one

Click the pencil, change it, and click **Save config file**. New contents are written over the file the container has
mounted, so the app sees them at once: Gitberth offers to **Restart** the app for one that reads its config only when it
starts. A new path, or another service, is mounted as a new file is.

### Remove one

Click the bin next to it. A Git, uploaded or Compose app's next deploy leaves it out; any other app's goes with
**Review and apply**.

Config files are part of Gitberth's data, so a [backup](backups-and-restore.md) carries them, and restoring an app (or a
copy of it) brings them back. Adding, changing and removing one goes in the [audit log](audit-log.md), without its
contents.

## Related

- [Manage apps](manage-apps.md)
- [App settings](../reference/app-settings.md#config-files)
- [Security](../concepts/security.md)
- [Audit log](audit-log.md)
