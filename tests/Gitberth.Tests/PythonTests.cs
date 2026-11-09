using Gitberth.Detection;

namespace Gitberth.Tests;

/// <summary>Python detection: frameworks and where their app is, Django, Procfiles, scripts, and how they win or lose against other kinds.</summary>
public class PythonTests
{
    private const string Manage = """
        import os
        import sys

        if __name__ == "__main__":
            os.environ.setdefault("DJANGO_SETTINGS_MODULE", "mysite.settings")
            from django.core.management import execute_from_command_line
            execute_from_command_line(sys.argv)
        """;

    private const string StartProjectSettings = """
        from pathlib import Path
        BASE_DIR = Path(__file__).resolve().parent.parent
        SECRET_KEY = "django-insecure-x"
        DEBUG = True
        ALLOWED_HOSTS = []
        INSTALLED_APPS = ["django.contrib.admin", "django.contrib.staticfiles"]
        WSGI_APPLICATION = "mysite.wsgi.application"
        DATABASES = {"default": {"ENGINE": "django.db.backends.sqlite3", "NAME": BASE_DIR / "db.sqlite3"}}
        STATIC_URL = "static/"
        STATIC_ROOT = BASE_DIR / "staticfiles"
        """;

    private const string Wsgi = "import os\nfrom django.core.wsgi import get_wsgi_application\nos.environ.setdefault(\"DJANGO_SETTINGS_MODULE\", \"mysite.settings\")\napplication = get_wsgi_application()\n";

