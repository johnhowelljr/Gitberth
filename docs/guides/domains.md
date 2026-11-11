# Domains

Gitberth can give any app domains of its own, like `app.example.com`, served over HTTPS through the Cloudflare Tunnel on the host, the way Vercel gives a project its domains. You add a domain on the app's page, and Gitberth writes the tunnel's route and the DNS record. This is optional: it's off until you turn it on, and apps stay reachable on your LAN either way.

## Before you start

- **A Cloudflare account** with your domain's zone (`example.com`) on it.
- **A Cloudflare Tunnel** created in Cloudflare's Zero Trust dashboard, and **cloudflared running on the host** as a container started with the tunnel's token. Gitberth reads which tunnel to use from that container's `--token` option, its `TUNNEL_TOKEN` variable or its token file, so there's nothing to type in about the tunnel. Only one tunnel may run on the host. For example:

  ```bash
  docker run -d --name cloudflared --restart unless-stopped --network host \
    cloudflare/cloudflared:latest tunnel --no-autoupdate run --token <your tunnel token>
  ```

  You can also paste that command into **New app** (see [Paste a docker run or Compose service](paste-docker-run-or-compose.md)), so cloudflared shows up as an app like any other.
- **A Cloudflare API token.** In Cloudflare, go to your profile's API tokens and choose **Create Token**, then **Create Custom Token**, with these permissions, for your account and your domains' zones:
  - **Account › Cloudflare Tunnel › Edit**
  - **Zone › DNS › Edit**
  - **Zone › Zone › Read**
  - **Account › Access: Apps and Policies › Edit** and **Account › Access: Organizations, Identity Providers, and Groups › Read**, only to [protect domains](#protect-a-domain)

cloudflared on the host's network (`--network host`, as above) reaches apps through their host ports. cloudflared on the `gitberth` network reaches them by name. If it's on neither, Gitberth joins it to `gitberth` when you save the token.

## Turn on domains

1. Go to **Settings › Domains (Cloudflare)**.
2. Turn on **Manage app domains through Cloudflare**.
   Gitberth says **Domains turned on.**, and the section shows the token fields.
3. Paste the token in **Cloudflare API token** and click **Save token**.
   Gitberth finds the cloudflared container, checks the token against its tunnel, saves the token encrypted, and routes the domains your apps already have. It says **Token saved. Domains are routed.** The section shows **Token saved** and the tunnel, like **Tunnel 1a2b3c4d from the cloudflared container**.

![Settings, Domains (Cloudflare)](../images/settings-domains.webp)

Routes you already made in Cloudflare that go to an app become that app's domains (see [Domains made in Cloudflare](#domains-made-in-cloudflare)). If anything needs attention, the notes under the token say what. When the token can't be saved, the message says why: no cloudflared container, more than one tunnel, a tunnel run from a credentials file, or a token without the permissions above. [Troubleshooting](troubleshooting.md#domains) lists them.

## Add a domain to an app

1. Open the app's page and click the **Network** tab.
2. In the **Domains** card, type the domain, like `app.example.com`, and click **Add domain**.
   Gitberth adds the tunnel route and the DNS record, and the domain shows in the card.
3. Check the pill next to it: **Live** means visitors reach the app. **Not working** comes with a line that says why, and a button to fix it when there's one (see [When a domain isn't working](#when-a-domain-isnt-working)).

The line under each domain says where its route goes, for example **Port 3000, through host port 3001**, or **Port 3000, by name (orders-api:3000)** when cloudflared is on the `gitberth` network.

The app needs a web port: domains go to it. Without one the card says **Mark a web port in Ports first: domains go to the web port.** See [The web port](networking.md#the-web-port).

### When the web port is Internal

With cloudflared on the host's network, the tunnel reaches apps through a host port. If the app's web port is **Internal**, the button reads **Add domain and publish**, and the line under it says what happens: Gitberth also publishes the web port as **Host only**, on `127.0.0.1` at a free host port, so the tunnel reaches it and the LAN still doesn't.

- A Git app restarts once to take the port (or takes it when it next starts, if it's stopped).
- Any other app is recreated once. The change dialog opens first, titled **Publish port 80 for app.example.com?**, and its button is **Recreate and add the domain**. The domain is added once the port is published.

## What Gitberth does in Cloudflare

- **Tunnel route.** One route for the domain that covers every path: pages, `/_next` scripts, APIs and sign-in. It goes to the web port where the app's running container publishes it, so settings you haven't applied yet can't break a live domain. A stopped app's routes stay as they are.
- **DNS.** A proxied DNS record that sends the domain to the tunnel. Gitberth marks the records it adds with the comment **Added by Gitberth**.
- **Checking.** Gitberth checks the routes and records again after every deploy and every change to an app's settings or ports, and moves a route when the app's host port changes. **Check again** at the top of the card reads them again; **Fix** writes what's missing.

## The main domain

When an app has several domains, one is its main domain, tagged **Main**: the address Gitberth tells the app it has, in `VERCEL_URL` and, for apps that use Auth.js, `AUTH_URL` (see [Environment variables](environment-variables.md)). It's the shortest one unless you click **Make main** on another. Front ends build that address into their code, so a new main domain reaches the app with its next build.

## Protect a domain

A protected domain asks visitors to sign in before they reach the app, through Cloudflare Access: Cloudflare emails them a one-time code, and only the email addresses you list get in. Use it for a staging copy, an admin tool, or anything that shouldn't be open to the whole internet. [Previews](previews.md)' domains are protected unless their app says otherwise.

### Set up protection once

1. In Cloudflare, open [Zero Trust](https://one.dash.cloudflare.com/) once and choose a team name, if you never have. Its free plan is enough. Your team domain, like `acme.cloudflareaccess.com`, is where visitors sign in.
2. Give Gitberth's Cloudflare token **Account › Access: Apps and Policies › Edit** and **Account › Access: Organizations, Identity Providers, and Groups › Read** too (edit the token in Cloudflare; it keeps its value). Cloudflare keeps the two apart: the first makes the Access applications, the second reads your team domain.
3. In Gitberth, go to **Settings › Domains (Cloudflare) › Protection**, type the email addresses that may sign in, one per line, in **Who may sign in**, and click **Save**.
   The tag next to **Protection** says **Ready**, with **Visitors sign in at acme.cloudflareaccess.com.** When something's missing, the **Protection isn't ready** banner says what: Zero Trust not set up, the token without the Access permission, or nobody listed. **Check again** asks Cloudflare again. When it finds that Gitberth may use Cloudflare Access while an Access application Gitberth made is still to be removed (see [Remove a domain](#remove-a-domain)), Gitberth tries to remove it again within 15 minutes.

### Turn it on for a domain

1. On the app's **Network** tab, in the **Domains** card, turn on **Protected** next to the domain.
2. Confirm with **Protect**.
   Gitberth makes a self-hosted Access application named **Gitberth: app.example.com** that allows the listed addresses, and sets the tunnel's route for the domain to require an Access sign-in (`originRequest.access`), so cloudflared checks it too. The line under the domain ends with **behind Cloudflare Access**.

Changing **Who may sign in** updates every protected domain's application. Turning **Protected** off (confirm with **Stop protecting**) takes the check off the route first and then removes the application Gitberth made; removing the domain or its app does the same. Until the application is gone, visitors still have to sign in: if it can't be removed, Gitberth says **… still asks visitors to sign in: Gitberth couldn't remove its Cloudflare Access application.** with the reason (or **… may still ask visitors to sign in: …** when it couldn't see the account's Access applications at all) and how it tries again. While the app runs, the card says **Still behind Cloudflare Access, though it isn't protected any more.**, and **Fix** tries again. While it doesn't, the card has no **Fix**: when Cloudflare refused, the note asks you to click **Check again** in **Settings › Domains (Cloudflare) › Protection** once that's fixed, as for a removed domain (see [Remove a domain](#remove-a-domain)). For a domain you removed, or one whose app you deleted, Gitberth remembers the application and tries again until it's gone (see [Remove a domain](#remove-a-domain)), or until another tunnel on your Cloudflare account serves that name and uses the application (another Gitberth took the name over, say): removing it then would turn everyone away there, so Gitberth leaves it to that one. Gitberth only ever changes or removes applications it named **Gitberth: …**; your own stay as they are.

While protection isn't set up, the switch is off and greyed out, and the card says why. A domain that's protected but can't be protected right now (the token lost the permission, or Zero Trust was turned off) isn't served at all rather than served open: its route is taken out of the tunnel and the card says **Not served until it's behind Cloudflare Access.** with the reason. It comes back once protection works again. When Cloudflare Access only failed for a moment (Cloudflare was busy or couldn't be reached), the note ends with **Gitberth tries again every 15 minutes.**, and it does. A problem that lasts, like the ones above, waits for you to fix it: Gitberth doesn't ask Cloudflare again every 15 minutes in vain.

If Cloudflare can't take the change when you turn **Protected** on (it's busy, say), the domain stays marked protected but its old, open route is still there. Gitberth says so, the card says **Served without Cloudflare Access, so anyone can open it.**, and Gitberth tries again every 15 minutes until the domain is behind Access or out of the tunnel. **Fix** tries at once. Turning **Protected** off works the same way: until the tunnel takes the change, Gitberth says the domain **isn't open to everyone yet**, the card says **Still behind Cloudflare Access, though it isn't protected any more.**, and Gitberth tries again every 15 minutes. That carries on if Gitberth restarts meanwhile: within 15 minutes of starting, it syncs once by itself. Gitberth also remembers the Access check it put on each route, so turning protection off takes it off even when its application was deleted in Cloudflare's dashboard; a check you added yourself is never touched.

Rules for a protected domain that you made in the tunnel yourself (a path rule to another service, say) aren't Gitberth's, so it never adds the Access check to them. If they skip it, anyone can open those addresses: the card says **Some of its rules in the tunnel aren't Gitberth's and skip Cloudflare Access: remove them in Cloudflare.** Delete them in the tunnel's settings in Cloudflare, or add the Access check to them yourself.

> **Note**
> Anything that calls a protected domain without a browser, such as another service's webhooks or a mobile app, can't sign in, so it stops getting through. Gitberth's own webhook host for GitHub (see [GitHub integration](github-integration.md#instant-deploys)) is never protected.

## Domains made in Cloudflare

Gitberth works alongside routes and records you made yourself:

- A route that already goes exactly where Gitberth would send the domain (plain HTTP to the app's port) is taken over as the app's domain.
- A route with a path filter is taken over as it is. Cloudflare answers 404 for anything outside the filter, `/_next` scripts included, until you click **Serve every path** and confirm. Widening it could expose more of an app than you meant to, so Gitberth never does it by itself. If you narrow one of Gitberth's routes in Cloudflare later, Gitberth keeps it narrowed.
- Path rules on the domain that go to other services stay ahead of Gitberth's rule, and your other rules keep their order. Routes to anything else are left alone. When Gitberth rewrites a rule, its settings carry over, Cloudflare Access included.
- A DNS record that points somewhere else, such as an old Vercel record, is never replaced unless you click **Point it here** and confirm. If the new record can't be added, the old ones are put back.

## When a domain isn't working

The line under a **Not working** domain says why. The button next to it fixes what Gitberth can:

| What the card says | What to do |
|---|---|
| The tunnel has no route for it yet. | Click **Fix**. |
| The tunnel still sends it to …, where the app was before. | Click **Fix**. |
| A wildcard rule above it in the tunnel catches its traffic first. | Click **Fix**: Gitberth moves the domain's rule above the wildcard. |
| It has no DNS record yet. | Click **Fix**. |
| Its DNS record leads to the tunnel, but Cloudflare's proxy is off for it. | Click **Fix**: Gitberth turns the proxy on. |
| Only addresses matching "…" reach the app, so Cloudflare answers 404 for its other pages, scripts and sign-in. | Click **Serve every path** if the whole app should answer there. |
| Its DNS points somewhere else (…), so visitors don't reach the tunnel on the host. | Click **Point it here** to replace the record. |
| Gitberth's token can't see a Cloudflare zone for it in the tunnel's account. … | Add the domain's zone to that Cloudflare account, or give the token access to it. |
| Not serving: … isn't running, or its saved ports aren't live yet. | Start the app, or apply its saved settings. |
| Protected: Gitberth puts it behind Cloudflare Access before it's served. | Click **Fix**. |
| Served without Cloudflare Access, so anyone can open it. … | Click **Fix**: Gitberth puts it behind Access, or stops serving it until it can. When it got that way because Cloudflare couldn't take a change, Gitberth also tries every 15 minutes by itself. |
| Some of its rules in the tunnel aren't Gitberth's and skip Cloudflare Access: remove them in Cloudflare. | Delete those rules in the tunnel's settings in Cloudflare, or add the Access check to them yourself. Gitberth never changes them. |
| Not served until it's behind Cloudflare Access. … | Set up [protection](#set-up-protection-once), or turn **Protected** off. When the rest says Cloudflare was busy or couldn't be reached, click **Fix**, or leave it: Gitberth tries again every 15 minutes. |
| Still behind Cloudflare Access, though it isn't protected any more. Fix takes it out. | Click **Fix**. When the tunnel couldn't take the change, or Cloudflare was busy, Gitberth also tries every 15 minutes by itself. When Cloudflare refused to remove the Access application (the token lost **Account › Access: Apps and Policies › Edit**, say), the note under the list says so: fix that first. |
| Couldn't be checked. | Cloudflare didn't answer while Gitberth checked the domain; the banner above the list says why. A protected domain says this even while its app isn't running, since it may still be served without Cloudflare Access. Click **Check again** in a moment. |

A domain two or more levels below the zone, like `a.b.example.com`, also shows **Cloudflare's free certificate covers example.com and *.example.com only, so a.b.example.com needs Total TLS or an advanced certificate in Cloudflare.**

## Remove a domain

1. In the **Domains** card, click the bin next to the domain.
2. Confirm with **Remove**.
   Gitberth takes the route out of the tunnel, and removes the DNS record if Gitberth added it. A record you made yourself stays. The app keeps running.

Deleting an app takes its domains out the same way. If Cloudflare can't be reached at that moment, the note says **…: … Gitberth tries again every 15 minutes until it's done.** Gitberth does that while domains are on, and also tries at every sync; the route is never taken over again in the meantime.

A protected domain's Access application goes too. If it can't be removed, the note says **… still asks visitors to sign in: Gitberth couldn't remove its Cloudflare Access application. …**, or **… may still ask visitors to sign in: Gitberth couldn't remove its Cloudflare Access application or see whether it's gone. …** when Gitberth couldn't see the account's Access applications. Either way, it tries again until the application is gone:

- When Cloudflare was busy or couldn't be reached, the note ends with **Gitberth tries again every 15 minutes.**, and it does.
- When Cloudflare refused (the token lost its Access permission, say), give the token **Account › Access: Apps and Policies › Edit** again, then click **Check again** in **Settings › Domains (Cloudflare) › Protection**, as the note says: once Gitberth may use Cloudflare Access there, it tries again within 15 minutes. Gitberth doesn't ask every 15 minutes in vain meanwhile, but it also tries the next time it updates your domains in Cloudflare (when you add or change a domain, or deploy an app, say), and once within 15 minutes of starting. **Replace token** tries at once.
- When the token may still see the application but not remove it, the note also offers to delete **Gitberth: …** in the Zero Trust dashboard yourself: Gitberth stops trying once it sees the application is gone (after **Check again**, within 15 minutes).
- When Zero Trust isn't set up for the tunnel's Cloudflare account, that account has no Access applications, so there's nothing left to remove: Gitberth forgets it.

It also stops, and leaves the application where it is, once another tunnel on your Cloudflare account serves that name with it (another Gitberth took the name over, say); the note then says **Left "Gitberth: …" in Cloudflare Access: … is served through another tunnel now, which uses it.**

## Turn domains off

Turn off **Manage app domains through Cloudflare** in **Settings › Domains (Cloudflare)**. Nothing changes in Cloudflare: the tunnel, DNS, token and domains stay as they are, and Gitberth stops contacting Cloudflare and reading the tunnel container. Each app's **Domains** card says **Domains are off**.

An app deleted while domains are off keeps its routes until you turn them back on, and in the meantime no new app gets the port or name those routes go to. The dialog that removes it says so: the domains stay in your Cloudflare Tunnel for now, and Gitberth takes them out when domains are turned back on. When you turn domains back on with a token saved, Gitberth syncs right away and picks up where it left off.

## How traffic reaches the app

```mermaid
flowchart LR
    visitor["Visitor's browser"] -->|"HTTPS"| cf["Cloudflare: proxied DNS record"]
    cf -->|"tunnel"| cloudflared["cloudflared on the host"]
    cloudflared -->|"127.0.0.1 or LAN host port"| app["App container, web port"]
    cloudflared -.->|"by name, on the gitberth network"| app
    pc["PC on your LAN"] -->|"192.168.50.10:host port"| app
```

cloudflared makes an outbound connection to Cloudflare, so there's no port to open on your router. Visitors on the domain and devices on your LAN reach the same container. An app with an own LAN address keeps using its host port for its domains, because the tunnel can't reach own LAN addresses.

For apps with a domain, a Git deploy starts the new version next to the old one and moves the domain's visitors to it before the old one stops. See [How deploys and changes work](../concepts/how-deploys-work.md#deploys-without-downtime).

> **Note**
> Gitberth's own page is an app too, so you could give it a domain. Whoever reaches Gitberth controls Docker on the host. Put Cloudflare Access in front of that domain before you add it. See [Security](../concepts/security.md#putting-a-domain-in-front-of-gitberth).

## Related

- [Networking](networking.md)
- [GitHub integration](github-integration.md)
- [Gitberth's settings](../reference/gitberth-settings.md#domains-cloudflare)
- [Troubleshooting](troubleshooting.md#domains)
- [Security](../concepts/security.md)
