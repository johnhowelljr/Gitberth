# Uptime checks, the status page and the down page

Gitberth checks each app from outside, the way a visitor reaches it, like an uptime monitor such as UptimeRobot. A broken
tunnel, DNS record or certificate, or an app that stops answering, sends a [notification](notifications.md), and a
public status page can show how your apps are doing. While an app can't be served, its domains show a clean "temporarily
unavailable" page instead of Cloudflare's error.

## What's checked

Every app is checked every minute, on its own, with nothing to set up. **Check each app every** in **Settings › Uptime
and status page** changes how often, from 30 seconds to 15 minutes. What a check asks depends on the app:

| The app has | Gitberth asks |
|---|---|
| A [domain](domains.md), with domains on | Its main domain over HTTPS: `https://app.example.com/`, through Cloudflare and your tunnel, as a visitor would. |
| No domain, and a web port on the LAN | Its LAN address: `http://192.168.50.10:3001/`. |
| Neither (a worker, a database, a port only this host reaches) | Nothing. |

An app that's stopped isn't checked: it's paused. Neither is a [preview](previews.md), nor a Git app before its first
deploy is live. While a deploy replaces an app's container, it isn't checked either: it's being updated, not down.

A check waits 10 seconds for an answer, never follows a redirect, and checks the certificate as a browser does. The app
is **up** when it answers with a status below 500 that isn't Gitberth's down page. Anything else is a failure, and says
which:

| Failure | What it means |
|---|---|
| **Not reachable** | Nothing answered: the name doesn't resolve, the tunnel isn't running, or nothing listens on the LAN address. |
| **Server error 502** | The answer was a server error. From Cloudflare, `502` usually means the tunnel couldn't reach the app, and `530` that no tunnel is running. |
| **Showing the down page** | The domain shows Gitberth's [down page](#the-down-page): the app isn't running. |
| **Certificate problem** | The certificate isn't valid for the name, has expired, or isn't trusted. |
| **Timed out** | No answer within 10 seconds. |

## When an app goes down

Two failed checks in a row make an app **down**: an incident starts at the first of them, and Gitberth sends **An app
goes down** to your [notifications](notifications.md), with the address it checked and what it found. One good check
ends the incident, and sends that it's back up and for how long it was down: "It was down for 4 minutes (Server error
502)". A failure between two good checks is only a blip.

Gitberth also keeps an eye on the certificate of each domain it checks over HTTPS. One that expires within 14 days is
told once, and again within 3 days (at most once a day for a name). Cloudflare renews its certificates long before that,
so such a notice means something to look at in Cloudflare.

Both kinds of notice are on unless you turn them off in **Settings › Notifications**, and an app left out of
notifications isn't told about. In a webhook's JSON they're `uptime.down`, `uptime.up` and `certificate.expiring`, with
the checked address in `address`.

## What you see

- The app's **Overview** has an **Uptime** card: whether it's up, its uptime over the last 24 hours, 7 days and 30 days,
  its response times over the last day, its recent incidents, the address it checks and when, and its certificate's
  expiry. **Check now** checks it at once.
- The card also has the app's **Uptime check** switch, and the **Path** it asks for (`/` unless you set one, like
  `/health`). Each saves on its own.
- The **Health** page shows each app's uptime under its state.

The history stays in `/data/uptime.db`: every check for 7 days, an hourly summary and the incidents for 400 days.

## The status page

A public page with your apps' state, 90 days of uptime and the last two weeks' incidents, like a Statuspage or Better
Stack status page. It's off until you turn it on:

1. Open **Settings** and go to **Uptime and status page**.
2. Turn on **Status page**, and give it a **Title** and, if you like, a **Description**.
3. Choose the **Apps it lists**: every app with uptime checks (the default, apps added later too), or only the ones you
   pick.
4. Turn on **Show response times** if you want them on it.
5. Click **Save status page**.

It's then on your network at `/status`, without signing in, and as JSON at `/status.json` for a dashboard or a script.
While it's off both answer `404`.

