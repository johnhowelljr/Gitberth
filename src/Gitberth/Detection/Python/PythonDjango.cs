using System.Text;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>A Django project: where manage.py is, which settings it runs with, and its WSGI and ASGI modules.</summary>
internal sealed partial class DjangoApp
{
    private static readonly string[] NestedFolders = ["src", "app", "backend", "server", "web", "project", "django"];

    private readonly PythonProject _p;

    private DjangoApp(PythonProject p) => _p = p;

    /// <summary>manage.py's folder: "" at the app's root, or a folder like src.</summary>
    public string Dir { get; private init; } = "";

    /// <summary>The settings the server runs with: the WSGI module's default when it has one, else manage.py's.</summary>
    public string SettingsModule { get; private init; } = "";

    /// <summary>The settings file and the ones it star-imports (base.py), for the checks.</summary>
    public string Settings { get; private init; } = "";

    public string? SettingsFile { get; private init; }

    /// <summary>Set as DJANGO_SETTINGS_MODULE when manage.py's default differs from the server's (cookiecutter-django's local vs production).</summary>
    public string? ManageSettings { get; private init; }

    public string WsgiApplication { get; private init; } = "";
    public string? AsgiApplication { get; private init; }

    /// <summary>Served with uvicorn: an asgi.py, plus Channels, Daphne or uvicorn in the dependencies (or ASGI_APPLICATION set).</summary>
    public bool Asgi { get; private init; }

    private string Manage => Dir.Length == 0 ? "python manage.py" : $"python {PythonApps.ShellQuote(Dir)}/manage.py";

    private string Shown => SettingsFile ?? "settings.py";

    /// <summary>The Django project in the folder: manage.py at its root, or one folder down when the dependencies are at the root.</summary>
    public static DjangoApp? Find(PythonProject p)
    {
        var dir = p.Files.Exists("manage.py") ? "" : p.HasDependencyFile ? NestedManage(p) : null;
        if (dir is null) return null;
        var manage = p.Text(RepoFiles.Join(dir, "manage.py")) ?? "";
        if (!manage.Contains("django", StringComparison.OrdinalIgnoreCase)) return null;

        var manageSettings = SettingsDefault().Match(manage) is { Success: true } m ? m.Groups["module"].Value : FindSettings(p, dir);
        var wsgi = ApplicationSetting(manageSettings is null ? "" : SettingsText(p, dir, manageSettings, out _), "WSGI_APPLICATION")
            ?? NextTo(p, dir, manageSettings, "wsgi") ?? AnyWsgi(p, dir);
        // gunicorn runs with the WSGI module's default settings, which may not be manage.py's.
        var wsgiSettings = wsgi is not null && ModuleFile(p, dir, Module(wsgi)) is { } wsgiFile && SettingsDefault().Match(p.Text(wsgiFile) ?? "") is { Success: true } w
            ? w.Groups["module"].Value : null;
        var effective = wsgiSettings ?? manageSettings ?? "settings";
        var settings = SettingsText(p, dir, effective, out var settingsFile);
        var asgi = ApplicationSetting(settings, "ASGI_APPLICATION")
            ?? (wsgi is not null ? NextTo(p, dir, wsgi, "asgi") : null) ?? NextTo(p, dir, effective, "asgi");
        var servesAsgi = asgi is not null && ModuleFile(p, dir, Module(asgi)) is not null
            && (new[] { "channels", "daphne", "uvicorn", "hypercorn" }.Any(p.Has) || ApplicationSetting(settings, "ASGI_APPLICATION") is not null);
        return new DjangoApp(p)
        {
            Dir = dir,
            SettingsModule = effective,
            Settings = settings,
            SettingsFile = settingsFile,
            ManageSettings = wsgiSettings is not null && manageSettings is not null && wsgiSettings != manageSettings ? manageSettings : null,
            WsgiApplication = wsgi ?? $"{effective.Split('.')[0]}.wsgi.application",
            AsgiApplication = asgi,
            Asgi = servesAsgi,
        };
    }

