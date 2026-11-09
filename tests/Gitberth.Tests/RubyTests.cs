using Gitberth.Detection;

namespace Gitberth.Tests;

public class RubyTests
{
    private const string RailsGemfile = "source \"https://rubygems.org\"\n\ngem \"rails\", \"~> 8.1\"\ngem \"puma\", \">= 5.0\"\n";

    private static DetectionResult Analyze(TempRepo repo, string? hint = null) => new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(hint));

    private static string Lf(string? text) => (text ?? "").ReplaceLineEndings("\n");

    /// <summary>A Gemfile.lock listing the given gems (name, version) for the given platforms.</summary>
    private static string Lock(string platforms, params (string Name, string Version)[] gems) =>
        "GEM\n  remote: https://rubygems.org/\n  specs:\n"
        + string.Concat(gems.Select(g => $"    {g.Name} ({g.Version})\n      something (>= 1)\n"))
        + $"\nPLATFORMS\n{string.Concat(platforms.Split(' ').Select(p => $"  {p}\n"))}\nDEPENDENCIES\n  rails (~> 8.1)\n\nRUBY VERSION\n   ruby 3.3.6p108\n\nBUNDLED WITH\n   2.6.2\n";

    private static TempRepo RailsApp(string gemfile = RailsGemfile) => new TempRepo()
        .With("Gemfile", gemfile)
        .With("config/application.rb", "require_relative \"boot\"\nrequire \"rails/all\"\n")
        .With("config.ru", "require_relative \"config/environment\"\nrun Rails.application\n")
        .With("bin/rails", "#!/usr/bin/env ruby\n");

    [Fact]
    public void Rails_app_with_postgres_and_jsbundling_builds_its_assets_and_prepares_the_database()
    {
        using var repo = RailsApp(RailsGemfile + "gem \"pg\"\ngem \"propshaft\"\ngem \"jsbundling-rails\"\n\ngroup :development, :test do\n  gem \"sqlite3\"\nend\n")
            .With("Gemfile.lock", Lock("x86_64-linux arm64-darwin", ("rails", "8.1.4.1"), ("pg", "1.6.2"), ("propshaft", "1.3.1"), ("jsbundling-rails", "1.3.1"), ("nokogiri", "1.18.10-x86_64-linux-gnu")))
            .With(".ruby-version", "3.3.6\n")
            .With("config/database.yml", "production:\n  url: <%= ENV[\"DATABASE_URL\"] %>\n")
            .With("config/credentials.yml.enc", "xyz")
            .With("package.json", """{ "scripts": { "build": "esbuild app/javascript/*.* --bundle --outdir=app/assets/builds" }, "devDependencies": { "esbuild": "^0.25.0" } }""")
            .With("yarn.lock", "");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.Ruby, r.Kind);
        Assert.Equal("Ruby (Rails)", r.Title);
        Assert.Equal(3000, r.ContainerPort);
        Assert.Equal("", r.BuildContext);
        Assert.Contains("Ruby 3.3 (.ruby-version: 3.3.6)", r.Evidence);
        Assert.Contains("Rails 8.1", r.Evidence);
        var dockerfile = Lf(r.Dockerfile);
        Assert.StartsWith("FROM ruby:3.3-slim AS base\nWORKDIR /app\nRUN apt-get update -qq && \\\n    apt-get install --no-install-recommends -y postgresql-client && \\", dockerfile);
        Assert.Contains("ENV BUNDLE_PATH=/usr/local/bundle BUNDLE_WITHOUT=development:test BUNDLE_DEPLOYMENT=1 \\\n    RAILS_ENV=production RAILS_LOG_TO_STDOUT=1 RAILS_SERVE_STATIC_FILES=1 \\\n    MALLOC_ARENA_MAX=2\n", dockerfile);
        Assert.Contains("apt-get install --no-install-recommends -y build-essential git pkg-config libyaml-dev libpq-dev && \\", dockerfile);
        // sqlite3 is only for development and test, so neither its headers nor its library come along.
        Assert.DoesNotContain("sqlite", dockerfile);
        Assert.Contains("COPY --from=node:24-slim /usr/local/bin/node /usr/local/bin/node\n", dockerfile);
        Assert.Contains("RUN npm install -g corepack@latest && corepack enable\n", dockerfile);
        // jsbundling runs the build itself during assets:precompile, so it isn't run twice.
        Assert.Contains("""
            COPY . .
            # gitberth:build-args
            # The gems install into a cache kept for this Gemfile.lock and Debian release, so their native extensions compile once.
            """.ReplaceLineEndings("\n"), dockerfile);
        Assert.Contains("RUN yarn install --frozen-lockfile\nRUN SECRET_KEY_BASE_DUMMY=1 ruby bin/rails assets:precompile\nRUN rm -rf node_modules tmp/cache\n", dockerfile);
        Assert.DoesNotContain("yarn build", dockerfile);
        Assert.Contains("CMD [\"sh\", \"-c\", \"rm -f tmp/pids/server.pid && ruby bin/rails db:prepare && exec ruby bin/rails server -b 0.0.0.0 -p ${PORT:-3000}\"]", dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("RAILS_MASTER_KEY"));
        Assert.Contains(r.Warnings, w => w.Contains("db:prepare") && w.Contains("DATABASE_URL"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("Gemfile.lock"));
        Assert.Contains(r.EnvSuggestions, s => s.Key == "RAILS_MASTER_KEY" && s.IsSecret);
    }

    [Fact]
    public void Minimal_rails_app_starts_rails_server_and_asks_for_a_secret_key_base()
    {
        using var repo = RailsApp();
        var r = Analyze(repo);

        Assert.Equal("Ruby (Rails)", r.Title);
        Assert.Contains("Ruby 3.4 (Gitberth's default)", r.Evidence);
        Assert.Contains(r.Warnings, w => w.StartsWith("No Gemfile.lock is committed"));
        Assert.Contains(r.Warnings, w => w.Contains("SECRET_KEY_BASE"));
        Assert.Contains(r.EnvSuggestions, s => s.Key == "SECRET_KEY_BASE" && s.IsSecret);
        Assert.Equal("""
            FROM ruby:3.4-slim AS base
            WORKDIR /app
            # Bundler leaves out the development and test gems. MALLOC_ARENA_MAX=2 keeps a threaded Ruby server's memory down.
            ENV BUNDLE_PATH=/usr/local/bundle BUNDLE_WITHOUT=development:test RAILS_ENV=production \
                RAILS_LOG_TO_STDOUT=1 RAILS_SERVE_STATIC_FILES=1 MALLOC_ARENA_MAX=2

            FROM base AS build
            # Compilers and headers for gems with native extensions.
            RUN apt-get update -qq && \
                apt-get install --no-install-recommends -y build-essential git pkg-config libyaml-dev && \
                rm -rf /var/lib/apt/lists /var/cache/apt/archives
            COPY . .
            # gitberth:build-args
            # The gems install into a cache kept for this Gemfile and Debian release, so their native extensions compile once.
            RUN --mount=type=cache,id=gems-3.4-HASH,target=/gems \
                gems=/gems/$(. /etc/os-release && echo "$VERSION_CODENAME") && \
                BUNDLE_PATH=$gems bundle install && \
                cp -a $gems/. /usr/local/bundle/ && \
                rm -rf /usr/local/bundle/ruby/*/cache /usr/local/bundle/ruby/*/bundler/gems/*/.git

            FROM base
            COPY --from=build /usr/local/bundle /usr/local/bundle
            COPY --from=build /app /app
            ENV PORT=3000
            # gitberth:volumes
            EXPOSE 3000
            CMD ["sh", "-c", "rm -f tmp/pids/server.pid && exec ruby bin/rails server -b 0.0.0.0 -p ${PORT:-3000}"]

            """.ReplaceLineEndings("\n"), System.Text.RegularExpressions.Regex.Replace(Lf(r.Dockerfile), "gems-3.4-[0-9a-f]{12}", "gems-3.4-HASH"));
    }

    [Fact]
    public void The_gem_cache_is_named_after_the_lockfile()
    {
        using var a = RailsApp().With("Gemfile.lock", Lock("x86_64-linux", ("rails", "8.1.4.1")));
        using var b = RailsApp().With("Gemfile.lock", Lock("x86_64-linux", ("rails", "8.1.3")));
        string Id(TempRepo repo) => System.Text.RegularExpressions.Regex.Match(Analyze(repo).Dockerfile!, "id=(gems-[^,]+)").Groups[1].Value;
        Assert.Matches("^gems-3.3-[0-9a-f]{12}$", Id(a));
        Assert.NotEqual(Id(a), Id(b));
    }

    [Fact]
    public void Rails_before_7_1_precompiles_with_a_placeholder_secret()
    {
        using var repo = RailsApp("gem \"rails\", \"~> 7.0.8\"\ngem \"sprockets-rails\"\n");
        Assert.Contains("RUN SECRET_KEY_BASE=precompile-placeholder ruby bin/rails assets:precompile\n", Analyze(repo).Dockerfile);
    }

    [Fact]
    public void Rails_production_settings_turn_into_warnings_and_solid_queue_runs_in_puma()
    {
        using var repo = RailsApp(RailsGemfile + "gem \"sqlite3\"\ngem \"solid_queue\"\n")
            .With("config/database.yml", "production:\n  primary:\n    database: storage/production.sqlite3\n")
            .With("config/environments/production.rb", "Rails.application.configure do\n  config.force_ssl = true\n  config.hosts << \"example.com\"\n  # config.hosts = []\nend\n")
            .With("config/puma.rb", "port ENV.fetch(\"PORT\", 3000)\nplugin :solid_queue if ENV[\"SOLID_QUEUE_IN_PUMA\"]\n");
        var r = Analyze(repo);

        Assert.Contains(r.Warnings, w => w.Contains("add a volume at /app/storage"));
        Assert.Contains(r.Warnings, w => w.Contains("config.force_ssl"));
        Assert.Contains(r.Warnings, w => w.Contains("config.hosts"));
        Assert.Contains("SOLID_QUEUE_IN_PUMA=1", r.Dockerfile);
        Assert.Contains("apt-get install --no-install-recommends -y sqlite3 && \\", r.Dockerfile);
        Assert.Contains("libsqlite3-dev", r.Dockerfile);
    }

    [Fact]
    public void Code_that_reads_credentials_without_the_encrypted_file_gets_a_warning_not_a_variable()
    {
        using var repo = RailsApp().With("config/initializers/stripe.rb", "Stripe.api_key = Rails.application.credentials.stripe[:secret_key]\n");
        var r = Analyze(repo);
        Assert.Contains(r.Warnings, w => w.StartsWith("The code reads Rails.application.credentials"));
        Assert.DoesNotContain(r.EnvSuggestions, s => s.Key.Contains("STRIPE", StringComparison.Ordinal));
    }

    [Fact]
    public void Rails_without_bin_rails_runs_config_ru_with_puma_and_its_tasks_with_rake()
    {
        using var repo = new TempRepo()
            .With("Gemfile", RailsGemfile + "gem \"sprockets-rails\"\n")
            .With("config/application.rb", "require \"rails\"\n")
            .With("config.ru", "run Rails.application\n")
            .With("Rakefile", "Rails.application.load_tasks\n")
            .With("config/database.yml", "production:\n  adapter: postgresql\n");
        var r = Analyze(repo);

        Assert.Contains("RUN SECRET_KEY_BASE_DUMMY=1 bundle exec rake assets:precompile\n", r.Dockerfile);
        Assert.Contains("CMD [\"sh\", \"-c\", \"bundle exec rake db:prepare && exec bundle exec puma -b tcp://0.0.0.0:${PORT:-3000}\"]", r.Dockerfile);
    }

    [Fact]
    public void Sinatra_with_config_ru_runs_on_puma()
    {
        using var repo = new TempRepo()
            .With("Gemfile", "gem 'sinatra', '~> 4.2'\ngem 'puma'\n")
            .With("config.ru", "require_relative 'app'\nrun App\n")
            .With("app.rb", "require 'sinatra/base'\n");
        var r = Analyze(repo);

        Assert.Equal("Ruby (Sinatra)", r.Title);
        Assert.Equal(9292, r.ContainerPort);
        Assert.Contains("RACK_ENV=production", r.Dockerfile);
        Assert.Contains("CMD [\"sh\", \"-c\", \"exec bundle exec puma -b tcp://0.0.0.0:${PORT:-9292}\"]", r.Dockerfile);
        Assert.Contains("Runs config.ru with Puma on port 9292", r.Evidence);
    }

    [Fact]
    public void Classic_sinatra_runs_its_own_file()
    {
        using var repo = new TempRepo()
            .With("Gemfile", "gem \"sinatra\"\ngem \"puma\"\n")
            .With("main.rb", "require \"sinatra\"\nget(\"/\") { \"hi\" }\n");
        var r = Analyze(repo);

        Assert.Equal(4567, r.ContainerPort);
        Assert.Contains("CMD [\"sh\", \"-c\", \"exec bundle exec ruby main.rb -o 0.0.0.0 -p ${PORT:-4567}\"]", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("rackup"));
    }

    [Theory]
    [InlineData("gem \"rack\", \"~> 3.2\"\ngem \"rackup\"\ngem \"webrick\"\n", "exec bundle exec rackup -o 0.0.0.0 -p ${PORT:-9292}")]
    [InlineData("gem \"rack\", \"~> 2.2\"\n", "exec bundle exec rackup -o 0.0.0.0 -p ${PORT:-9292}")]
    [InlineData("gem \"roda\"\ngem \"unicorn\"\n", "exec bundle exec unicorn -o 0.0.0.0 -p ${PORT:-9292}")]
    [InlineData("gem \"rack\"\ngem \"thin\"\n", "exec bundle exec thin start -a 0.0.0.0 -p ${PORT:-9292}")]
    public void Rack_apps_run_on_the_server_in_their_bundle(string gemfile, string start)
    {
        using var repo = new TempRepo().With("Gemfile", gemfile).With("config.ru", "run ->(env) { [200, {}, [\"ok\"]] }\n");
        Assert.Contains(start, Analyze(repo).Dockerfile);
    }

    [Fact]
    public void Rack_app_without_a_server_gets_puma_and_a_warning()
    {
        using var repo = new TempRepo().With("Gemfile", "gem \"rack\", \"~> 3.1\"\n").With("config.ru", "run ->(env) { [200, {}, [\"ok\"]] }\n");
        var r = Analyze(repo);
        Assert.Equal("Ruby (Rack)", r.Title);
        Assert.Contains("exec bundle exec puma", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("bundle add puma"));
    }

    [Fact]
    public void Hanami_compiles_its_assets_with_node()
    {
        using var repo = new TempRepo()
            .With("Gemfile", "gem \"hanami\", \"~> 2.2\"\ngem \"puma\"\n")
            .With("config.ru", "require \"hanami/boot\"\nrun Hanami.app\n")
            .With("config/assets.js", "export default {};\n")
            .With("package.json", """{ "type": "module", "dependencies": { "hanami-assets": "^2.2.0" } }""")
            .With("package-lock.json", "{}");
        var r = Analyze(repo);

        Assert.Equal("Ruby (Hanami)", r.Title);
        Assert.Equal(2300, r.ContainerPort);
        Assert.Contains("HANAMI_ENV=production", r.Dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=npm,target=/root/.npm npm ci\nRUN bundle exec hanami assets compile\nRUN rm -rf node_modules\n", Lf(r.Dockerfile));
    }

    [Fact]
    public void Procfile_web_and_release_start_the_app()
    {
        using var repo = RailsApp()
            .With("Procfile", "web: RAILS_ENV=production bundle exec puma -C config/puma.rb -p 5000\nrelease: bin/rails db:migrate\n");
        var r = Analyze(repo);

        Assert.Equal(5000, r.ContainerPort);
        Assert.Contains("CMD [\"sh\", \"-c\", \"bin/rails db:migrate && RAILS_ENV=production exec bundle exec puma -C config/puma.rb -p 5000\"]", r.Dockerfile);
        Assert.Contains("Runs the Procfile's release command first: `bin/rails db:migrate`", r.Evidence);
    }

    [Fact]
    public void Procfile_worker_runs_without_a_port()
    {
        using var repo = new TempRepo().With("Gemfile", "gem \"sidekiq\"\n").With("Procfile", "worker: bundle exec sidekiq -C config/sidekiq.yml\n");
        var r = Analyze(repo);

        Assert.Equal("Ruby (worker)", r.Title);
        Assert.Null(r.ContainerPort);
        Assert.DoesNotContain("EXPOSE", r.Dockerfile);
        Assert.Contains("CMD [\"sh\", \"-c\", \"exec bundle exec sidekiq -C config/sidekiq.yml\"]", r.Dockerfile);
    }

    [Fact]
    public void A_gemfile_for_tooling_is_not_an_app()
    {
        // React Native and fastlane repos keep a Gemfile for their tools.
        using var repo = new TempRepo().With("Gemfile", "gem \"fastlane\"\ngem \"cocoapods\"\n").With("index.html", "<p>hi</p>");
        var r = Analyze(repo);
        Assert.Equal(ProjectKind.Static, r.Kind);
        Assert.DoesNotContain(new ProjectAnalyzer().FindApps(repo.Root), c => c.Kind == ProjectKind.Ruby);
    }

    [Fact]
    public void A_front_end_built_with_bun_gets_bun_in_the_build_stage()
    {
        using var repo = RailsApp()
            .With("package.json", """{ "packageManager": "bun@1.3.2", "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("bun.lock", "{}");
        var dockerfile = Lf(Analyze(repo).Dockerfile);
        Assert.Contains("COPY --from=oven/bun:1.3.2 /usr/local/bin/bun /usr/local/bin/bun\nRUN ln -s bun /usr/local/bin/bunx\n", dockerfile);
        Assert.Contains("bun install --frozen-lockfile", dockerfile);
    }

    [Fact]
    public void Rails_wins_over_a_package_json_that_only_builds_assets_but_not_over_a_node_server()
    {
        using var assets = RailsApp().With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""");
        Assert.Equal(ProjectKind.Ruby, Analyze(assets).Kind);

        using var server = RailsApp().With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.0.0" } }""");
        Assert.Equal(ProjectKind.NodeServer, Analyze(server).Kind);
        Assert.Equal(ProjectKind.Ruby, Analyze(server, "ruby").Kind);
    }

    [Fact]
    public void The_ruby_hint_picks_the_app_next_to_a_dockerfile()
    {
        using var repo = RailsApp().With("Dockerfile", "FROM ruby:3.4\nEXPOSE 80\n");
        Assert.Equal(ProjectKind.Dockerfile, Analyze(repo).Kind);
        Assert.Equal(ProjectKind.Ruby, Analyze(repo, "ruby").Kind);
        Assert.Contains(new ProjectAnalyzer().FindApps(repo.Root), c => c.Kind == ProjectKind.Ruby && c.Hint == "ruby");
    }

    [Fact]
    public void Lockfile_without_a_linux_platform_gets_one_in_the_build()
    {
        using var repo = RailsApp().With("Gemfile.lock", Lock("arm64-darwin-24", ("rails", "8.1.4.1"), ("nokogiri", "1.18.10-arm64-darwin")));
        var r = Analyze(repo);
        Assert.Contains("RUN BUNDLE_DEPLOYMENT=false bundle lock --add-platform x86_64-linux aarch64-linux\n", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("arm64-darwin-24") && w.Contains("bundle lock --add-platform"));
        Assert.DoesNotContain("liblzma-dev", r.Dockerfile);
    }

    [Fact]
    public void Nokogiri_without_a_precompiled_build_gets_what_it_compiles_with()
    {
        using var source = RailsApp().With("Gemfile.lock", Lock("ruby x86_64-linux", ("rails", "8.1.4.1"), ("nokogiri", "1.18.10")));
        Assert.Contains("libyaml-dev patch zlib1g-dev liblzma-dev", Analyze(source).Dockerfile);

        using var precompiled = RailsApp().With("Gemfile.lock", Lock("x86_64-linux", ("rails", "8.1.4.1"), ("nokogiri", "1.18.10-x86_64-linux-gnu")));
        Assert.DoesNotContain("liblzma-dev", Analyze(precompiled).Dockerfile);
    }

    [Theory]
    [InlineData("ruby \"3.2.2\"\n", null, "3.2.2", "Gemfile: ruby \"3.2.2\"")]
    [InlineData("ruby \"~> 3.3\"\n", null, "3.4", "Gemfile: ruby \"~> 3.3\"")]
    [InlineData("ruby \">= 3.1\", \"< 3.4\"\n", null, "3.3", "Gemfile: ruby \">= 3.1, < 3.4\"")]
    [InlineData("ruby \"~> 3.2.0\"\n", null, "3.2", "Gemfile: ruby \"~> 3.2.0\"")]
    [InlineData("ruby file: \".ruby-version\"\n", "3.3.4", "3.3.4", ".ruby-version: 3.3.4")]
    [InlineData("", "ruby-3.3.4", "3.3", ".ruby-version: 3.3.4")]
    [InlineData("", null, "3.4", "Gitberth's default")]
    public void Ruby_version_comes_from_the_gemfile_or_version_files(string rubyLine, string? versionFile, string tag, string source)
    {
        using var repo = new TempRepo().With("Gemfile", rubyLine + "gem \"puma\"\n").With("config.ru", "run App\n");
        if (versionFile is not null) repo.With(".ruby-version", versionFile + "\n");
        var r = Analyze(repo);
        Assert.Contains($"FROM ruby:{tag}-slim AS base", r.Dockerfile);
        Assert.Contains($"Ruby {tag} ({source})", r.Evidence);
    }

    [Fact]
    public void Old_ruby_from_tool_versions_or_the_lockfile_warns()
    {
        using var tools = new TempRepo().With("Gemfile", "gem \"puma\"\n").With("config.ru", "run App\n").With(".tool-versions", "nodejs 22.1.0\nruby 3.1.4\n");
        var r = Analyze(tools);
        Assert.Contains("FROM ruby:3.1-slim AS base", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("Ruby 3.1 is past end-of-life"));

        using var locked = RailsApp().With("Gemfile.lock", Lock("x86_64-linux", ("rails", "8.1.4.1")));
        Assert.Contains("Ruby 3.3 (Gemfile.lock: ruby 3.3.6)", Analyze(locked).Evidence);
    }

    [Fact]
    public void Gemfile_groups_decide_what_the_image_gets()
    {
        var app = new RubyAppProbe("""
            source "https://rubygems.org"
            gem "rails"
            gem "rspec-rails", group: :test
            gem "pg", groups: [:production]
            group :development, :test do
              platforms :mri do
                gem "debug" # a comment with "quotes"
              end
              gem "sqlite3"
            end
            group :production do
              gem "redis"
            end
            if ENV["CI"]
              gem "simplecov"
            end
            ruby "3.4.1"
            """);
        Assert.Equal(["pg", "rails", "redis", "simplecov"], app.Gems.Order());
        Assert.Equal(["debug", "rspec-rails", "sqlite3"], app.DevGems.Order());
        Assert.Equal(["3.4.1"], app.RubyRequirements);
    }

    [Fact]
    public void Lockfile_is_read_for_versions_platforms_and_precompiled_builds()
    {
        var app = new RubyAppProbe("gem \"rails\"\n", Lock("ruby x86_64-linux-gnu", ("rails", "8.1.4.1"), ("nokogiri", "1.18.10-x86_64-linux-gnu"), ("nokogiri", "1.18.10")));
        Assert.Equal("8.1.4.1", app.Locked["rails"]);
        Assert.Equal("1.18.10", app.Locked["nokogiri"]);
        Assert.Contains("nokogiri", app.LinuxBuilds);
        Assert.Equal(["ruby", "x86_64-linux-gnu"], app.Platforms);
        Assert.Equal("3.3.6", app.LockedRuby);
    }

    [Theory]
    [InlineData("~> 3.2", "3.4", true)]
    [InlineData("~> 3.2", "4.0", false)]
    [InlineData("~> 3.2.0", "3.2", true)]
    [InlineData("~> 3.2.0", "3.3", false)]
    [InlineData(">= 3.1", "4.0", true)]
    [InlineData("< 3.4", "3.4", false)]
    [InlineData("<= 3.4", "3.4", true)]
    [InlineData("3.3", "3.3", true)]
    public void Ruby_requirements_are_checked_against_minor_versions(string requirement, string minor, bool satisfied) =>
        Assert.Equal(satisfied, RubyVersions.Satisfies(minor, requirement));

    [Theory]
    [InlineData(null, "bundle exec puma", "exec bundle exec puma")]
    [InlineData("bin/rails db:prepare", "bundle exec puma", "bin/rails db:prepare && exec bundle exec puma")]
    [InlineData(null, "RACK_ENV=production WEB=1 bundle exec puma", "RACK_ENV=production WEB=1 exec bundle exec puma")]
    [InlineData(null, "bin/rails db:migrate && bin/rails server", "bin/rails db:migrate && bin/rails server")]
    [InlineData(null, "exec bundle exec puma", "exec bundle exec puma")]
    public void Start_commands_exec_the_server(string? before, string command, string expected) =>
        Assert.Equal(expected, RubyLanguage.Chain(before, command));

    [Theory]
    [InlineData("bundle exec puma -p 5000", 5000)]
    [InlineData("bundle exec puma --port=5001 -C config/puma.rb", 5001)]
    [InlineData("bin/rails server -p ${PORT:-4000}", 4000)]
    [InlineData("bundle exec puma -b tcp://0.0.0.0:7000", 7000)]
    [InlineData("bundle exec puma -p $PORT", null)]
    public void Ports_in_start_commands_are_found(string command, int? port) => Assert.Equal(port, RubyLanguage.PortIn(command));

    [Theory]
    [InlineData("port ENV.fetch(\"PORT\") { 3001 }", 3001)]
    [InlineData("port ENV.fetch('PORT', 4001)", 4001)]
    [InlineData("port 3000", null)]
    public void Puma_config_ports_are_found(string config, int? port) => Assert.Equal(port, RubyLanguage.PumaPort(config));

    [Fact]
    public void Env_scan_finds_env_reads_with_their_defaults_and_skips_specs()
    {
        using var repo = RailsApp()
            .With("app/models/payment.rb", "KEY = ENV[\"STRIPE_SECRET_KEY\"]\nURL = ENV.fetch('REDIS_URL', \"redis://localhost:6379\")\nWORKERS = ENV.fetch(\"WORKERS\") { 2 }\n")
            .With("config/puma.rb", "threads ENV.fetch(\"RAILS_MAX_THREADS\", 3)\nport ENV.fetch(\"PORT\", 3000)\n")
            .With("config/storage.yml", "amazon:\n  bucket: <%= ENV[\"S3_BUCKET\"] %>\n")
            .With("spec/models/payment_spec.rb", "ENV[\"SPEC_ONLY\"]\n")
            .With("config/environments/development.rb", "ENV[\"DEV_ONLY\"]\n");
        var env = Analyze(repo).EnvSuggestions.ToDictionary(s => s.Key);

        Assert.True(env["STRIPE_SECRET_KEY"].IsSecret);
        Assert.Equal("", env["REDIS_URL"].Value);
        Assert.Equal("used in app/models/payment.rb (default redis://localhost:6379)", env["REDIS_URL"].Source);
        Assert.Contains("WORKERS", env.Keys);
        Assert.Contains("S3_BUCKET", env.Keys);
        Assert.DoesNotContain("RAILS_MAX_THREADS", env.Keys);
        Assert.DoesNotContain("PORT", env.Keys);
        Assert.DoesNotContain("SPEC_ONLY", env.Keys);
        Assert.DoesNotContain("DEV_ONLY", env.Keys);
    }

    [Fact]
    public void App_finder_offers_a_rails_app_in_a_sub_folder()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.0.0" } }""")
            .With("backend/Gemfile", RailsGemfile)
            .With("backend/config/application.rb", "require \"rails\"\n")
            .With("backend/bin/rails", "#!/usr/bin/env ruby\n");
        var apps = new ProjectAnalyzer().FindApps(repo.Root);
        Assert.Contains(apps, c => c.Dir == "backend" && c.Kind == ProjectKind.Ruby && c.Title == "Ruby (Rails)");
        var r = new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(AppDir: "backend"));
        Assert.Equal("backend", r.BuildContext);
    }

    /// <summary>Reads a Gemfile and Gemfile.lock the way detection does.</summary>
    private sealed class RubyAppProbe
    {
        private readonly RubyApp _app;

        public RubyAppProbe(string gemfile, string? lockfile = null)
        {
            using var repo = new TempRepo().With("Gemfile", gemfile);
            if (lockfile is not null) repo.With("Gemfile.lock", lockfile);
            _app = RubyApp.Load(repo.Root)!;
        }

        public HashSet<string> Gems => _app.Gems;
        public HashSet<string> DevGems => _app.DevGems;
        public List<string> RubyRequirements => _app.RubyRequirements;
        public Dictionary<string, string> Locked => _app.Locked;
        public HashSet<string> LinuxBuilds => _app.LinuxBuilds;
        public List<string> Platforms => _app.Platforms;
        public string? LockedRuby => _app.LockedRuby;
    }
}
