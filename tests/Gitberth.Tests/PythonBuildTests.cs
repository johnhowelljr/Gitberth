using Gitberth.Detection;

namespace Gitberth.Tests;

/// <summary>How Python apps are installed and built: each package manager, the Python version, native dependencies and the image.</summary>
public class PythonBuildTests
{
    private const string FastApi = "from fastapi import FastAPI\napp = FastAPI()\n";
    private const string Flask = "from flask import Flask\napp = Flask(__name__)\n";

    private static DetectionResult Analyze(TempRepo repo) => PythonTests.Analyze(repo);

    [Fact]
    public void A_root_requirements_file_that_only_includes_a_dev_one_gives_way_to_production()
    {
        using var repo = new TempRepo()
            .With("app.py", Flask)
            .With("requirements.txt", "-r requirements/local.txt\n")
            .With("requirements/local.txt", "-r base.txt\nflask-debugtoolbar\n")
            .With("requirements/base.txt", "flask>=3\n")
            .With("requirements/production.txt", "-r base.txt\ngunicorn>=23\n");
        var r = Analyze(repo);
        Assert.Contains("pip install -r requirements/production.txt\n", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e.StartsWith("Found requirements/production.txt; dependencies install with pip"));
        Assert.DoesNotContain("'gunicorn<27'", r.Dockerfile); // the production file has it
    }

    [Fact]
    public void Without_a_root_requirements_file_the_production_one_is_used()
    {
        using var repo = new TempRepo().With("app.py", Flask).With("requirements/prod.txt", "flask\n");
        Assert.Contains("pip install -r requirements/prod.txt 'gunicorn<27'", Analyze(repo).Dockerfile);
    }

