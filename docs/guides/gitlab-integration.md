# GitLab

Gitberth deploys from GitLab the way it deploys from GitHub: paste a project's URL in **New app**, and it works out how to build it, builds it, and deploys every new commit on its branch. Merge requests get previews, deploys are reported on GitLab's commits and merge requests, a push deploys at once through a project hook, and the project's CI/CD variables reach every deploy. It works with gitlab.com and with a self-hosted GitLab.

Everything here is set in **Settings › GitLab**, and none of it is ever the reason a deploy fails or waits.

## Set it up

1. Go to **Settings › GitLab**.
2. **GitLab address**: leave it as `https://gitlab.com`, or type your own GitLab's address, like `https://gitlab.example.com` (with the path it's served under, if it has one: `https://example.com/gitlab`). A plain `http://` address on your network works too.
3. **Token**: paste a token (see [The token's scopes](#the-tokens-scopes)) and click **Save token**. Gitberth asks GitLab who the token is and which scopes it has before it saves it, and refuses a token GitLab refuses. It's kept encrypted and never shown again; **Replace token** saves another, **Remove token** forgets it.
4. Add an app: **New app › Git repository**, paste the project's URL, **Check**, then **Create and deploy**.

Once saved, the section shows **Token saved**, whose it is, the GitLab it's for, when it expires (highlighted with **renew it soon** in its last 14 days) and its scopes. When it lacks a scope Gitberth uses, a banner names each one and what it's for. **Check the token** asks GitLab again, after you've changed the token's scopes, say.

A public project needs no token: it's cloned, watched, built and previewed without one. GitLab then hears nothing from Gitberth, and the project's CI/CD variables can't be read.

## The token's scopes

A [personal access token](https://gitlab.com/-/user_settings/personal_access_tokens), or a project or group access token, with:

| Scope | What Gitberth uses it for |
|---|---|
| **api** | Everything below: reading and cloning, the CI/CD variables (read and saved), [commit statuses](#commit-statuses), [the merge request comment](#comments-on-merge-requests) and [the project hook](#instant-deploys). |
| **read_api** | Without **api**: merge requests for previews, the CI/CD variables at deploy, and build filters. |
| **read_repository** | Without **api**: cloning private projects and checking them for new commits. |

With **read_api** and **read_repository** alone, Gitberth deploys, previews and reads the variables, and reports nothing to GitLab. The token's user also needs a role on each project: **Reporter** to comment, **Developer** for statuses, **Maintainer** for the project's CI/CD variables and its hook. Gitberth never changes your code.

When GitLab refuses something on a project, **Settings › GitLab** says what's missing in a banner like **Commit statuses aren't set on acme/web**, and Gitberth stops asking on that project for an hour, or until you save a token again. Other projects carry on. A token GitLab refuses outright (expired, revoked) stops it everywhere until it's replaced: **Commit statuses aren't set: GitLab refused the token**. Deploys carry on either way.

GitLab's rate limit (an answer `429`, with `Retry-After`) is said, never pushed through: the call fails with when to try again, and Gitberth asks again later, as it does with GitHub's.

## A project's URL

Any of these works, groups and subgroups included:

- `https://gitlab.com/acme/platform/web`, with or without `.git`
- `gitlab.com/acme/platform/web`, without a scheme
- a page of the project: `https://gitlab.com/acme/platform/web/-/tree/main/api` also picks the branch `main` and the folder `api`
- `git@gitlab.com:acme/platform/web.git`

Only projects on the GitLab in **Settings › GitLab** are taken: one GitLab at a time, as there's one GitHub token. Change the address and every GitLab app follows it (a project is kept by its path, never with the address). The app's page and its Settings › Source link to the project there, with GitLab's mark.

## What works, as on GitHub

| | On GitLab |
|---|---|
| Deploy on push | The watcher runs one `git ls-remote` per project every 30 seconds (**Settings › Deploys**) and deploys a new commit on the app's branch. |
| [Build filters](deploys-and-rollbacks.md#skip-commits-that-dont-need-a-deploy) | What a push changed comes from GitLab's compare (the running commit against the new one), so nothing more is fetched. When GitLab can't say (its compare timed out, or it can't be asked), the commit is built and the log says why. |
| [Previews](previews.md) | Every open merge request gets a preview, called **MR !12**, which fetches `refs/merge-requests/12/head` from the project. A merge request from a fork (another project) gets one only while the app's **Also pull requests from forks** is on, with the same reduced privileges as a fork's pull request on GitHub. A merged or closed one takes its preview, and the app's Activity says which. **Promote** builds the preview's commit as production. |
| [Variables](environment-variables.md) | The project's CI/CD variables, at build time and when it runs; see [CI/CD variables](#cicd-variables). |
| Statuses and a comment | See [Commit statuses](#commit-statuses) and [Comments on merge requests](#comments-on-merge-requests). |
| Instant deploys | See [Instant deploys](#instant-deploys). |
| Vercel's variables | `VERCEL_GIT_PROVIDER` is `gitlab`, `VERCEL_GIT_REPO_OWNER` the project's namespace (`acme/platform`). |
| AI agents and the API | `create_app` takes a GitLab project's URL; `get_app` says `git.host: gitlab`; `get_settings` has a `gitlab` block (never the token). |

## CI/CD variables

The project's variables in GitLab (**Settings › CI/CD › Variables**) reach every app from it, at build time and when it runs, by their **environment scope**:

| Scope | Who gets it |
|---|---|
| `*` | Every deploy: the app, and its previews. |
| `production` | The app's own deploys. |
| `preview/*` | Its previews. |

A variable for `production` or `preview/*` wins over the `*` one of the same name. A **masked** variable is secret in Gitberth too: masked in the deploy log, and passed to the build as a secret. A **protected** one reaches production only, never a preview (GitLab gives protected variables to protected branches only). A **File** variable isn't passed (Gitberth passes plain ones), and one for any other scope is left to GitLab's own pipelines; the deploy log says which were left out. An app's own values in Gitberth win over GitLab's, as they do over GitHub's.

Each successful read is kept, encrypted, and used when GitLab can't be read, so an outage or an expired token never starts an app without its environment.

The app's **Environment** tab lists them under **Stored in GitLab**, each with its scope (and **protected**, **file** or **not used** where that applies), and saves and deletes them: pick **Every deploy (\*)**, **Production (production)** or **Previews (preview/\*)**, then **Save to GitLab**. A new variable is saved plain (not masked, not protected); change that in GitLab if you like. Saving and deleting need the **api** scope and the Maintainer role, and each is recorded in the [audit log](audit-log.md), its value only when it isn't a secret.

## Commit statuses

On by default (**Settings › GitLab › Commit statuses**). Each deploy that builds a commit marks it on GitLab, where it shows on the commit, its pipeline and the merge request, under the same name as on GitHub (**Gitberth: Web**, and **Gitberth preview: Web** for a preview), with the same text and link:

| When | State |
|---|---|
| It's building | `running` |
| It's live, or it was skipped | `success` |
| The build or the new version's checks failed | `failed` |
| Cancelled, or Gitberth restarted during it | `canceled` |

The status is on the branch the deploy builds: the app's, or the preview's. A fork's merge request and a promote leave the branch to GitLab.

GitLab moves a status through its own states, and refuses to set one to the state it's in already (`Cannot transition status`, when a build that Gitberth's restart cut short is tried again, say). Gitberth never asks for the same state twice in a row, and takes that answer as done, never as a problem.

## Comments on merge requests

On by default (**Settings › GitLab › Comments on merge requests**). A preview of a merge request keeps one comment on it up to date, as on GitHub: its state, address, commit, time and a link to the deploy log. If its ID was lost, Gitberth finds its own comment again (it starts with an invisible marker) rather than making a second; if someone deletes it, the next deploy makes a new one.

## Instant deploys

GitLab's project hooks share the host name for webhooks in **Settings › GitHub integration › Instant deploys** (which needs [domains](domains.md)): with one set, `https://hooks.example.com/hooks/gitlab` goes to Gitberth through the tunnel too, next to `/hooks/github` and deploy hooks, and nothing else on that host does.

Gitberth adds its project hook to every GitLab project it deploys, and to new ones as you add them: that URL, **Push events** and **Merge request events**, SSL verification on, and a secret token of its own (never GitHub's webhook secret). The list in **Settings › GitLab** says, per project, **Webhook added** or why not. As with GitHub, it only ever changes its own hook (the one it added, or one with exactly its URL), moves it when the host name changes, and leaves any other alone.

Without the **api** scope or the Maintainer role, add it by hand on each project (**Settings › Webhooks › Add new webhook**) with the **URL**, the **Secret token** (**Show** reveals it, the copy button copies it) and the triggers **Settings › GitLab** shows.

Every delivery's `X-Gitlab-Token` is compared with the secret in fixed time before anything else is read. A wrong one gets `401`, and after 10 in 10 minutes from one address `429`; the right one always gets through. A push or a merge request on a project Gitberth deploys gets `202` and makes Gitberth look at it at once (a deploy it starts shows **Push on GitLab**); anything else gets `204`. GitLab gives a delivery 10 seconds and turns off a hook that keeps failing, so Gitberth answers at once, and takes one too big to read (over 1 MB) with `202` after looking at every project. Polling carries on as well, so nothing is missed if a delivery is.

## Related

- [GitHub integration](github-integration.md)
- [Previews](previews.md)
- [Environment variables](environment-variables.md)
- [Gitberth's settings](../reference/gitberth-settings.md#gitlab)
