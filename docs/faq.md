# FAQ

Short answers, each with a link to the page that has the detail.

## About Gitberth

**What does Gitberth need to run?**
A host with Docker. Gitberth is one container that uses the host's Docker socket and keeps its data in SQLite files in one volume: there's no database server, Kubernetes or reverse proxy to set up. The install guide uses a Proxmox LXC. See [Install](getting-started/install.md) and [Architecture](concepts/architecture.md).

**Is it free? What's the license?**
See the [License section of the README](../README.md#license).

**Does it manage containers it didn't start?**
Yes. Every container on the host is an app, whoever started it: a database, cloudflared, a Compose stack. Each has its own page with its settings, logs, charts and network. See [Manage apps](guides/manage-apps.md).

**Can one Gitberth manage several hosts?**
No. One Gitberth manages the Docker host it runs on.

**Do my apps stop when Gitberth restarts or updates?**
No. Gitberth isn't in the path of your apps' traffic, so they keep answering on the LAN and on their domains. See [Updating Gitberth](guides/updating-gitberth.md).

**Where's the help inside Gitberth?**
Click **?** in the top bar, or **Help** at the foot of the sidebar. **Help for this page** opens the page of these docs about the page you're on, and **Search help** finds any topic. Labels with a dotted underline, and small info icons, show a tip when you point at them. See [Get help in Gitberth](getting-started/first-steps.md#get-help-in-gitberth).

## Deploying

**Which repos can it deploy?**
GitHub repositories and GitLab projects (gitlab.com, or your own GitLab), public or private. Private ones need a token in **Settings › GitHub access** or **Settings › GitLab**. See [Deploy from GitHub or GitLab](guides/deploy-from-github.md) and [GitLab](guides/gitlab-integration.md).

**Does it use webhooks?**
It can. By default Gitberth runs one `git ls-remote` per repo every 30 seconds, so a push deploys within about that long; change the interval in **Settings › Deploys**. With domains on, GitHub and GitLab can tell Gitberth about a push the moment it happens, through the tunnel. See [Instant deploys](guides/github-integration.md#instant-deploys).

**Can it run a Compose file with several services?**
Yes, as one app with all its services, from a repo or pasted. See [Compose apps](guides/compose-apps.md). [App templates](guides/templates.md) install popular self-hosted apps that way in one click.

**Can it give each pull request its own preview?**
Yes: a preview of each pull request (or GitLab merge request), and of branches if you like, with its own address and database. See [Preview deployments](guides/previews.md).

**What if my repo has no Dockerfile?**
Gitberth works out how to build static sites, Node.js, Python, Go, Rust, Deno, .NET, Ruby, PHP and Java projects, and hands anything else to Railpack. See [How Gitberth builds](reference/build-detection.md).

**Can it run Elixir, Gleam, C++…?**
Yes, through Railpack, Railway's open-source builder: Gitberth hands it any project it doesn't know how to build itself, such as Elixir (Phoenix too), Gleam, C or C++ with CMake or Meson, Zig, or a shell script. An app can also be set to build with Railpack always. See [Railpack builds](reference/railpack.md).

**Is there downtime during a deploy?**
Not for visitors on an app's domain, when the app can swap that way: the new version starts next to the old one first. Other apps, and apps with a volume, are down for a few seconds. See [How deploys and changes work](concepts/how-deploys-work.md#deploys-without-downtime).

**What happens if a new version doesn't start?**
The old container keeps running, or comes back. A failed build never touches the running container. See [How deploys and changes work](concepts/how-deploys-work.md).

## Updates

**Does Gitberth update my images by itself?**
Only for apps whose **Updates** choice is **Automatically**, in the hours set in **Settings › Updates** (03:00 to 05:00 by default). Never a database, Gitberth itself, or a Compose container. New apps get the choice set in **Settings › Updates › New apps from an image**. See [Image updates](guides/updates.md).

**Can I undo an image update?**
Yes. If the new container fails its checks, Gitberth puts the old one back by itself. After a successful update, click **Roll back to …** on the app's **Updates** card or in its **More** menu, while the image it ran before is still on the host. See [Roll back an update](guides/updates.md#roll-back-an-update). To go further back, set an older tag in the app's **Settings › Image** (see [Go back to an older version](guides/updates.md#go-back-to-an-older-version)).

**Does Gitberth update itself?**
No. Run `docker compose pull && docker compose up -d` in `/opt/gitberth`. See [Updating Gitberth](guides/updating-gitberth.md).

## Networking and domains

**How do I reach an app from my PC?**
At the host's address and the app's host port, like `http://192.168.50.10:3000`. Nothing is installed on the PC or the router. See [Networking](guides/networking.md).

**Can a container have its own IP on my LAN?**
Yes, from a block of addresses you keep out of your router's DHCP range. See [Give a container its own LAN address](guides/networking.md#give-a-container-its-own-lan-address).

**How do I find out what uses a port?**
Type it in **Check a port** on the **Network** page. See [See who holds a host port](guides/networking.md#see-who-holds-a-host-port).

**Do I need Cloudflare?**
Only for domains. Without it, apps are reached on your LAN. See [Domains](guides/domains.md).

**Do I need to open a port on my router for domains?**
No. cloudflared connects out to Cloudflare, and Cloudflare serves the domain over HTTPS. See [Domains](guides/domains.md#how-traffic-reaches-the-app).

**Does Gitberth do HTTPS on the LAN?**
No. It doesn't terminate TLS itself; apps on a domain get HTTPS from Cloudflare.

**Can I put Gitberth's own page on a domain?**
Yes, with Cloudflare Access in front of it first. See [Putting a domain in front of Gitberth](concepts/security.md#putting-a-domain-in-front-of-gitberth).

## Monitoring

**Can Gitberth tell me when something breaks?**
Yes, by email, Discord, Slack, ntfy or a webhook: a failed deploy, a crash, a failing health check, an app that goes down. See [Notifications](guides/notifications.md).

**Can it check my apps from outside, and show a status page?**
Yes. Each app is checked every minute the way a visitor reaches it, and a public status page can list them. See [Uptime checks, the status page and the down page](guides/uptime-and-status-page.md).

**Who changed what?**
The [audit log](guides/audit-log.md) records every change, who made it and how it ended, Docker changes made outside Gitberth too.

## Data and security

**Can an app get a database of its own?**
Yes: a Postgres database or a Redis, with its connection string set in a variable for it. See [Databases](guides/databases.md).

**Can an AI agent use Gitberth?**
Yes, over MCP or a plain HTTP API, with a token of its own and the access you give it. See [AI agents and the API](guides/ai-agents.md).

**Where is my data?**
Gitberth's is in the `gitberth_gitberth-data` volume. Your apps' data is in their own volumes. See [Back up and restore](guides/backups-and-restore.md).

**How are my tokens and passwords stored?**
Encrypted, with keys kept in the same volume. See [How secrets are stored](concepts/security.md#how-secrets-are-stored).

**I forgot the admin password.**
See [Forgot the admin password](guides/troubleshooting.md#forgot-the-admin-password).

**Something's wrong. Where do I look?**
[Troubleshooting](guides/troubleshooting.md) lists Gitberth's messages with their causes and fixes. Gitberth's own log is `docker compose logs gitberth`.
