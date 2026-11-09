using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>
/// PHP apps: Laravel, Symfony, Slim, other Composer apps, and plain PHP (an index.php in the app's folder or in public/).
/// They run in Apache with mod_php (see <see cref="PhpDockerfiles"/>).
/// </summary>
internal sealed partial class PhpLanguage : ILanguage
{
    public ProjectKind Kind => ProjectKind.Php;

    public string Hint => "php";

    public IReadOnlyList<string> Markers { get; } = ["composer.json", "index.php"];

    private static readonly (string Package, string Name)[] Frameworks =
    [
        ("laravel/framework", "Laravel"), ("symfony/framework-bundle", "Symfony"), ("slim/slim", "Slim"),
        ("cakephp/cakephp", "CakePHP"), ("codeigniter4/framework", "CodeIgniter"), ("yiisoft/yii2", "Yii"),
    ];

    /// <summary>
    /// What a framework's apps commonly need, installed up front so that pointing the app at MySQL, Postgres or Redis
    /// through its variables needs no other image. Laravel's Redis client defaults to the phpredis extension.
    /// </summary>
    private static readonly Dictionary<string, string[]> FrameworkExtensions = new(StringComparer.Ordinal)
    {
        ["Laravel"] = ["bcmath", "intl", "pdo_mysql", "pdo_pgsql", "redis", "zip"],
        ["Symfony"] = ["intl", "pdo_mysql", "pdo_pgsql", "zip"],
    };

    /// <summary>Compiled into the official php images, so there's nothing to install.</summary>
    private static readonly HashSet<string> BuiltIn = new(StringComparer.Ordinal)
    {
        "core", "ctype", "curl", "date", "dom", "fileinfo", "filter", "ftp", "hash", "iconv", "json", "libxml", "mbstring", "mysqlnd",
        "openssl", "pcre", "pdo", "pdo_sqlite", "phar", "posix", "random", "readline", "reflection", "session", "simplexml", "sodium",
        "spl", "sqlite3", "standard", "tokenizer", "xml", "xmlreader", "xmlwriter", "zlib",
    };

