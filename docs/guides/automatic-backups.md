# Automatic backups

**Settings › Automatic backups** saves a backup on a schedule, with nobody there: the same items as [Save a backup](backups-and-restore.md#save-a-backup), to a folder on this host, a network share (SMB), an FTP server or an S3-compatible bucket. Each file is compressed and encrypted, and only your passphrase opens it. Gitberth keeps the newest few at the destination and removes older ones of its own.

## Set them up

1. Open **Settings › Automatic backups**.
2. Under **Passphrase**, type a passphrase twice (12 characters at least) and click **Set passphrase**.
3. Under **When**, pick **Every day**, **Every week** or **Every N hours**, and the time.
4. Under **Where**, pick the destination and fill it in, then click **Test the destination**: Gitberth writes a small file there, lists it, reads it back and removes it, and says how each step went.
5. Set **Keep the last** (7 unless you change it), look through **What's saved**, and click **Save**.

**Back up now** makes one at once, whatever the schedule. The card shows the last backup (when, its size, how long it took, or why it failed) and when the next one runs; **The last runs** lists the last 20.

## When they run

| Choice | When |
|---|---|
| **Off** | Never on its own (the default). **Back up now** still works. |
| **Every day** | Once a day, at the time you set. |
| **Every week** | Once a week, on the day and at the time you set. |
| **Every N hours** | Every 1 to 168 hours, counted from the last backup. |

Times are in Gitberth's time zone: `TZ` in its `.env` file (see [Configuration](../reference/configuration.md)). A backup that was due while Gitberth was down runs once, 5 minutes after it starts again.

One backup is made at a time: an automatic one waits for a backup downloading to your browser to end, and none runs while a restore does. What's being saved keeps running, as with **Save a backup**: databases are dumped while they serve, and a container pauses only while its own volume or folder is copied. **Stop everything while saving** works as it does there.

## Where they go

A backup is streamed to its destination as it's made: it never takes room on Gitberth's own disk. It's written under a temporary name and renamed once it's whole, so a backup that failed half-way leaves nothing behind. Every file is named `gitberth-auto-<host>-<date>-<time>.gbak` (the date and time in UTC).

### A folder on this host

Any folder of the Docker host, by its whole path, such as `/mnt/backups`. Gitberth can't see the host's folders itself, so a helper container of its own image mounts the folder and writes there; the folder is made when it isn't there. Files get mode 600 (only root reads them).

Use a disk other than the one Docker runs on, mounted at that folder: a backup on the same disk goes with it. The host's root and its system folders (`/etc`, `/proc`, `/var/lib/docker` and the like) can't be picked.

### A network share (SMB)

A share on a NAS or a Windows machine, as `//server/share`, or `//server/share/folder` for a folder in it, which is made when it isn't there. Give the account that may write there, its password and, only if the share asks for one, its domain.

Gitberth talks to the share with `smbclient`, in a helper container it builds once from Alpine with `samba-client` and `curl` (the image `gitberth-helper/transfer:1`; building it needs Docker Hub and Alpine's packages, once). The user name and password go in a file inside that helper, never on a command line. The password is kept encrypted with Gitberth's other secrets.

### An FTP server

A NAS or server that takes FTP, by its address and folder: `ftp://server/folder` (a port after the server's name if it isn't 21, such as `ftp://nas:2121/backups`). Give the account that may write there and its password. The folder is made when it isn't there.

- **TLS when it's offered.** With `ftp://`, Gitberth asks the server for TLS and uses it when the server has it; a server without TLS is used as it is. With `ftps://`, TLS is used from the first byte. A server whose TLS certificate this host doesn't trust (a NAS's own, say) is refused, rather than used without checking.
- **The password stays inside the helper.** Gitberth talks to the server with `curl`, in the same helper as SMB's. The user name and password go in a file inside that helper (`netrc`, readable by it alone), never on a command line. The password is kept encrypted with Gitberth's other secrets.
- **Only its own files.** A backup is uploaded under a temporary name and renamed once it's whole; only Gitberth's own automatic files are listed, read and removed.

SFTP isn't offered yet: using it safely means checking the server's host key, which needs a setting of its own.

### An S3-compatible bucket

AWS S3, Backblaze B2, Wasabi, MinIO or Cloudflare R2:

| Field | What to enter |
|---|---|
| **Endpoint** | The service's address, such as `https://s3.us-west-004.backblazeb2.com` or `http://minio:9000`. Empty for AWS S3. |
| **Region** | The bucket's region, such as `us-east-1`. Cloudflare R2 takes `auto`. |
| **Bucket** | Its name. It must exist: Gitberth doesn't make one. |
| **Folder in the bucket** | Optional, such as `backups/gitberth`. |
| **Access key** and **Secret key** | A key that may put, get, list and delete objects in this bucket, and nothing more. The secret key is kept encrypted. |
| **Path-style addresses** | The bucket in the address's path. On for every service but AWS S3, which uses the bucket in the host name unless its name has dots. |

A backup goes up as a multipart upload, in parts of 32 MiB as it's made, each signed. If anything fails, the upload is cancelled, so nothing half-written stays in the bucket.

## The passphrase

Automatic backups are encrypted with your passphrase, which Gitberth never keeps:

- When you set it, Gitberth makes a key pair. It keeps the public key, which can lock files but never open them, and the private key sealed with your passphrase (PBKDF2-HMAC-SHA256, 600,000 rounds, then AES-256-GCM).
- Each backup gets a random key of its own, which only the private key can unlock (ECDH on P-256, then HKDF-SHA256). The body is compressed and encrypted as a saved file's is (AES-256-GCM, in 1 MiB chunks).
- The file carries what it needs: the sealed private key and the locked file key. The file and its passphrase are enough to open it on any Gitberth; Gitberth's own settings can't open it without the passphrase.

**Change passphrase…** makes a new key pair for new backups. Backups made before still open with the passphrase they were made with: the card and the restore list show each file's key ID, so you know which passphrase opens it. Keep your passphrases in a password manager: nobody can recover one, Gitberth included.

## What's saved

The same list as **Save a backup**, chosen once under **What's saved**. Gitberth keeps what you untick as left out, so an app you add later is saved by itself.

### Host folders

A folder of the host that a container mounts (a bind mount, like `/srv/photos:/photos`) is listed under that container, in **Save a backup** too. It's copied through a helper that mounts it read-only, while the containers that use it pause for that moment. A folder over 2 GiB is listed with its size and left unticked: tick it to save it.

Never listed: Docker's socket, the host's root, `/proc`, `/sys`, `/dev`, `/run`, `/var/run`, `/etc` and its files, `/var/lib/docker`, and Gitberth's own data.

When an app comes back from a backup, its folders come back too:

| Choice | Its host folder |
|---|---|
| **Comes back as it was** | A missing or empty folder is filled. One that holds files is kept as it is, and the restore's log says so. |
| **Replace it** | The folder's files as they are now move to `<folder>.before-<date and time>`, then it's filled from the file. |
| **Restore as a copy named …** | The copy gets a folder of its own next to it, `<folder>-<copy's name>`, filled from the file: a copy shares no folder with the original. |

## Keep the last N

After each backup, Gitberth removes this host's older automatic backups at the destination, so the newest N stay (1 to 365; 7 unless you change it). Only files named `gitberth-auto-<this host>-…` count: another Gitberth's backups at the same place, and any file of your own, are never removed.

## When a backup fails

A failed backup says why on the card and in **The last runs**, and in the [audit log](audit-log.md). Gitberth also:

- sends **An automatic backup fails** to your [notifications](notifications.md): once, again when the reason changes or a day later while it keeps failing, and once more when a backup works again;
- shows **Automatic backups are failing** on the dashboard until one works. It shows too when the schedule is on and no backup has worked for twice its interval.

Gitberth tries again at the next scheduled time. Click **Test the destination** to check the settings, and **Back up now** once they're right.

## Restore an automatic backup

1. Open **Settings › Save and restore**. Under **Restore from a file**, next to **Or restore one of the automatic backups at …**, click **List them**.
2. Each backup there shows its date, size, host and key. Click **Use this one**: Gitberth fetches it as if you had uploaded it.
3. Type its passphrase and click **Open the file**, then pick what comes back, as in [Restore a backup](backups-and-restore.md#restore-a-backup).

On a new machine, set up **Automatic backups** with the same destination first (no passphrase needed to list and fetch), then restore from it.

## Related

- [Back up and restore](backups-and-restore.md)
- [Gitberth's settings](../reference/gitberth-settings.md#save-and-restore)
- [Notifications](notifications.md)
- [Security](../concepts/security.md#how-secrets-are-stored)
