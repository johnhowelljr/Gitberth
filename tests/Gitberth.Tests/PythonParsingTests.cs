using Gitberth.Detection;

namespace Gitberth.Tests;

/// <summary>The pure helpers behind Python detection: TOML, requirements files, version ranges, ports and commands.</summary>
public class PythonParsingTests
{
    [Fact]
    public void Reads_the_toml_that_pyproject_files_use()
    {
        var doc = Toml.Parse("""
            # A comment
            [project]
            name = "demo"   # trailing comment
            requires-python = '>=3.11'
            dependencies = [
                "fastapi[standard]>=0.115",  # with a comment
                'httpx',
            ]
            [project.optional-dependencies]
            dev = ["pytest"]

            [tool.poetry.dependencies]
            python = "^3.12"
            uvicorn = { version = "^0.30", extras = ["standard"] }
            "quoted.key" = "1"
            [[tool.poetry.source]]
            name = "private"
            [[tool.poetry.source]]
            name = "other"
            [tool.uv]
            package = false
            workers = 4
            ratio = 0.5
            description = \"\"\"
            Line one \
              still one
            \"\"\"
            this line is broken
            after = "the broken line"
            """.Replace("\\\"", "\""));

        Assert.Equal("demo", Toml.String(doc, "project", "name"));
        Assert.Equal(">=3.11", Toml.String(doc, "project", "requires-python"));
        Assert.Equal(["fastapi[standard]>=0.115", "httpx"], Toml.Strings(doc, "project", "dependencies"));
        Assert.Equal(["pytest"], Toml.Strings(doc, "project", "optional-dependencies", "dev"));
        Assert.Equal("^3.12", Toml.String(doc, "tool", "poetry", "dependencies", "python"));
        Assert.Equal(["standard"], Toml.Strings(doc, "tool", "poetry", "dependencies", "uvicorn", "extras"));
        Assert.Equal("1", Toml.String(doc, "tool", "poetry", "dependencies", "quoted.key"));
        Assert.Equal(2, Assert.IsType<List<object?>>(Toml.Get(doc, "tool", "poetry", "source")).Count);
        Assert.False(Toml.Bool(doc, "tool", "uv", "package"));
        Assert.Equal(4L, Toml.Get(doc, "tool", "uv", "workers"));
        Assert.Equal(0.5, Toml.Get(doc, "tool", "uv", "ratio"));
        Assert.Equal("Line one still one\n", Toml.String(doc, "tool", "uv", "description"));
        Assert.Equal("the broken line", Toml.String(doc, "tool", "uv", "after"));
    }

    [Fact]
    public void Reads_a_pipfile()
    {
        var doc = Toml.Parse("[[source]]\nurl = \"https://pypi.org/simple\"\n\n[packages]\nflask = \"*\"\nrequests = {version = \"*\", extras = [\"socks\"]}\n\n[requires]\npython_version = \"3.12\"\n");
        var deps = PythonDependencies.FromTable(Toml.Table(doc, "packages")!).ToList();
        Assert.Equal(["flask", "requests"], deps.Select(d => d.Name));
        Assert.True(deps[1].HasExtra("socks"));
        Assert.Equal("3.12", Toml.String(doc, "requires", "python_version"));
    }

    [Theory]
    [InlineData("Django>=5.2,<6", "django", "")]
    [InlineData("fastapi[standard, all] >= 0.115 ; python_version >= '3.10'", "fastapi", "standard,all")]
    [InlineData("Flask_SQLAlchemy==3.1", "flask-sqlalchemy", "")]
    [InlineData("zope.interface", "zope-interface", "")]
    [InlineData("pkg @ https://example.com/pkg-1.0.tar.gz", "pkg", "")]
    public void Reads_requirement_specs(string spec, string name, string extras)
    {
        var dep = PythonDependencies.FromPep508(spec);
        Assert.NotNull(dep);
        Assert.Equal(name, dep.Name);
        Assert.Equal(extras, string.Join(',', dep.Extras));
    }

