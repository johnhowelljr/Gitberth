# GitHub integration

Gitberth can tell GitHub how each deploy went, the way Vercel does: a status on the commit it deployed, and for a preview of a pull request a comment on the pull request with the preview's address. With domains on, GitHub can also tell Gitberth about a push the moment it happens, so a deploy starts right away instead of at the next check. All of it uses the GitHub token in **Settings › GitHub access**, and none of it is ever the reason a deploy fails or waits.

## The token's permissions

A [fine-grained personal access token](https://github.com/settings/personal-access-tokens/new) on the repositories you deploy, with these **Repository permissions**:

| Permission | What Gitberth uses it for |
|---|---|
| **Contents: Read-only** | Cloning private repos and checking them for new commits. |
| **Variables: Read and write** | The repo variables apps keep their environment in (see [Environment variables](environment-variables.md)). |
| **Commit statuses: Read and write** | [Commit statuses](#commit-statuses). |
| **Pull requests: Read and write** | [Comments on pull requests](#comments-on-pull-requests). |
| **Webhooks: Read and write** | Adding Gitberth's webhook to each repo for [instant deploys](#instant-deploys). |

A classic token needs the `repo` scope, and `admin:repo_hook` for the webhook. Gitberth never changes your code.

You can leave any of the last three out: when GitHub refuses one, **Settings › GitHub integration** says which permission is missing in a banner, like **Commit statuses aren't set: GitHub refused the token**, and Gitberth stops asking for an hour, or until you save a token again. Deploys carry on either way. To add a permission, edit the token on GitHub (its permissions can change without a new token) and click **Replace token** with the same one, so Gitberth tries again at once.

## Commit statuses

On by default (**Settings › GitHub integration › Commit statuses**). Each deploy that builds a commit marks it on GitHub, where it shows next to the commit, on the pull request and in branch protection:

| When | State | Text | Link |
|---|---|---|---|
| The commit is cloned and building | pending | **Building and deploying on Gitberth** | The deploy log |
| It's live | success | **Deploy is live at app.example.com** | The app: its main domain, else its LAN address |
| The build or the new version's checks failed | failure | **Deploy failed: see the deploy log on Gitberth**, or **The new version didn't pass its checks; the previous one still runs** | The deploy log |
| Cancelled, or Gitberth restarted during it | error | **Deploy cancelled**, or **Gitberth restarted during the deploy; it's tried again** | The deploy log |

Each app has its own status, named **Gitberth: Web** (the app's name), because one repo can hold several apps; a preview's is **Gitberth preview: Web**. The text never repeats the deploy's error, since anyone who can see the repo sees it: the deploy log has the details.

Only deploys that build a commit report one. Restarts and settings changes, which run the same commit again, don't.

> **Note**
> The deploy log link goes to Gitberth's own page, at the address you last opened **Settings** at (for example `http://192.168.50.10:8080`). It works from your LAN. Open **Settings** once from another machine so Gitberth knows it.

## Comments on pull requests

On by default (**Settings › GitHub integration › Comments on pull requests**). A preview of a pull request (see [Previews](previews.md)) keeps one comment on the pull request, made with its first deploy and edited with each one after:

```text
Gitberth preview of Web: Ready

| Preview | app-pr-12.example.com |
| Commit  | abc1234               |
| Updated | Nov 9, 2026 14:03 UTC |

Deploy log (on your network)
```

The state is **Building**, **Ready**, **Failed** or **Cancelled**. If someone deletes the comment, the next deploy makes a new one.

## Instant deploys

Without them, Gitberth checks every repo for new commits every 30 seconds (**Settings › Deploys**). With them, GitHub sends a webhook to Gitberth on every push, pull request and deleted branch, and Gitberth looks at that repo right away. A deploy it starts that way shows **Push on GitHub** as its trigger. Polling keeps running too, so nothing is missed if a delivery is.

GitHub has to reach Gitberth from the internet, so instant deploys need [domains](domains.md) turned on with a Cloudflare token.

1. Go to **Settings › GitHub integration**.
2. Under **Instant deploys**, type a host name nothing else uses, like `hooks.example.com`, in **Host name for GitHub's webhooks**, and click **Turn on**.
   Gitberth routes **only** `https://hooks.example.com/hooks/github` to itself through the tunnel, adds the DNS record, and makes a secret for the webhook. Every other address on that host name answers 404, and it's never behind Cloudflare Access (GitHub can't sign in).
3. Gitberth adds the webhook to every repo it deploys, and to new ones as you add them. The list under the section says, per repo, **Webhook added** or why not.

Without **Webhooks: Read and write** on the token, add the webhook by hand on each repo, from the values the section shows: on GitHub, open the repo's **Settings › Webhooks › Add webhook** and fill in

| GitHub's field | Value |
|---|---|
| **Payload URL** | `https://hooks.example.com/hooks/github` (**Payload URL** in Gitberth, with a copy button) |
| **Content type** | `application/json` |
| **Secret** | The **Secret** Gitberth shows (**Show** reveals it, the copy button copies it) |
| **Which events** | **Let me select individual events**: **Pushes**, **Pull requests** and **Branch or tag deletion** |

GitHub sends a ping when the webhook is added; Gitberth answers it, and GitHub shows a green tick next to the delivery.

### How deliveries are checked

Every delivery is signed by GitHub with the secret (`X-Hub-Signature-256`), and Gitberth checks the signature before it reads anything else. One with a wrong signature gets `401` and nothing happens; after 10 wrong ones in 10 minutes from the same address, that address gets `429` until the 10 minutes are up. A delivery larger than 1 MB is refused. A delivery never deploys by itself: it only makes Gitberth look at the repo now, with the same rules as polling (a commit that already failed isn't retried in a loop, a rollback isn't undone).

| Gitberth answers | When |
|---|---|
| `200` | GitHub's ping. |
| `202` | A push, pull request or deleted branch in a repo Gitberth deploys: it looks now. |
| `204` | Any other event, or a repo Gitberth doesn't deploy. |
| `401` | A wrong or missing signature. |

To stop, click **Turn off** next to the host name. Gitberth takes the route out of your tunnel and the DNS record it added, and goes back to checking every 30 seconds. The webhooks stay on your repos (their deliveries fail); delete them on GitHub if you like.

## Related

- [Previews](previews.md)
- [Deploy from GitHub](deploy-from-github.md)
- [Domains](domains.md)
- [Gitberth's settings](../reference/gitberth-settings.md#github-integration)