    [Fact]
    public void A_uv_lock_syncs_frozen_without_dev_dependencies()
    {
        using var repo = new TempRepo()
            .With("main.py", FastApi)
            .With("pyproject.toml", "[project]\nname = 'api'\nversion = '0.1.0'\nrequires-python = '>=3.11'\ndependencies = ['fastapi>=0.115', 'uvicorn>=0.30']\n\n[dependency-groups]\ndev = ['pytest']\n")
            .With("uv.lock", "version = 1\n")
            .With("requirements.txt", "fastapi\n");
        var r = Analyze(repo);

        Assert.Contains("COPY --from=ghcr.io/astral-sh/uv:0.13 /uv /uvx /bin/", r.Dockerfile);
        Assert.Contains("ENV UV_PROJECT_ENVIRONMENT=/opt/venv UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_PYTHON_DOWNLOADS=never VIRTUAL_ENV=/opt/venv PATH=/opt/venv/bin:$PATH", r.Dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=uv,target=/root/.cache/uv uv sync --frozen --no-dev --no-install-project --python /usr/local/bin/python", r.Dockerfile);
        Assert.DoesNotContain("uv pip install", r.Dockerfile); // uvicorn is a dependency
        Assert.Contains("FROM python:3.14-slim AS build", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e == "Found pyproject.toml; dependencies install with uv from uv.lock");
        Assert.Contains(r.Evidence, e => e.StartsWith("requirements.txt is there too, but uv.lock is what's installed"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("lock"));
    }

    [Fact]
    public void A_uv_package_is_installed_too_and_gets_its_server()
    {
        using var repo = new TempRepo()
            .With("src/api/main.py", FastApi)
            .With("src/api/__init__.py", "")
            .With("pyproject.toml", "[build-system]\nrequires = ['hatchling']\nbuild-backend = 'hatchling.build'\n\n[project]\nname = 'api'\nversion = '1'\ndependencies = ['fastapi']\n")
            .With("uv.lock", "version = 1\n");
        var r = Analyze(repo);
        Assert.Contains("uv sync --frozen --no-dev --python /usr/local/bin/python\n", r.Dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=uv,target=/root/.cache/uv uv pip install 'uvicorn[standard]<1'", r.Dockerfile);
        Assert.Contains("PYTHONPATH=/app/src", r.Dockerfile);
        Assert.Contains("exec uvicorn api.main:app", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e.Contains("along with the project itself, since it's a package"));
    }

    [Fact]
    public void A_uv_project_without_its_lockfile_resolves_on_each_build_and_says_so()
    {
        using var repo = new TempRepo()
            .With("main.py", FastApi)
            .With("pyproject.toml", "[project]\nname = 'api'\nversion = '1'\ndependencies = ['fastapi']\n\n[tool.uv]\ndev-dependencies = []\n");
        var r = Analyze(repo);
        Assert.Contains("uv sync --no-dev --no-install-project --python /usr/local/bin/python", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w == "No uv.lock is committed, so each build resolves the dependencies again and may get different versions.");
    }

    [Fact]
    public void A_plain_pyproject_app_installs_only_its_dependencies()
    {
        // No [build-system] and no package named after it: `pip install .` would fail, so only the dependencies install.
        using var repo = new TempRepo()
            .With("main.py", FastApi)
            .With("routers/items.py", "")
            .With("pyproject.toml", "[project]\nname = 'my-api'\nversion = '1'\ndependencies = [\n  'fastapi>=0.115',  # web\n  \"uvicorn[standard]\",\n]\n");
        var r = Analyze(repo);
        Assert.Contains("RUN --mount=type=cache,id=uv,target=/root/.cache/uv uv pip install -r pyproject.toml\n", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e == "Found pyproject.toml; its dependencies install with uv pip");
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void A_plain_pyproject_package_is_installed_with_its_dependencies()
    {
        using var repo = new TempRepo()
            .With("my_api/app.py", Flask)
            .With("my_api/__init__.py", "")
            .With("pyproject.toml", "[build-system]\nrequires = ['setuptools>=61']\nbuild-backend = 'setuptools.build_meta'\n\n[project]\nname = 'my-api'\nversion = '1'\ndependencies = ['flask']\n");
        var r = Analyze(repo);
        Assert.Contains("uv pip install . 'gunicorn<27'", r.Dockerfile);
        Assert.Contains("exec gunicorn my_api.app:app", r.Dockerfile);
    }

    [Fact]
    public void Poetry_installs_the_main_group_into_the_virtualenv()
    {
        using var repo = new TempRepo()
            .With("main.py", FastApi)
            .With("pyproject.toml", "[tool.poetry]\nname = 'api'\nversion = '0.1.0'\n\n[tool.poetry.dependencies]\npython = \"^3.11\"\nfastapi = \"^0.115\"\n\n[tool.poetry.group.dev.dependencies]\npytest = \"^8\"\n\n[build-system]\nrequires = ['poetry-core']\nbuild-backend = 'poetry.core.masonry.api'\n")
            .With("poetry.lock", "# lock");
        var r = Analyze(repo);

        Assert.Contains("RUN --mount=type=cache,id=pip,target=/root/.cache/pip pip install 'poetry>=2,<3'\n", r.Dockerfile);
        Assert.Contains("RUN python -m venv /opt/venv\nENV VIRTUAL_ENV=/opt/venv PATH=/opt/venv/bin:$PATH\nCOPY . .", r.Dockerfile);
        // No package named "api" here, so the project itself isn't installed.
        Assert.Contains("RUN --mount=type=cache,id=poetry,target=/root/.cache/pypoetry poetry install --only main --no-root --no-interaction --no-ansi", r.Dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=pip,target=/root/.cache/pip pip install 'uvicorn[standard]<1'", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e == "Found pyproject.toml; the main dependencies install with Poetry from poetry.lock");
        Assert.Contains(r.Evidence, e => e == "Python 3.14 (Poetry: python = \"^3.11\")");
    }

    [Fact]
    public void A_poetry_package_installs_its_root_too()
    {
        using var repo = new TempRepo()
            .With("api/main.py", FastApi)
            .With("pyproject.toml", "[tool.poetry]\nname = 'service'\npackages = [{ include = 'api' }]\n\n[tool.poetry.dependencies]\nfastapi = '*'\n\n[build-system]\nrequires = ['poetry-core']\n");
        var r = Analyze(repo);
        Assert.Contains("poetry install --only main --no-interaction --no-ansi", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.StartsWith("No poetry.lock is committed"));
    }

    [Fact]
    public void Pdm_syncs_production_dependencies()
    {
        using var repo = new TempRepo()
            .With("app.py", Flask)
            .With("pyproject.toml", "[project]\nname = 'app'\nversion = '1'\ndependencies = ['flask', 'gunicorn']\n\n[tool.pdm]\ndistribution = false\n")
            .With("pdm.lock", "");
        var r = Analyze(repo);
        Assert.Contains("ENV PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_ROOT_USER_ACTION=ignore PDM_CHECK_UPDATE=false PDM_IGNORE_SAVED_PYTHON=1", r.Dockerfile);
        Assert.Contains("pip install 'pdm>=2,<3'", r.Dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=pdm,target=/root/.cache/pdm pdm sync --prod --no-self", r.Dockerfile);

        using var unlocked = new TempRepo()
            .With("app.py", Flask)
            .With("pyproject.toml", "[project]\nname = 'app'\nversion = '1'\ndependencies = ['flask']\n\n[tool.pdm]\n");
        var u = Analyze(unlocked);
        Assert.Contains("pdm install --prod --no-self\n", u.Dockerfile);
        Assert.Contains(u.Warnings, w => w.StartsWith("No pdm.lock is committed"));
    }

    [Fact]
    public void Pipenv_syncs_the_lockfile_and_reads_the_python_version_from_the_pipfile()
    {
        using var repo = new TempRepo()
            .With("app.py", Flask)
            .With("Pipfile", "[packages]\nflask = \"*\"\npsycopg = {extras = [\"binary\"], version = \"*\"}\n\n[dev-packages]\npytest = \"*\"\n\n[requires]\npython_version = \"3.12\"\n")
            .With("Pipfile.lock", "{}");
        var r = Analyze(repo);
        Assert.Contains("FROM python:3.12-slim AS build", r.Dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=pipenv,target=/root/.cache/pipenv --mount=type=cache,id=pip,target=/root/.cache/pip pipenv sync\n", r.Dockerfile);
        Assert.Contains("pip install 'gunicorn<27'", r.Dockerfile);
        Assert.DoesNotContain("libpq5", r.Dockerfile); // psycopg[binary] brings its own libpq
        Assert.Contains(r.Evidence, e => e == "Python 3.12 (Pipfile: python_version 3.12)");
    }

    [Fact]
    public void A_pipfile_without_a_lockfile_is_installed_and_warned_about()
    {
        using var repo = new TempRepo().With("app.py", Flask).With("Pipfile", "[packages]\nflask = \"*\"\n");
        var r = Analyze(repo);
        Assert.Contains("pipenv install\n", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.StartsWith("No Pipfile.lock is committed"));
    }

    [Fact]
    public void A_setup_py_package_is_installed_with_pip()
    {
        using var repo = new TempRepo()
            .With("shop/app.py", Flask)
            .With("shop/__init__.py", "")
            .With("setup.py", "from setuptools import setup, find_packages\nsetup(name='shop', packages=find_packages(), install_requires=['flask>=2', 'gunicorn'])\n");
        var r = Analyze(repo);
        Assert.Contains("RUN --mount=type=cache,id=pip,target=/root/.cache/pip pip install .\n", r.Dockerfile);
        Assert.Contains("exec gunicorn shop.app:app", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e == "Found setup.py; the project and its dependencies install with pip");
    }

    [Fact]
    public void An_apps_setup_py_that_only_lists_dependencies_installs_just_those()
    {
        // Installing the app itself would have setuptools guess its packages, and fail on app.py next to migrations/.
        using var repo = new TempRepo()
            .With("app.py", Flask)
            .With("migrations/env.py", "")
            .With("setup.py", "from setuptools import setup\nsetup(name='app', install_requires=['flask>=2', 'gunicorn'])\n");
        var r = Analyze(repo);
        Assert.Contains("RUN --mount=type=cache,id=pip,target=/root/.cache/pip pip install 'flask>=2' gunicorn\n", r.Dockerfile);
        Assert.Equal("Python (Flask)", r.Title);
    }

    [Theory]
    [InlineData(".python-version", "3.12.4\n", "3.12", ".python-version: 3.12.4")]
    [InlineData("runtime.txt", "python-3.11.9\n", "3.11", "runtime.txt: python-3.11.9")]
    [InlineData("pyproject.toml", "[project]\nname = 'x'\nrequires-python = '>=3.10,<3.13'\n", "3.12", "requires-python: >=3.10,<3.13")]
    [InlineData("pyproject.toml", "[project]\nname = 'x'\nrequires-python = '>=3.15'\n", "3.15", "requires-python: >=3.15")]
    [InlineData("README.md", "", "3.14", "Gitberth's default")]
    public void The_python_version_comes_from_the_repo(string file, string content, string version, string source)
    {
        using var repo = new TempRepo().With("main.py", FastApi).With("requirements.txt", "fastapi\n").With(file, content);
        var r = Analyze(repo);
        Assert.Contains($"FROM python:{version}-slim AS build", r.Dockerfile);
        Assert.Contains($"FROM python:{version}-slim\n", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e == $"Python {version} ({source})");
    }

    [Fact]
    public void An_end_of_life_python_is_warned_about()
    {
        using var repo = new TempRepo().With("main.py", FastApi).With("requirements.txt", "fastapi\n").With("runtime.txt", "python-3.9.18");
        var r = Analyze(repo);
        Assert.Contains("FROM python:3.9-slim", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w == "Python 3.9 is past end-of-life (October 2025) and gets no security fixes. Consider moving to Python 3.14.");
    }

    [Fact]
    public void Psycopg2_is_compiled_in_the_full_image_and_runs_with_libpq()
    {
        using var repo = new TempRepo().With("app.py", Flask).With("requirements.txt", "flask\npsycopg2==2.9.10\nmysqlclient\n");
        var r = Analyze(repo);

        Assert.Contains("# The full image has the compilers and headers that psycopg2 and mysqlclient build with.\nFROM python:3.14 AS build\n", r.Dockerfile);
        Assert.Contains("RUN apt-get update && apt-get install -y --no-install-recommends pkg-config && rm -rf /var/lib/apt/lists/*", r.Dockerfile);
        Assert.Contains("FROM python:3.14-slim\n# The shared libraries psycopg2 and mysqlclient load at run time.\nRUN apt-get update && apt-get install -y --no-install-recommends libpq5 libmariadb3 && rm -rf /var/lib/apt/lists/*", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e == "psycopg2 and mysqlclient are built from source, so the build uses the full python:3.14 image, and the app's image adds libpq5 and libmariadb3");
    }

    [Theory]
    [InlineData("psycopg2-binary", false, null)]
    [InlineData("psycopg[binary]", false, null)]
    [InlineData("psycopg", false, "libpq5")]
    [InlineData("python-ldap", true, null)]
    public void Native_dependencies_pick_the_images(string requirement, bool full, string? runtime)
    {
        using var repo = new TempRepo().With("app.py", Flask).With("requirements.txt", $"flask\n{requirement}\n");
        var dockerfile = Analyze(repo).Dockerfile!;
        var final = dockerfile[dockerfile.LastIndexOf("FROM ", StringComparison.Ordinal)..];
        Assert.StartsWith(full ? "FROM python:3.14\n" : "FROM python:3.14-slim\n", final);
        if (runtime is null) Assert.DoesNotContain("apt-get", final);
        else Assert.Contains(runtime, final);
    }

    [Fact]
    public void A_frontend_built_with_pnpm_gets_corepack_in_its_stage()
    {
        using var repo = new TempRepo()
            .With("manage.py", "import os\nos.environ.setdefault('DJANGO_SETTINGS_MODULE', 'site.settings')\n# django\n")
            .With("site/settings.py", "INSTALLED_APPS = ['django.contrib.staticfiles']\nSTATIC_ROOT = 'static'\nALLOWED_HOSTS = ['*']\nCSRF_TRUSTED_ORIGINS = ['https://x']\nMIDDLEWARE = ['whitenoise']\n")
            .With("site/wsgi.py", "application = None")
            .With("requirements.txt", "django\n")
            .With("package.json", """{ "packageManager": "pnpm@10.0.0", "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("pnpm-lock.yaml", "lockfileVersion: '9.0'");
        var r = Analyze(repo);
        Assert.Equal("Python (Django)", r.Title);
        Assert.Contains("FROM node:24-slim AS assets\nWORKDIR /app\nENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0\nRUN npm install -g corepack@latest && corepack enable\nCOPY . .\n# gitberth:build-args\nRUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store pnpm install --frozen-lockfile\n", r.Dockerfile);
        Assert.Contains("RUN pnpm run build && rm -rf node_modules", r.Dockerfile);
        // collectstatic runs after the assets are in place.
        Assert.True(r.Dockerfile!.IndexOf("COPY --from=assets", StringComparison.Ordinal) < r.Dockerfile.IndexOf("collectstatic", StringComparison.Ordinal));
    }

    [Fact]
    public void A_frontend_built_with_bun_gets_bun_in_its_stage()
    {
        using var repo = new TempRepo()
            .With("app.py", "from flask import Flask\napp = Flask(__name__)\n")
            .With("requirements.txt", "flask\n")
            .With("package.json", """{ "packageManager": "bun@1.3.2", "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("bun.lock", "{}");
        var r = Analyze(repo);
        Assert.Contains("FROM node:24-slim AS assets\nWORKDIR /app\nCOPY --from=oven/bun:1.3.2 /usr/local/bin/bun /usr/local/bin/bun\nRUN ln -s bun /usr/local/bin/bunx\nCOPY . .\n", r.Dockerfile);
        Assert.Contains("bun install --frozen-lockfile", r.Dockerfile);
        Assert.Contains("RUN bun run build && rm -rf node_modules", r.Dockerfile);
    }

    [Fact]
    public void The_marker_lines_are_where_gitberth_expands_them()
    {
        using var repo = new TempRepo().With("main.py", FastApi).With("requirements.txt", "fastapi\n");
        var dockerfile = Analyze(repo).Dockerfile!;
        // Build-time variables reach the install; volumes are made in the final image.
        Assert.Contains("COPY . .\n# gitberth:build-args\nRUN --mount=type=cache,id=pip", dockerfile);
        Assert.Contains("COPY --from=build /app /app\n# gitberth:volumes\nEXPOSE 8000\n", dockerfile);

        var expanded = DockerfileMarkers.Expand(dockerfile, ["PIP_INDEX_URL"], ["/app/data"], ["PIP_INDEX_URL"]);
        Assert.Contains("RUN --mount=type=secret,id=PIP_INDEX_URL,env=PIP_INDEX_URL --mount=type=cache,id=pip", expanded);
        Assert.Contains("RUN mkdir -p '/app/data'", expanded);
    }

    [Fact]
    public void The_ignore_file_leaves_out_virtualenvs_and_caches()
    {
        using var repo = new TempRepo().With("main.py", FastApi).With("requirements.txt", "fastapi\n").With("env/settings.json", "{}");
        var ignore = Analyze(repo).DockerIgnore!;
        foreach (var entry in new[] { ".git", ".venv", "venv", "**/__pycache__", "**/*.py[cod]", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", "**/*.egg-info", "**/node_modules" })
            Assert.Contains(entry + "\n", ignore);
        Assert.DoesNotContain("\nenv\n", ignore); // a folder of the app's, not a virtualenv

        using var venv = new TempRepo().With("main.py", FastApi).With("requirements.txt", "fastapi\n").With("env/pyvenv.cfg", "home = /usr/bin");
        Assert.Contains("\nenv\n", Analyze(venv).DockerIgnore);
    }
}