| Part | What it shows |
|---|---|
| The banner | **All systems operational**, **Partial outage** (some apps are down) or **Major outage** (every app is). |
| Each app | Its name as Gitberth shows it, whether it's operational or down, a bar per day for 90 days (green, amber or red by that day's uptime; hover one for its percentage), its uptime over the 90 days, and its response time when chosen. |
| Past 14 days | Each incident: "web was down for 4 minutes on Nov 12, 14:05–14:09 UTC: Server error 502". |

The page never shows an address, a container's name or an error's own text, and everything on it is escaped. It
refreshes itself every minute, and reads well on a phone, in light and dark.

### On the internet

Give the status page a **Host name on the internet**, like `status.example.com`, which needs [domains](domains.md). The
tunnel then sends that whole host to the status page, with its DNS record like any domain, and nothing else of Gitberth's
answers there: `https://status.example.com/` is the page, `https://status.example.com/status.json` its JSON, and any
other address `404`. Pick a name of its own: not an app's domain, nor the host name of
[instant deploys](github-integration.md#instant-deploys).

## The down page

While an app can't be served, its domains show a clean page instead of Cloudflare's error. It's shown:

| When | How soon |
|---|---|
| The app is stopped | At once: **Stop** sends its domains there. |
| Its container isn't running (it crashed, keeps restarting, or was stopped outside Gitberth) | After 20 seconds, so a restart never shows it. It goes back to the app as soon as the container runs. An app that keeps going down and up only goes back once it has run for a minute, so a crash loop doesn't change the tunnel over and over. |
| A deploy has to stop the app before it starts the new version (an app with a volume, say) | Before the old container stops: Gitberth waits until the tunnel runs it. Visitors go back to the app once the new version passed its checks, or once the old one is back after a rollback. A deploy that runs the new version next to the old one needs no down page. |
| A protected domain's protection can't be applied (Cloudflare Access isn't set up, or fails for a moment) | At once: the down page is all that's served until it can be protected. |

The page answers `503` with `Retry-After` (30 seconds while the app is being updated or is down, longer when it was
stopped on purpose), is never cached or indexed, and has no outside styles, scripts or images. It shows the domain's
name, "This site is temporarily unavailable", why in a few words (it's being updated, it isn't running right now, or
it's held back), your message if you wrote one, and a link to the status page when it's on the internet and lists the
app. A client that asks for JSON gets `{"status":503,"error":"unavailable",…}`. It never names the app, its container,
an address or an error.

A [protected](domains.md#protect-a-domain) domain's down page says nothing beyond its name. While Cloudflare Access works,
visitors sign in before they see it, as they would for the app.

### Turn it off, or add a message

On the app's **Network** tab, the **Domains** card has a **Down page** switch (on for every app unless you turn it off)
and a **Message** for visitors, like "Back by 9:00 UTC." Both save on their own. With the down page off, a domain shows
Cloudflare's error while its app can't be served, as before.

Each domain has a preview button, which opens its down page as visitors would see it (only when you're signed in).

### How it works

The tunnel rule of a domain on its down page goes to Gitberth itself (where cloudflared reaches it, as for the webhook's
host name) with a reserved host name under `gitberth.invalid` as the request's `Host`. Gitberth answers such a request
with the down page and nothing else, before anything of its own sees it: never its sign-in page, its pages, its hooks or
its files. A visitor can't choose that host name (cloudflared sets it), and anyone on your network who sends one only gets
the same page. When the domain goes back to its app, the rule loses that host name and keeps everything else (its Access
check, say).

When cloudflared can't reach Gitberth's own container, the down page can't be shown: the domain stays as it was, and the
Domains card says why.

A domain whose route sends a `Host` of your own (its **HTTP Host Header** setting in Cloudflare) doesn't show the down
page either: the page's host name would have to take that header's place, and the app would get its domain back without
it. Such a domain shows Cloudflare's error while its app can't be served, as before, and the Domains card says why. A
protected one is the exception: while its protection can't be applied it still shows only the down page (it can't stay
on the app without Cloudflare Access), and its route comes back without that header, as it did before 1.3.

## Related

- [Domains](domains.md)
- [Notifications](notifications.md)
- [Health and monitoring](health-and-monitoring.md)
- [Gitberth's settings](../reference/gitberth-settings.md#uptime-and-status-page)
- [App settings](../reference/app-settings.md#down-page)