    internal static DetectionResult Analyze(TempRepo repo, string? hint = null, string? appDir = null) =>
        new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(hint, AppDir: appDir));

    private static TempRepo Django(string settings = StartProjectSettings, string requirements = "Django>=5.2,<6\n") => new TempRepo()
        .With("manage.py", Manage)
        .With("mysite/settings.py", settings)
        .With("mysite/urls.py", "urlpatterns = []")
        .With("mysite/wsgi.py", Wsgi)
        .With("mysite/asgi.py", "from django.core.asgi import get_asgi_application\napplication = get_asgi_application()\n")
        .With("requirements.txt", requirements);

    [Fact]
    public void Django_runs_migrations_then_gunicorn_and_collects_static_files_at_build_time()
    {
        using var repo = Django();
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.Python, r.Kind);
        Assert.Equal("Python (Django)", r.Title);
        Assert.Equal(8000, r.ContainerPort);
        Assert.Contains("RUN --mount=type=cache,id=pip,target=/root/.cache/pip pip install -r requirements.txt 'gunicorn<27'", r.Dockerfile);
        Assert.Contains("RUN python manage.py collectstatic --noinput || touch .collectstatic-at-start", r.Dockerfile);
        Assert.Contains(
            """CMD ["sh", "-c", "if [ -f .collectstatic-at-start ]; then python manage.py collectstatic --noinput; fi; python manage.py migrate --noinput && exec gunicorn mysite.wsgi:application --bind 0.0.0.0:${PORT:-8000} --workers ${WEB_CONCURRENCY:-2} --forwarded-allow-ips='*' --access-logfile -"]""",
            r.Dockerfile);
        Assert.Contains("ENV PYTHONUNBUFFERED=1 PATH=/opt/venv/bin:$PATH PORT=8000", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e == "Django settings: mysite.settings");
        Assert.Contains(r.Evidence, e => e.Contains("gunicorn isn't in the dependencies"));

        Assert.Contains(r.Warnings, w => w.StartsWith("ALLOWED_HOSTS is empty in mysite/settings.py"));
        Assert.Contains(r.Warnings, w => w.Contains("SECURE_PROXY_SSL_HEADER") && w.Contains("CSRF_TRUSTED_ORIGINS"));
        Assert.Contains(r.Warnings, w => w.StartsWith("whitenoise isn't installed"));
        Assert.Contains(r.Warnings, w => w.StartsWith("DEBUG = True in mysite/settings.py"));
        Assert.Contains(r.Warnings, w => w.Contains("SQLite inside the container"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("STATIC_ROOT"));
    }

    [Fact]
    public void Django_settings_ready_for_production_give_no_warnings()
    {
        const string settings = """
            import dj_database_url
            DEBUG = False
            ALLOWED_HOSTS = ["example.com"]
            CSRF_TRUSTED_ORIGINS = ["https://example.com"]
            SECURE_PROXY_SSL_HEADER = ("HTTP_X_FORWARDED_PROTO", "https")
            INSTALLED_APPS = ["django.contrib.staticfiles"]
            MIDDLEWARE = ["django.middleware.security.SecurityMiddleware", "whitenoise.middleware.WhiteNoiseMiddleware"]
            DATABASES = {"default": dj_database_url.config(default="sqlite:///db.sqlite3")}
            STATIC_ROOT = BASE_DIR / "staticfiles"
            """;
        using var repo = Django(settings, "django>=5.2\nwhitenoise\ngunicorn\ndj-database-url\n");
        var r = Analyze(repo);

        Assert.Empty(r.Warnings);
        Assert.Contains("pip install -r requirements.txt\n", r.Dockerfile);
        Assert.DoesNotContain(r.Evidence, e => e.Contains("gunicorn isn't"));
    }

    [Fact]
    public void Django_finds_the_wsgi_module_next_to_its_settings()
    {
        using var repo = Django(StartProjectSettings.Replace("WSGI_APPLICATION", "X_WSGI"));
        Assert.Contains("exec gunicorn mysite.wsgi:application --bind", Analyze(repo).Dockerfile);
    }

    [Fact]
    public void Django_without_static_root_builds_without_collected_static_files_and_says_so()
    {
        using var repo = Django(StartProjectSettings.Replace("STATIC_ROOT = BASE_DIR / \"staticfiles\"", ""));
        var r = Analyze(repo);
        Assert.Contains(r.Warnings, w => w.StartsWith("STATIC_ROOT isn't set in mysite/settings.py"));
        Assert.Contains("RUN python manage.py collectstatic --noinput || echo \"collectstatic failed, so no static files were collected\"", r.Dockerfile);
        Assert.DoesNotContain(".collectstatic-at-start", r.Dockerfile);
    }

    [Fact]
    public void Django_without_the_staticfiles_app_or_a_database_skips_collectstatic_and_migrate()
    {
        using var repo = Django("SECRET_KEY = 'x'\nALLOWED_HOSTS = ['*']\nCSRF_TRUSTED_ORIGINS = ['https://x']\n");
        var r = Analyze(repo);
        Assert.DoesNotContain("collectstatic", r.Dockerfile);
        Assert.DoesNotContain("migrate", r.Dockerfile);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Django_with_channels_serves_asgi_with_uvicorn()
    {
        using var repo = Django(StartProjectSettings + "ASGI_APPLICATION = \"mysite.asgi.application\"\n", "django>=5.2\nchannels>=4\n");
        var r = Analyze(repo);

        Assert.Equal("Python (Django, ASGI)", r.Title);
        Assert.Contains("exec uvicorn mysite.asgi:application --host 0.0.0.0 --port ${PORT:-8000} --proxy-headers --forwarded-allow-ips='*'", r.Dockerfile);
        Assert.Contains("'uvicorn[standard]<1'", r.Dockerfile);
        Assert.DoesNotContain("gunicorn", r.Dockerfile);
    }

    [Fact]
    public void Channels_on_plain_uvicorn_is_warned_about_websockets()
    {
        using var repo = Django(StartProjectSettings + "ASGI_APPLICATION = \"mysite.asgi.application\"\n", "django>=5.2\nchannels>=4\nuvicorn\n");
        var r = Analyze(repo);
        Assert.DoesNotContain("'uvicorn[standard]<1'", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.StartsWith("Channels serves WebSockets"));
    }

    [Fact]
    public void Django_asgi_py_alone_stays_on_gunicorn()
    {
        using var repo = Django();
        Assert.Equal("Python (Django)", Analyze(repo).Title);
    }

    [Fact]
    public void Cookiecutter_django_runs_manage_py_with_the_settings_its_wsgi_module_uses()
    {
        using var repo = new TempRepo()
            .With("manage.py", Manage.Replace("mysite.settings", "config.settings.local"))
            .With("config/settings/base.py", "INSTALLED_APPS = ['django.contrib.staticfiles']\nSTATIC_ROOT = '/app/staticfiles'\nDATABASES = {'default': env.db('DATABASE_URL')}\nALLOWED_HOSTS = []\n")
            .With("config/settings/local.py", "from .base import *\nDEBUG = True\n")
            .With("config/settings/production.py", "from .base import *  # noqa\nALLOWED_HOSTS = env.list('DJANGO_ALLOWED_HOSTS')\nSECURE_PROXY_SSL_HEADER = ('HTTP_X_FORWARDED_PROTO', 'https')\nMIDDLEWARE = ['whitenoise.middleware.WhiteNoiseMiddleware']\n")
            .With("config/wsgi.py", Wsgi.Replace("mysite.settings", "config.settings.production"))
            .With("requirements.txt", "-r requirements/production.txt\n")
            .With("requirements/base.txt", "django>=5.2\ndjango-environ\n")
            .With("requirements/local.txt", "-r base.txt\ndjango-debug-toolbar\n")
            .With("requirements/production.txt", "-r base.txt\ngunicorn\npsycopg[c]\n");
        var r = Analyze(repo);
        var dockerfile = r.Dockerfile!;

        Assert.Equal("Python (Django)", r.Title);
        Assert.Contains("exec gunicorn config.wsgi:application", dockerfile);
        Assert.Contains("ENV DJANGO_SETTINGS_MODULE=config.settings.production\n", dockerfile); // the build stage, for collectstatic
        Assert.Contains("DJANGO_SETTINGS_MODULE=config.settings.production", dockerfile[dockerfile.LastIndexOf("FROM ", StringComparison.Ordinal)..]);
        Assert.Contains(r.Evidence, e => e == "Django settings: config.settings.production");
        // production.py and the base.py it star-imports are what's checked, not local.py.
        Assert.DoesNotContain(r.Warnings, w => w.Contains("DEBUG") || w.Contains("ALLOWED_HOSTS") || w.Contains("whitenoise") || w.Contains("SECURE_PROXY"));
        Assert.Contains("FROM python:3.14 AS build", r.Dockerfile); // psycopg[c] compiles
        Assert.Contains("libpq5", r.Dockerfile);
    }

    [Fact]
    public void Django_one_folder_down_runs_from_there()
    {
        using var repo = new TempRepo()
            .With("requirements.txt", "django>=5.2\n")
            .With("src/manage.py", Manage)
            .With("src/mysite/settings.py", "DATABASES = {}\nALLOWED_HOSTS = ['*']\nCSRF_TRUSTED_ORIGINS = []\n")
            .With("src/mysite/wsgi.py", Wsgi);
        var r = Analyze(repo);

        Assert.Equal("Python (Django)", r.Title);
        Assert.Contains("python src/manage.py migrate --noinput && exec gunicorn mysite.wsgi:application --chdir src --bind", r.Dockerfile);
    }

    [Fact]
    public void A_flask_script_manage_py_is_not_django()
    {
        using var repo = new TempRepo()
            .With("manage.py", "from flask_script import Manager\nfrom app import app\nmanager = Manager(app)\n")
            .With("app.py", "from flask import Flask\napp = Flask(__name__)\n")
            .With("requirements.txt", "flask\nflask-script\n");
        Assert.Equal("Python (Flask)", Analyze(repo).Title);
    }

    [Fact]
    public void FastAPI_runs_on_uvicorn_trusting_the_tunnel()
    {
        using var repo = new TempRepo()
            .With("main.py", "from fastapi import FastAPI\n\napp = FastAPI(title='x')\n")
            .With("requirements.txt", "fastapi[standard]>=0.115\n");
        var r = Analyze(repo);

        Assert.Equal("Python (FastAPI)", r.Title);
        Assert.Equal(8000, r.ContainerPort);
        Assert.Contains("""CMD ["sh", "-c", "exec uvicorn main:app --host 0.0.0.0 --port ${PORT:-8000} --proxy-headers --forwarded-allow-ips='*'"]""", r.Dockerfile);
        // fastapi[standard] brings uvicorn along.
        Assert.Contains("pip install -r requirements.txt\n", r.Dockerfile);
        Assert.Contains("EXPOSE 8000", r.Dockerfile);
    }

    [Theory]
    [InlineData("app/main.py", "app.main:app")]
    [InlineData("app/__init__.py", "app:app")]
    [InlineData("api/index.py", "api.index:app")]
    [InlineData("server.py", "server:app")]
    public void FastAPI_app_module_is_found(string file, string spec)
    {
        using var repo = new TempRepo()
            .With(file, "from fastapi import FastAPI\nfrom .routes import router\napp: FastAPI = FastAPI()\napp.include_router(router)\n")
            .With("app/routes.py", "router = None")
            .With("tests/test_main.py", "from fastapi import FastAPI\ntest_app = FastAPI()\n")
            .With("requirements.txt", "fastapi\n");
        Assert.Contains($"exec uvicorn {spec} --host", Analyze(repo).Dockerfile);
    }

    [Fact]
    public void An_app_factory_runs_with_uvicorn_factory()
    {
        using var repo = new TempRepo()
            .With("app/main.py", "from fastapi import FastAPI\n\ndef create_app() -> FastAPI:\n    return FastAPI()\n")
            .With("requirements.txt", "fastapi\nuvicorn\n");
        var r = Analyze(repo);
        Assert.Contains("exec uvicorn app.main:create_app --factory --host 0.0.0.0", r.Dockerfile);
        Assert.DoesNotContain("'uvicorn[standard]<1'", r.Dockerfile);
    }

    [Fact]
    public void When_the_app_isnt_found_a_conventional_module_is_assumed_and_said()
    {
        using var repo = new TempRepo()
            .With("requirements.txt", "fastapi\n")
            .With("main.py", "import uvicorn\n");
        var r = Analyze(repo);
        Assert.Equal("Python (FastAPI)", r.Title);
        Assert.Contains("exec uvicorn main:app", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.StartsWith("Couldn't find where the FastAPI app is created, so `main:app` is assumed") && w.Contains("web: uvicorn <module>:<app>"));
    }

    [Theory]
    [InlineData("starlette", "from starlette.applications import Starlette\napp = Starlette()\n", "Starlette", "uvicorn app:app")]
    [InlineData("litestar", "from litestar import Litestar\napp = Litestar([])\n", "Litestar", "uvicorn app:app")]
    [InlineData("quart", "from quart import Quart\napp = Quart(__name__)\n", "Quart", "uvicorn app:app")]
    [InlineData("falcon", "import falcon\napp = falcon.App()\n", "Falcon", "gunicorn app:app")]
    [InlineData("falcon", "import falcon.asgi\napp = falcon.asgi.App()\n", "Falcon", "uvicorn app:app")]
    [InlineData("bottle", "from bottle import Bottle\napp = Bottle()\n", "Bottle", "gunicorn app:app")]
    [InlineData("bottle", "import bottle\napplication = bottle.default_app()\n", "Bottle", "gunicorn app:application")]
    [InlineData("flask", "from flask import Flask\napplication = Flask(__name__)\n", "Flask", "gunicorn app:application")]
    public void Each_framework_runs_on_its_server(string package, string code, string framework, string command)
    {
        using var repo = new TempRepo().With("app.py", code).With("requirements.txt", package + "\n");
        var r = Analyze(repo);
        Assert.Equal($"Python ({framework})", r.Title);
        Assert.Equal(8000, r.ContainerPort);
        Assert.Contains($"exec {command} ", r.Dockerfile);
    }

    [Fact]
    public void Starlette_listed_next_to_fastapi_is_fastapi()
    {
        using var repo = new TempRepo()
            .With("main.py", "from fastapi import FastAPI\napp = FastAPI()\n")
            .With("requirements.txt", "starlette==0.47.0\nfastapi==0.115.0\n");
        Assert.Equal("Python (FastAPI)", Analyze(repo).Title);
    }

    [Fact]
    public void Sanic_runs_its_own_server_and_trusts_one_proxy()
    {
        using var repo = new TempRepo()
            .With("server.py", "from sanic import Sanic\napp = Sanic('hello')\n")
            .With("requirements.txt", "sanic\n");
        var r = Analyze(repo);
        Assert.Equal("Python (Sanic)", r.Title);
        Assert.Contains("""exec sanic server:app --host=0.0.0.0 --port=${PORT:-8000}""", r.Dockerfile);
        Assert.Contains("SANIC_PROXIES_COUNT=1", r.Dockerfile);
        Assert.DoesNotContain("gunicorn", r.Dockerfile);
    }

    [Fact]
    public void A_flask_factory_is_called_by_gunicorn()
    {
        using var repo = new TempRepo()
            .With("flaskr/__init__.py", "from flask import Flask\n\ndef create_app(test_config=None):\n    app = Flask(__name__)\n    return app\n")
            .With("requirements.txt", "flask\n");
        var r = Analyze(repo);
        Assert.Equal("Python (Flask)", r.Title);
        Assert.Contains("exec gunicorn 'flaskr:create_app()' --bind 0.0.0.0:${PORT:-8000}", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e.Contains("gunicorn flaskr:create_app()"));
    }

    [Fact]
    public void A_wsgi_py_that_calls_the_factory_is_used_as_is()
    {
        using var repo = new TempRepo()
            .With("app/__init__.py", "from flask import Flask\n\ndef create_app():\n    return Flask(__name__)\n")
            .With("wsgi.py", "from app import create_app\n\napp = create_app()\n")
            .With("requirements.txt", "flask\n");
        Assert.Contains("exec gunicorn wsgi:app --bind", Analyze(repo).Dockerfile);
    }

    [Fact]
    public void A_gunicorn_config_file_keeps_its_say_over_workers_and_logs()
    {
        using var repo = new TempRepo()
            .With("app.py", "from flask import Flask\napp = Flask(__name__)\n")
            .With("gunicorn.conf.py", "workers = 4\naccesslog = '-'\n")
            .With("requirements.txt", "flask\ngunicorn\n");
        var r = Analyze(repo);
        Assert.Contains("""exec gunicorn app:app --bind 0.0.0.0:${PORT:-8000} --forwarded-allow-ips='*'"]""", r.Dockerfile);
    }

    [Fact]
    public void Aiohttp_and_tornado_run_their_own_server_on_the_port_in_their_code()
    {
        using var repo = new TempRepo()
            .With("app.py", "from aiohttp import web\napp = web.Application()\nif __name__ == '__main__':\n    web.run_app(app, port=int(os.getenv('PORT', '9000')))\n")
            .With("requirements.txt", "aiohttp\n");
        var r = Analyze(repo);
        Assert.Equal("Python (aiohttp)", r.Title);
        Assert.Equal(9000, r.ContainerPort);
        Assert.Contains("ENV PYTHONUNBUFFERED=1 PATH=/opt/venv/bin:$PATH PORT=9000", r.Dockerfile);
        Assert.Contains("""CMD ["python", "app.py"]""", r.Dockerfile);

        using var tornado = new TempRepo()
            .With("server.py", "import tornado.web\napp = tornado.web.Application([])\napp.listen(8888)\n")
            .With("requirements.txt", "tornado\n");
        var t = Analyze(tornado);
        Assert.Equal("Python (Tornado)", t.Title);
        Assert.Equal(8888, t.ContainerPort);
        Assert.Contains(t.Warnings, w => w.Contains("xheaders=True"));
    }

    [Fact]
    public void An_aiohttp_app_without_run_app_is_served_by_gunicorns_aiohttp_worker()
    {
        using var repo = new TempRepo()
            .With("main.py", "from aiohttp import web\n\nasync def create_app():\n    return web.Application()\n")
            .With("requirements.txt", "aiohttp\n");
        var r = Analyze(repo);
        Assert.Equal(8080, r.ContainerPort);
        Assert.Contains("exec gunicorn main:create_app --worker-class aiohttp.GunicornWebWorker --bind 0.0.0.0:${PORT:-8080}", r.Dockerfile);
    }

    [Fact]
    public void A_bot_that_uses_aiohttp_as_a_client_is_a_worker()
    {
        using var repo = new TempRepo()
            .With("bot.py", "import aiohttp\nimport discord\nclient = discord.Client()\nclient.run(os.environ['DISCORD_TOKEN'])\n")
            .With("requirements.txt", "discord.py\naiohttp\n");
        var r = Analyze(repo);
        Assert.Equal("Python worker (bot.py)", r.Title);
        Assert.Null(r.ContainerPort);
        Assert.DoesNotContain("EXPOSE", r.Dockerfile);
        Assert.DoesNotContain("PORT=", r.Dockerfile);
        Assert.Contains("""CMD ["python", "bot.py"]""", r.Dockerfile);
        Assert.Contains(r.EnvSuggestions, s => s.Key == "DISCORD_TOKEN" && s.IsSecret);
    }

    [Fact]
    public void Streamlit_runs_its_main_script_on_8501()
    {
        using var repo = new TempRepo()
            .With("Home.py", "import streamlit as st\nst.title('Home')\n")
            .With("pages/1_Stats.py", "import streamlit as st\n")
            .With("utils.py", "import streamlit as st\n")
            .With("requirements.txt", "streamlit\npandas\n");
        var r = Analyze(repo);
        Assert.Equal("Python (Streamlit)", r.Title);
        Assert.Equal(8501, r.ContainerPort);
        Assert.Contains("exec streamlit run Home.py --server.port=${PORT:-8501} --server.address=0.0.0.0 --server.headless=true", r.Dockerfile);
        Assert.Contains("STREAMLIT_BROWSER_GATHER_USAGE_STATS=false", r.Dockerfile);
    }

    [Fact]
    public void Gradio_listens_where_launch_is_told_and_warns_about_localhost()
    {
        using var repo = new TempRepo()
            .With("app.py", "import gradio as gr\ndemo = gr.Interface(fn=str, inputs='text', outputs='text')\ndemo.launch(server_name='127.0.0.1', server_port=7861)\n")
            .With("requirements.txt", "gradio\n");
        var r = Analyze(repo);
        Assert.Equal("Python (Gradio)", r.Title);
        Assert.Equal(7861, r.ContainerPort);
        Assert.Contains("GRADIO_SERVER_NAME=0.0.0.0", r.Dockerfile);
        Assert.Contains("export GRADIO_SERVER_PORT=${PORT:-7861} && exec python app.py", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.StartsWith("app.py listens on 127.0.0.1 only"));
    }

    [Fact]
    public void Gradio_mounted_in_a_fastapi_app_is_the_fastapi_app()
    {
        using var repo = new TempRepo()
            .With("main.py", "import gradio as gr\nfrom fastapi import FastAPI\napp = FastAPI()\napp = gr.mount_gradio_app(app, gr.Blocks(), path='/ui')\n")
            .With("requirements.txt", "gradio\nfastapi\n");
        Assert.Equal("Python (FastAPI)", Analyze(repo).Title);
    }

    [Fact]
    public void Dash_is_served_by_gunicorn_through_its_flask_server()
    {
        using var repo = new TempRepo()
            .With("app.py", "from dash import Dash, html\napp = Dash(__name__)\napp.layout = html.Div('x')\nserver = app.server\n")
            .With("requirements.txt", "dash\n");
        var r = Analyze(repo);
        Assert.Equal("Python (Dash)", r.Title);
        Assert.Equal(8050, r.ContainerPort);
        Assert.Contains("exec gunicorn app:server --bind 0.0.0.0:${PORT:-8050}", r.Dockerfile);

        using var dev = new TempRepo()
            .With("app.py", "from dash import Dash, html\napp = Dash(__name__)\nif __name__ == '__main__':\n    app.run(debug=True)\n")
            .With("requirements.txt", "dash\n");
        var d = Analyze(dev);
        Assert.Contains("""CMD ["python", "app.py"]""", d.Dockerfile);
        Assert.Contains("HOST=0.0.0.0", d.Dockerfile);
        Assert.Contains(d.Warnings, w => w.Contains("server = app.server"));
    }

    [Fact]
    public void A_dash_app_on_the_fastapi_backend_is_served_by_uvicorn()
    {
        using var repo = new TempRepo()
            .With("app.py", "from dash import Dash, html\napp = Dash(__name__, backend=\"fastapi\")\napp.layout = html.Div('x')\nserver = app.server\n")
            .With("requirements.txt", "dash[fastapi]\n");
        var r = Analyze(repo);
        Assert.Equal("Python (Dash)", r.Title);
        Assert.Contains("exec uvicorn app:server --host 0.0.0.0 --port ${PORT:-8050}", r.Dockerfile);
    }

    [Fact]
    public void A_procfile_web_line_is_the_start_command()
    {
        using var repo = new TempRepo()
            .With("Procfile", "release: python manage.py migrate\nweb: gunicorn mysite.wsgi --log-file -\nworker: celery -A mysite worker\n")
            .With("manage.py", Manage)
            .With("mysite/settings.py", "DATABASES = {}\nALLOWED_HOSTS = ['*']\nSECURE_PROXY_SSL_HEADER = None\n")
            .With("mysite/wsgi.py", Wsgi)
            .With("requirements.txt", "django\ngunicorn\ncelery\n");
        var r = Analyze(repo);

        Assert.Equal("Python (Django)", r.Title);
        Assert.Equal(8000, r.ContainerPort);
        // The release command runs instead of Gitberth's own migrate; gunicorn binds $PORT by itself.
        Assert.Contains("""CMD ["sh", "-c", "python manage.py migrate && exec gunicorn mysite.wsgi --log-file -"]""", r.Dockerfile);
        Assert.Contains("FORWARDED_ALLOW_IPS=*", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e.Contains("Procfile's web command"));
        Assert.Contains(r.Warnings, w => w.StartsWith("The Procfile also has worker"));
    }

    [Theory]
    [InlineData("web: gunicorn app:app --bind 0.0.0.0:5000", 5000)]
    [InlineData("web: uvicorn main:app --host 0.0.0.0 --port $PORT", 8000)]
    [InlineData("web: streamlit run app.py --server.port=8502", 8502)]
    [InlineData("web: python server.py", 8123)]
    public void A_procfile_commands_port_comes_from_the_command_or_the_file_it_runs(string procfile, int port)
    {
        using var repo = new TempRepo()
            .With("Procfile", procfile + "\n")
            .With("server.py", "from http.server import HTTPServer\nHTTPServer(('', 8123), None).serve_forever()\n")
            .With("requirements.txt", "requests\n");
        var r = Analyze(repo);
        Assert.Equal(port, r.ContainerPort);
        Assert.Equal("Python (Procfile)", r.Title);
    }

    [Fact]
    public void A_procfile_uvicorn_without_host_gets_one_and_gunicorn_is_installed_when_missing()
    {
        using var repo = new TempRepo()
            .With("Procfile", "web: uvicorn main:app --port $PORT\n")
            .With("main.py", "from fastapi import FastAPI\napp = FastAPI()\n")
            .With("requirements.txt", "fastapi\n");
        var r = Analyze(repo);
        Assert.Contains("UVICORN_HOST=0.0.0.0", r.Dockerfile);
        Assert.Contains("'uvicorn[standard]<1'", r.Dockerfile);
        Assert.Contains("exec uvicorn main:app --port $PORT", r.Dockerfile);
    }

    [Fact]
    public void A_procfile_with_only_a_worker_is_a_worker()
    {
        using var repo = new TempRepo()
            .With("Procfile", "worker: python -m bot\n")
            .With("bot/__main__.py", "print('hi')")
            .With("requirements.txt", "requests\n");
        var r = Analyze(repo);
        Assert.Equal("Python worker (Procfile)", r.Title);
        Assert.Null(r.ContainerPort);
        Assert.Contains("""CMD ["python", "-m", "bot"]""", r.Dockerfile);
    }

    [Fact]
    public void A_script_that_serves_http_gets_its_port()
    {
        using var repo = new TempRepo()
            .With("main.py", "import os\nfrom http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler\n\nif __name__ == '__main__':\n    port = int(os.environ.get('PORT', 8080))\n    ThreadingHTTPServer(('0.0.0.0', port), SimpleHTTPRequestHandler).serve_forever()\n");
        var r = Analyze(repo);
        Assert.Equal("Python (main.py)", r.Title);
        Assert.Equal(8080, r.ContainerPort);
        Assert.Contains("FROM python:3.14-slim\n", r.Dockerfile);
        Assert.DoesNotContain("AS build", r.Dockerfile); // nothing to install
        Assert.Contains("""CMD ["python", "main.py"]""", r.Dockerfile);
    }

    [Fact]
    public void A_main_py_without_a_main_block_is_a_module_not_an_entry()
    {
        using var repo = new TempRepo()
            .With("main.py", "def helper():\n    return 1\n")
            .With("requirements.txt", "requests\n");
        Assert.Equal(ProjectKind.Unknown, Analyze(repo).Kind);
    }

    [Fact]
    public void A_python_package_with_no_app_is_a_library_and_isnt_offered()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.0.0" } }""")
            .With("server.js", "")
            .With("sdk/pyproject.toml", "[build-system]\nrequires = ['hatchling']\nbuild-backend = 'hatchling.build'\n\n[project]\nname = 'acme-sdk'\nversion = '1.0.0'\ndependencies = ['httpx']\n")
            .With("sdk/src/acme_sdk/__init__.py", "")
            .With("sdk/src/acme_sdk/__main__.py", "print('cli')")
            .With("api/main.py", "from fastapi import FastAPI\napp = FastAPI()\n")
            .With("api/requirements.txt", "fastapi\n");
        var apps = new ProjectAnalyzer().FindApps(repo.Root);

        Assert.DoesNotContain(apps, a => a.Dir == "sdk");
        var api = Assert.Single(apps, a => a.Dir == "api");
        Assert.Equal(ProjectKind.Python, api.Kind);
        Assert.Equal("Python (FastAPI)", api.Title);
        Assert.Equal(ProjectKind.Unknown, Analyze(repo, appDir: "sdk").Kind);
    }

    [Fact]
    public void A_web_framework_beats_a_package_json_that_only_builds_assets()
    {
        using var repo = new TempRepo()
            .With("app.py", "from flask import Flask\napp = Flask(__name__)\n")
            .With("requirements.txt", "flask\n")
            .With("package.json", """{ "scripts": { "build": "tailwindcss -i src/in.css -o static/out.css --minify" }, "devDependencies": { "tailwindcss": "^4.0.0" } }""")
            .With("package-lock.json", "{}");
        var r = Analyze(repo);

        Assert.Equal("Python (Flask)", r.Title);
        Assert.StartsWith("FROM node:24-slim AS assets\n", r.Dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=npm,target=/root/.npm npm ci\n", r.Dockerfile);
        Assert.Contains("RUN npm run build && rm -rf node_modules\n", r.Dockerfile);
        Assert.Contains("COPY --from=assets /app ./\n# gitberth:build-args\nRUN --mount=type=cache,id=pip", r.Dockerfile);
        Assert.Equal(2, r.Dockerfile!.Split("# gitberth:build-args").Length - 1);
        Assert.Contains(r.Evidence, e => e.StartsWith("package.json has a build script"));
    }

    [Fact]
    public void A_node_server_beats_a_python_app_unless_the_hint_picks_python()
    {
        using var repo = new TempRepo()
            .With("app.py", "from flask import Flask\napp = Flask(__name__)\n")
            .With("requirements.txt", "flask\n")
            .With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.0.0" } }""")
            .With("server.js", "");
        Assert.Equal(ProjectKind.NodeServer, Analyze(repo).Kind);
        Assert.Equal(ProjectKind.Python, Analyze(repo, "python").Kind);

        var apps = new ProjectAnalyzer().FindApps(repo.Root);
        Assert.Contains(apps, a => a.Dir == "" && a.Hint == "python" && a.Kind == ProjectKind.Python);
        Assert.Contains(apps, a => a.Dir == "" && a.Hint is null && a.Kind == ProjectKind.NodeServer);
    }

    [Fact]
    public void Python_functions_in_api_next_to_a_front_end_leave_the_front_end_the_app()
    {
        // Vercel's layout: a Vite front end, and Python functions under api/.
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("api/index.py", "from flask import Flask\napp = Flask(__name__)\n")
            .With("requirements.txt", "flask\n");
        Assert.Equal(ProjectKind.NodeSpa, Analyze(repo).Kind);
        Assert.Contains("exec gunicorn api.index:app", Analyze(repo, "python").Dockerfile);
    }

    [Fact]
    public void A_plain_script_is_weak_so_a_node_app_in_the_same_folder_wins()
    {
        using var repo = new TempRepo()
            .With("main.py", "if __name__ == '__main__':\n    print('hi')\n")
            .With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""");
        Assert.Equal(ProjectKind.NodeSpa, Analyze(repo).Kind);
        Assert.Equal("Python worker (main.py)", Analyze(repo, "python").Title);
    }

    [Fact]
    public void A_dockerfile_still_wins()
    {
        using var repo = new TempRepo()
            .With("Dockerfile", "FROM python:3.13-slim\nEXPOSE 9000\n")
            .With("main.py", "from fastapi import FastAPI\napp = FastAPI()\n")
            .With("requirements.txt", "fastapi\n");
        Assert.Equal(ProjectKind.Dockerfile, Analyze(repo).Kind);
        Assert.Equal(ProjectKind.Python, Analyze(repo, "python").Kind);
    }

    [Fact]
    public void A_python_app_in_a_monorepo_builds_from_its_own_folder()
    {
        using var repo = new TempRepo()
            .With(".python-version", "3.12\n")
            .With("backend/main.py", "from fastapi import FastAPI\napp = FastAPI()\n")
            .With("backend/requirements.txt", "fastapi\n")
            .With("frontend/package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""");
        var r = Analyze(repo, appDir: "backend");

        Assert.Equal("backend", r.BuildContext);
        Assert.Contains("FROM python:3.12-slim AS build", r.Dockerfile); // from the repo root's .python-version
        Assert.Contains(r.Evidence, e => e == "Python 3.12 (the repo root's .python-version: 3.12)");
        var apps = new ProjectAnalyzer().FindApps(repo.Root);
        Assert.Contains(apps, a => a.Dir == "backend" && a.Kind == ProjectKind.Python);
    }

    [Fact]
    public void Env_vars_the_code_reads_are_suggested()
    {
        using var repo = new TempRepo()
            .With("main.py", """
                import os
                from os import getenv
                from fastapi import FastAPI
                app = FastAPI()
                DB = os.environ["DATABASE_URL"]
                REDIS = os.environ.get("REDIS_URL", "redis://localhost")
                KEY = os.getenv("STRIPE_API_KEY")
                MODE = getenv("APP_MODE")
                PORT = int(os.environ.get("PORT", 8000))
                PATH = os.environ["PYTHONPATH"]
                """)
            .With("settings.py", """
                import environ
                env = environ.Env(DEBUG=(bool, False))
                SECRET_KEY = env("SECRET_KEY")
                HOSTS = env.list("ALLOWED_HOSTS", default=[])
                DATABASES = {"default": env.db()}
                CACHES = {"default": env.cache_url()}
                """)
            .With("config.py", """
                from decouple import config
                SENTRY_DSN = config("SENTRY_DSN", default="")
                """)
            .With("app/settings.py", """
                from pydantic import Field
                from pydantic_settings import BaseSettings, SettingsConfigDict

                class Settings(BaseSettings):
                    model_config = SettingsConfigDict(env_prefix="ACME_")

                    openai_api_key: str
                    timeout: int = 30
                    region: str = Field("eu", alias="AWS_REGION")

                    def url(self) -> str:
                        return "x"
                """)
            .With("tests/test_x.py", "import os\nos.environ['TEST_ONLY']\n")
            .With("requirements.txt", "fastapi\n")
            .With(".env.example", "FROM_EXAMPLE=1\n");
        var env = Analyze(repo).EnvSuggestions.ToDictionary(s => s.Key);

        foreach (var key in new[] { "DATABASE_URL", "REDIS_URL", "STRIPE_API_KEY", "APP_MODE", "DEBUG", "SECRET_KEY", "ALLOWED_HOSTS", "CACHE_URL", "SENTRY_DSN", "ACME_OPENAI_API_KEY", "ACME_TIMEOUT", "AWS_REGION", "FROM_EXAMPLE" })
            Assert.True(env.ContainsKey(key), $"{key} wasn't suggested");
        Assert.Equal("used in main.py", env["DATABASE_URL"].Source);
        Assert.True(env["STRIPE_API_KEY"].IsSecret);
        Assert.Equal("", env["REDIS_URL"].Value);
        Assert.DoesNotContain("PORT", env.Keys);
        Assert.DoesNotContain("PYTHONPATH", env.Keys);
        Assert.DoesNotContain("TEST_ONLY", env.Keys);
    }
}
