# Gitberth's settings

The **Settings** page holds the settings for Gitberth as a whole: GitHub and registry access, image updates, domains, the network, deploys, the admin password, and what this installation runs on. Open it from **Settings** at the bottom of the sidebar, or from the account menu (**A**) at the top right. Each app has its own settings on its page instead; see [App settings](app-settings.md).

The list on the left jumps to each section: **GitHub access**, **Registries**, **Updates**, **Domains** (the **Domains (Cloudflare)** section), **Network**, **Own LAN addresses**, **Deploys**, **Admin password** and **System**. Each section saves on its own.

![The Settings page](../images/settings.webp)

Gitberth's own process settings, such as the data folder and the Docker address, are environment variables instead: see [Configuration](configuration.md).

## GitHub access

For private repositories, and the repo variables apps keep their environment in.

![Settings, GitHub access](../images/settings-github.webp)

| Field or button | What it does |
|---|---|
| **Token** (**Replace the token** once one is saved) | A GitHub personal access token. A fine-grained token needs **Contents: Read-only** and **Variables: Read and write** on the repositories you deploy; a classic token needs the **repo** scope. Gitberth never changes your code; it only reads and saves the repo's variables. |
| **Save token** / **Replace token** | Asks GitHub who owns the token, then saves it encrypted. An invalid or expired token isn't saved. |
| **Remove** | Forgets the token. Private repos can't be cloned until you add one again. |

Once saved, the section shows **Token saved**, the GitHub account it belongs to, and when it expires. In its last 14 days the expiry is highlighted with **renew it soon**. Without a token it shows **No token** and **Private repositories can't be cloned yet.** Public repos need no token.

Default: no token. Saving a token makes Gitberth check every repo again right away. See [Deploy from GitHub](../guides/deploy-from-github.md) for how to create one.

## Registries

Sign-ins for private images, used to pull them and to check them for updates. Public images need none.

![Settings, Registries](../images/settings-registries.webp)

| Field or button | What it does |
|---|---|
| **Registry** | The registry's address, like `ghcr.io`, `docker.io` or a registry on your LAN with its port. Docker Hub's other names count as `docker.io`. |
| **Username** | The user name it signs in with. |
| **Password or token** | Never shown again once it's saved. For GitHub's registry, use a token that can read packages. |
| **Verify and save** | Signs in once through Docker to check it works, then saves it encrypted. For a registry that already has a sign-in, the new one replaces it once it's verified. If the registry refuses, the **Not saved** banner says why and nothing is saved. |
| **Remove** | Asks first. Apps on that registry's private images can't be pulled or checked for updates until you add a sign-in again. Nothing that runs changes. |

Each saved sign-in shows its registry, user name, when it was added, and **signed in …** with when it was last verified (or **saved**). When Gitberth can't read its saved password any more, for example because its keys were lost, it says **needs its password again: enter it below**: type the registry, user name and password again and click **Verify and save**. There's one sign-in per registry. Default: none.

**New app** also uses a registry's sign-in to read an image's ports, volumes and health check before it's pulled.

## Updates

How often images are checked for a newer version, and when apps may update on their own. See [Image updates](../guides/updates.md).

![Settings, Updates](../images/settings-updates.webp)

| Field | What it does | Default | When to change it |
|---|---|---|---|
| **Check for updates every** | How many hours apart Gitberth asks a registry about each image, 1 to 168. One request per image, however many apps use it. Apps set to **When I ask** aren't checked. | 6 hours | Shorter to hear of updates sooner; longer to send fewer requests. |
| **Automatic updates run between** | The hours in which apps set to **Automatically** update. A window can run past midnight. The times are in Gitberth's time zone, the `TZ` in its `.env`; the help under the field names it, like **Gitberth's time zone: UTC (set with TZ). Only apps set to update on their own, and never a database.** | 03:00 and 05:00 | To update at a time when nobody uses the apps. |
| **New apps from an image** | The **Updates** choice a new app gets, from a registry, a local image, a pasted `docker run` or Compose service, or the blank form: **When I ask**, **Tell me** or **Automatically**. The line under it says what that means. An image pinned by digest or ID gets **When I ask**, since it can't update, and a database gets **Tell me** instead of **Automatically**. Databases, Gitberth itself and Compose containers never update by themselves. | **Tell me** | Each app's own choice is in its **Settings › Image**. |

Click **Save** to save the section. It refuses an interval outside 1 to 168 hours ("Every 1 to 168 hours."), and a window that starts and ends at the same time ("Give the window some length.").

## Domains (Cloudflare)

Optional: apps on their own domains through the Cloudflare Tunnel on the host. See [Domains](../guides/domains.md).

![Settings, Domains (Cloudflare)](../images/settings-domains.webp)