    /// <summary>The start command: gunicorn for WSGI, uvicorn for ASGI.</summary>
    public void Plan(PythonApp app)
    {
        app.Strong = true;
        app.Port = PythonApps.DefaultPort;
        var chdir = Dir.Length == 0 ? null : Dir;
        if (Asgi)
        {
            var spec = Spec(AsgiApplication!);
            app.Command = PythonApps.Uvicorn(spec, app.Port.Value, factory: false, appDir: chdir);
            app.Server = "uvicorn";
            app.Evidence.Add($"Serves ASGI with `uvicorn {spec}`, trusting Cloudflare Tunnel's X-Forwarded-* headers");
            if (_p.Has("channels") && _p.Dependency("uvicorn") is { } uvicorn && !uvicorn.HasExtra("standard") && !_p.Has("websockets") && !_p.Has("wsproto"))
                app.Warnings.Add("Channels serves WebSockets, which uvicorn only speaks with the websockets package. Depend on uvicorn[standard] instead of uvicorn.");
            return;
        }
        var wsgi = Spec(WsgiApplication);
        app.Command = PythonApps.Gunicorn(_p, wsgi, app.Port.Value, chdir);
        app.Server = "gunicorn";
        app.Evidence.Add($"Starts with `gunicorn {wsgi}`, trusting Cloudflare Tunnel's X-Forwarded-* headers");
        if (WsgiModuleMissing()) app.Warnings.Add($"Couldn't find the WSGI module, so `{wsgi}` is assumed. Set WSGI_APPLICATION in {Shown}, or add a Procfile with a web line.");
    }

    /// <summary>collectstatic at build time, migrations before the server starts, and what the settings show needs fixing.</summary>
    public void AddBuildAndChecks(PythonApp app)
    {
        if (Settings.Length == 0)
        {
            app.BuildSteps.Add($"RUN {Manage} collectstatic --noinput || echo \"collectstatic failed, so no static files were collected\"");
            app.Warnings.Add($"Couldn't find the settings module {SettingsModule}, so migrations aren't run before the app starts. Set DJANGO_SETTINGS_MODULE in manage.py, or customize the Dockerfile.");
            return;
        }
        app.Evidence.Add($"Django settings: {SettingsModule}");
        if (ManageSettings is not null)
        {
            app.EnvComments.Add($"# The settings the WSGI module defaults to, so manage.py's commands use them too (it defaults to {ManageSettings}).");
            app.Env.Add(new("DJANGO_SETTINGS_MODULE", SettingsModule));
            app.Evidence.Add($"manage.py defaults to {ManageSettings} but the server to {SettingsModule}, so DJANGO_SETTINGS_MODULE={SettingsModule} for both");
        }

        var staticFiles = Settings.Contains("django.contrib.staticfiles", StringComparison.Ordinal);
        if (staticFiles)
        {
            if (StaticRoot().IsMatch(Settings))
            {
                app.BuildSteps.Add("# When the settings need variables that only exist at run time, the static files are collected as the container starts instead.");
                app.BuildSteps.Add($"RUN {Manage} collectstatic --noinput || touch .collectstatic-at-start");
                app.CollectStaticAtStart = $"{Manage} collectstatic --noinput";
                app.Evidence.Add("Collects the static files at build time (collectstatic)");
            }
            else
            {
                app.BuildSteps.Add("# STATIC_ROOT isn't set, so this fails; the build carries on without collected static files.");
                app.BuildSteps.Add($"RUN {Manage} collectstatic --noinput || echo \"collectstatic failed, so no static files were collected\"");
                app.Warnings.Add($"STATIC_ROOT isn't set in {Shown}, so collectstatic can't gather the static files and the build carries on without them. Set STATIC_ROOT = BASE_DIR / \"staticfiles\".");
            }
            if (!_p.Has("whitenoise") && !Settings.Contains("whitenoise", StringComparison.OrdinalIgnoreCase))
                app.Warnings.Add("whitenoise isn't installed, so with DEBUG off nothing serves the static files (CSS, JavaScript, the admin's styles). Add whitenoise to the dependencies and \"whitenoise.middleware.WhiteNoiseMiddleware\" to MIDDLEWARE, right after SecurityMiddleware.");
        }

        var procfile = _p.Procfile;
        if (Databases().IsMatch(Settings) && procfile?.Release is null && procfile?.Web?.Contains("migrate", StringComparison.Ordinal) != true)
        {
            app.BeforeStart.Add($"{Manage} migrate --noinput");
            app.Evidence.Add("Applies the migrations before it starts (migrate)");
        }

        if (AllowedHostsEmpty(Settings))
            app.Warnings.Add($"ALLOWED_HOSTS is empty in {Shown}, so Django turns away every request that isn't for localhost (400 Bad Request). Add your domain and the host's LAN address, or read them from an environment variable.");
        if (!Settings.Contains("SECURE_PROXY_SSL_HEADER", StringComparison.Ordinal) && !Settings.Contains("CSRF_TRUSTED_ORIGINS", StringComparison.Ordinal))
            app.Warnings.Add($"{Shown} sets neither SECURE_PROXY_SSL_HEADER nor CSRF_TRUSTED_ORIGINS. Behind Cloudflare Tunnel, add SECURE_PROXY_SSL_HEADER = (\"HTTP_X_FORWARDED_PROTO\", \"https\") and CSRF_TRUSTED_ORIGINS = [\"https://<your domain>\"], so Django knows requests came over HTTPS and accepts forms posted from your domain.");
        if (Settings.Contains("django.db.backends.sqlite3", StringComparison.Ordinal) && !DatabaseUrl().IsMatch(Settings))
            app.Warnings.Add($"{Shown} keeps the database in SQLite inside the container, so each deploy starts with an empty one. Point DATABASES at Postgres, or put the SQLite file in a folder (like data/) and add a volume for it.");
        if (DebugOn().IsMatch(Settings))
            app.Warnings.Add($"DEBUG = True in {Shown} shows visitors error pages with your code and settings. Read it from an environment variable and leave it off in production.");
    }

