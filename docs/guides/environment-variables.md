# Environment variables

An app's environment variables live on its **Environment** tab. Every app has its own there. An app from GitHub or GitLab
also gets the variables kept with its repository, and the ones Gitberth sets to tell it where it runs, the way Vercel does.
This page covers them all: how to set them, how secrets are kept, and which value wins.

## Where an app's variables come from

| Source | Which apps | Where you change them |
|---|---|---|
| The app's own variables | Every app | The **Environment** tab |
| The image's own variables | Apps that run an image | Folded on the **Environment** tab; change one to make it the app's own |
| The repo's variables in GitHub | Apps from GitHub | The **Stored in GitHub** card on the **Environment** tab, or GitHub |
| A GitLab project's CI/CD variables, by environment scope | Apps from GitLab | The **Stored in GitLab** card on the **Environment** tab, or GitLab ([GitLab](gitlab-integration.md#cicd-variables)) |
| Variables Gitberth sets, like Vercel | Git apps | Turned on or off in **Settings** › **Source** |
| Connection strings of the app's [databases](databases.md) (`DATABASE_URL`, `REDIS_URL`) | Every app | **Settings** › **Databases** |
| Where the app is reached: `APP_URL`, `APP_HOST`, `APP_SCHEME`, `APP_HTTPS` | Apps installed from a [template](templates.md) | Set from its main domain, or its LAN address without one |

For a Git app, a value you set on the app wins over the same name in GitHub or GitLab, and both win over Gitberth's
Vercel-style variables. A database's connection string wins over them all: it's the one the database Gitberth made
answers to. A template's `APP_URL` and the rest lose to a variable of the same name on the tab.

The tab shows the variables Gitberth sets on cards of their own, under the app's own: **Set by Gitberth from its
databases**, **Set by Gitberth: where it's reached** (a template's app) and, for a Git app, **Stored in GitHub** (or
**Stored in GitLab**) and **Set by Gitberth, like on Vercel**.

## Set an app's own variables

