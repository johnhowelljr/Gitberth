# Notifications

Gitberth can tell you when something goes wrong, without you having to open it: a deploy that failed, an app that crashed or ran out of memory, a health check that started failing. It sends a short message with a link to the deploy log or the app's logs, by email, to a Discord or Slack channel, to your phone through [ntfy](https://ntfy.sh), or to any URL that takes a JSON POST (Home Assistant, n8n, a script of your own).

Nothing is sent until you add somewhere to send it. Sending never holds up a deploy, and a deploy never fails because a message couldn't be sent.

## Add a destination

1. Open **Settings** and go to **Notifications**.
2. Click **Add a destination** and pick what kind: **Email**, **Discord**, **Slack**, **ntfy** or **Webhook**.
3. Fill in its fields (below), and give it a **Name** if you like: the list calls it that.
4. Click **Add and send a test**. Gitberth saves it and sends a test right away. The message at the bottom right says whether the test went, and the destination's row keeps saying how its last message went.

You can add up to 10 destinations. Every message goes to all of them.

| Kind | What it needs |
|---|---|
| **Discord** | The channel's webhook URL: in Discord, the channel's settings › **Integrations** › **Webhooks** › **New Webhook** › **Copy Webhook URL**. A message is an embed with a colored edge (red for a failure, amber for a warning, green when something's fine again), and never pings anyone. |
| **Slack** | An incoming webhook URL: a Slack app with **Incoming Webhooks** turned on, and its webhook URL for the channel. |
| **ntfy** | The topic's URL, like `https://ntfy.sh/your-topic`, or your own ntfy server's (plain `http://` is fine on your LAN). Pick a topic nobody can guess: anyone who knows it can read it. For a topic that needs signing in, add an **Access token** from your ntfy server (`tk_…`). Failures come as high priority, with a tap that opens the link. |
| **Webhook** | Any URL that takes a POST. Optionally a **Bearer token**, sent as `Authorization: Bearer …`. See [What a webhook gets](#what-a-webhook-gets). |
| **Email** | Your mail server (`smtp.gmail.com`, `smtp.fastmail.com`, …), its port (587), **STARTTLS** or **None**, the user name and password it signs in with (often your mail address and an app password), the address the mail comes **From**, and the addresses it goes **To**, separated by commas. |

> **Note**
> Port 465, where TLS starts before anything else, doesn't work: Gitberth's mail client speaks STARTTLS on port 587, which every big provider has. For Gmail, use an [app password](https://myaccount.google.com/apppasswords).

A webhook URL is the key to a Discord or Slack channel, so Gitberth keeps every destination encrypted, and never shows a saved URL, token or password again: the list shows only where it goes (`https://discord.com/…`, `ntfy.sh/yo••••`). To change one, click **Edit**; a secret field left empty keeps the saved one. **Remove** asks first.

## What you're told about

Under **Tell me when**:

| Switch | What sends a message | Default |
|---|---|---|
| **A deploy fails** | A build that failed, a new version that didn't pass its checks (the previous version keeps running, and the message says so), and a [scheduled restart](scheduled-jobs-and-restarts.md) that didn't work. Not a deploy you cancelled, and not one Gitberth's own restart cut short: that's tried again by itself, and the retry's outcome is told. | On |
| **A deploy succeeds** | Deploys that build a commit: a push, **Deploy**, a promote, a rollback. Not restarts, settings changes or starts, which would say the same thing again and again, and not a [skipped commit](deploys-and-rollbacks.md#skip-commits-that-dont-need-a-deploy), which changed nothing. | Off |
| **An app crashes** | Its process exits with an error, or it runs out of memory. A stop you or Gitberth asked for isn't a crash, and neither is a process ended by SIGTERM (a host shutting down, say). | On |
| **A health check fails** | Docker marks the app unhealthy: its [health check](health-and-monitoring.md) failed several times in a row. And a second message once it passes again. | On |
| **Previews too** | Deploys of [previews](previews.md). Off, only production apps are told about. | Off |

Crashes of any container count, not only the apps Gitberth deploys. A crash while a deploy is still checking the new version isn't told on its own: the deploy's message says it.

### A crash loop

An app that crashes again and again sends one message, **web crashed**, saying whether it restarted itself or is stopped now. Gitberth then counts the crashes for 30 minutes, and if there were more sends one more message: **web keeps crashing: It crashed 14 more times in the last 30 minutes, and keeps restarting.** A failing health check is told once every 30 minutes at most, too.

### Leave an app out

Each app has a **Notifications** switch on its **Settings** tab, under **General**. Turned off, nothing is sent about that app: its deploys, crashes or health check. The sentence under **Tell me when** counts the apps left out.

## Links

Each message links to Gitberth: a deploy's message to its deploy log, a crash to the app's logs. The links use the address you last opened **Settings** at, so they work wherever that address does (usually your LAN). Open **Settings** once from another machine if a link shows an address only the host knows.

## What a webhook gets

A `POST` with `Content-Type: application/json`, the header `X-Gitberth-Event` and this body:

```json
{
  "event": "deploy.failed",
  "level": "critical",
  "title": "Deploy of web failed",
  "text": "Deploy #42 (New commit, commit 3f9c2ab) failed. npm ci exited with code 1",
  "url": "http://192.168.1.20:8080/apps/3/deployments/42",
  "app": "web",
  "appId": 3,
  "container": "gb-web",
  "preview": false,
  "deployment": 42,
  "commit": "3f9c2ab8d1e04c5a9b7f6e2d1c0b9a8f7e6d5c4b",
  "trigger": "Poll",
  "hook": null,
  "time": "2026-11-12T08:15:00Z"
}
```

`event` is one of `deploy.failed`, `deploy.succeeded`, `container.crashed`, `container.unhealthy`, `container.healthy` and `test`; `level` is `critical`, `warning`, `good` or `info`. Fields that don't apply are `null`: a crash has no `deployment`, an app Gitberth doesn't deploy has no `appId`, and `hook` names the [deploy hook](deploy-hooks.md) that started the deploy (its `trigger` is `Hook`).

## When a message doesn't arrive

The destination's row says how its last message went: **Sent 2 min ago** with the message, or **Failed 2 min ago** and why, like **Discord says the URL doesn't exist (404): the webhook was deleted, or the URL is wrong.** A service that's busy or down for a moment (a 5xx, a 429, no answer in time) gets one more try 30 seconds later. **Send a test** sends one straight away.

| What the row says | What to do |
|---|---|
| **… refused it (401)** or **(403)** | For Discord or Slack, the webhook was deleted: make a new one and paste its URL in **Edit**. For ntfy or a webhook, check the token. |
| **… says the URL doesn't exist (404)** | Paste the URL again; for Discord or Slack, the webhook was deleted. |
| **… couldn't be reached: its name isn't known (DNS)** | The host can't resolve the name: check the URL, and the host's DNS. |
| **The mail server refused the sign-in or the connection** | Check the user name and password (an app password for Gmail), and that the server takes STARTTLS on that port. |
| **The mail server … couldn't be reached on port 587** | The host can't reach the mail server: some networks block outgoing mail ports. |

How each delivery went is kept until Gitberth restarts.

## Related

- [Gitberth settings](../reference/gitberth-settings.md#notifications)
- [App settings](../reference/app-settings.md)
- [Health and monitoring](health-and-monitoring.md)
- [Deploys and rollbacks](deploys-and-rollbacks.md)