    /// <summary>Whether every ALLOWED_HOSTS assignment is an empty list, and nothing adds to it.</summary>
    internal static bool AllowedHostsEmpty(string settings)
    {
        var assignments = AllowedHosts().Matches(settings);
        if (assignments.Count == 0 || AllowedHostsChanged().IsMatch(settings)) return false;
        return assignments.All(a => EmptyList().IsMatch(a.Groups["value"].Value));
    }

    private bool WsgiModuleMissing() => ModuleFile(_p, Dir, Module(WsgiApplication)) is null;

    // "mysite.wsgi.application" → "mysite.wsgi:application".
    private static string Spec(string application) => $"{Module(application)}:{application[(application.LastIndexOf('.') + 1)..]}";

    private static string Module(string application) => application.Contains('.') ? application[..application.LastIndexOf('.')] : application;

    private static string? NestedManage(PythonProject p)
    {
        var dirs = p.Files.Files
            .Where(f => f.Count(c => c == '/') == 1 && f.EndsWith("/manage.py", StringComparison.Ordinal) && PythonFiles.IsAppCode(p.Files, f))
            .Select(f => f[..f.IndexOf('/')])
            .Where(NodeScripts.IsSafePath)
            .ToList();
        return dirs.OrderBy(d => Array.IndexOf(NestedFolders, d) is var i and >= 0 ? i : NestedFolders.Length).ThenBy(d => d, StringComparer.Ordinal).FirstOrDefault();
    }

