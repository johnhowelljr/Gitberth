using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <summary>
/// Ruby apps: Rails, Hanami, Sinatra, Roda and any other Rack app (config.ru), and apps whose Procfile says how they
/// start. Gems install with Bundler on the ruby slim image, and the app's own server listens on $PORT.
/// </summary>
internal sealed partial class RubyLanguage : ILanguage
{
    public ProjectKind Kind => ProjectKind.Ruby;

    public string Hint => "ruby";

    public IReadOnlyList<string> Markers { get; } = ["Gemfile", "config.ru"];

    /// <summary>
    /// Gems whose native extensions need a system library: what they compile against, and what the app then runs with
    /// (for the databases, what Rails' own Dockerfile installs). image_processing pulls in mini_magick as well as
    /// ruby-vips, so ImageMagick only comes for a Gemfile that asks for mini_magick itself.
    /// </summary>
    private static readonly (string Gem, string Build, string Runtime, bool OnlyWhenListed)[] NativeGems =
    [
        ("pg", "libpq-dev", "postgresql-client", false),
        ("mysql2", "default-libmysqlclient-dev", "default-mysql-client", false),
        ("trilogy", "", "default-mysql-client", false),
        ("sqlite3", "libsqlite3-dev", "sqlite3", false),
        ("ruby-vips", "", "libvips", false),
        ("mini_magick", "", "imagemagick", true),
    ];

    /// <summary>Gems that hook the front end's build into assets:precompile, which then runs it.</summary>
    private static readonly string[] AssetHooks = ["jsbundling-rails", "cssbundling-rails", "vite_rails", "vite_ruby", "shakapacker", "webpacker"];

    private static readonly string[] SinatraScripts = ["app.rb", "main.rb", "server.rb", "application.rb", "web.rb"];

