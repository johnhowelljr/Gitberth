# How Gitberth builds each kind of project

When you add an app from GitHub, Gitberth looks at the repository (or the app's folder in it) and works out how to build
and run it. This page lists what it detects, what it runs, and on which container port. Use it to check why Gitberth
picked what it did, or what to change in your repository so it picks something else.

## Summary

| The repo (or folder) contains | How it runs | Container port |
|---|---|---|
| `Dockerfile` | Your Dockerfile, as it is | From `EXPOSE` |
| A .NET web project | ASP.NET Core on `mcr.microsoft.com/dotnet/aspnet:<version>` | 8080 |
| A .NET worker or console project | `mcr.microsoft.com/dotnet/runtime:<version>` | None |
| A Blazor WebAssembly project | nginx | 80 |
| `manage.py` (Django) | gunicorn, or uvicorn when it uses Channels; migrations run first | 8000 |
| FastAPI, Starlette, Litestar, Quart or an ASGI Falcon app | uvicorn on `python:<version>-slim` | 8000 |
| Flask, Bottle or Falcon | gunicorn on `python:<version>-slim` | 8000 |
| Sanic | Sanic's own server | 8000 |
| Streamlit, Gradio or Dash | `streamlit run`, the app's script, or gunicorn | 8501, 7860, 8050 |
| aiohttp or Tornado | The script that starts the server | Detected, else 8080 or 8888 |
| A `Procfile` with a `web:` line | That command, after the `release:` one | Detected, else 8000 |
| `go.mod` with a main package | A Go binary on `alpine:3` (Debian slim when it uses cgo), built with `golang:<version>` | Detected, else the framework's, else 8080 |
| `Cargo.toml` with a binary | A Rust binary on `debian:trixie-slim`, built with `rust:<version>` | Detected, else Rocket's 8000 or Loco's 5150, else 8080 |
| `deno.json` with a `start` task, or a `main.ts` that serves with `Deno.serve` | `deno task start`, `deno serve` or `deno run` on `denoland/deno:<version>` | Detected, else 8000 |
| A Rails app (`config/application.rb`) | `bin/rails server` on `ruby:<version>-slim`, after `bin/rails db:prepare` | 3000 |
| `config.ru`: Sinatra, Roda, Hanami or any Rack app | Puma, or the Gemfile's Unicorn, Thin or rackup | 9292 (Hanami 2300) |
| A Ruby app's Procfile `web` process | That command | From the command, else the framework's |
| `composer.json` with Laravel, Symfony, Slim or another framework | Apache with mod_php on `php:<version>-apache`, serving `public/` | 8080 |
| `index.php`, in the folder or in `public/` | Apache with mod_php | 8080 |
| `pom.xml` or `build.gradle(.kts)` with Spring Boot, Quarkus, Micronaut, Ktor or the `application` plugin | Built with Maven or Gradle, run on `eclipse-temurin:<version>-jre` | From the app's `application.properties`, `application.yml` or code, else 8080 |
| `package.json` with Next.js, Nuxt or SvelteKit (adapter-node) | A Node.js server | 3000 |
| `package.json` with Vite, Create React App, Angular, static SvelteKit or Astro, or Next.js with `output: 'export'` | Built with Node.js, served by nginx | 80 |
| Any other `package.json`: Express, Fastify, Koa, hapi, NestJS, Hono, AdonisJS, Strapi, React Router, a TypeScript server… | `start:prod` or `start`, else the file it starts from, on `node:<version>-slim` | Detected, else the framework's (Strapi 1337, AdonisJS 3333, Feathers 3030), else 3000 |
| `package.json` for Bun: Elysia, `Bun.serve`, a script that runs bun | `oven/bun:<version>`, run with bun | Detected, else 3000 |
| A Python script (`main.py` with `if __name__ == "__main__":`, `bot.py`, `worker.py`…) | `python <file>` | None, unless it serves HTTP |
| `index.html` | nginx | 80 |

"Detected" means Gitberth reads the port from the app's start command, its configuration or its code.

## How Gitberth decides

When a folder could be more than one kind of project, Gitberth tries these in order, and the first match wins:

1. **A Dockerfile** in the app's folder (`Dockerfile`, `dockerfile` or `Containerfile`).
2. **A .NET project.**
3. **A Python, Ruby, PHP, Java or Kotlin, Go, Rust or Deno app with a web framework, an app server or a Procfile.** This
   wins over a `package.json` in the same folder that only builds front-end assets, like a Django or Laravel app with
   Vite. It doesn't win over a `package.json` that runs a Node.js server.
4. **A Node.js or Bun app** (`package.json`).
5. **Any other Python, Go, Rust or Deno program**, such as a script or a worker.
6. **A static site** (`index.html`).

When nothing matches, Gitberth can't build the app by itself: add a Dockerfile to the repository, or write one in
Gitberth with **Customize the Dockerfile**.

## Your own Dockerfile

- Gitberth builds with it as it is. The container port comes from its `EXPOSE` line; without one, Gitberth says so and you
  set the port yourself.
- A Dockerfile in a sub-folder is built from the repository root or from its own folder, whichever its `COPY` lines
  expect.
- Variants such as `Dockerfile.prod` are offered as separate apps when you add the repository.
- Your Dockerfile gets the app's build-time variables as build arguments, for the `ARG`s it declares. A variable from the
  repo's GitHub settings doesn't override an `ARG` declared before the first `FROM`. See
  [Environment variables](../guides/environment-variables.md#build-time-variables-git-apps).

## .NET

- **Which project:** SDK-style .NET projects (`.csproj` or `.fsproj`). A web project wins over Blazor WebAssembly, which
  wins over a worker or console app. When there are several, Gitberth picks one, says so, and offers **Project to run**.
  .NET Framework projects can't run in a Linux container and aren't built.
- **Version:** the project's target framework, such as `net10.0`, picks the `sdk` and runtime images. Gitberth warns
  about versions near or past their end of support.
- **Web apps** run on the `aspnet` image and listen on port 8080 through `ASPNETCORE_HTTP_PORTS`. When the project has a
  JavaScript front end (a `package.json` or `ClientApp/package.json` next to it), Node.js is added to the build stage.
- **Workers and console apps** run on the `runtime` image (`aspnet` when they use ASP.NET Core), with no port.
- **Blazor WebAssembly** is published, and nginx serves its `wwwroot`.
- **Behind Cloudflare Tunnel:** Gitberth suggests `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` for web apps, so they see
  the real scheme and visitor address.

## Node.js

### Version and install

- **Node.js version:** from `.nvmrc`, then `.node-version`, then `engines.node` in `package.json`, else Gitberth's
  default. An app in a sub-folder also looks at the repository root's `.nvmrc`.
- **Dependencies** install with the package manager the lockfile names. Gitberth warns when no lockfile is committed.
- When the `build` or `start` script works in a sub-folder that has its own `package.json`, such as
  `cd admin && npm run build`, `npm --prefix admin …` or `yarn --cwd admin …`, Gitberth installs that folder's
  dependencies too. Workspaces are left to the root install.

### Full-stack frameworks and static front ends

- **Next.js, Nuxt and SvelteKit with `adapter-node`** run as a Node.js server on port 3000. A `start` script that runs
  `next dev` is skipped, and Gitberth runs `next start`. Next.js with `output: 'standalone'` makes a smaller image.
- **Vite, Create React App, Angular, static SvelteKit (`adapter-static`), Astro with static output, and Next.js with
  `output: 'export'`** are built with Node.js, and nginx serves the result on port 80.

### Apps set up for Vercel

- A SvelteKit app with `adapter-auto` or `adapter-vercel` is built with `@sveltejs/adapter-node` instead, and runs on
  port 3000.
- An Astro app with `@astrojs/vercel` is built with `@astrojs/node` in standalone mode, and runs on port 4321, like one
  that uses `@astrojs/node` already.
- Gitberth adds the adapter version that matches the framework's and points the config's import at it, only in the
  build's copy of the repository. Astro 4 and older with static output still build a static site.

### Node.js servers

- **How it starts:** `start:prod` when there is one, else `start`.
  - A script that runs a development server (nodemon, ts-node-dev, `tsx watch`, `node --watch`, `next dev`, vite) is
    skipped, and Gitberth runs the file it runs.
  - With no start script, it runs the file the dev script runs, `main`, or the first of `bin/www`, `server.js`,
    `index.js`, `app.js`, `src/index.ts` and the like.
- **NestJS** runs `start:prod`, or the build's output (`node dist/main.js`), never `nest start`, which compiles the app
  each time it starts.
- **AdonisJS** builds with `node ace build` and starts `node bin/server.js` in `build/`. Set `APP_KEY`.
- **Strapi** builds its admin panel and runs `strapi start` on port 1337. Set its keys, and add a volume at `/app/.tmp`
  to keep its SQLite database.
- **TypeScript:**
  - With a build step (`tsc`, `tsup`, `nest build`), the build's output runs: `main`, or wherever the tsconfig's `outDir`
    and `rootDir` put the entry.
  - Without one, Node.js 22.18 and later run a `.ts` file by stripping its types. That works when the file has no enums,
    namespaces, parameter properties or decorators, its imports name their `.ts` files, and there's either no
    `tsconfig.json` or one that sets `verbatimModuleSyntax`.
  - Otherwise tsx runs it: the app's own, or one Gitberth adds to the image.
- **Where it listens:** `PORT=` or `--port` in the start script, Strapi's `config/server`, Feathers'
  `config/default.json`, then the code: `process.env.PORT || 4000`, `listen(4000)`, `listen({ port: 4000 })`,
  `serve({ fetch, port })`, `const PORT = 4000`. Gitberth sets `PORT` and `HOST=0.0.0.0`.
- **Warnings** Gitberth gives:
  - The code pins `localhost` or `127.0.0.1`.
  - Fastify's `listen()` has no host, so Fastify listens on localhost only. fastify-cli gets `FASTIFY_ADDRESS=0.0.0.0`
    instead.
  - An app with sessions doesn't trust the tunnel's `X-Forwarded-Proto`. The settings are
    `app.set('trust proxy', 1)`, Fastify's `trustProxy` and Koa's `app.proxy`.

## Bun

- **When:** an app runs on Bun, on `oven/bun:<version>`, when its start script runs bun. With no start script, it also does
  when its dev script runs bun, when it uses Elysia (without `@elysiajs/node`) or Bun's types or APIs, or when it installs
  with bun.
- **Version:** from `packageManager` (`bun@1.2.21`) or `engines.bun`, else `oven/bun:1`.
- **Installs:** `bun install --frozen-lockfile` from `bun.lock` or `bun.lockb`. Bun runs TypeScript as it is.
- **Other apps with a bun lockfile**, such as a front end or a Python app's assets, install and build with bun in the
  Node.js image and run on Node.js.
- **Variables:** `Bun.env.X` is read for suggested variables, like `process.env.X`.

## Python

### Which app

- **Django:** `manage.py`. The settings come from `manage.py`, and the server is the WSGI module next to them (gunicorn),
  or ASGI with uvicorn for Channels.
- **Web frameworks:** FastAPI, Starlette, Litestar, Quart and ASGI Falcon run with uvicorn; Flask, Bottle and Falcon with
  gunicorn; Sanic with its own server. Data apps win over the frameworks they're built on: Streamlit runs with
  `streamlit run` (port 8501), Gradio runs its script (7860), and Dash runs with gunicorn (8050).
- **aiohttp and Tornado** run the script that starts the server, on the port it sets, else 8080 or 8888.
- **A `Procfile`** with a `web:` line runs that command, after the `release:` one.
- **A script:** `main.py` or `app.py` with an `if __name__ == "__main__":` block, or `server.py`, `run.py`, `start.py`,
  `bot.py` or `worker.py`, runs with `python <file>`. It has no port unless it serves HTTP.

### Dependencies

Dependencies install into a virtualenv in a build stage, which the final `python:<version>-slim` image copies, so the
build tools stay out of it.

| The repo has | Gitberth runs |
|---|---|
| `uv.lock` | `uv sync` |
| `poetry.lock` | Poetry, with the main group |
| `pdm.lock` | `pdm sync` |
| `Pipfile.lock` | `pipenv sync` |
| `requirements.txt` | pip. When the root file only includes others, `requirements/production.txt` instead. |
| A plain `pyproject.toml` | `uv pip` |
| `setup.py` | pip |

- A lockfile wins over a requirements file, and Gitberth warns when there's no lockfile.
- The project itself is installed only when it's a package. gunicorn or uvicorn is added when the app doesn't depend on
  it.
- **Native dependencies:** packages that compile against system libraries, like psycopg2 and mysqlclient, build in the
  full `python:<version>` image. The app's image gets the libraries they load.

### Version and serving

- **Python version:** from `.python-version`, `runtime.txt`, `requires-python`, the Pipfile, or Poetry's `python`, else
  3.14. Gitberth warns about versions past their end of life.
- **Behind Cloudflare Tunnel:** gunicorn and uvicorn trust the tunnel's `X-Forwarded-For` and `X-Forwarded-Proto`
  headers, so apps see HTTPS and the visitor's address. gunicorn runs `$WEB_CONCURRENCY` workers, 2 by default.

### Django

- `collectstatic` runs at build time, and runs again as the container starts when the settings need variables that only
  exist then.
- Migrations run before the server starts.
- Gitberth warns when `ALLOWED_HOSTS` is empty, when neither `SECURE_PROXY_SSL_HEADER` nor `CSRF_TRUSTED_ORIGINS` is set,
  when the database is SQLite inside the container, or when static files have no whitenoise.

### Front-end assets and variables

- **Front-end assets:** a `package.json` with a `build` script next to the app (Tailwind, Vite) is built in a Node.js stage
  first, and the Python image gets the built files without `node_modules`. A Python web framework wins over such a
  `package.json`, and a Node.js server wins over the Python app.
- **Environment variables:** besides `.env.example`, Gitberth suggests the variables the code reads through `os.environ`,
  `os.getenv`, django-environ, python-decouple and pydantic-settings.

## Go

- **Which package:** the main package at the module root, else one under `cmd/` (the one named like the module or its
  folder first), else the only other one. A module without one is a library and isn't offered.
- **Go version:** it builds with the `golang` image for `go.mod`'s `go` line, or its `toolchain` line when that's newer.
  For a Go release newer than Gitberth knows, Go downloads the toolchain itself.
- **Modules:**
  - They download first, in a layer that's reused until `go.mod` or `go.sum` change.
  - A `vendor/` folder is used as it is.
  - Without a `go.sum`, the build runs `go mod tidy` to write one.
- **The image:**
  - The binary is static and runs on Alpine, with CA certificates and time zones. Code that imports `"C"` or uses
    go-sqlite3 builds with cgo and runs on Debian slim.
  - The repository's other files, such as templates, static files and migrations, sit next to the binary in `/app`, the
    working directory, as in a checkout.
- **Starting:** the app gets `PORT`. A listen flag like `-addr` is given `$PORT` on every interface, and a cobra `serve`
  command is started. PocketBase runs `serve --http=0.0.0.0:$PORT`; give it a volume at `/app/pb_data`.

## Rust

- **Which binary:** one from the crate or from a workspace member: `default-run`, else the one named like the package,
  else one whose crate uses a web framework. A library crate isn't offered.
- **Rust version:** it builds with the `rust` image for `rust-toolchain.toml`, else Gitberth's default. `rust-version` in
  `Cargo.toml` only raises that, since it's the oldest Rust the crate supports.
- **Build:** `cargo build --release`, with `--locked` when `Cargo.lock` is committed. Cargo's downloads and `target/` are
  kept between builds.
- **The image:** Debian slim with CA certificates and OpenSSL, plus libpq, SQLite or the MySQL client when `Cargo.lock`
  shows the app links them. The repository's other files sit next to the binary, as for Go.
- **Rocket** gets `ROCKET_ADDRESS=0.0.0.0` and its port from `PORT`. **Loco** starts with
  `start --binding 0.0.0.0 --port $PORT`.

## Deno

- **Detected:** a folder with `deno.json` and a `start` or `serve` task, or a `main.ts` (`main.js`, `server.ts`…) that
  serves with Deno's own APIs. A `package.json` alone stays a Node.js app.
- **Version:** the image is `denoland/deno` at the version in `.dvmrc`, `.tool-versions` or `mise.toml`, else Gitberth's
  default.
- **Build:** the dependencies download into the image while it builds, and the `build` task runs when there is one.
- **Starting:**
  - It starts with `deno task start`.
  - When that task runs a development server (with `--watch`, or Fresh 1's `dev.ts`), the `preview` task or `main.ts`
    runs instead.
  - An entry file that exports a `fetch` handler runs with `deno serve` on `PORT`.
  - `Deno.serve` listens on port 8000 unless the code says otherwise; it doesn't read `PORT`.

A Go, Rust or Deno app that serves HTTP wins over a `package.json` in the same folder that only builds front-end assets.
Next to a Node.js server, the Node.js server is picked and both are offered.

## Ruby

- **Version:** the Gemfile's `ruby` line, `.ruby-version`, `.tool-versions` or `Gemfile.lock`, else Ruby 3.4.
- **Gems:** Bundler installs them without the development and test groups, from `Gemfile.lock` when it's committed. They
  go into a build cache kept per `Gemfile.lock`, so native extensions compile once.
- **System libraries:** pg, mysql2 and sqlite3 get theirs. A `Gemfile.lock` made on a Mac gets the Linux platforms added
  in the build.
- **Rails apps** precompile their assets with a placeholder secret, run `bin/rails db:prepare` at start, and need
  `RAILS_MASTER_KEY` or `SECRET_KEY_BASE`. They listen on port 3000, or the port in `config/puma.rb`.
- **Rack apps** (`config.ru`: Sinatra, Roda, Hanami or any other) run with Puma, or the Gemfile's Unicorn, Thin or rackup,
  on port 9292 (Hanami 2300).
- **A Procfile** `web` process runs that command, on the port it names, else the framework's.

## PHP

- **Server:** Apache with mod_php, as www-data, on port 8080, serving `public/` for a framework app.
- **Version:** `composer.json`'s `config.platform.php`, then `require.php`, else PHP 8.4.
- **Extensions** come from `composer.json`, `composer.lock` and what the code uses, plus the usual ones for Laravel and
  Symfony.
- **Behind Cloudflare Tunnel:** Apache takes HTTPS and the visitor's address from the tunnel's headers, so apps need no
  trusted-proxy settings.
- **Laravel** runs `php artisan migrate --force` and caches its config, routes and views as the container starts. It logs
  to the container's output and needs `APP_KEY`.
- **Symfony** builds with `APP_ENV=prod`, and compiles its `.env` files, assets and cache while building.

## Java and Kotlin

- **Build:** Maven or Gradle, with the repository's `mvnw` or `gradlew` when it has one, skipping tests. A multi-module
  build runs its application module.
- **Version:** the build's toolchain or compiler settings, or `.java-version`, else Java 21.
- **Spring Boot** listens on `PORT` through `SERVER_PORT` and trusts the proxy's `X-Forwarded-*` headers.
- **Quarkus** runs `quarkus-app/`. **Micronaut** and **Ktor** run their jar or the application plugin's start script.
- **Memory:** the heap is sized from the container's memory limit.

## Front ends inside Ruby, PHP, Java and Kotlin apps

A Rails, Laravel or Spring Boot app is built as what it is even when it has a `package.json` too. If that `package.json`
has a `build` script, Node.js runs it in the app's own build stage, and only the result goes into the image. A
`package.json` that runs a Node.js server still wins.

## Static sites

A folder with `index.html`, or one in `public/`, `www/`, `site/`, `docs/`, `html/`, `src/`, `dist/` or `build/`, is served
by nginx on port 80.

## Repos with several apps

When you add a repository, Gitberth lists everything in it that it can run:

- the root app;
- every folder with its own `package.json`, Dockerfile or another language's project file, up to three levels down;
- Dockerfile variants such as `Dockerfile.prod`;
- each runnable .NET project.

The most likely one is selected, and you can pick another. A root `vercel.json` with `rootDirectory` counts as a strong
vote. Tests, fixtures, tooling and libraries are left out.

## Apps in a sub-folder

- A Node.js app in a sub-folder is built from the repository root, so it can use `cd ..`, `file:../shared` dependencies
  and workspaces.
- If the folder has a `vercel.json` with `installCommand` and `buildCommand`, Gitberth runs those, exactly as Vercel does.
- A Dockerfile in a sub-folder is built from the repository root or from its own folder, whichever its `COPY` lines
  expect.

## The Dockerfile Gitberth writes

- Gitberth generates the Dockerfile again on every deploy, so a new Node.js or .NET version in the repository is picked up
  by itself. You see it in each deploy's log.
- To edit it yourself, turn on **Customize the Dockerfile** when you add the app, or set **Build** to **Custom
  Dockerfile** in the app's **Settings** › **Source**. It starts from the generated one.
- Keep the `# gitberth:build-args` and `# gitberth:volumes` lines in a custom Dockerfile. Gitberth fills them in at build
  time: the first with the build-time variables (secret ones as BuildKit secrets, never written into the image's history,
  with `ARG GITBERTH_SECRETS` so a changed secret builds the steps that use it again), the second with the app's volume
  folders, created and handed to the app's user.
- A custom Dockerfile started from a generated one for a sub-folder app keeps the `# gitberth:context repo-root` line,
  and is still built from the repository root.
- Your repository is never modified: the Dockerfile is written outside the clone.

## Builds reach your other containers

A build runs on the host's network, with every container on the `gitberth` network added by name. A build step can use
the same `postgres:5432` the app uses when it runs, for example to apply database migrations the way a Vercel build does.
Docker doesn't let a build join a network itself; host networking with the names added gives the same result. Gitberth
reads only those containers' names and addresses, never their settings, which can hold secrets.

## The container port follows the app

When a deploy finds the app now listens on another port, Gitberth moves the container port to match. That happens when
you point the app at another folder, the framework changes, or the Dockerfile's `EXPOSE` changes. The host port stays
the same. A container port you set yourself is left alone.

## Related

- [Deploy an app from GitHub](../guides/deploy-from-github.md)
- [Environment variables](../guides/environment-variables.md)
- [Deploys and rollbacks](../guides/deploys-and-rollbacks.md)
- [Troubleshooting](../guides/troubleshooting.md)
