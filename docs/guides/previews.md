# Preview deployments

A Git app can give each pull request, and each branch if you like, a preview of its own: the app built from that code
and run next to production, with its own address, like a preview deployment on Vercel. Try a change on its preview,
then **Promote** it, and production builds that commit.

This page covers turning previews on, what a preview is, its address, the limit and how previews go, environments
(variables for production or for previews), and Promote.

## Turn previews on

1. Open the Git app and click the **Settings** tab, then **Previews**.
2. Under **Previews**, choose:
   - **Pull requests**: every open pull request gets a preview, and a new commit on it deploys the preview again.
   - **Pull requests and branches**: pull requests, and every branch other than the app's own once it gets a new
     commit. Branches that are already there when you turn this on wait for their next commit, so old branches aren't
     all built at once.
3. Set the other choices, then click **Save previews**:

| Setting | What it does | Default |
|---|---|---|
| **At most running** | How many previews run at once. More wait, and start when one is removed. | 3 |
| **Remove after (days without a new commit)** | A preview with no new commit for this long is removed. | 7 |
| **Also pull requests from forks** | Previews pull requests whose code is in someone else's copy of the repo. | Off |
| **Protect previews' domains** | Visitors sign in through Cloudflare Access before they see a preview. | On |

Gitberth looks at the repo again as soon as you save. A preview shows up within a minute of a new pull request or commit,
or at once when GitHub tells Gitberth about it (see [GitHub integration](github-integration.md)).

> **Warning**
> **Also pull requests from forks** builds and runs anyone's code on this server: whoever opens a pull request decides
> what its preview runs, on your network. Leave it off unless you trust everyone who can open one.

Previews follow the app's repository only while the app deploys by itself: **Deploy on push** on, and the app running.

### Pull requests and GitHub

Gitberth finds a repo's branches and pull request commits with git, like it does for deploys, and asks GitHub's API
which pull requests are open. A public repository needs no token, but GitHub then allows only 60 questions an hour from
your host, so Gitberth asks when a pull request changes, when GitHub tells it about the repo, and every 5 minutes. With a
token saved in Settings, private repositories work too; the token needs **Pull requests** access to the repo. When
GitHub can't be asked, the **Previews** tab says why, and previews already there keep following their commits.

## What a preview is

A preview is an app of its own, made from its parent's settings:

- **Its own container, image, volumes and name**: `gb-orb-pr-12` for pull request 12 of the app `orb`, or
  `gb-orb-feature-login` for the branch `feature/login`. Long names are cut and given a short hash.
- **Its parent's settings**, copied again before each of its deploys: the subfolder, how it's built, the health URL,
  memory and CPU limits, restart policy, Vercel's variables, and its command, labels, logging and other advanced
  settings. Its ports are the parent's container ports; only the web port is published.
- **Its own volumes.** A named volume of the parent (`data`) is the preview's own (`gb-orb-pr-12-data`), so a preview
  starts with empty data and never touches production's.
- **No host folders.** A folder of the host mounted into production isn't mounted into a preview: a preview never writes
  into production's files. The preview's page says which ones it leaves out.
- **No fixed addresses.** Its parent's own LAN address, a fixed address on a network, and the names production answers
  to on its networks stay production's.

A preview's own page shows **Preview of orb for PR #12** at the top, and has no **Settings** or **Environment** tab:
change the parent's, and the preview takes them with its next deploy.

Previews stay out of the Apps list, the menu, search and the dashboard's counts. They're on their parent's **Previews**
tab. Their deploys show in **Recent deploys** on the dashboard as `orb · PR #12: deployed abc1234`.

## Addresses

Every preview's web port is published on the LAN on a host port of its own, picked like any new app's, so the LAN
reaches it at `http://192.168.50.10:3002`.

When [domains](domains.md) are on and the parent has a domain, a preview also gets one, next to the parent's main domain:

| Parent's main domain | Pull request 12 | Branch `feature/login` |
|---|---|---|
| `app.example.com` | `app-pr-12.example.com` | `app-git-feature-login.example.com` |
| `example.com` | `orb-pr-12.example.com` | `orb-git-feature-login.example.com` |

The name is one level below the zone, so Cloudflare's free certificate covers it. With **Protect previews' domains** on,
visitors sign in with Cloudflare Access (an emailed code) before they reach the preview, once Access is set up in
**Settings** › **Domains** (see [Domains](domains.md)).

## The Previews tab

A Git app's **Previews** tab lists its previews, newest activity first. For each one:

- the pull request (its number and title, linking to GitHub) or the branch;
- its state: **Building**, **Ready**, **Failed**, **Waiting** or **Stopped**;
- its commit, its domain and LAN address, its last commit, and when it's removed without a new one;
- **Open**, **Log** (its newest deploy), and in **⋯**: **Redeploy**, **Promote to production…** and **Remove…**.

## The limit, and how previews go

A preview only starts when fewer than **At most running** previews of the app run. Otherwise it shows
**Waiting: 3 previews run already**, and starts when one goes. A preview that runs already deploys its new commits
whatever the limit.

A preview is removed, with its container, images, volumes and domain (its tunnel route and DNS record too), when:

- its pull request is closed or merged;
- its branch is deleted, or a pull request is opened from it (the pull request's preview takes over);
- it has had no new commit for the days you chose;
- previews are turned off (Gitberth asks first), or branch previews, or pull requests from forks;
- its parent is removed;
- you click **Remove…**.

Each removal is noted on the parent's **Activity**, such as `Preview PR #12 removed: the pull request was merged.`
A preview that expired or that you removed comes back with the next commit on its pull request or branch.

## Environments

Each of a Git app's variables on its **Environment** tab is for one environment, chosen at the end of its row:

| Choice | Who gets it |
|---|---|
| **All deploys** | Production and every preview. |
| **Production** | Only the app's own container. |
| **Previews** | Only its previews. |

One name can have a value in each: `API_URL` for all deploys, and another `API_URL` for previews. A **Production** or
**Previews** value wins over an **All deploys** one of the same name. The repo's variables in GitHub go to every deploy.

Changing a variable for previews never marks production as needing a new build or restart; previews take it with their
next deploy.

### Vercel's variables in a preview

A preview gets Vercel's variables for a preview:

| Variable | In a preview |
|---|---|
| `VERCEL_ENV`, `VERCEL_TARGET_ENV` | `preview` |
| `VERCEL_URL`, `VERCEL_BRANCH_URL` | The preview's own domain; not set when it has none. |
| `VERCEL_PROJECT_PRODUCTION_URL` | The parent's main domain. |
| `VERCEL_GIT_COMMIT_REF` | The branch (a pull request's head branch). |
| `VERCEL_GIT_PULL_REQUEST_ID` | The pull request's number. |

`AUTH_URL` and `NEXTAUTH_URL` (Auth.js) are the preview's own domain.

## Promote to production

When a preview is ready, open the app's **Previews** tab, click **⋯** on it, then **Promote to production…** and
**Promote**.

- Production builds the preview's exact commit again, with production's variables, and deploys it on its own address.
  The deploy shows as **Promoted** in its history.
- Production stays on that commit: Gitberth doesn't deploy the production branch's commit of that moment over it. The
  next new commit on the production branch is deployed as usual, and so is a **Deploy** you click.
- The preview keeps running.
- If the promoted build fails, production keeps what it ran, and follows its branch again.

## Related

- [Deploy an app from GitHub](deploy-from-github.md)
- [Deploys and rollbacks](deploys-and-rollbacks.md)
- [Environment variables](environment-variables.md)
- [Domains](domains.md)
- [GitHub integration](github-integration.md)
