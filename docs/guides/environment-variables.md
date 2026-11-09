# Environment variables

An app's environment variables live on its **Environment** tab. Every app has its own there. An app from GitHub also gets
the variables kept with its repository in GitHub, and the ones Gitberth sets to tell it where it runs, the way Vercel does.
This page covers all three: how to set them, how secrets are kept, and which value wins.

## Where an app's variables come from

| Source | Which apps | Where you change them |
|---|---|---|
| The app's own variables | Every app | The **Environment** tab |
| The image's own variables | Apps that run an image | Folded on the **Environment** tab; change one to make it the app's own |
| The repo's variables in GitHub | Git apps | The **Stored in GitHub** card on the **Environment** tab, or GitHub |
| Variables Gitberth sets, like Vercel | Git apps | Turned on or off in **Settings** › **Source** |

For a Git app, a value you set on the app wins over the same name in GitHub, and both win over Gitberth's Vercel-style
variables.

## Set an app's own variables

1. Open the app and click the **Environment** tab.

   ![An app's Environment tab, with its own variables and, folded under them, the ones that come from its image](../images/app-environment.webp)

2. Change a value in place, or click **Add variable** and type the name and value.

   To add several at once, click **Paste .env**, paste lines like `DATABASE_URL=postgres://app:…@postgres/app`, and click
   **Add N**. Each line sets that variable, replacing one of the same name; lines starting with `#` are skipped.

3. Use the buttons at the end of each row:
   - the eye shows or hides a secret value;
   - the copy button copies the real value, even when it's masked;
   - the lock marks the variable **Secret**, or makes it plain again;
   - the wrench (Git apps) passes the variable to the build too: it shows **Build time**;
   - undo puts back what was there; the cross removes the variable.

   **Undo every change here** drops all your edits on this tab.

4. Apply the change with the bar at the bottom of the page. For an app that isn't from Git, the container is recreated
   with the new variables, and the change dialog shows what happens first. For a Git app, a deploy without a build makes
   them live. See [Change settings](manage-apps.md#change-settings).

For a Git app, an empty value means "not set": a variable Gitberth suggested that you leave empty never overrides the
app's own default. For any other app, an empty value is passed to the container as empty; remove the variable instead.

### The image's variables

Variables the image sets itself, like `PATH` or `PG_MAJOR`, are folded under **N from the image**. Click **Show** to see
them. They're passed on as the image has them. Edit one and it becomes the app's own, with the value you give it.

## Secrets

A secret value is masked everywhere Gitberth shows it, in the UI and in build logs. Gitberth stores the values it keeps
encrypted.

- Gitberth marks a variable secret by itself when its name looks like one (it contains `SECRET`, `PASSWORD`, `TOKEN`,
  `API_KEY`, `PRIVATE`, `CREDENTIAL`, `DSN`, or ends in `_KEY`, and the like) or its value is a URL with a password in it,
  such as `postgres://app:password@postgres/app`. Those are always secret, and the lock can't be turned off.
- Mark any other variable secret with its lock.
- The copy button copies the real value.

[Security](../concepts/security.md) explains how the values are encrypted.

## Production and previews (Git apps)

A Git app's variable is for **All deploys**, for **Production** only, or for **Previews** only: choose it at the end of
the variable's row. One name can have a value for each, and a **Production** or **Previews** value wins over an **All
deploys** one. See [Preview deployments](previews.md#environments).

## Build-time variables (Git apps)

A Git app's variables are passed to the running container. Turn on **Build time** for the ones the build needs too. Front
ends build these into their code: `VITE_*`, `NEXT_PUBLIC_*`, `REACT_APP_*`, `PUBLIC_*` and `NG_APP_*`. Gitberth turns it
on by itself for those names when it suggests them for a Node.js app.

- Changing a build-time variable needs a new build. Gitberth saves it, and the next deploy builds the app with it.
- In the Dockerfiles Gitberth generates, secret values reach the build as BuildKit secrets, so they're never written into
  the image's history. Other values go in as build arguments.
- A Dockerfile in your repo only gets build arguments, and only for the `ARG`s it declares.

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

## Variables Gitberth sets, like Vercel

For every Git app, Gitberth sets Vercel's system variables, at build time and when the app runs, so code written for
Vercel finds what it expects. The **Set by Gitberth, like on Vercel** card on the **Environment** tab lists what it adds
for the app. A variable with the same name that you set, in Gitberth or in GitHub, wins, even when you set it for run time
only.

To turn them all off for an app, turn off **Vercel's variables** in **Settings** › **Source**.

| Variables | What they hold |
|---|---|
| `VERCEL_URL`, `VERCEL_BRANCH_URL`, `VERCEL_PROJECT_PRODUCTION_URL` | The app's main domain, as a bare host name such as `app.example.com`. Set only when Gitberth routes a domain to the app. |
| `VERCEL_ENV`, `VERCEL_TARGET_ENV` | `production`, or `preview` when the app deploys another branch than the repo's default one, and in every [preview](previews.md#vercels-variables-in-a-preview). |
| `VERCEL_GIT_PROVIDER`, `VERCEL_GIT_REPO_OWNER`, `VERCEL_GIT_REPO_SLUG`, `VERCEL_GIT_COMMIT_REF`, `VERCEL_GIT_COMMIT_SHA`, `VERCEL_GIT_COMMIT_MESSAGE`, `VERCEL_GIT_COMMIT_AUTHOR_NAME` | The deployed commit: `github`, the owner, the repository, the branch, the commit, its first line, and its author. |
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

When you [add an app from GitHub](deploy-from-github.md), Gitberth suggests the variables it finds:

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