    [Fact]
    public void Reads_requirements_files_with_includes_options_and_links()
    {
        using var repo = new TempRepo()
            .With("requirements.txt", """
                # Production
                -r requirements/base.txt
                --index-url https://pypi.org/simple
                -c constraints.txt
                gunicorn==23.0.0 \
                    --hash=sha256:abc
                psycopg2==2.9.10  # compiled
                -e git+https://github.com/acme/lib.git@main#egg=acme-lib
                https://example.com/wheels/thing-1.0-py3-none-any.whl
                -e .
                """)
            .With("requirements/base.txt", "Django>=5\nwhitenoise[brotli]\n-r ../requirements.txt\n")
            .With("constraints.txt", "urllib3<3\n");
        var files = RepoFiles.Scan(repo.Root);

        var r = PythonDependencies.ReadRequirements(files, "requirements.txt");
        Assert.Equal(["django", "whitenoise", "gunicorn", "psycopg2", "acme-lib"], r.Dependencies.Select(d => d.Name));
        Assert.True(r.Dependencies[1].HasExtra("brotli"));
        Assert.True(r.InstallsProject);
        Assert.False(r.OnlyIncludes);

        using var includesOnly = new TempRepo().With("requirements.txt", "--index-url https://pypi.org/simple\n-r requirements/prod.txt\n").With("requirements/prod.txt", "flask\n");
        Assert.True(PythonDependencies.ReadRequirements(RepoFiles.Scan(includesOnly.Root), "requirements.txt").OnlyIncludes);
    }

    [Fact]
    public void Reads_install_requires_from_setup_py_and_setup_cfg()
    {
        var fromPy = PythonDependencies.FromSetup("setup(\n    name='x',\n    install_requires=[\n        'flask>=2',\n        \"gunicorn\",\n    ],\n)", null);
        Assert.Equal(["flask", "gunicorn"], fromPy.Select(d => d.Name));

        var fromCfg = PythonDependencies.FromSetup(null, "[metadata]\nname = x\n\n[options]\ninstall_requires =\n    django>=4\n    celery[redis]\npackages = find:\n");
        Assert.Equal(["django", "celery"], fromCfg.Select(d => d.Name));
    }

    [Theory]
    [InlineData(">=3.10", "3.14")]
    [InlineData(">=3.9,<3.13", "3.12")]
    [InlineData(">= 3.8, < 3.12", "3.11")]
    [InlineData("~=3.11", "3.14")]
    [InlineData("~=3.11.2", "3.11")]
    [InlineData("==3.12.*", "3.12")]
    [InlineData("==3.11", "3.11")]
    [InlineData("<3.13.4", "3.13")]
    [InlineData(">3.14", "3.14")]
    [InlineData(">=3.10,!=3.14.*", "3.13")]
    [InlineData(">=3.15", "3.15")]
    [InlineData(">=3.16", "3.16")]
    [InlineData("^3.11", "3.14")]
    [InlineData("~3.12", "3.12")]
    [InlineData(">=3.10 <3.12", "3.11")]
    [InlineData("3.10.*", "3.10")]
    [InlineData("<3.11 || >=3.13", "3.14")]
    [InlineData("*", "3.14")]
    public void Picks_the_python_release_a_range_allows(string spec, string expected) =>
        Assert.Equal(expected, PythonVersions.FromSpec(spec).ToString());

    [Theory]
    [InlineData("3.12.4", "3.12")]
    [InlineData("python-3.11.9", "3.11")]
    [InlineData("cpython-3.13.0-linux", "3.13")]
    [InlineData("3.13t", "3.13")]
    public void Reads_exact_versions(string text, string expected) => Assert.Equal(expected, PythonVersions.FromExact(text).ToString());

    [Fact]
    public void Warns_about_end_of_life_and_unknown_releases()
    {
        var today = new DateOnly(2026, 11, 4);
        Assert.Empty(PythonVersions.Warnings(new PyVersion(3, 11), today));
        Assert.Equal("Python 3.10 is past end-of-life (October 2026) and gets no security fixes. Consider moving to Python 3.14.", Assert.Single(PythonVersions.Warnings(new PyVersion(3, 10), today)));
        Assert.Single(PythonVersions.Warnings(new PyVersion(3, 11), new DateOnly(2027, 10, 1)));
        Assert.Contains("newer than Gitberth knows", Assert.Single(PythonVersions.Warnings(new PyVersion(3, 16), today)));
        Assert.Contains("needs Python 3", Assert.Single(PythonVersions.Warnings(new PyVersion(2, 7), today)));
    }

    [Theory]
    [InlineData("gunicorn app:app", "exec gunicorn app:app")]
    [InlineData("python manage.py migrate && gunicorn x.wsgi", "python manage.py migrate && exec gunicorn x.wsgi")]
    [InlineData("python -c \"print('a && b')\" && gunicorn x", "python -c \"print('a && b')\" && exec gunicorn x")]
    [InlineData("exec uvicorn main:app", "exec uvicorn main:app")]
    [InlineData("bin/setup; gunicorn x", "bin/setup; gunicorn x")]
    [InlineData("gunicorn x | tee log", "gunicorn x | tee log")]
    public void Execs_the_last_command_of_a_chain(string script, string expected) =>
        Assert.Equal(expected, PythonDockerfiles.WithExec(script));