1. Open the app and click the **Environment** tab.

   ![An app's Environment tab with its own variables: a secret, one passed to the build too, and one only previews get](../images/app-environment.webp)

2. Change a value in place, or click **Add variable** and type the name and value.

   To add several at once, click **Paste .env**, paste lines like `DATABASE_URL=postgres://app:…@postgres/app`, and click
   **Add N**. Each line sets that variable, replacing one of the same name; lines starting with `#` are skipped.

3. Use the buttons at the end of each row:
   - the eye shows or hides a secret value;
   - the copy button copies the real value, even when it's masked;
   - the lock marks the variable **Secret**, or makes it plain again (never for a `RAILPACK_` variable, which can't be
     secret: see [Railpack builds](#railpack-builds));
   - the wrench (Git apps) passes the variable to the build too: it shows **Build time** (always, for a `RAILPACK_`
     variable);
   - undo puts back what was there; the cross removes the variable.

   **Undo every change here** drops all your edits on this tab.

4. Apply the change with the bar at the bottom of the page. For an app that runs an image, the container is recreated
   with the new variables, and the change dialog shows what happens first. For a Git app or a Compose app, a deploy
   without a build makes them live. See [Change settings](manage-apps.md#change-settings).

For a Git app, an empty value means "not set": a variable Gitberth suggested that you leave empty never overrides the
app's own default. For any other app, an empty value is passed to the container as empty; remove the variable instead.

### Variables from the app's databases

A variable Gitberth sets from one of the app's [databases](databases.md) is listed on the **Set by Gitberth from its
databases** card, marked **Secret**, with the database it comes from. For a Git app or a Compose app, each deploy sets it
over a value of the same name on the tab, which then shows **Replaced by Gitberth**; a value for **Previews** only stays
the previews'. For any other app, Gitberth keeps it in the container's environment, and its row shows **Set by Gitberth**
and can't be edited here: **Settings** › **Databases** adds, copies and removes it.

### Compose apps

A [Compose app](compose-apps.md)'s variables are what its Compose file's `${…}` are filled with. Gitberth also writes them
to the project's `.env`, so a service with `env_file: .env` takes them. A service gets a variable only when the file
passes it on, under `environment:` or with `env_file:`. A database's variable comes in parts too, such as
`DATABASE_URL_HOST`, for a file that wants them one by one; the card lists them.

### The image's variables

Variables the image sets itself, like `PATH` or `PG_MAJOR`, are folded under **N from the image**. Click **Show** to see
them. They're passed on as the image has them. Edit one and it becomes the app's own, with the value you give it.

## Secrets

A secret value is masked everywhere Gitberth shows it, in the UI and in build logs. Gitberth stores the values it keeps
encrypted.

- Gitberth marks a variable secret by itself when its name looks like one (it contains `SECRET`, `PASSWORD`, `TOKEN`,
  `API_KEY`, `PRIVATE`, `CREDENTIAL`, `DSN`, or ends in `_KEY`, and the like) or its value is a URL with a password in it,
  such as `postgres://app:password@postgres/app`. Those are always secret, and the lock can't be turned off.
- Mark any other variable secret with its lock, but a `RAILPACK_` one: it [can't be secret](#railpack-builds).
- The copy button copies the real value.

[Security](../concepts/security.md) explains how the values are encrypted.

## Production and previews (Git apps)

A Git app's variable is for **All deploys**, for **Production** only, or for **Previews** only: choose it at the end of
the variable's row. One name can have a value for each, and a **Production** or **Previews** value wins over an **All
deploys** one. See [Preview deployments](previews.md#environments).

A preview never gets production's database by accident. Gitberth gives each preview a database of its own when it can
(the app's own [databases](databases.md#previews), or a `postgres://` URL to a server on this host it can use). Any other
variable that says where a database is (`DATABASE_URL`, a `mysql://…` value, and the like) needs a **Previews** value
before previews deploy. See [Production's database](previews.md#productions-database).

## Build-time variables (Git apps)

A Git app's variables are passed to the running container. Turn on **Build time** for the ones the build needs too. Front
ends build these into their code: `VITE_*`, `NEXT_PUBLIC_*`, `REACT_APP_*`, `PUBLIC_*` and `NG_APP_*`. Gitberth turns it
on by itself for those names when it suggests them for a Node.js app.

- Changing a build-time variable needs a new build. Gitberth saves it, and the next deploy builds the app with it.
- In the Dockerfiles Gitberth generates, secret values reach the build as BuildKit secrets, so they're never written into
  the image's history. Other values go in as build arguments.
- Docker's build cache doesn't see secret values, so Gitberth gives the build steps that use them one more build
  argument, `GITBERTH_SECRETS`, made from the values with a key only this Gitberth has: it gives none of them away, and
  a changed secret (or a preview's own value) builds those steps again instead of reusing what an older value built.
- A Dockerfile in your repo only gets build arguments, and only for the `ARG`s it declares.

### Railpack builds

- **`RAILPACK_` variables are always build-time.** A variable whose name starts with `RAILPACK_`, like
  `RAILPACK_NODE_VERSION` or `RAILPACK_START_CMD`, configures [Railpack's build](../reference/railpack.md#settings), so
  its **Build time** is on and can't be turned off. Changing one marks the app for a rebuild.
- **`RAILPACK_` variables can't be secret.** Railpack writes them into the build plan and the image, so the lock is off
  and can't be turned on, and Gitberth doesn't save one that holds a password, such as `--api-key=…` or a URL with a
  password in it. Keep the secret in a variable of its own and write `$NAME` in the command, such as
  `RAILPACK_START_CMD=./server --api-key $API_KEY`: the start command runs through a shell, and every build step gets the
  build-time variables. One that's secret anyway, such as a GitLab variable masked there, isn't passed to Railpack, and
  the deploy log says so.
- **Every build-time value is a secret to Railpack.** Railpack has no build arguments, so in a Railpack build every
  build-time value goes in as a BuildKit secret, secret or not, and every build step gets all of them as environment
  variables. None is written into the image, and Railpack's planning never sees a secret value.
- See [Build-time variables and secrets](../reference/railpack.md#build-time-variables-and-secrets).

## Variables kept in GitHub

A Git app can keep its environment with its repository, in the repo's GitHub Actions variables (**Settings** › **Secrets
and variables** › **Actions** › **Variables** on GitHub). Every app from that repository gets them, at build time and when
it runs, like Vercel's environment variables.

The **Stored in GitHub** card on the **Environment** tab lists them. **Open in GitHub** opens the repo's variables page.

- **Add or change one**: type its **Name** and **Value** and click **Save to GitHub**. To change one, click its pencil; its
  name and value fill the form. Names use letters, digits and underscores, and can't start with a digit or `GITHUB_`.
- **Delete one**: click its bin. Gitberth asks first, since it goes for every app from the repository.
- **Make it live**: a change in GitHub takes effect with the app's next deploy. Click **Deploy** on the app's page.

This needs a token with **Variables: Read and write** (fine-grained) or the `repo` scope (classic). See
[Connect GitHub](../getting-started/first-steps.md#connect-github).

> **Note**
> GitHub's variables aren't hidden: anyone who can see the repository can read them. Gitberth masks the ones that look
> secret, but keep real secrets on the app in Gitberth, marked **Secret**.

How they're used:

- **The app's own value wins.** A variable set on the app in Gitberth overrides the GitHub one, and the deploy log says
  which ones it overrode.
- **Where the app listens is Gitberth's.** `PORT`, `HOST`, `HOSTNAME`, `ASPNETCORE_URLS` and `ASPNETCORE_HTTP_PORTS` from
  GitHub are ignored, because Gitberth sets where the app listens. Set them on the app in Gitberth to override that.
- **GitHub can be down.** Gitberth keeps an encrypted copy of the last successful read. If GitHub can't be reached during
  a deploy, it uses the copy and the log says so, so an app never starts without its environment.
- **Your own Dockerfile's base images stay put.** A GitHub variable doesn't override an `ARG` that your Dockerfile
  declares before its first `FROM`, such as one that picks the Node.js version.

When you add an app, a suggested variable that the repo already has in GitHub shows **Set in the repo's GitHub variables**
with an empty value, so it doesn't override the GitHub one.

An app from GitLab gets its project's CI/CD variables the same way, by their environment scope (`*` for every deploy,
`production` for the app's own, `preview/*` for its previews), on a **Stored in GitLab** card with **Save to GitLab**: see
[GitLab](gitlab-integration.md#cicd-variables).

## Variables Gitberth sets, like Vercel

For every Git app, Gitberth sets Vercel's system variables, at build time and when the app runs, so code written for
Vercel finds what it expects. The **Set by Gitberth, like on Vercel** card on the **Environment** tab lists what it adds
for the app. A variable with the same name that you set, in Gitberth, GitHub or GitLab, wins, even when you set it for run
time only.

To turn them all off for an app, turn off **Vercel's variables** in **Settings** › **Source**.

| Variables | What they hold |
|---|---|
| `VERCEL_URL`, `VERCEL_BRANCH_URL`, `VERCEL_PROJECT_PRODUCTION_URL` | The app's main domain, as a bare host name such as `app.example.com`. Set only when Gitberth routes a domain to the app. |
| `VERCEL_ENV`, `VERCEL_TARGET_ENV` | `production`, or `preview` when the app deploys another branch than the repo's default one, and in every [preview](previews.md#vercels-variables-in-a-preview). |
| `VERCEL_GIT_PROVIDER`, `VERCEL_GIT_REPO_OWNER`, `VERCEL_GIT_REPO_SLUG`, `VERCEL_GIT_COMMIT_REF`, `VERCEL_GIT_COMMIT_SHA`, `VERCEL_GIT_COMMIT_MESSAGE`, `VERCEL_GIT_COMMIT_AUTHOR_NAME` | The deployed commit: `github` (or `gitlab`), the owner (a GitLab project's namespace), the repository, the branch, the commit, its first line, and its author. |
| `AUTH_URL`, `NEXTAUTH_URL` | For apps that use Auth.js (`next-auth` or an `@auth/` package): `https://` and the main domain. If you set either one yourself, Gitberth sets neither. |

- **The main domain** is the shortest of the app's domains, as on Vercel. Choose another with **Make main** on the app's
  **Domains** card. Front ends build it into their code, so changing it marks the app for a rebuild. See
  [Domains](domains.md).
- **Framework copies.** As on Vercel, the app's framework also gets each variable with its public prefix, such as
  `NEXT_PUBLIC_VERCEL_URL`. The prefix comes from `vercel.json`'s `framework`, or the app's packages: Next.js, Nuxt,
  Gatsby, Create React App, Astro, SvelteKit, SolidStart, Vue CLI, RedwoodJS, Hydrogen, Sanity and Vite.
- **Left out where they'd mislead:**
  - `VERCEL=1` is never set: Next.js and other tools take it to mean they run on Vercel itself.
  - Builds don't get the bare `VERCEL_ENV`, or `AUTH_URL` and `NEXTAUTH_URL`. Nuxt and other Nitro apps would build for
    Vercel, and Auth.js reads its address when it runs. The prefixed copies are there.
  - An app whose code checks `process.env.VERCEL` gets no bare `VERCEL_URL` or `VERCEL_BRANCH_URL`, since code like that
    takes them to mean it runs on Vercel. `VERCEL_PROJECT_PRODUCTION_URL` is still set.
  - A Dockerfile in your repo only gets, at build time, the ones it declares with `ARG`.
- **New apps.** Gitberth doesn't suggest these names, or `AUTH_URL` and `NEXTAUTH_URL`, from a `.env.example`: an example
  value like `http://localhost:3000` would override them. If an app's own `AUTH_URL` points at `localhost` while it has a
  domain, the deploy log warns about it.

## Suggested variables for a new app

When you [add an app from GitHub or GitLab](deploy-from-github.md), Gitberth suggests the variables it finds:

- in `.env.example`, `.env.sample`, `.env.template`, `.env.dist`, `example.env`, `sample.env` or `.env.defaults`;
- in the code: `process.env.X` and `Bun.env.X` in Node.js and Bun; `os.environ`, `os.getenv`, django-environ,
  python-decouple and pydantic-settings in Python; `appsettings.json` and connection strings in .NET; and the like for
  the other languages;
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` for a .NET web app, so it sees the real scheme and visitor address behind
  Cloudflare Tunnel.

Names that look secret start with an empty value. Fill in what the app needs and leave the rest empty.

## Related

- [Deploy an app from GitHub](deploy-from-github.md)
- [Manage apps](manage-apps.md)
- [App settings](../reference/app-settings.md#environment-tab)
- [Security](../concepts/security.md)