| Field or button | What it does |
|---|---|
| **Manage app domains through Cloudflare** | Off by default. Off, Gitberth doesn't contact Cloudflare or read your tunnel's settings, and your tunnel and DNS stay exactly as you set them. Turned on with a token saved, Gitberth routes every app's domains right away. |
| **Cloudflare API token** (**Replace the API token** once one is saved) | A custom Cloudflare API token with **Account › Cloudflare Tunnel › Edit, Zone › DNS › Edit and Zone › Zone › Read**, for your account and your domains' zones. |
| **Save token** / **Replace token** | Finds the cloudflared container on the host, checks the token against its tunnel, saves it encrypted, and routes the domains. Routes you already made in Cloudflare that go to an app become its domains. |
| **Remove** | Forgets the token. Routes already in your tunnel stay as they are. |

With domains on, the section shows **Token saved** or **No token**, and the tunnel Gitberth uses, like **Tunnel 1a2b3c4d from the cloudflared container**, or why it can't use one. Notes under the help text say what the last sync did, and what needs attention. With domains off and a token saved, it shows **Token saved** with **Not used while domains are off.**

## Network

How devices on your LAN reach containers. See [Networking](../guides/networking.md).

![Settings, Network](../images/settings-network.webp)

| Field | What it does | Default | When to change it |
|---|---|---|---|
| **LAN address of this host** | The address every LAN address and connection string Gitberth shows uses: an IP, or a name your PC resolves. The help under it shows the address Gitberth detected and the interface it's on. | Empty: Gitberth uses `GITBERTH_PUBLIC_HOST` if it's set, else the detected address, else the address your browser used. | When the detected address is the wrong one, or you'd rather use a name. |

Click **Save** to save it. When the saved address isn't one of the host's any more, a banner says so, shows the detected address, and **Use it** saves that one instead.

**Shared network** shows the Docker network every app is on (`gitberth` by default), its driver, subnet and how many containers are on it, with **Open in Network ›**.

## Own LAN addresses

Lets a container have an address of its own on your LAN, from a block you keep out of the router's DHCP range. Off by default; the tag next to the title says **On** or **Off**. See [Give a container its own LAN address](../guides/networking.md#give-a-container-its-own-lan-address).

![Settings, Own LAN addresses](../images/settings-own-lan.webp)

While it's off:

| Field or button | What it does |
|---|---|
| **Block** | The addresses Gitberth gives out: a /24 to a /30 like `192.168.50.224/28`, or a range of 4 to 256 addresses. The line under it says what the block covers, or what's wrong with it. |
| **Suggest a block** | Fills in the highest /28 of the host's network where no device answers. |
| **Test** | A helper tries every address of the block and the router, and lists what it found. Nothing is kept. |
| **Turn on** | Makes the Docker network `gitberth-lan` on the host's interface, tests the block from it, and keeps it only when the test passes. |

The section also shows what Gitberth detected: the interface, the network and the router. If Gitberth can't set this up, a **Gitberth can't set this up yet** banner says why.

While it's on, the section shows the **Block**, the **Containers** that have an address (each links to its app), and **Turn off…**, which lists the containers that would lose their address and asks first.

## Deploys

How often Gitberth looks for new commits.

![Settings, Deploys](../images/settings-deploys.webp)

| Field | What it does | Default | When to change it |
|---|---|---|---|
| **Check for new commits every** | How many seconds apart Gitberth runs one `git ls-remote` per repo, 10 to 3600. | 30 seconds | Longer for fewer requests to GitHub; each app also has its own **Deploy on push** switch. |

Click **Save** to save it. Gitberth checks right away with the new interval.

## Admin password

The section says **Sign in as admin. Use at least 10 characters. Changing it signs every other browser out.**

| Field | What it does |
|---|---|
| **Current password** | The password you sign in with now. |
| **New password** and **New password again** | The new one, twice. |

Click **Change password**. Every other browser that's signed in goes to the sign-in page: on its next request, or within a minute for a page that's left open. The browser you changed it in stays signed in and says **Password changed. Every other browser is signed out.** See [Security](../concepts/security.md#sign-in-and-the-lockout).

## System

What this Gitberth installation is talking to. Nothing here can be changed.

| Line | What it shows |
|---|---|
| **Docker engine** | The engine's version and the docker CLI's, or **Not reachable** with the reason. |
| **git** | The version of git Gitberth uses, or **Not found**. |
| **Data folder** | Where Gitberth keeps its data inside its container, `/data` in the published image. |
| **Version** | Gitberth's version, and the commit a published image was built from, like `1.0.0 (3f9c2ab)`. |

## Related

- [Configuration](configuration.md)
- [App settings](app-settings.md)
- [Security](../concepts/security.md)
