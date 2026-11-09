namespace Gitberth.Detection;

/// <summary>How a PHP app is built and served.</summary>
/// <param name="Version">The php image's version, like 8.4.</param>
/// <param name="Extensions">Extensions to add to the image, besides the ones it has built in.</param>
/// <param name="Env">Settings for both stages, like APP_ENV=production.</param>
/// <param name="Composer">composer.json is there, so Composer installs the packages in a build stage.</param>
/// <param name="Node">The package.json whose dependencies install after Composer's, for the asset build in <paramref name="Steps"/>.</param>
/// <param name="BeforeInstall">RUN lines before Composer installs, like the folders Laravel's install scripts write to.</param>
/// <param name="Steps">RUN lines after the installs: asset builds and the framework's own build commands.</param>
/// <param name="DocRoot">The folder Apache serves, relative to the app: "public", or "" for the app's own folder.</param>
/// <param name="FrontController">Paths that aren't files go to index.php, as framework routers expect.</param>
/// <param name="StartScript">Shell lines that run as the container starts, before Apache; null to start Apache only.</param>
/// <param name="StartComment">What the start script is for, as a comment above it.</param>
internal sealed record PhpPlan(
    string Version, IReadOnlyList<string> Extensions, IReadOnlyList<string> Env, bool Composer, NodeAssets? Node,
    IReadOnlyList<string> BeforeInstall, IReadOnlyList<string> Steps, string DocRoot, bool FrontController,
    IReadOnlyList<string>? StartScript = null, string? StartComment = null);

/// <summary>
/// PHP runs in Apache with mod_php, on the official php:&lt;version&gt;-apache image: it covers every PHP version, its
/// configuration has been stable for years, and .htaccess files work as they do on shared hosting, so plain PHP apps and
/// framework apps run unchanged. The container runs as www-data, which owns the app's files, so an app can write next
/// to its code as it would on a PaaS.
/// </summary>
internal static class PhpDockerfiles
{
    /// <summary>Apache runs as www-data, so it listens on a port above 1024.</summary>
    public const int Port = 8080;

    public static string Ignore(bool composer, string? framework) =>
        ".git\n**/node_modules\n"
        + (composer ? "/vendor\n" : "")
        // A Vite dev server's marker file and caches built on a developer's machine would break the build's own.
        + (framework == "Laravel" ? "/public/hot\n/bootstrap/cache/*.php\n" : "")
        + (framework == "Symfony" ? "/var/cache\n/var/log\n" : "");

    public static string Render(PhpPlan p)
    {
        var image = $"php:{p.Version}-apache";
        if (!p.Composer && p.Node is null)
        {
            // Nothing to install: one stage that copies the app in.
            return RubyDockerfiles.Lines(
            [
                $"FROM {image}",
                .. Runtime(p),
                .. Apache(p),
                DockerfileMarkers.BuildArgs,
                "COPY --chown=www-data:www-data . /var/www/html",
                .. Final(p),
            ]);
        }
        return RubyDockerfiles.Lines(
        [
            $"FROM {image} AS base",
            .. Runtime(p),
            "",
            "FROM base AS build",
            p.Composer ? "COPY --from=composer:2 /usr/bin/composer /usr/local/bin/composer" : null,
            p.Composer ? "ENV COMPOSER_ALLOW_SUPERUSER=1 COMPOSER_CACHE_DIR=/root/.cache/composer" : null,
            p.Composer ? "# Composer fetches packages with git and unzip." : null,
            p.Composer ? "RUN apt-get update && apt-get install -y --no-install-recommends git unzip && rm -rf /var/lib/apt/lists/*" : null,
            .. p.Node?.Setup() ?? [],
            "COPY . .",
            DockerfileMarkers.BuildArgs,
            .. p.BeforeInstall,
            p.Composer ? "RUN --mount=type=cache,id=composer,target=/root/.cache/composer \\\n    composer install --no-dev --optimize-autoloader --no-interaction" : null,
            p.Node?.Install,
            .. p.Steps,
            p.Node is null ? null : "RUN rm -rf node_modules",
            "",
            "FROM base",
            .. Apache(p),
            .. Start(p),
            "COPY --from=build --chown=www-data:www-data /var/www/html /var/www/html",
            .. Final(p),
        ]);
    }

    private static IEnumerable<string?> Runtime(PhpPlan p) =>
    [
        p.Extensions.Count == 0 ? null : "# install-php-extensions adds each extension together with the system libraries it needs.",
        p.Extensions.Count == 0 ? null : "COPY --from=mlocati/php-extension-installer:2 /usr/bin/install-php-extensions /usr/local/bin/",
        p.Extensions.Count == 0 ? null : $"RUN install-php-extensions {string.Join(' ', p.Extensions)}",
        "RUN cp \"$PHP_INI_DIR/php.ini-production\" \"$PHP_INI_DIR/php.ini\"",
        // Under mod_php the container's variables reach PHP through getenv() and $_ENV only, and the production settings turn $_ENV off.
        "COPY <<'EOF' /usr/local/etc/php/conf.d/zz-gitberth.ini",
        "; $_ENV holds the container's environment variables, where Symfony and many apps look for them.",
        "variables_order = \"EGPCS\"",
        "EOF",
        p.Env.Count == 0 ? null : "ENV " + string.Join(' ', p.Env),
    ];

    private static IEnumerable<string?> Apache(PhpPlan p) =>
    [
        $"# Apache serves {(p.DocRoot.Length == 0 ? "the app's folder" : p.DocRoot + "/")} on $PORT, with .htaccess rewrites{(p.FrontController ? " and other paths sent to index.php" : "")}.",
        "# Behind Cloudflare Tunnel it takes the visitor's address and HTTPS from X-Forwarded-For and X-Forwarded-Proto, from private addresses only.",
        "RUN a2enmod rewrite remoteip",
        "COPY <<'EOF' /etc/apache2/ports.conf",
        "Listen ${PORT}",
        "EOF",
        "COPY <<'EOF' /etc/apache2/sites-available/000-default.conf",
        "<VirtualHost *:${PORT}>",
        $"    DocumentRoot /var/www/html{(p.DocRoot.Length == 0 ? "" : "/" + p.DocRoot)}",
        p.FrontController ? "    FallbackResource /index.php" : null,
        "    RemoteIPHeader X-Forwarded-For",
        "    RemoteIPTrustedProxy 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16",
        "    SetEnvIf X-Forwarded-Proto \"^https$\" HTTPS=on",
        "</VirtualHost>",
        "EOF",
    ];

    private static IEnumerable<string?> Start(PhpPlan p) => p.StartScript is null ? [] :
    [
        p.StartComment is null ? null : "# " + p.StartComment,
        "COPY <<'EOF' /usr/local/bin/gitberth-start",
        "set -e",
        .. p.StartScript,
        "exec apache2-foreground",
        "EOF",
    ];

    private static IEnumerable<string?> Final(PhpPlan p) =>
    [
        "# Apache and the app run as www-data (uid 33), which owns the app's files and its volume folders.",
        $"ENV PORT={Port} APP_UID=33",
        DockerfileMarkers.Volumes,
        "USER www-data",
        $"EXPOSE {Port}",
        p.StartScript is null ? "CMD [\"apache2-foreground\"]" : "CMD [\"sh\", \"/usr/local/bin/gitberth-start\"]",
    ];
}