    // Without a DJANGO_SETTINGS_MODULE default in manage.py: a settings.py (or settings package) one folder down.
    private static string? FindSettings(PythonProject p, string dir)
    {
        var prefix = dir.Length == 0 ? "" : dir + "/";
        var depth = prefix.Count(c => c == '/') + 1;
        var file = p.Files.Files.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.Count(c => c == '/') == depth && f.EndsWith("/settings.py", StringComparison.Ordinal))
            ?? p.Files.Files.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.Count(c => c == '/') == depth + 1 && f.EndsWith("/settings/__init__.py", StringComparison.Ordinal));
        return file is null ? null : PythonFiles.ModuleName(file[prefix.Length..]);
    }

    /// <summary>The text of a settings module and of the modules it star-imports (from .base import *).</summary>
    private static string SettingsText(PythonProject p, string dir, string module, out string? file)
    {
        file = ModuleFile(p, dir, module);
        if (file is null) return "";
        var text = p.Text(file) ?? "";
        var all = new StringBuilder(text);
        var package = file.EndsWith("/__init__.py", StringComparison.Ordinal) ? module : module.Contains('.') ? module[..module.LastIndexOf('.')] : "";
        foreach (Match m in StarImport().Matches(text))
        {
            if (Resolve(package, m.Groups["module"].Value) is { } imported && ModuleFile(p, dir, imported) is { } importedFile && importedFile != file)
                all.Append('\n').Append(p.Text(importedFile));
        }
        return all.ToString();
    }

    // A relative import (".base", "..common") resolved against the package it's in.
    private static string? Resolve(string package, string target)
    {
        if (!target.StartsWith('.')) return target;
        var dots = target.TakeWhile(c => c == '.').Count();
        var parts = package.Length == 0 ? new List<string>() : package.Split('.').ToList();
        if (dots - 1 > parts.Count) return null;
        parts.RemoveRange(parts.Count - (dots - 1), dots - 1);
        var rest = target[dots..];
        if (rest.Length > 0) parts.Add(rest);
        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    /// <summary>The module's file under manage.py's folder (mysite/settings.py, or mysite/settings/__init__.py); null when there's none.</summary>
    private static string? ModuleFile(PythonProject p, string dir, string module)
    {
        var path = RepoFiles.Join(dir, module.Replace('.', '/'));
        return p.Files.Exists(path + ".py") ? path + ".py" : p.Files.Exists(path + "/__init__.py") ? path + "/__init__.py" : null;
    }

    // "mysite.wsgi.application" from WSGI_APPLICATION = "mysite.wsgi.application".
    private static string? ApplicationSetting(string settings, string name)
    {
        var m = Regex.Match(settings, $@"^{name}[ \t]*=[ \t]*['""](?<value>[\w.]+\.\w+)['""]", RegexOptions.Multiline);
        return m.Success ? m.Groups["value"].Value : null;
    }

    // The wsgi.py (or asgi.py) in the settings' package or a package above it: mysite/wsgi.py for mysite.settings,
    // config/wsgi.py for config.settings.production.
    private static string? NextTo(PythonProject p, string dir, string? module, string name)
    {
        if (module is null) return null;
        var parts = module.Split('.');
        for (var n = parts.Length - 1; n >= 0; n--)
        {
            var candidate = string.Join('.', parts.Take(n).Append(name));
            if (ModuleFile(p, dir, candidate) is not null) return candidate + ".application";
        }
        return null;
    }

    private static string? AnyWsgi(PythonProject p, string dir)
    {
        var prefix = dir.Length == 0 ? "" : dir + "/";
        var depth = prefix.Count(c => c == '/') + 1;
        var file = p.Files.Files.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.Count(c => c == '/') == depth
            && f.EndsWith("/wsgi.py", StringComparison.Ordinal) && (p.Text(f) ?? "").Contains("get_wsgi_application", StringComparison.Ordinal));
        return file is not null && PythonFiles.ModuleName(file[prefix.Length..]) is { } module ? module + ".application" : null;
    }

    [GeneratedRegex(@"DJANGO_SETTINGS_MODULE['""]\s*,\s*['""](?<module>[\w.]+)['""]")]
    private static partial Regex SettingsDefault();

    [GeneratedRegex(@"^from[ \t]+(?<module>\.*[\w.]*)[ \t]+import[ \t]+\*", RegexOptions.Multiline)]
    private static partial Regex StarImport();

    [GeneratedRegex(@"^ALLOWED_HOSTS[ \t]*(?::[^=\n]*)?=[ \t]*(?<value>[^\n]*)", RegexOptions.Multiline)]
    private static partial Regex AllowedHosts();

    [GeneratedRegex(@"ALLOWED_HOSTS[ \t]*\+=|ALLOWED_HOSTS\.(?:append|extend|insert)\(")]
    private static partial Regex AllowedHostsChanged();

    [GeneratedRegex(@"^(?:\[\s*\]|\(\s*\)|list\(\s*\))\s*(?:#.*)?$")]
    private static partial Regex EmptyList();

    [GeneratedRegex(@"^STATIC_ROOT\b", RegexOptions.Multiline)]
    private static partial Regex StaticRoot();

    [GeneratedRegex(@"^DATABASES\b", RegexOptions.Multiline)]
    private static partial Regex Databases();

    [GeneratedRegex(@"dj_database_url|DATABASE_URL|\.db\(|db_url\(")]
    private static partial Regex DatabaseUrl();

    [GeneratedRegex(@"^DEBUG[ \t]*=[ \t]*True\b", RegexOptions.Multiline)]
    private static partial Regex DebugOn();
}