    [Theory]
    [InlineData("web.run_app(app, port=int(os.environ.get('PORT', 8080)))", 8080)]
    [InlineData("port = int(os.environ.get(\"PORT\") or 5000)", 5000)]
    [InlineData("PORT = 8765\nhttpd = HTTPServer(('', PORT), Handler)", 8765)]
    [InlineData("app.listen(8888)", 8888)]
    [InlineData("server = make_server('', 8001, app)", 8001)]
    [InlineData("demo.launch(server_port=7000)", null)]
    [InlineData("print('hello')", null)]
    public void Reads_the_port_python_code_listens_on(string code, int? port) => Assert.Equal(port, PythonApps.PortInCode(code));

    [Theory]
    [InlineData("gunicorn app:app -b 0.0.0.0:5000", 5000)]
    [InlineData("gunicorn app:app --bind=:7000 --workers 3", 7000)]
    [InlineData("uvicorn main:app --port 9000", 9000)]
    [InlineData("gunicorn app:app --bind 0.0.0.0:$PORT", null)]
    public void Reads_the_port_a_command_listens_on(string command, int? port) => Assert.Equal(port, PythonApps.PortInCommand(command));

    [Fact]
    public void Reads_a_procfile()
    {
        var procfile = Procfile.Parse("# processes\nweb: gunicorn app:app\nrelease: python manage.py migrate\nclock: python clock.py\nworker:   celery -A app worker\n")!;
        Assert.Equal("gunicorn app:app", procfile.Web);
        Assert.Equal("python manage.py migrate", procfile.Release);
        Assert.Equal("worker", procfile.Background!.Value.Key);
        Assert.Null(Procfile.Parse("# nothing here\n"));
    }

    [Theory]
    [InlineData("[project]\nname = 'api'\n", "api/__init__.py", false)]
    [InlineData("[build-system]\nrequires = ['hatchling']\n[project]\nname = 'my-api'\n", "src/my_api/__init__.py", true)]
    [InlineData("[build-system]\nrequires = ['hatchling']\n[project]\nname = 'my-api'\n", "main.py", false)]
    [InlineData("[build-system]\nrequires = ['setuptools']\n[project]\nname = 'x'\n[tool.setuptools]\npackages = ['app']\n", "app/__init__.py", true)]
    [InlineData("[build-system]\nrequires = ['poetry-core']\n[tool.poetry]\nname = 'api'\npackage-mode = false\n", "api/__init__.py", false)]
    [InlineData("[project]\nname = 'x'\n[tool.uv]\npackage = true\n", "main.py", true)]
    public void Tells_packages_from_apps(string pyproject, string file, bool package)
    {
        using var repo = new TempRepo().With("pyproject.toml", pyproject).With(file, "");
        var files = RepoFiles.Scan(repo.Root);
        Assert.Equal(package, PythonProject.IsPackageProject(Toml.Parse(pyproject), files));
    }

    [Fact]
    public void Reads_pydantic_settings_fields()
    {
        const string code = """
            from typing import ClassVar
            from pydantic_settings import BaseSettings

            class Settings(BaseSettings):
                database_url: str
                debug: bool = False
                _private: str = "x"
                VERSION: ClassVar[str] = "1"

                class Config:
                    env_prefix = "SHOP_"

            class Other:
                not_a_setting: str
            """;
        Assert.Equal(["SHOP_DATABASE_URL", "SHOP_DEBUG"], PythonEnv.PydanticSettings(code));
    }

    [Fact]
    public void Django_allowed_hosts_counts_as_empty_only_when_nothing_fills_it()
    {
        Assert.True(DjangoApp.AllowedHostsEmpty("ALLOWED_HOSTS = []\n"));
        Assert.True(DjangoApp.AllowedHostsEmpty("ALLOWED_HOSTS: list[str] = []  # fill me\n"));
        Assert.False(DjangoApp.AllowedHostsEmpty("ALLOWED_HOSTS = []\nALLOWED_HOSTS += ['x']\n"));
        Assert.False(DjangoApp.AllowedHostsEmpty("ALLOWED_HOSTS = []\nALLOWED_HOSTS = os.environ['HOSTS'].split(',')\n"));
        Assert.False(DjangoApp.AllowedHostsEmpty("DEBUG = True\n"));
    }
}