    // Set by Gitberth's image, or the web server's own request variables ($_SERVER['HTTP_HOST'] and friends).
    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.Ordinal)
    {
        "APP_ENV", "APP_DEBUG", "LOG_CHANNEL", "PORT", "APP_UID", "HTTPS", "PATH", "HOME", "USER", "UNIQUE_ID", "PHP_SELF", "SHELL_VERBOSITY",
    };

    public LanguageMatch? Detect(string repoRoot, string appDir, RepoFiles files, AnalyzeOptions options)
    {
        var folder = appDir.Length == 0 ? repoRoot : Path.Combine(repoRoot, appDir.Replace('/', Path.DirectorySeparatorChar));
        // public/ of the PHP app in the folder above is that app's document root, not an app of its own.
        if (IsDocRootOfParent(repoRoot, appDir)) return null;
        var r = new DetectionResult { Kind = ProjectKind.Php, ContainerPort = PhpDockerfiles.Port };
        if (PhpApp.Load(folder, r.Warnings) is not { } app) return null;
        // An index.php in a folder of a bigger PHP site (a WordPress theme, a "silence is golden" placeholder) is part of it.
        if (!app.Composer && InsidePhpSite(repoRoot, appDir)) return null;
        bool Exists(string path) => File.Exists(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));

        var framework = Frameworks.FirstOrDefault(f => app.Has(f.Package)).Name;
        var docRoot = framework is "Laravel" or "Symfony" or "Slim" or "CodeIgniter" && Exists("public/index.php") ? "public" : PhpApp.DocRoot(folder);
        // A library or a command-line tool: nothing for a web server to serve.
        if (docRoot is null) return null;

        r.Title = framework is null ? "PHP" : $"PHP ({framework})";
        r.DockerIgnore = PhpDockerfiles.Ignore(app.Composer, framework);
        var (version, versionSource, versionWarning) = PhpVersions.Resolve(app);
        r.Evidence.Add(app.Composer
            ? $"Found composer.json; packages install with Composer{(app.Locked ? " from composer.lock" : "")}, without require-dev"
            : $"Found {(docRoot.Length == 0 ? "" : docRoot + "/")}index.php and no composer.json, so there's nothing to install");
        r.Evidence.Add($"PHP {version} ({versionSource}), served by Apache with mod_php from {(docRoot.Length == 0 ? "the app's folder" : docRoot + "/")} on port {PhpDockerfiles.Port}");
        if (versionWarning is not null) r.Warnings.Add(versionWarning);
        r.Warnings.AddRange(PhpVersions.Warnings(version));
        if (app.Composer && !app.Locked) r.Warnings.Add("No composer.lock is committed, so each build installs the newest versions composer.json allows.");

        var extensions = new SortedSet<string>(StringComparer.Ordinal);
        if (framework is not null && FrameworkExtensions.TryGetValue(framework, out var common)) extensions.UnionWith(common);
        extensions.UnionWith(app.RequiredExtensions);
        extensions.UnionWith(ExtensionsUsedIn(files));
        extensions.ExceptWith(BuiltIn);
        // OPcache is part of PHP from 8.5; before that the images ship it turned off.
        if (Compare(version, "8.5") < 0) extensions.Add("opcache");
        else extensions.Remove("opcache");
        r.Evidence.Add($"Extensions: {string.Join(", ", extensions)}");

        var steps = new List<string>();
        var node = NodeAssets.Detect(folder, r) is { HasBuild: true } pkg ? pkg : null;
        if (node is not null)
        {
            node.Describe(r, "the front-end assets");
            steps.Add(node.Build);
        }

        var env = new List<string>();
        var beforeInstall = new List<string>();
        List<string>? start = null;
        string? startComment = null;
        switch (framework)
        {
            case "Laravel":
                env.AddRange(["APP_ENV=production", "LOG_CHANNEL=stderr"]);
                // Composer's scripts write to bootstrap/cache, and Laravel needs these to run; git doesn't keep empty folders.
                beforeInstall.Add("RUN mkdir -p bootstrap/cache storage/app/public storage/framework/cache/data storage/framework/sessions storage/framework/views storage/logs");
                start =
                [
                    "php artisan migrate --force",
                    "php artisan storage:link || true",
                    "php artisan config:cache || echo \"Gitberth: config:cache failed, so the app runs without a config cache\"",
                    "php artisan route:cache || echo \"Gitberth: route:cache failed, so the app runs without a route cache\"",
                    "php artisan view:cache || echo \"Gitberth: view:cache failed, so the app runs without a view cache\"",
                ];
                startComment = "Laravel's deploy steps run as the container starts, when the database is reachable and the caches can hold its settings.";
                LaravelAdvice(app, r, folder);
                break;
            case "Symfony":
                env.AddRange(["APP_ENV=prod", "APP_DEBUG=0"]);
                if (app.Has("symfony/flex") && File.Exists(Path.Combine(folder, ".env")))
                {
                    // Compiles the .env files into .env.local.php, so they aren't parsed on every request.
                    steps.Add("RUN composer dump-env prod");
                }
                if (app.Has("symfony/asset-mapper"))
                {
                    if (Exists("importmap.php")) steps.Add("RUN php bin/console importmap:install");
                    steps.Add("RUN php bin/console asset-map:compile");
                    r.Evidence.Add("Compiles the AssetMapper assets into public/assets while building");
                }
                if (Exists("bin/console")) steps.Add("RUN php bin/console cache:clear");
                if (app.Has("doctrine/doctrine-migrations-bundle"))
                {
                    start = ["php bin/console doctrine:migrations:migrate --no-interaction --allow-no-migration"];
                    startComment = "Doctrine's migrations run as the container starts, when the database is reachable.";
                    r.Evidence.Add("Runs the Doctrine migrations as the container starts");
                    r.Warnings.Add("The database must be reachable when the container starts, because the Doctrine migrations run first: set DATABASE_URL.");
                }
                if (!SymfonySecretSet(folder)) r.Warnings.Add("Symfony needs APP_SECRET: set it as a secret, to a long random string (`php -r 'echo bin2hex(random_bytes(32));'` prints one).");
                break;
        }
        r.Evidence.Add("Apache sets HTTPS and the visitor's address from Cloudflare Tunnel's headers, so the app needs no trusted-proxy settings");
        if (start is not null) r.Evidence.Add($"Starts with: {string.Join(", then ", start.Select(s => $"`{s.Split(" || ")[0]}`"))}, then Apache");

        r.Dockerfile = PhpDockerfiles.Render(new PhpPlan(version, extensions.ToList(), env, app.Composer, node, beforeInstall, steps,
            docRoot, FrontController: framework is not null, start, startComment));
        return new LanguageMatch(r, Strong: true);
    }

    /// <summary>What a Laravel app needs that Gitberth can't do for it.</summary>
    private static void LaravelAdvice(PhpApp app, DetectionResult r, string folder)
    {
        var laravel = app.VersionOf("laravel/framework");
        // After "Found composer.json" and the PHP version.
        if (laravel is { } v) r.Evidence.Insert(Math.Min(2, r.Evidence.Count), $"Laravel {v.Major}");
        r.Evidence.Add("Logs go to the container's output (LOG_CHANNEL=stderr)");
        r.Warnings.Add("Laravel needs APP_KEY: set it as a secret. `php artisan key:generate --show` prints one, or use base64: followed by 32 random bytes in base64 (`openssl rand -base64 32`).");

        var example = RubyApp.Read(Path.Combine(folder, ".env.example")) is { } text
            ? DotEnv.Parse(text).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value)
            : new Dictionary<string, string>();
        // .env.example's values become suggested variables, and a kept development value would override the image's.
        var dev = new List<string>();
        if (example.TryGetValue("APP_ENV", out var appEnv) && appEnv != "production") dev.Add("APP_ENV to production");
        if (example.TryGetValue("APP_DEBUG", out var debug) && debug.Equals("true", StringComparison.OrdinalIgnoreCase)) dev.Add("APP_DEBUG to false");
        if (example.TryGetValue("LOG_CHANNEL", out var log) && log != "stderr") dev.Add("LOG_CHANNEL to stderr");
        if (dev.Count > 0)
            r.Warnings.Add($"The suggested variables come from .env.example, which is set up for development: change {string.Join(", ", dev)} before deploying.");

        // Laravel 11 made SQLite the default database.
        var connection = example.GetValueOrDefault("DB_CONNECTION")
            ?? (RubyApp.Read(Path.Combine(folder, "config", "database.php")) is { } config && DefaultConnection().Match(config) is { Success: true } m ? m.Groups[1].Value : null)
            ?? (laravel is { Major: < 11 } ? "mysql" : "sqlite");
        if (connection == "sqlite")
            r.Warnings.Add("The database is SQLite in database/database.sqlite, inside the container, so it's lost on every deploy. Set DB_CONNECTION and the DB_ variables for MySQL or Postgres, or keep SQLite in a volume: add one at /var/www/html/storage/database and set DB_DATABASE=/var/www/html/storage/database/database.sqlite.");
        else
            r.Warnings.Add("The database must be reachable when the container starts, because `php artisan migrate --force` runs first: set the DB_ variables.");
    }

    /// <summary>Whether .env (Symfony commits one) gives APP_SECRET a value, or the app doesn't read it at all.</summary>
    private static bool SymfonySecretSet(string folder)
    {
        var env = RubyApp.Read(Path.Combine(folder, ".env"));
        if (env is not null && DotEnv.Parse(env).Any(p => p.Key == "APP_SECRET" && p.Value.Length > 0)) return true;
        var framework = RubyApp.Read(Path.Combine(folder, "config", "packages", "framework.yaml")) ?? "";
        return File.Exists(Path.Combine(folder, "config", "packages", "framework.yaml")) && !framework.Contains("APP_SECRET", StringComparison.Ordinal);
    }

    /// <summary>A docroot folder (public/, web/ …) whose parent folder holds a Composer app.</summary>
    internal static bool IsDocRootOfParent(string repoRoot, string appDir)
    {
        if (appDir.Length == 0) return false;
        var slash = appDir.LastIndexOf('/');
        var name = appDir[(slash + 1)..];
        if (name.Length == 0 || !PhpApp.DocRoots.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        var parent = Path.Combine(repoRoot, (slash < 0 ? "" : appDir[..slash]).Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(Path.Combine(parent, "composer.json")) || File.Exists(Path.Combine(parent, "artisan"));
    }

    /// <summary>Whether a folder above this one, up to the repo root, holds a PHP app (an index.php or composer.json).</summary>
    internal static bool InsidePhpSite(string repoRoot, string appDir)
    {
        var parts = appDir.Length == 0 ? [] : appDir.Split('/');
        for (var up = parts.Length - 1; up >= 0; up--)
        {
            var above = Path.Combine([repoRoot, .. parts.Take(up)]);
            if (File.Exists(Path.Combine(above, "index.php")) || File.Exists(Path.Combine(above, "composer.json"))) return true;
        }
        return false;
    }

    /// <summary>Extensions the app's own code uses (mysqli, PDO's mysql: and pgsql: DSNs, GD, ZipArchive …).</summary>
    internal static IEnumerable<string> ExtensionsUsedIn(RepoFiles files)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in files.WithExtension(".php").Where(f => !IsTestPath(f)).Take(2000))
        {
            var text = RubyApp.Read(files.Full(rel));
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match m in ExtensionUse().Matches(text))
            {
                var group = m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success);
                if (group is not null) found.Add(group.Name);
            }
        }
        return found;
    }

    public void ScanEnv(RepoFiles files, Action<string, string, string> add)
    {
        if (files.Exists("artisan")) add("APP_KEY", "", "Laravel encrypts cookies and sessions with it: `php artisan key:generate --show` prints one");
        foreach (var rel in files.WithExtension(".php", ".yaml", ".yml", ".xml").Where(f => !IsTestPath(f)).Take(3000))
        {
            var text = RubyApp.Read(files.Full(rel));
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match m in SymfonyEnv().Matches(text))
            {
                // %env(resolve:DATABASE_URL)%: processors come first, the variable last.
                var key = m.Groups[1].Value.Split(':')[^1];
                if (!IgnoredKeys.Contains(key)) add(key, "", $"used in {rel}");
            }
            if (!rel.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match m in PhpEnv().Matches(text))
            {
                var key = m.Groups["key"].Value;
                if (IgnoredKeys.Contains(key) || (m.Groups["server"].Success && ServerVariable().IsMatch(key))) continue;
                add(key, "", $"used in {rel}");
            }
            // env('X', 'default') is how Laravel's config files read the environment.
            if (!rel.StartsWith("config/", StringComparison.Ordinal)) continue;
            foreach (Match m in LaravelEnv().Matches(text))
            {
                var key = m.Groups["key"].Value;
                if (IgnoredKeys.Contains(key)) continue;
                var fallback = m.Groups["default"].Success ? m.Groups["default"].Value.Trim('\'', '"') : "";
                add(key, "", fallback.Length == 0 ? $"used in {rel}" : $"used in {rel} (default {fallback})");
            }
        }
    }

    private static bool IsTestPath(string path) =>
        path.StartsWith("tests/", StringComparison.Ordinal) || path.StartsWith("test/", StringComparison.Ordinal) || path.Contains("/tests/", StringComparison.Ordinal);

    private static int Compare(string a, string b)
    {
        static (int, int) Parse(string v) => (int.Parse(v.Split('.')[0], CultureInfo.InvariantCulture), int.Parse(v.Split('.')[1], CultureInfo.InvariantCulture));
        return Parse(a).CompareTo(Parse(b));
    }

    [GeneratedRegex(@"'default'\s*=>\s*env\(\s*'DB_CONNECTION'\s*,\s*'(\w+)'")]
    private static partial Regex DefaultConnection();

    [GeneratedRegex(@"(?<mysqli>\bmysqli_\w+\(|\bnew\s+\\?mysqli\b)|(?<pdo_mysql>[""']mysql:)|(?<pdo_pgsql>[""']pgsql:)|(?<pgsql>\bpg_connect\()|(?<gd>\bimage(?:create\w*|jpeg|png|webp|copyresampled)\()|(?<zip>\bZipArchive\b)|(?<bcmath>\bbc(?:add|sub|mul|div|mod|pow|sqrt|comp|scale)\()|(?<redis>\bnew\s+\\?Redis\()|(?<intl>\b(?:NumberFormatter|IntlDateFormatter|Collator)\b)|(?<exif>\bexif_read_data\()|(?<gmp>\bgmp_\w+\()|(?<soap>\bSoap(?:Client|Server)\b)|(?<imagick>\bnew\s+\\?Imagick\b)")]
    private static partial Regex ExtensionUse();

    [GeneratedRegex(@"%env\(([A-Za-z0-9_:\-]+)\)%")]
    private static partial Regex SymfonyEnv();

    [GeneratedRegex(@"(?:\bgetenv\(\s*|\$_ENV\[\s*|(?<server>\$_SERVER\[\s*))[""'](?<key>[A-Z][A-Z0-9_]*)[""']")]
    private static partial Regex PhpEnv();

    [GeneratedRegex(@"\benv\(\s*[""'](?<key>[A-Za-z_][A-Za-z0-9_]*)[""']\s*(?:,\s*(?<default>'[^'\n]*'|""[^""\n]*""|-?\d+(?:\.\d+)?|true|false))?")]
    private static partial Regex LaravelEnv();

    [GeneratedRegex(@"^(?:HTTP|REQUEST|SERVER|REMOTE|SCRIPT|PHP|CONTENT|PATH|QUERY|DOCUMENT|GATEWAY|REDIRECT|ORIG|CONTEXT|AUTH)_")]
    private static partial Regex ServerVariable();
}
