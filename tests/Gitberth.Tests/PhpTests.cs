using Gitberth.Detection;

namespace Gitberth.Tests;

public class PhpTests
{
    private static DetectionResult Analyze(TempRepo repo, string? hint = null) => new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(hint));

    private static string Lf(string? text) => (text ?? "").ReplaceLineEndings("\n");

    private static TempRepo LaravelApp(string require = "\"php\": \"^8.2\", \"laravel/framework\": \"^12.0\"") => new TempRepo()
        .With("composer.json", $$"""{ "require": { {{require}} }, "require-dev": { "phpunit/phpunit": "^11.0", "ext-xdebug": "*" } }""")
        .With("artisan", "#!/usr/bin/env php\n<?php\n")
        .With("public/index.php", "<?php\n")
        .With("bootstrap/app.php", "<?php\n");

    [Fact]
    public void Laravel_with_vite_builds_assets_and_runs_its_deploy_steps_at_start()
    {
        using var repo = LaravelApp()
            .With("composer.lock", """{ "packages": [ { "name": "laravel/framework", "version": "v12.30.1", "require": { "php": "^8.2", "ext-mbstring": "*" } }, { "name": "intervention/image", "version": "3.11.0", "require": { "ext-gd": "*" } } ], "packages-dev": [ { "name": "x/y", "require": { "ext-pcov": "*" } } ] }""")
            .With("package.json", """{ "type": "module", "scripts": { "build": "vite build", "dev": "vite" }, "devDependencies": { "laravel-vite-plugin": "^2.0.0", "vite": "^7.0.0" } }""")
            .With("package-lock.json", "{}")
            .With(".env.example", "APP_NAME=Laravel\nAPP_ENV=local\nAPP_KEY=\nAPP_DEBUG=true\nLOG_CHANNEL=stack\nDB_CONNECTION=sqlite\n");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.Php, r.Kind);
        Assert.Equal("PHP (Laravel)", r.Title);
        Assert.Equal(8080, r.ContainerPort);
        Assert.Contains("Laravel 12", r.Evidence);
        Assert.Contains("PHP 8.4 (composer.json requires php ^8.2), served by Apache with mod_php from public/ on port 8080", r.Evidence);
        Assert.Contains("Extensions: bcmath, gd, intl, opcache, pdo_mysql, pdo_pgsql, redis, zip", r.Evidence);
        var dockerfile = Lf(r.Dockerfile);
        Assert.StartsWith("FROM php:8.4-apache AS base\n", dockerfile);
        Assert.Contains("RUN install-php-extensions bcmath gd intl opcache pdo_mysql pdo_pgsql redis zip\n", dockerfile);
        Assert.DoesNotContain("xdebug", dockerfile);
        Assert.DoesNotContain("pcov", dockerfile);
        Assert.Contains("ENV APP_ENV=production LOG_CHANNEL=stderr\n", dockerfile);
        Assert.Contains("""
            COPY . .
            # gitberth:build-args
            RUN mkdir -p bootstrap/cache storage/app/public storage/framework/cache/data storage/framework/sessions storage/framework/views storage/logs
            RUN --mount=type=cache,id=composer,target=/root/.cache/composer \
                composer install --no-dev --optimize-autoloader --no-interaction
            RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN npm run build
            RUN rm -rf node_modules
            """.ReplaceLineEndings("\n"), dockerfile);
        Assert.Contains("    DocumentRoot /var/www/html/public\n    FallbackResource /index.php\n", dockerfile);
        Assert.Contains("""
            COPY <<'EOF' /usr/local/bin/gitberth-start
            set -e
            php artisan migrate --force
            php artisan storage:link || true
            """.ReplaceLineEndings("\n"), dockerfile);
        Assert.Contains("exec apache2-foreground\nEOF\nCOPY --from=build --chown=www-data:www-data /var/www/html /var/www/html\n", dockerfile);
        Assert.EndsWith("ENV PORT=8080 APP_UID=33\n# gitberth:volumes\nUSER www-data\nEXPOSE 8080\nCMD [\"sh\", \"/usr/local/bin/gitberth-start\"]\n", dockerfile);
        Assert.Contains("/public/hot", r.DockerIgnore);
        Assert.Contains("/vendor", r.DockerIgnore);

        Assert.Contains(r.Warnings, w => w.Contains("APP_KEY") && w.Contains("key:generate --show"));
        Assert.Contains(r.Warnings, w => w.Contains("APP_ENV to production, APP_DEBUG to false, LOG_CHANNEL to stderr"));
        Assert.Contains(r.Warnings, w => w.Contains("SQLite") && w.Contains("DB_DATABASE=/var/www/html/storage/database/database.sqlite"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("composer.lock"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("TrustProxies"));
    }

    [Fact]
    public void Laravel_on_mysql_needs_its_database_at_start()
    {
        using var repo = LaravelApp().With(".env.example", "APP_ENV=production\nDB_CONNECTION=mysql\nDB_HOST=127.0.0.1\n");
        var r = Analyze(repo);
        Assert.Contains(r.Warnings, w => w.Contains("migrate --force") && w.Contains("DB_ variables"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("SQLite"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains(".env.example"));
        Assert.Contains(r.Warnings, w => w.StartsWith("No composer.lock is committed"));
    }

    [Fact]
    public void Laravel_10_defaulted_to_mysql_and_11_to_sqlite()
    {
        using var ten = LaravelApp("\"php\": \"^8.1\", \"laravel/framework\": \"^10.10\"");
        Assert.DoesNotContain(Analyze(ten).Warnings, w => w.Contains("SQLite"));
        using var eleven = LaravelApp("\"php\": \"^8.2\", \"laravel/framework\": \"^11.31\"");
        Assert.Contains(Analyze(eleven).Warnings, w => w.Contains("SQLite"));
    }

    [Fact]
    public void Symfony_compiles_its_env_assets_and_cache_while_building_and_migrates_at_start()
    {
        using var repo = new TempRepo()
            .With("composer.json", """{ "require": { "php": ">=8.2", "symfony/framework-bundle": "7.4.*", "symfony/flex": "^2", "symfony/asset-mapper": "7.4.*", "doctrine/doctrine-migrations-bundle": "^3.4", "symfony/runtime": "7.4.*" } }""")
            .With("composer.lock", """{ "packages": [] }""")
            .With(".env", "APP_ENV=dev\nAPP_SECRET=3f4b8c\nDATABASE_URL=\"postgresql://app:!ChangeMe!@127.0.0.1:5432/app\"\n")
            .With("importmap.php", "<?php return [];\n")
            .With("bin/console", "#!/usr/bin/env php\n")
            .With("public/index.php", "<?php\n")
            .With("config/packages/doctrine.yaml", "doctrine:\n  dbal:\n    url: '%env(resolve:DATABASE_URL)%'\n");
        var r = Analyze(repo);

        Assert.Equal("PHP (Symfony)", r.Title);
        var dockerfile = Lf(r.Dockerfile);
        Assert.Contains("ENV APP_ENV=prod APP_DEBUG=0\n", dockerfile);
        Assert.Contains("RUN install-php-extensions intl opcache pdo_mysql pdo_pgsql zip\n", dockerfile);
        Assert.Contains("composer install --no-dev --optimize-autoloader --no-interaction\nRUN composer dump-env prod\nRUN php bin/console importmap:install\nRUN php bin/console asset-map:compile\nRUN php bin/console cache:clear\n", dockerfile);
        Assert.Contains("set -e\nphp bin/console doctrine:migrations:migrate --no-interaction --allow-no-migration\nexec apache2-foreground\n", dockerfile);
        Assert.Contains("/var/cache", r.DockerIgnore);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("APP_SECRET"));
        Assert.Contains(r.Warnings, w => w.Contains("DATABASE_URL"));
        Assert.Contains(r.EnvSuggestions, s => s.Key == "DATABASE_URL" && s.Source == "used in config/packages/doctrine.yaml");
    }

    [Fact]
    public void Symfony_without_a_secret_says_so()
    {
        using var repo = new TempRepo()
            .With("composer.json", """{ "require": { "symfony/framework-bundle": "^7.4", "symfony/runtime": "^7.4" } }""")
            .With("public/index.php", "<?php\n")
            .With("config/packages/framework.yaml", "framework:\n  secret: '%env(APP_SECRET)%'\n");
        var r = Analyze(repo);
        Assert.Contains(r.Warnings, w => w.StartsWith("Symfony needs APP_SECRET"));
        Assert.DoesNotContain("cache:clear", r.Dockerfile);
        Assert.Equal("CMD [\"apache2-foreground\"]\n", Lf(r.Dockerfile)[Lf(r.Dockerfile).LastIndexOf("CMD", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Slim_serves_public_through_index_php()
    {
        using var repo = new TempRepo()
            .With("composer.json", """{ "require": { "php": "^8.1", "slim/slim": "^4.15", "slim/psr7": "^1.8" } }""")
            .With("public/index.php", "<?php\n$app->run();\n");
        var r = Analyze(repo);

        Assert.Equal("PHP (Slim)", r.Title);
        Assert.Contains("    DocumentRoot /var/www/html/public\n    FallbackResource /index.php\n", Lf(r.Dockerfile));
        Assert.Contains("CMD [\"apache2-foreground\"]", r.Dockerfile);
        Assert.Contains("RUN install-php-extensions opcache\n", Lf(r.Dockerfile));
    }

    [Fact]
    public void Plain_php_runs_in_one_stage_with_the_extensions_its_code_uses()
    {
        using var repo = new TempRepo()
            .With("index.php", "<?php\n$db = new mysqli('db', 'u', 'p');\n$pdo = new PDO('pgsql:host=db');\n")
            .With("lib/images.php", "<?php\n$im = imagecreatetruecolor(10, 10);\n")
            .With("tests/fixture.php", "<?php\n$r = new Redis();\n");
        var r = Analyze(repo);

        Assert.Equal("PHP", r.Title);
        Assert.Equal("""
            FROM php:8.4-apache
            # install-php-extensions adds each extension together with the system libraries it needs.
            COPY --from=mlocati/php-extension-installer:2 /usr/bin/install-php-extensions /usr/local/bin/
            RUN install-php-extensions gd mysqli opcache pdo_pgsql
            RUN cp "$PHP_INI_DIR/php.ini-production" "$PHP_INI_DIR/php.ini"
            COPY <<'EOF' /usr/local/etc/php/conf.d/zz-gitberth.ini
            ; $_ENV holds the container's environment variables, where Symfony and many apps look for them.
            variables_order = "EGPCS"
            EOF
            # Apache serves the app's folder on $PORT, with .htaccess rewrites.
            # Behind Cloudflare Tunnel it takes the visitor's address and HTTPS from X-Forwarded-For and X-Forwarded-Proto, from private addresses only.
            RUN a2enmod rewrite remoteip
            COPY <<'EOF' /etc/apache2/ports.conf
            Listen ${PORT}
            EOF
            COPY <<'EOF' /etc/apache2/sites-available/000-default.conf
            <VirtualHost *:${PORT}>
                DocumentRoot /var/www/html
                RemoteIPHeader X-Forwarded-For
                RemoteIPTrustedProxy 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16
                SetEnvIf X-Forwarded-Proto "^https$" HTTPS=on
            </VirtualHost>
            EOF
            # gitberth:build-args
            COPY --chown=www-data:www-data . /var/www/html
            # Apache and the app run as www-data (uid 33), which owns the app's files and its volume folders.
            ENV PORT=8080 APP_UID=33
            # gitberth:volumes
            USER www-data
            EXPOSE 8080
            CMD ["apache2-foreground"]

            """.ReplaceLineEndings("\n"), Lf(r.Dockerfile));
        Assert.Equal(".git\n**/node_modules\n", r.DockerIgnore);
    }

    [Fact]
    public void Plain_php_in_public_is_served_from_there()
    {
        using var repo = new TempRepo().With("public/index.php", "<?php echo 'hi';\n").With("README.md", "x");
        var r = Analyze(repo);
        Assert.Equal(ProjectKind.Php, r.Kind);
        Assert.Contains("    DocumentRoot /var/www/html/public\n", Lf(r.Dockerfile));
        Assert.DoesNotContain("FallbackResource", r.Dockerfile);
    }

    [Fact]
    public void A_composer_library_is_not_an_app()
    {
        using var repo = new TempRepo()
            .With("composer.json", """{ "name": "acme/lib", "type": "library", "require": { "php": "^8.2" } }""")
            .With("src/Thing.php", "<?php\n");
        Assert.Equal(ProjectKind.Unknown, Analyze(repo).Kind);
        Assert.Empty(new ProjectAnalyzer().FindApps(repo.Root));
    }

    [Fact]
    public void Index_files_inside_a_php_site_are_part_of_it()
    {
        using var repo = new TempRepo()
            .With("index.php", "<?php require __DIR__ . '/wp-blog-header.php';\n")
            .With("wp-content/index.php", "<?php // Silence is golden.\n")
            .With("wp-content/themes/twentytwenty/index.php", "<?php get_header();\n");
        Assert.Equal([""], new ProjectAnalyzer().FindApps(repo.Root).Select(a => a.Dir));
    }

    [Fact]
    public void A_frameworks_public_folder_is_not_offered_as_an_app_of_its_own()
    {
        using var repo = LaravelApp().With("public/index.php", "<?php\n");
        var apps = new ProjectAnalyzer().FindApps(repo.Root);
        Assert.Contains(apps, c => c.Dir == "" && c.Title == "PHP (Laravel)");
        Assert.DoesNotContain(apps, c => c.Dir == "public");
    }

    [Fact]
    public void Laravel_wins_over_its_vite_package_json_and_the_php_hint_picks_it_over_a_dockerfile()
    {
        using var repo = LaravelApp()
            .With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""");
        Assert.Equal(ProjectKind.Php, Analyze(repo).Kind);
        repo.With("Dockerfile", "FROM php:8.4-fpm\nEXPOSE 9000\n");
        Assert.Equal(ProjectKind.Dockerfile, Analyze(repo).Kind);
        Assert.Equal(ProjectKind.Php, Analyze(repo, "php").Kind);
    }

    [Theory]
    [InlineData("\"php\": \">=8.1 <8.4\"", null, "8.3")]
    [InlineData("\"php\": \"^7.4|^8.0\"", null, "8.4")]
    [InlineData("\"php\": \"~7.4.0\"", null, "7.4")]
    [InlineData("\"php\": \"8.5.*\"", null, "8.5")]
    [InlineData("\"php\": \"^8.1\"", "8.2.12", "8.2")]
    [InlineData("\"php\": \">=9.0\"", null, "8.5")]
    public void Php_version_comes_from_composer_json(string require, string? platform, string version)
    {
        var config = platform is null ? "" : $$""", "config": { "platform": { "php": "{{platform}}" } }""";
        using var repo = new TempRepo()
            .With("composer.json", $$"""{ "require": { {{require}}, "slim/slim": "^4.0" }{{config}} }""")
            .With("public/index.php", "<?php\n");
        var r = Analyze(repo);
        Assert.StartsWith($"FROM php:{version}-apache AS base", r.Dockerfile);
        // OPcache is part of PHP from 8.5.
        Assert.Equal(version != "8.5", r.Dockerfile!.Contains("opcache"));
    }

    [Fact]
    public void Old_or_unknown_php_versions_warn()
    {
        using var old = new TempRepo().With("composer.json", """{ "require": { "php": "^7.4" } }""").With("index.php", "<?php\n");
        Assert.Contains(Analyze(old).Warnings, w => w.StartsWith("PHP 7.4 is past end-of-life"));
        using var eight2 = new TempRepo().With("composer.json", """{ "require": { "php": "8.2.*" } }""").With("index.php", "<?php\n");
        Assert.Contains(Analyze(eight2).Warnings, w => w.StartsWith("PHP 8.2 reaches end-of-life"));
        using var future = new TempRepo().With("composer.json", """{ "require": { "php": ">=9.0" } }""").With("index.php", "<?php\n");
        Assert.Contains(Analyze(future).Warnings, w => w.Contains("none of the PHP versions Gitberth knows"));
    }

    [Theory]
    [InlineData("8.4", "^8.2", true)]
    [InlineData("8.4", "^7.4 || ^8.0", true)]
    [InlineData("8.4", "~8.4.0", true)]
    [InlineData("8.5", "~8.4.0", false)]
    [InlineData("8.4", "~8.2", true)]
    [InlineData("8.4", ">=8.1 <8.4", false)]
    [InlineData("8.3", ">= 8.1, < 8.4", true)]
    [InlineData("8.4", ">=8.4.1", true)]
    [InlineData("8.2", "8.1 - 8.3", true)]
    [InlineData("8.4", "8.1 - 8.3", false)]
    [InlineData("8.4", "*", true)]
    [InlineData("8.4", "8.*", true)]
    [InlineData("7.4", "8.*", false)]
    [InlineData("8.4", "^8.2@dev", true)]
    public void Composer_constraints_are_checked_against_minor_versions(string minor, string constraint, bool satisfied) =>
        Assert.Equal(satisfied, PhpVersions.Satisfies(minor, constraint));

    [Fact]
    public void Env_scan_finds_getenv_env_superglobals_laravel_config_and_symfony_placeholders()
    {
        using var repo = LaravelApp()
            .With("config/services.php", "<?php\nreturn ['stripe' => ['key' => env('STRIPE_KEY'), 'region' => env('STRIPE_REGION', 'eu')], 'debug' => env('APP_DEBUG', false)];\n")
            .With("app/Support/Mailer.php", "<?php\n$h = getenv('SMTP_HOST');\n$p = $_ENV['SMTP_PORT'];\n$u = $_SERVER['SMTP_USER'];\n$host = $_SERVER['HTTP_HOST'];\n$x = env('NOT_IN_CONFIG');\n")
            .With("config/packages/messenger.yaml", "framework:\n  messenger:\n    transports:\n      async: '%env(MESSENGER_TRANSPORT_DSN)%'\n")
            .With("tests/Feature/ExampleTest.php", "<?php\ngetenv('TEST_ONLY');\n");
        var env = Analyze(repo).EnvSuggestions.ToDictionary(s => s.Key);

        Assert.True(env["APP_KEY"].IsSecret);
        Assert.Equal("used in config/services.php (default eu)", env["STRIPE_REGION"].Source);
        Assert.True(env["STRIPE_KEY"].IsSecret);
        Assert.Contains("SMTP_HOST", env.Keys);
        Assert.Contains("SMTP_PORT", env.Keys);
        Assert.Contains("SMTP_USER", env.Keys);
        Assert.Contains("MESSENGER_TRANSPORT_DSN", env.Keys);
        Assert.DoesNotContain("HTTP_HOST", env.Keys);
        Assert.DoesNotContain("APP_DEBUG", env.Keys);
        Assert.DoesNotContain("NOT_IN_CONFIG", env.Keys);
        Assert.DoesNotContain("TEST_ONLY", env.Keys);
    }
}