    // Gitberth sets these, or they only tune the server; the scan doesn't suggest them.
    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.Ordinal)
    {
        "RAILS_ENV", "RACK_ENV", "APP_ENV", "HANAMI_ENV", "PORT", "BINDING", "PIDFILE", "RAILS_LOG_TO_STDOUT", "RAILS_SERVE_STATIC_FILES",
        "RAILS_MAX_THREADS", "RAILS_MIN_THREADS", "WEB_CONCURRENCY", "RAILS_LOG_LEVEL", "SECRET_KEY_BASE_DUMMY", "BUNDLE_GEMFILE",
        "SOLID_QUEUE_IN_PUMA", "JOB_CONCURRENCY", "MALLOC_ARENA_MAX", "RAILS_MASTER_KEY", "HOME", "PATH", "USER", "CI",
    };

    public LanguageMatch? Detect(string repoRoot, string appDir, RepoFiles files, AnalyzeOptions options)
    {
        var folder = appDir.Length == 0 ? repoRoot : Path.Combine(repoRoot, appDir.Replace('/', Path.DirectorySeparatorChar));
        if (RubyApp.Load(folder) is not { } app) return null;
        // RepoFiles leaves out bin/, where Rails keeps bin/rails, so these look at the folder itself.
        bool Exists(string path) => File.Exists(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
        string? Text(string path) => RubyApp.Read(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));

        var rails = Exists("config/application.rb") && (app.InBundle("rails") || app.InBundle("railties"));
        var rack = Exists("config.ru");
        var hanami = !rails && rack && app.InBundle("hanami");
        var web = app.Procfile.GetValueOrDefault("web");
        var worker = web is null ? app.Procfile.GetValueOrDefault("worker") : null;
        var framework = rails ? "Rails" : hanami ? "Hanami" : app.InBundle("sinatra") ? "Sinatra" : app.InBundle("roda") ? "Roda" : rack ? "Rack" : null;
        // Classic Sinatra runs its own file (ruby app.rb) when there's no config.ru.
        var script = framework == "Sinatra" && !rack && web is null
            ? SinatraScripts.FirstOrDefault(s => Text(s) is { } code && RequiresSinatra().IsMatch(code))
            : null;
        if (!rails && !hanami && !rack && web is null && worker is null && script is null) return null;

        var r = new DetectionResult
        {
            Kind = ProjectKind.Ruby,
            Title = framework is not null ? $"Ruby ({framework})" : web is null ? "Ruby (worker)" : "Ruby",
            DockerIgnore = RubyDockerfiles.Ignore,
        };
        var (tag, tagSource) = RubyVersions.Resolve(app);
        r.Evidence.Add($"Found Gemfile; gems install with Bundler{(app.Lockfile is null ? "" : " from Gemfile.lock")}, without the development and test groups");
        r.Evidence.Add($"Ruby {tag} ({tagSource})");
        var railsVersion = rails ? app.VersionOf("rails") ?? app.VersionOf("railties") : null;
        if (railsVersion is { } rv) r.Evidence.Add($"Rails {rv.Major}.{rv.Minor}");
        if (app.Lockfile is null) r.Warnings.Add("No Gemfile.lock is committed, so each build may resolve different gem versions.");
        if (app.LacksLinuxPlatform)
            r.Warnings.Add($"Gemfile.lock only lists {string.Join(", ", app.Platforms)}, so the build adds the Linux platforms to its copy. Run `bundle lock --add-platform x86_64-linux aarch64-linux` and commit Gemfile.lock to make builds repeatable.");
        r.Warnings.AddRange(RubyVersions.Warnings(tag));

        var build = new List<string>();
        var runtime = new List<string>();
        foreach (var (gem, needs, runs, onlyWhenListed) in NativeGems)
        {
            if (onlyWhenListed ? !app.Gems.Contains(gem) : !app.InBundle(gem)) continue;
            if (needs.Length > 0 && !build.Contains(needs)) build.Add(needs);
            if (runs.Length > 0 && !runtime.Contains(runs)) runtime.Add(runs);
        }
        // Without a precompiled build, nokogiri compiles its own libxml2, which needs these (nokogiri's install guide).
        if (app.NokogiriFromSource) build.AddRange(["patch", "zlib1g-dev", "liblzma-dev"]);
        if (runtime.Count > 0) r.Evidence.Add($"The gems need {string.Join(", ", runtime)} at runtime, which the image installs");

        var env = new List<string>();
        if (rails) env.AddRange(["RAILS_ENV=production", "RAILS_LOG_TO_STDOUT=1", "RAILS_SERVE_STATIC_FILES=1"]);
        else if (hanami) env.AddRange(["HANAMI_ENV=production", "RACK_ENV=production"]);
        else env.AddRange(["RACK_ENV=production", "APP_ENV=production"]);

        NodeAssets? node = null;
        var steps = new List<string>();
        // The rails command only works in an app with bin/rails; without it, its tasks run through rake.
        var binRails = Exists("bin/rails");
        var tasks = binRails ? "ruby bin/rails" : Exists("Rakefile") ? "bundle exec rake" : null;
        if (rails && tasks is null)
            r.Warnings.Add("bin/rails and the Rakefile aren't in the repo, so the build can't precompile assets or prepare the database: commit bin/rails.");
        if (rails && tasks is not null)
        {
            // jsbundling, cssbundling, vite_rails and shakapacker run the front end's build as part of assets:precompile.
            var hooked = AssetHooks.Any(app.InBundle);
            if (NodeAssets.Detect(folder, r) is { } pkg && (hooked || pkg.HasBuild))
            {
                node = pkg;
                node.Describe(r, "the front-end assets");
                if (!hooked) steps.Add(node.Build);
            }
            if (hooked || new[] { "propshaft", "sprockets-rails", "sprockets" }.Any(app.InBundle))
            {
                // Rails 7.1 added SECRET_KEY_BASE_DUMMY; before that any SECRET_KEY_BASE does, since precompiling doesn't use it.
                var dummy = railsVersion is { } v && (v.Major < 7 || (v.Major == 7 && v.Minor < 1))
                    ? "SECRET_KEY_BASE=precompile-placeholder" : "SECRET_KEY_BASE_DUMMY=1";
                steps.Add($"RUN {dummy} {tasks} assets:precompile");
                r.Evidence.Add("Precompiles the assets while building, with a placeholder secret so the master key isn't needed");
            }
        }
        else if (hanami && Exists("config/assets.js"))
        {
            node = NodeAssets.Detect(folder, r);
            node?.Describe(r, "the assets with `hanami assets compile`");
            steps.Add("RUN bundle exec hanami assets compile");
        }
        else if (NodeAssets.Detect(folder, r) is { HasBuild: true } pkg)
        {
            node = pkg;
            node.Describe(r, "the front-end assets");
            steps.Add(node.Build);
        }
        // The image only needs what the asset build made.
        if (rails && steps.Count > 0) steps.Add("RUN rm -rf node_modules tmp/cache");
        else if (node is not null) steps.Add("RUN rm -rf node_modules");

        var release = app.Procfile.GetValueOrDefault("release");
        var dbPrepare = rails && tasks is not null && Exists("config/database.yml");
        var prepare = release ?? (dbPrepare ? $"{tasks} db:prepare" : null);
        int? port;
        string start;
        if (web is not null)
        {
            port = PortIn(web) ?? (rails ? PumaPort(Text("config/puma.rb")) ?? 3000 : hanami ? 2300 : framework is null ? 5000 : 9292);
            start = Chain(prepare, web);
            r.Evidence.Add($"Starts the Procfile's web process: `{web}`");
            if (release is not null) r.Evidence.Add($"Runs the Procfile's release command first: `{release}`");
        }
        else if (rails && binRails)
        {
            port = PumaPort(Text("config/puma.rb")) ?? 3000;
            // An older Rails leaves tmp/pids/server.pid behind, which stops a restarted container from starting.
            start = "rm -f tmp/pids/server.pid && " + Chain(prepare, $"ruby bin/rails server -b 0.0.0.0 -p ${{PORT:-{port}}}");
            r.Evidence.Add($"Starts with `bin/rails server` on port {port}");
        }
        else if (rails)
        {
            port = PumaPort(Text("config/puma.rb")) ?? 3000;
            var (server, name) = RackServer(app, port.Value, r);
            start = Chain(prepare, server);
            r.Evidence.Add($"There's no bin/rails, so {name} runs config.ru on port {port}");
        }
        else if (script is not null)
        {
            port = 4567;
            start = Chain(release, $"bundle exec ruby {script} -o 0.0.0.0 -p ${{PORT:-{port}}}");
            r.Evidence.Add($"Starts with `ruby {script}`, Sinatra's own server, on port {port}");
            if (!app.InBundle("rackup") && app.VersionOf("sinatra") is null or { Major: >= 4 })
                r.Warnings.Add($"Sinatra 4 needs the rackup gem to run {script} itself: add `gem \"rackup\"` and `gem \"puma\"` to the Gemfile, or add a config.ru.");
        }
        else if (rack)
        {
            port = hanami ? 2300 : 9292;
            var (server, name) = RackServer(app, port.Value, r);
            start = Chain(release, server);
            r.Evidence.Add($"Runs config.ru with {name} on port {port}");
        }
        else
        {
            port = null;
            start = Chain(release, worker!);
            r.Evidence.Add($"Runs the Procfile's worker process: `{worker}`; there's no web process, so no port");
        }
        if (port is not null && framework is not null)
            r.Evidence.Add("Behind Cloudflare Tunnel, Rack reads X-Forwarded-Proto and trusts X-Forwarded-For from private addresses, so the app sees HTTPS and the visitor's address");

        if (rails) RailsAdvice(app, files, r, Text, dbPrepare, env);
        env.Add("MALLOC_ARENA_MAX=2");

        r.ContainerPort = port;
        r.Dockerfile = RubyDockerfiles.Render(new RubyPlan(
            tag, env, build, runtime, app.Lockfile is not null, app.LacksLinuxPlatform, app.BundleHash, node, steps, port, start));
        return new LanguageMatch(r, Strong: true);
    }

    /// <summary>What a Rails app needs that Gitberth can't do for it, and Solid Queue's jobs inside Puma.</summary>
    private static void RailsAdvice(RubyApp app, RepoFiles files, DetectionResult r, Func<string, string?> text, bool dbPrepare, List<string> env)
    {
        var production = text("config/environments/production.rb") ?? "";
        var credentials = files.Exists("config/credentials.yml.enc") || files.Exists("config/credentials/production.yml.enc");
        // Credentials aren't variables Gitberth can suggest: they're one encrypted file, opened with the master key.
        var readsCredentials = files.WithExtension(".rb", ".erb", ".yml").Where(f => !IsTestOrToolingPath(f)).Take(2000)
            .Any(f => RubyApp.Read(files.Full(f))?.Contains("Rails.application.credentials", StringComparison.Ordinal) == true);
        if (credentials)
            r.Warnings.Add("The app's encrypted credentials need RAILS_MASTER_KEY: set it as a secret, to the contents of config/master.key (or config/credentials/production.key), which isn't in the repo. Rails fails without it.");
        else if (readsCredentials)
            r.Warnings.Add("The code reads Rails.application.credentials, but no encrypted credentials file is in the repo, so those values are empty: commit config/credentials.yml.enc and set RAILS_MASTER_KEY, or read them from variables.");
        if (!credentials && !SetsSecretKeyBase().IsMatch(production + (text("config/application.rb") ?? "")))
            r.Warnings.Add("Rails needs SECRET_KEY_BASE in production: set it as a secret (`bin/rails secret` prints one).");

        var databases = new[] { "pg", "mysql2", "trilogy" }.Where(app.InBundle).ToList();
        if (dbPrepare)
        {
            r.Evidence.Add("Prepares the database (db:prepare) before the server starts, like Rails' own Docker entrypoint");
            if (databases.Count > 0)
                r.Warnings.Add("The database must be reachable when the container starts, because db:prepare runs first: set DATABASE_URL, or what config/database.yml reads.");
            else if (app.InBundle("sqlite3"))
                r.Warnings.Add((text("config/database.yml") ?? "").Contains("storage/", StringComparison.Ordinal)
                    ? "The SQLite database is in storage/, inside the container, so it's lost on every deploy: add a volume at /app/storage."
                    : "The SQLite database lives inside the container, so it's lost on every deploy: point config/database.yml's production database into storage/, as Rails 8 does, and add a volume at /app/storage.");
        }
        if (ForceSsl().IsMatch(production))
            r.Warnings.Add("production.rb sets config.force_ssl, so plain-HTTP requests get a redirect to HTTPS: open the app through its domain (Cloudflare Tunnel says the request was HTTPS), not its host port.");
        if (SetsHosts().IsMatch(production))
            r.Warnings.Add("production.rb sets config.hosts, so Rails turns away other host names with \"Blocked hosts\": list the app's domains there.");
        // Rails 8 runs Solid Queue's workers inside Puma on a single server; without this its jobs would never run.
        if (app.InBundle("solid_queue") && (text("config/puma.rb") ?? "").Contains("SOLID_QUEUE_IN_PUMA", StringComparison.Ordinal))
        {
            env.Add("SOLID_QUEUE_IN_PUMA=1");
            r.Evidence.Add("Background jobs run inside Puma (SOLID_QUEUE_IN_PUMA), as Rails 8 does on a single server");
        }
    }

    /// <summary>The server that runs config.ru, and its name for the evidence.</summary>
    private static (string Command, string Name) RackServer(RubyApp app, int port, DetectionResult r)
    {
        var listen = $"${{PORT:-{port}}}";
        if (app.InBundle("puma")) return ($"bundle exec puma -b tcp://0.0.0.0:{listen}", "Puma");
        if (app.InBundle("unicorn")) return ($"bundle exec unicorn -o 0.0.0.0 -p {listen}", "Unicorn");
        if (app.InBundle("thin")) return ($"bundle exec thin start -a 0.0.0.0 -p {listen}", "Thin");
        // Rack 2 ships rackup; from Rack 3 it's a gem of its own.
        if (app.InBundle("rackup") || app.VersionOf("rack") is { Major: < 3 }) return ($"bundle exec rackup -o 0.0.0.0 -p {listen}", "rackup");
        r.Warnings.Add("The Gemfile has no server to run config.ru with, and Gitberth starts it with Puma: add it with `bundle add puma`.");
        return ($"bundle exec puma -b tcp://0.0.0.0:{listen}", "Puma");
    }

    /// <summary>
    /// `before &amp;&amp; exec command`, so the server replaces the shell and gets Docker's stop signal. A command that chains
    /// several itself is run as it is, and leading VAR=value settings go before exec.
    /// </summary>
    internal static string Chain(string? before, string command)
    {
        var m = LeadingAssignments().Match(command);
        var run = ShellOperators().IsMatch(command) || m.Groups[2].Value.StartsWith("exec ", StringComparison.Ordinal) ? command
            : $"{m.Groups[1].Value}exec {m.Groups[2].Value}";
        return before is null ? run : $"{before} && {run}";
    }

    /// <summary>A port a start command spells out (-p 5000, --port=5000, ${PORT:-5000}); null when it only uses $PORT.</summary>
    internal static int? PortIn(string command) =>
        PortArgument().Match(command) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>The default in config/puma.rb's `port ENV.fetch("PORT") { 3000 }` or `port ENV.fetch("PORT", 3000)`.</summary>
    internal static int? PumaPort(string? pumaConfig) =>
        pumaConfig is not null && PumaPortFetch().Match(pumaConfig) is { Success: true } m
            ? int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, CultureInfo.InvariantCulture)
            : null;

    public void ScanEnv(RepoFiles files, Action<string, string, string> add)
    {
        if (files.Exists("config/credentials.yml.enc") || files.Exists("config/credentials/production.yml.enc"))
            add("RAILS_MASTER_KEY", "", "reads config/credentials.yml.enc: the contents of config/master.key, which isn't in the repo");
        else if (files.Exists("config/application.rb"))
            add("SECRET_KEY_BASE", "", "Rails signs cookies with it: `bin/rails secret` prints one");
        foreach (var rel in files.WithExtension(".rb", ".erb", ".yml", ".yaml", ".rake", ".ru").Where(f => !IsTestOrToolingPath(f)).Take(3000))
        {
            var text = RubyApp.Read(files.Full(rel));
            if (string.IsNullOrEmpty(text) || !text.Contains("ENV", StringComparison.Ordinal)) continue;
            foreach (Match m in EnvReference().Matches(text))
            {
                var key = m.Groups["key"].Value;
                if (IgnoredKeys.Contains(key)) continue;
                var fallback = m.Groups["default"].Success ? m.Groups["default"].Value.Trim('"', '\'') : null;
                add(key, "", fallback is null || fallback.Length == 0 ? $"used in {rel}" : $"used in {rel} (default {fallback})");
            }
        }
    }

    // Tests and specs, the development and test settings, and Kamal's deploy config don't describe the running app.
    private static bool IsTestOrToolingPath(string path) =>
        path.StartsWith("spec/", StringComparison.Ordinal) || path.StartsWith("test/", StringComparison.Ordinal)
        || path.StartsWith("features/", StringComparison.Ordinal) || path.StartsWith("config/deploy", StringComparison.Ordinal)
        || path is "config/environments/development.rb" or "config/environments/test.rb";

    [GeneratedRegex(@"require\s*\(?\s*[""']sinatra[""']")]
    private static partial Regex RequiresSinatra();

    [GeneratedRegex(@"&&|\|\||[;|]")]
    private static partial Regex ShellOperators();

    [GeneratedRegex(@"^((?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*)(.+)$", RegexOptions.Singleline)]
    private static partial Regex LeadingAssignments();

    [GeneratedRegex(@"(?:(?:^|\s)(?:-p|--port)[\s=]+|\$\{PORT:-|tcp://[^\s:]+:)(\d{2,5})\b")]
    private static partial Regex PortArgument();

    [GeneratedRegex(@"ENV\.fetch\(\s*[""']PORT[""']\s*(?:,\s*(\d{2,5})\s*\)|\)\s*\{\s*(\d{2,5})\s*\})")]
    private static partial Regex PumaPortFetch();

    [GeneratedRegex(@"^\s*config\.secret_key_base\s*=", RegexOptions.Multiline)]
    private static partial Regex SetsSecretKeyBase();

    [GeneratedRegex(@"^\s*config\.force_ssl\s*=\s*true", RegexOptions.Multiline)]
    private static partial Regex ForceSsl();

    [GeneratedRegex(@"^\s*config\.hosts\s*(?:=|<<|\+=|\.push|\.concat)", RegexOptions.Multiline)]
    private static partial Regex SetsHosts();

    [GeneratedRegex(@"ENV(?:\[\s*[""'](?<key>[A-Za-z_][A-Za-z0-9_]*)[""']\s*\]|\.fetch\(\s*[""'](?<key>[A-Za-z_][A-Za-z0-9_]*)[""']\s*(?:,\s*(?<default>""[^""\n]*""|'[^'\n]*'|-?\d+(?:\.\d+)?|true|false))?|\.(?:key\?|has_key\?|include\?)\(\s*[""'](?<key>[A-Za-z_][A-Za-z0-9_]*)[""'])")]
    private static partial Regex EnvReference();
}
