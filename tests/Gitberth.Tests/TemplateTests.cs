using System.Text;
using Gitberth.Apps;
using Gitberth.Audit;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Services;
using Gitberth.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// App templates: every file of the catalog Gitberth ships passes its checks, and a file that breaks one is left out with
/// why; the gallery's search and categories; an install's plan (its variables, secrets, built-ins and name); a database's
/// connection string in parts for Compose apps; and an install end to end against the test rig.
/// </summary>
public sealed class TemplateTests
{
    // ---- The catalog Gitberth ships ----

    [Fact]
    public void Every_template_Gitberth_ships_passes_its_checks()
    {
        var catalog = TemplateCatalog.Bundled;
        Assert.Empty(catalog.LoadProblems);
        Assert.InRange(catalog.All.Count, 15, 20);
        // Every file in the folder is in the assembly, and each is in the gallery.
        var folder = Path.Combine(RepoRoot(), "src", "Gitberth", "Templates", "catalog");
        var files = Directory.GetFiles(folder, "*.yaml").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(files, catalog.All.Select(t => t.Id).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(catalog.All.Count, catalog.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in catalog.All)
        {
            Assert.NotNull(catalog.Icon(t.Id));
            Assert.NotEmpty(t.Images);
            Assert.Equal(t.Web.Service, t.Services[0]);
        }
    }

    [Fact]
    public void Every_shipped_image_is_pinned_to_a_version_and_no_secret_is_written_in_a_file()
    {
        foreach (var t in TemplateCatalog.Bundled.All)
        {
            foreach (var image in t.Images)
            {
                Assert.Null(TemplateCatalog.ImageProblem(image, t.LatestOnly));
                if (image.EndsWith(":latest", StringComparison.Ordinal)) Assert.False(string.IsNullOrWhiteSpace(t.LatestOnly), $"{t.Id} runs latest without saying why");
            }
            // The words of a secret's value never appear: secrets are ${…} the install fills.
            Assert.DoesNotMatch(@"(?i)(password|secret|token|key)\s*[:=]\s*['""]?[A-Za-z0-9+/]{12,}", t.Compose);
            Assert.False(Security.Masking.HasUrlPassword(t.Compose.Replace("${", "", StringComparison.Ordinal)), t.Id);
        }
    }

    [Fact]
    public void The_shipped_templates_cover_a_plain_app_postgres_redis_and_one_with_its_own_database()
    {
        var all = TemplateCatalog.Bundled.All;
        Assert.Contains(all, t => t.Databases.Count == 0 && t.Services.Count == 1);
        Assert.Contains(all, t => t.Databases.Any(d => d.Kind == DatabaseKind.Postgres));
        Assert.Contains(all, t => t.Databases.Any(d => d.Kind == DatabaseKind.Redis));
        Assert.Contains(all, t => t.Databases.Count == 0 && t.Services.Count > 1);
    }

    [Fact]
    public void Every_shipped_template_passes_the_checks_a_pull_requests_preview_gets()
    {
        foreach (var t in TemplateCatalog.Bundled.All)
        {
            var model = TemplateCatalog.ModelOf(t.Compose);
            Assert.NotNull(model);
            var plan = ComposeTransform.Apply(model!, new ComposeTarget
            {
                Slug = t.Id, WebService = t.Web.Service, Tag = "t", Preview = true, ReleaseDir = "/r", ReleaseHostDir = "/r", DataDir = "/d", DataHostDir = "/d",
                Ports = [new PortMapping { ContainerPort = t.Web.Port, HostPort = t.Web.Port, Access = Network.PortAccess.Lan, IsWeb = true }],
                Kind = _ => PathKind.None, LinksOut = (_, _) => false,
            });
            Assert.True(plan.Notes.Count == 0 && plan.Problems.Count == 0, $"{t.Id}: {string.Join(" / ", plan.Notes.Concat(plan.Problems))}");
        }
    }

    // Nothing to sign in to: tools that run in the browser, and a start page.
    private static readonly string[] NoAccounts = ["it-tools", "excalidraw", "homepage"];

    [Fact]
    public void Every_template_generates_its_admins_credentials_or_is_open_until_set_up()
    {
        foreach (var t in TemplateCatalog.Bundled.All.Where(t => !t.OpenUntilSetup && !NoAccounts.Contains(t.Id)))
        {
            // Its admin's password or token is made at install, and its file passes it on: nobody can sign in with a known one.
            var made = t.Secrets.Where(s => s.Name.Contains("PASSWORD", StringComparison.Ordinal) || s.Name.Contains("TOKEN", StringComparison.Ordinal)).ToList();
            Assert.True(made.Count > 0, $"{t.Id} neither makes its admin's credentials nor is open_until_setup");
            Assert.All(made, s => Assert.Contains("${" + s.Name + "}", t.Compose));
        }
        // The ones whose first visitor sets them up (or that sign in with a password everyone knows) say so.
        Assert.Equal(["actual-budget", "ghost", "gitea", "jellyfin", "mealie", "memos", "n8n", "syncthing", "uptime-kuma", "wordpress"],
            TemplateCatalog.Bundled.All.Where(t => t.OpenUntilSetup).Select(t => t.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_shipped_templates_preset_what_their_projects_let_them()
    {
        var catalog = TemplateCatalog.Bundled;
        // Stirling-PDF's first admin is made from a generated password, not its usual admin/stirling.
        var stirling = catalog.Find("stirling-pdf")!;
        Assert.Contains("SECURITY_INITIALLOGIN_PASSWORD: ${ADMIN_PASSWORD}", stirling.Compose);
        Assert.Contains("SECURITY_ENABLELOGIN: \"true\"", stirling.Compose);
        // Vaultwarden takes no sign-ups; its admin page invites, with a generated token.
        var vault = catalog.Find("vaultwarden")!;
        Assert.Contains("SIGNUPS_ALLOWED: \"false\"", vault.Compose);
        Assert.Contains(vault.Secrets, s => s.Name == "ADMIN_TOKEN" && s.Length >= 32);
        // A database server of its own has a generated root password, masked like any secret, rather than one it makes up and prints.
        foreach (var id in new[] { "ghost", "wordpress" })
        {
            var t = catalog.Find(id)!;
            Assert.Contains(t.Secrets, s => s.Name == "DB_ROOT_PASSWORD");
            Assert.DoesNotContain("RANDOM_ROOT_PASSWORD", t.Compose);
        }
        // Paperless installs any other OCR language it's asked for.
        var paperless = catalog.Find("paperless-ngx")!;
        Assert.Contains(paperless.Inputs, i => i.Name == "OCR_LANGUAGES" && !i.Required);
        Assert.Contains("PAPERLESS_OCR_LANGUAGES: ${OCR_LANGUAGES:-}", paperless.Compose);
        Assert.Contains("eng, deu, fra, ita and spa", paperless.Inputs.Single(i => i.Name == "OCR_LANGUAGE").Help);
        // Jellyfin runs an exact build.
        Assert.Equal(["jellyfin/jellyfin:12.4.20261026-211555"], catalog.Find("jellyfin")!.Images);
    }

    // ---- What a file must be ----

    private const string Good = """
        id: notes
        name: Notes
        tagline: A place for notes.
        description: Notes keeps your notes in one place, with tags and search, and an API for everything else you use.
        category: Documents
        website: https://notes.example
        source: https://github.com/example/notes
        license: MIT
        icon: notes.svg
        icon_source: monogram
        memory: 128
        web:
          service: web
          port: 8080
          health: /health
        inputs:
          - name: ADMIN_EMAIL
            label: Admin's email
            required: true
            kind: email
          - name: SIGNUPS
            label: Anyone can sign up
            default: "false"
            kind: bool
        secrets:
          - name: SECRET_KEY
            length: 32
        databases:
          - kind: postgres
            variable: DATABASE_URL
        notes:
          - The first account is the admin.
        compose: |
          services:
            web:
              image: example/notes:1.2.3
              environment:
                ADMIN_EMAIL: ${ADMIN_EMAIL}
                SIGNUPS: ${SIGNUPS}
                SECRET_KEY: ${SECRET_KEY}
                DB_HOST: ${DATABASE_URL_HOST}
                DB_PASSWORD: ${DATABASE_URL_PASSWORD}
                PUBLIC_URL: ${APP_URL}
              volumes:
                - data:/data
          volumes:
            data:
        """;

    private const string Icon = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect width="64" height="64" rx="15" fill="#5b7cfa"/></svg>""";

    private static TemplateCatalog CatalogOf(string yaml, string id = "notes", string? icon = Icon) =>
        new([(id + ".yaml", Encoding.UTF8.GetBytes(yaml)), .. icon is null ? Array.Empty<(string, byte[])>() : [(id + ".svg", Encoding.UTF8.GetBytes(icon))]]);

    [Fact]
    public void A_template_that_follows_the_rules_is_in_the_gallery_with_what_its_file_says()
    {
        var catalog = CatalogOf(Good);
        Assert.Empty(catalog.LoadProblems);
        var t = Assert.Single(catalog.All);
        Assert.Equal(("notes", "Notes", "web", 8080, "/health"), (t.Id, t.Name, t.Web.Service, t.Web.Port, t.Web.Health));
        Assert.Equal(["ADMIN_EMAIL", "SIGNUPS"], t.Inputs.Select(i => i.Name));
        Assert.Equal((TemplateInputKind.Email, true), (t.Inputs[0].Kind, t.Inputs[0].Required));
        Assert.Equal(new TemplateSecret("SECRET_KEY", 32, SecretAlphabet.Alphanumeric), t.Secrets.Single());
        Assert.Equal(new TemplateDatabase(DatabaseKind.Postgres, "DATABASE_URL"), t.Databases.Single());
        Assert.Equal(["example/notes:1.2.3"], t.Images);
        Assert.True(t.NeedsPostgres);
        Assert.Equal("Postgres", t.DatabasesText);
        Assert.Equal("128 MB", t.MemoryText);
        Assert.Equal("image/svg+xml", catalog.Icon("notes")!.ContentType);
    }

    [Theory]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:latest\n", "runs latest")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes\n", "has no tag")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      ports:\n        - \"8080:8080\"\n", "publishes ports itself")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      privileged: true\n", "has privileged")]
    [InlineData("        - data:/data\n", "        - /srv/notes:/data\n", "isn't a named volume")]
    [InlineData("PUBLIC_URL: ${APP_URL}", "PUBLIC_URL: ${PUBLIC_URL}", "uses ${PUBLIC_URL}")]
    [InlineData("SECRET_KEY: ${SECRET_KEY}", "SECRET_KEY: hunter2hunter2", "is written in the file")]
    [InlineData("SIGNUPS: ${SIGNUPS}", "SIGNUPS: \"no\"", "SIGNUPS is declared but its Compose file doesn't use it")]
    [InlineData("PUBLIC_URL: ${APP_URL}", "PUBLIC_URL: postgres://u:pw@db/x", "has a password written in the file")]
    [InlineData("  service: web\n", "  service: site\n", "There's no service site")]
    [InlineData("category: Documents", "category: Stuff", "isn't one of the gallery's")]
    [InlineData("license: MIT\n", "license: MIT\ncolour: blue\n", "doesn't know: colour")]
    [InlineData("    kind: email", "    kind: password", "kind Gitberth doesn't know")]
    [InlineData("    length: 32", "    length: 8", "16 to 128 characters")]
    [InlineData("website: https://notes.example", "website: http://notes.example", "isn't an https:// address")]
    [InlineData("  - The first account is the admin.", "  - the first account is the admin", "isn't a sentence")]
    [InlineData("DB_HOST: ${DATABASE_URL_HOST}\n        DB_PASSWORD: ${DATABASE_URL_PASSWORD}", "OTHER: x", "DATABASE_URL isn't used")]
    // What reaches past its containers into the host: never in a template's file, whichever way it's written.
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      use_api_socket: true\n", "the Docker API socket")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      security_opt:\n        - seccomp:unconfined\n        - apparmor:unconfined\n", "security options")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      sysctls:\n        net.ipv4.ip_forward: \"1\"\n", "sysctls")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      cgroup: host\n", "web has cgroup")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      uts: host\n", "web has uts")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      extra_hosts:\n        - \"host.docker.internal:host-gateway\"\n", "host names that lead to the host")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      post_start:\n        - command: [\"sh\", \"-c\", \"id\"]\n          privileged: true\n          user: root\n", "post_start hooks")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      gpus: all\n", "GPUs")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      runtime: nvidia\n", "its runtime")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      device_cgroup_rules:\n        - \"c 1:3 mr\"\n", "device rules")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      cgroup_parent: m-executor-abcd\n", "its cgroup parent")]
    [InlineData("  volumes:\n    data:", "  volumes:\n    data:\n      driver_opts:\n        type: none\n        o: bind\n        device: /", "a template's volumes are plain ones of its own")]
    [InlineData("  volumes:\n    data:", "  volumes:\n    data:\n      name: gitberth-data", "a template's volumes are plain ones of its own")]
    [InlineData("  volumes:\n    data:", "  volumes:\n    data:\n  networks:\n    lan:\n      driver: macvlan\n      driver_opts:\n        parent: eth0", "a template's networks are plain bridges of its own")]
    [InlineData("      image: example/notes:1.2.3\n", "      image: example/notes:1.2.3\n      x-defaults: &evil\n        privileged: true\n        network_mode: host\n        pid: host\n        volumes: [\"/:/host\"]\n    worker:\n      image: example/notes:1.2.3\n      <<: *evil\n", "anchors, aliases or merge keys")]
    public void A_template_that_breaks_a_rule_is_left_out_with_why(string from, string to, string why)
    {
        var yaml = Good.Replace(from.Replace("\n", Environment.NewLine, StringComparison.Ordinal), to.Replace("\n", Environment.NewLine, StringComparison.Ordinal), StringComparison.Ordinal);
        if (yaml == Good) yaml = Good.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Good, yaml);
        var catalog = CatalogOf(yaml);
        Assert.Empty(catalog.All);
        Assert.Contains(catalog.LoadProblems, p => p.Contains(why, StringComparison.Ordinal));
    }

    [Fact]
    public void A_template_needs_its_icon_and_an_icon_is_only_a_picture()
    {
        Assert.Contains("isn't next to it", Assert.Single(CatalogOf(Good, icon: null).LoadProblems));
        Assert.Contains(CatalogOf(Good, icon: Icon.Replace("<rect", "<script>alert(1)</script><rect", StringComparison.Ordinal)).LoadProblems,
            p => p.Contains("an icon is a picture", StringComparison.Ordinal));
        Assert.Contains(CatalogOf(Good, icon: Icon.Replace("<rect", "<image href=\"https://example.com/x.png\"/><rect", StringComparison.Ordinal)).LoadProblems,
            p => p.Contains("an icon is a picture", StringComparison.Ordinal));
        Assert.Contains(CatalogOf(Good, icon: Icon.Replace("<rect", "<rect onload=\"x()\"", StringComparison.Ordinal)).LoadProblems,
            p => p.Contains("an icon is a picture", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_whose_id_isnt_its_name_or_that_isnt_yaml_is_left_out()
    {
        Assert.Contains("isn't its file's name", Assert.Single(CatalogOf(Good, id: "other").LoadProblems, p => p.Contains(" id ", StringComparison.Ordinal)));
        Assert.Contains(CatalogOf("id: notes\nname: [unclosed").LoadProblems, p => p.Contains("isn't valid YAML", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("postgres:18", null)]
    [InlineData("ghcr.io/acme/app:v1.2.3", null)]
    [InlineData("registry.example:5000/app:2", null)]
    [InlineData("acme/app@sha256:0123456789abcdef", null)]
    [InlineData("registry.example:5000/app", "has no tag")]
    [InlineData("acme/app:latest", "runs latest")]
    [InlineData("acme/app:stable", "which moves")]
    [InlineData("acme/app:${TAG}", "is a variable")]
    public void An_image_is_pinned_to_a_version_tag(string image, string? why)
    {
        var problem = TemplateCatalog.ImageProblem(image, latestOnly: null);
        if (why is null) Assert.Null(problem);
        else Assert.Contains(why, problem);
        Assert.Null(TemplateCatalog.ImageProblem("acme/app:latest", "It publishes only latest."));
    }

    // ---- The gallery ----

    [Fact]
    public void The_gallery_searches_every_word_and_filters_by_category()
    {
        var catalog = TemplateCatalog.Bundled;
        Assert.Equal(catalog.All, catalog.Search(null));
        Assert.Equal(catalog.All, catalog.Search("   "));
        // A name that starts with the search comes first.
        Assert.Equal("Grafana", catalog.Search("graf")[0].Name);
        // Every word must be somewhere: the name, tagline, description, category or id.
        Assert.All(catalog.Search("recipes shopping"), t => Assert.Equal("mealie", t.Id));
        Assert.Contains(catalog.Search("PASSWORD"), t => t.Id == "vaultwarden");
        Assert.Empty(catalog.Search("no-such-thing-at-all"));
        // A category chip shows only its own, and the search narrows it further.
        var monitoring = catalog.Search(null, "Monitoring");
        Assert.NotEmpty(monitoring);
        Assert.All(monitoring, t => Assert.Equal("Monitoring", t.Category));
        Assert.Equal(["uptime-kuma"], catalog.Search("kuma", "Monitoring").Select(t => t.Id));
        Assert.Empty(catalog.Search("kuma", "Media"));
        // The chips: every category in use, in the gallery's order.
        Assert.Equal(catalog.All.Select(t => t.Category).Distinct().Count(), catalog.Categories.Count);
        Assert.Equal(catalog.Categories.OrderBy(c => TemplateCatalog.CategoryOrder.ToList().IndexOf(c)), catalog.Categories);
    }

    // ---- An install's plan ----

    private static AppTemplate Notes() => CatalogOf(Good).All.Single();

    [Fact]
    public void An_installs_variables_are_its_inputs_and_new_secrets_and_not_its_address()
    {
        var t = Notes();
        var made = 0;
        var (values, problems) = TemplateInstaller.Values(t, new Dictionary<string, string> { ["ADMIN_EMAIL"] = " me@example.com " }, s => $"generated-{s.Name}-{++made}");
        Assert.Empty(problems);
        // APP_URL isn't kept as a variable: each deploy says where the app is reached (TemplateBuiltIns).
        Assert.Equal(["ADMIN_EMAIL=me@example.com:False", "SIGNUPS=false:False", "SECRET_KEY=generated-SECRET_KEY-1:True"],
            values.Select(v => $"{v.Key}={v.Value}:{v.Secret}"));
        // The address: a domain's https one, else the LAN host's on the web port's host port.
        Assert.Equal(new TemplateAddress("https://notes.example.com", "notes.example.com", true), TemplateAddress.For("notes.example.com", "192.168.1.20", 8080));
        Assert.Equal(new TemplateAddress("http://192.168.1.20:8080", "192.168.1.20", false), TemplateAddress.For(null, "192.168.1.20", 8080));
        Assert.Equal("http://[fd00::5]:8080", TemplateAddress.For(null, "fd00::5", 8080).Url);
        Assert.Equal(["APP_URL=https://x.example.com", "APP_HOST=x.example.com", "APP_SCHEME=https", "APP_HTTPS=true"],
            TemplateBuiltIns.Values(TemplateAddress.For("x.example.com", "h", 1)).Select(v => $"{v.Key}={v.Value}"));
    }

    [Fact]
    public void An_input_that_doesnt_fit_its_kind_is_said_and_a_switch_is_true_or_false()
    {
        var t = Notes();
        var (values, problems) = TemplateInstaller.Values(t, new Dictionary<string, string> { ["ADMIN_EMAIL"] = "not an email", ["SIGNUPS"] = "on" }, _ => "x");
        Assert.Equal(["Admin's email isn't an email address."], problems);
        Assert.Equal("true", values.Single(v => v.Key == "SIGNUPS").Value);
        (_, problems) = TemplateInstaller.Values(t, new Dictionary<string, string>(), _ => "x");
        Assert.Equal(["Admin's email is needed."], problems);
        var url = new TemplateInput("SITE", "Site", null, null, false, TemplateInputKind.Url);
        Assert.Null(TemplateCatalog.InputProblem(url, "https://example.com"));
        Assert.NotNull(TemplateCatalog.InputProblem(url, "ftp://example.com"));
        Assert.NotNull(TemplateCatalog.InputProblem(new TemplateInput("N", "Count", null, null, false, TemplateInputKind.Number), "1.5"));
        Assert.NotNull(TemplateCatalog.InputProblem(new TemplateInput("T", "Title", null, null, false, TemplateInputKind.Text), "a\nb"));
    }

    [Fact]
    public void A_generated_secret_is_new_every_time_of_its_length_and_alphabet()
    {
        var alnum = new TemplateSecret("K", 48, SecretAlphabet.Alphanumeric);
        var first = TemplateInstaller.Generate(alnum);
        Assert.Equal(48, first.Length);
        Assert.Matches("^[A-Za-z0-9]{48}$", first);
        Assert.NotEqual(first, TemplateInstaller.Generate(alnum));
        Assert.Matches("^[0-9a-f]{64}$", TemplateInstaller.Generate(new TemplateSecret("H", 64, SecretAlphabet.Hex)));
    }

    [Fact]
    public void The_nth_app_of_a_template_gets_a_numbered_name()
    {
        Assert.Equal("Uptime Kuma", TemplateInstaller.FreeName("Uptime Kuma", 1));
        Assert.Equal("Uptime Kuma 2", TemplateInstaller.FreeName("Uptime Kuma", 2));
    }

    // ---- A database's connection string in parts ----

    [Fact]
    public void A_postgres_url_in_parts_has_its_host_port_name_user_and_password_unescaped()
    {
        Assert.Equal(
            [("DATABASE_URL_HOST", "postgres"), ("DATABASE_URL_PORT", "5432"), ("DATABASE_URL_NAME", "shop"), ("DATABASE_URL_USER", "shop"), ("DATABASE_URL_PASSWORD", "p@ss/word")],
            DatabaseUrls.Parts("DATABASE_URL", "postgres://shop:p%40ss%2Fword@postgres:5432/shop"));
        // No port: Postgres's own.
        Assert.Contains(("DB_PORT", "5432"), DatabaseUrls.Parts("DB", "postgresql://a:b@db/x?sslmode=disable"));
        Assert.Contains(("DB_NAME", "x"), DatabaseUrls.Parts("DB", "postgresql://a:b@db/x?sslmode=disable"));
    }

    [Fact]
    public void A_redis_url_in_parts_has_its_host_port_and_password()
    {
        Assert.Equal([("REDIS_URL_HOST", "shop-redis"), ("REDIS_URL_PORT", "6379"), ("REDIS_URL_PASSWORD", "s3cret")],
            DatabaseUrls.Parts("REDIS_URL", "redis://:s3cret@shop-redis:6379"));
        Assert.Equal([("R_HOST", "cache"), ("R_PORT", "6379")], DatabaseUrls.Parts("R", "redis://cache"));
        Assert.Empty(DatabaseUrls.Parts("X", "Host=db;Password=x"));
    }

    [Fact]
    public void A_compose_apps_env_file_gets_each_database_variable_in_parts_too_and_they_win()
    {
        var env = new List<EnvValue>
        {
            new("TITLE", "Shop", false),
            new("DATABASE_URL_HOST", "mine", false),
            new("DATABASE_URL", "postgres://shop:pw123456@postgres:5432/shop", true),
        };
        var provided = new[] { new ProvidedVariable("DATABASE_URL", "postgres://shop:pw123456@postgres:5432/shop", "the Postgres database shop on postgres", "pw123456") };
        var composed = DeployPipeline.ComposeEnvValues(env, provided);
        Assert.Equal(["DATABASE_URL_HOST", "DATABASE_URL_PORT", "DATABASE_URL_NAME", "DATABASE_URL_USER", "DATABASE_URL_PASSWORD"], composed.Parts);
        Assert.Equal(
            ["TITLE=Shop", "DATABASE_URL=postgres://shop:pw123456@postgres:5432/shop", "DATABASE_URL_HOST=postgres", "DATABASE_URL_PORT=5432",
             "DATABASE_URL_NAME=shop", "DATABASE_URL_USER=shop", "DATABASE_URL_PASSWORD=pw123456"],
            composed.Values.Select(v => $"{v.Key}={v.Value}"));
        // The app's own DATABASE_URL_HOST is replaced, and the deploy log says so.
        Assert.Equal(["DATABASE_URL_HOST"], composed.Replaced);
        Assert.Contains("In place of the value set on the Environment tab: DATABASE_URL_HOST (its database's, as Gitberth sets them).",
            DeployPipeline.EnvLines(composed, []));
        // Without databases nothing changes.
        var none = DeployPipeline.ComposeEnvValues([new EnvValue("TITLE", "Shop", false)], []);
        Assert.Equal(["TITLE=Shop"], none.Values.Select(v => $"{v.Key}={v.Value}"));
        Assert.Empty(none.Parts);
        Assert.Empty(DeployPipeline.EnvLines(none, []));
        // The parts' names by kind: Redis has no database name or user of its own.
        Assert.Equal(["DB_HOST", "DB_PORT", "DB_NAME", "DB_USER", "DB_PASSWORD"], DatabaseUrls.PartNames("DB", DatabaseKind.Postgres));
        Assert.Equal(["REDIS_URL_HOST", "REDIS_URL_PORT", "REDIS_URL_PASSWORD"], DatabaseUrls.PartNames("REDIS_URL", DatabaseKind.Redis));
    }

    [Fact]
    public void A_template_apps_env_file_says_where_its_reached_unless_the_app_has_its_own()
    {
        var builtIns = TemplateBuiltIns.Values(TemplateAddress.For("links.example.com", "192.168.1.20", 9090));
        var composed = DeployPipeline.ComposeEnvValues([new EnvValue("ADMIN_USERNAME", "admin", false)], [], builtIns);
        Assert.Equal(["ADMIN_USERNAME=admin", "APP_URL=https://links.example.com", "APP_HOST=links.example.com", "APP_SCHEME=https", "APP_HTTPS=true"],
            composed.Values.Select(v => $"{v.Key}={v.Value}"));
        Assert.Contains("Where it's reached, as its template asks: APP_URL=https://links.example.com, APP_HOST=links.example.com, APP_SCHEME=https, APP_HTTPS=true.",
            DeployPipeline.EnvLines(composed, builtIns));
        // A value of its own on the Environment tab wins, and the log says so.
        var own = DeployPipeline.ComposeEnvValues([new EnvValue("APP_URL", "https://mine.example.com", false)], [], builtIns);
        Assert.Equal("https://mine.example.com", own.Values.Single(v => v.Key == "APP_URL").Value);
        Assert.Equal(["APP_URL"], own.Overridden);
        Assert.Contains("Set on the Environment tab, so used in place of Gitberth's: APP_URL.", DeployPipeline.EnvLines(own, builtIns));
    }

    [Fact]
    public void Compose_up_waits_as_long_as_a_services_health_check_gives_it_to_start()
    {
        var least = TimeSpan.FromMinutes(3);
        static System.Text.Json.Nodes.JsonObject Model(string? start) => System.Text.Json.Nodes.JsonNode.Parse(start is null
            ? """{"services":{"web":{},"db":{"healthcheck":{"test":["CMD","true"]}}}}"""
            : """{"services":{"web":{"healthcheck":{"start_period":"START"}},"db":{}}}""".Replace("START", start, StringComparison.Ordinal))!.AsObject();
        Assert.Equal(least, DeployPipeline.WaitFor(Model(null), least));
        Assert.Equal(least, DeployPipeline.WaitFor(Model("30s"), least));
        Assert.Equal(TimeSpan.FromMinutes(11), DeployPipeline.WaitFor(Model("10m0s"), least));
        Assert.Equal(TimeSpan.FromMinutes(6), DeployPipeline.WaitFor(Model("5m"), least));
        Assert.Equal(TimeSpan.FromMinutes(20), DeployPipeline.WaitFor(Model("1h0m0s"), least));
        Assert.Equal(TimeSpan.FromSeconds(90), DeployPipeline.GoDuration(System.Text.Json.Nodes.JsonValue.Create("1m30s")));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), DeployPipeline.GoDuration(System.Text.Json.Nodes.JsonValue.Create("1.5s")));
        Assert.Equal(TimeSpan.FromSeconds(30), DeployPipeline.GoDuration(System.Text.Json.Nodes.JsonValue.Create(30_000_000_000L)));
        Assert.Null(DeployPipeline.GoDuration(System.Text.Json.Nodes.JsonValue.Create("ten minutes")));
        // Every shipped template that installs itself on its first start says how long that may take.
        foreach (var id in new[] { "nextcloud", "paperless-ngx", "ghost" })
            Assert.Contains("start_period: ", TemplateCatalog.Bundled.Find(id)!.Compose);
    }

    // ---- Installing ----

    private static (TemplateInstaller Installer, AuditLog Log) InstallerFor(AppsRig rig, TemplateCatalog catalog, AppService? apps = null, DatabaseServers? servers = null)
    {
        var log = new AuditLog();
        apps ??= rig.Apps;
        var compose = new ComposeApps(rig.Db, apps, rig.Actions, rig.Protector, rig.Cli, rig.Health, rig.Stats, rig.Options, NullLogger<ComposeApps>.Instance, audit: log);
        servers ??= new DatabaseServers(rig.Cli, rig.Settings, rig.Options, NullLogger<DatabaseServers>.Instance);
        return (new TemplateInstaller(catalog, compose, apps, rig.Actions, rig.Databases, servers, rig.Domains, NullLogger<TemplateInstaller>.Instance, rig.Cli, rig.Options), log);
    }

    private static async Task<AppDefinition> AppAsync(AppsRig rig, int id)
    {
        await using var db = rig.Db.CreateDbContext();
        return await db.Apps.AsNoTracking().Include(a => a.EnvVars).Include(a => a.Ports).Include(a => a.Domains).AsSplitQuery().SingleAsync(a => a.Id == id);
    }

    [Fact]
    public async Task Installing_a_template_makes_a_compose_app_with_its_variables_and_queues_its_first_deploy()
    {
        using var rig = new AppsRig();
        var (installer, log) = InstallerFor(rig, TemplateCatalog.Bundled);
        var installed = await installer.InstallAsync(new TemplateRequest("grafana", null, new Dictionary<string, string>()), "192.168.1.20");
        Assert.True(installed.Ok, installed.Message);
        Assert.Equal("gb-grafana_grafana", installed.Key);
        using var db = rig.Db.CreateDbContext();
        var app = db.Apps.AsNoTracking().Include(a => a.EnvVars).Include(a => a.Ports).Single(a => a.Id == installed.AppId);
        Assert.Equal(("Grafana", "grafana", AppSource.Compose, "grafana", "/api/health"), (app.Name, app.Slug, app.Source, app.TemplateId, app.HealthCheckPath));
        Assert.Equal(TemplateCatalog.Bundled.Find("grafana")!.Compose, rig.Protector.TryUnprotect(app.ComposeYamlProtected!));
        // Its inputs and generated secrets; its address isn't stored (each deploy sets it).
        Assert.Equal(["ADMIN_PASSWORD:True", "ADMIN_USER:False"], app.EnvVars.OrderBy(v => v.Key).Select(v => $"{v.Key}:{v.IsSecret}"));
        var password = rig.Protector.TryUnprotect(app.EnvVars.Single(v => v.Key == "ADMIN_PASSWORD").ProtectedValue);
        Assert.Matches("^[A-Za-z0-9]{20}$", password);
        Assert.Equal("3000/tcp:Lan:3000:True", string.Join(",", app.Ports.Select(p => $"{p.ContainerPort}/{p.Protocol}:{p.Access}:{p.HostPort}:{p.IsWeb}")));
        // Its first deploy, queued once.
        var deploy = db.Deployments.AsNoTracking().Single(d => d.AppId == app.Id);
        Assert.Equal((installed.DeploymentId, DeployTrigger.Created), (deploy.Id, deploy.Trigger));
        // Recorded as made from the template, and nothing secret anywhere it says.
        var entry = log.Entries.Single(e => e.Action == "app.created");
        Assert.Contains("from the template grafana", entry.Summary);
        Assert.DoesNotContain(password, System.Text.Json.JsonSerializer.Serialize(log.Entries));
        Assert.DoesNotContain(password, installed.Message);
    }

    [Fact]
    public async Task A_second_install_of_a_template_gets_a_free_name_and_slug()
    {
        using var rig = new AppsRig();
        var (installer, _) = InstallerFor(rig, TemplateCatalog.Bundled);
        var first = await installer.InstallAsync(new TemplateRequest("it-tools", null, new Dictionary<string, string>()), "host");
        var plan = await installer.PlanAsync(new TemplateRequest("it-tools", null, new Dictionary<string, string>()), "host");
        Assert.Equal(("IT-Tools 2", "it-tools-2"), (plan.Name, plan.Slug));
        var second = await installer.InstallAsync(new TemplateRequest("it-tools", null, new Dictionary<string, string>()), "host");
        Assert.True(first.Ok && second.Ok, second.Message);
        Assert.Equal(("gb-it-tools_it-tools", "gb-it-tools-2_it-tools"), (first.Key, second.Key));
        // A name asked for is used as it is.
        Assert.Equal("Tools", (await installer.PlanAsync(new TemplateRequest("it-tools", "Tools", new Dictionary<string, string>()), "host")).Name);
    }

    [Fact]
    public async Task A_template_that_needs_postgres_without_a_server_says_so_and_installs_nothing()
    {
        using var rig = new AppsRig();
        var (installer, _) = InstallerFor(rig, TemplateCatalog.Bundled);
        var plan = await installer.PlanAsync(new TemplateRequest("miniflux", null, new Dictionary<string, string>()), "host");
        Assert.True(plan.NeedsServer);
        Assert.Equal([TemplateInstaller.NoServer], plan.Problems);
        var before = await rig.CountAppsAsync();
        var installed = await installer.InstallAsync(new TemplateRequest("miniflux", null, new Dictionary<string, string>()), "host");
        Assert.False(installed.Ok);
        Assert.Equal(TemplateInstaller.NoServer, installed.Message);
        Assert.Equal(before, await rig.CountAppsAsync());
    }

    [Fact]
    public async Task An_install_with_a_bad_input_or_domain_installs_nothing_and_says_why()
    {
        using var rig = new AppsRig();
        var (installer, _) = InstallerFor(rig, CatalogOf(Good.Replace("databases:\n  - kind: postgres\n    variable: DATABASE_URL\n", "", StringComparison.Ordinal)
            .Replace("databases:\r\n  - kind: postgres\r\n    variable: DATABASE_URL\r\n", "", StringComparison.Ordinal)
            .Replace("DB_HOST: ${DATABASE_URL_HOST}", "DB_HOST: db", StringComparison.Ordinal).Replace("DB_PASSWORD: ${DATABASE_URL_PASSWORD}", "DB_PORT: \"5432\"", StringComparison.Ordinal)));
        var before = await rig.CountAppsAsync();
        var installed = await installer.InstallAsync(new TemplateRequest("notes", null, new Dictionary<string, string> { ["ADMIN_EMAIL"] = "nope" }), "host");
        Assert.False(installed.Ok);
        Assert.Contains("isn't an email address", installed.Message);
        // Domains are off here.
        var plan = await installer.PlanAsync(new TemplateRequest("notes", null, new Dictionary<string, string> { ["ADMIN_EMAIL"] = "a@b.co" }, "notes.example.com"), "host");
        Assert.StartsWith("Domains are off", plan.DomainProblem);
        Assert.Equal("http://host:8080", plan.Address.Url);
        Assert.Equal(before, await rig.CountAppsAsync());
    }

    private static Dictionary<string, string> NoInputs() => new();

    [Fact]
    public async Task A_domain_added_after_install_reaches_the_app_with_one_deploy_without_a_build()
    {
        using var rig = new AppsRig();
        await rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        var (installer, _) = InstallerFor(rig, TemplateCatalog.Bundled);
        var installed = await installer.InstallAsync(new TemplateRequest("linkding", null, NoInputs()), "192.168.1.20");
        Assert.True(installed.Ok, installed.Message);
        // Its first deploy ran.
        await using (var db = rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == installed.AppId).ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrentImage, "sissbruecker/linkding:1.47.0"));
        var builtIns = new TemplateBuiltIns(rig.Domains, rig.Apps);
        // Without a domain: its LAN address on its web port's host port (the LAN host Gitberth knows: none here, so localhost).
        var app = await AppAsync(rig, installed.AppId!.Value);
        Assert.Equal("http://localhost:9090", (await builtIns.ForAsync(app)).Single(b => b.Key == "APP_URL").Value);

        Assert.Null((await rig.Domains.AddAsync(app.Id, "links.example.com")).Error);
        app = await AppAsync(rig, app.Id);
        // A deploy without a build (Apply now) tells it: no new build is asked for.
        Assert.Equal((true, false), (app.ConfigChangePending, app.RebuildPending));
        Assert.Equal(["APP_URL=https://links.example.com", "APP_HOST=links.example.com", "APP_SCHEME=https", "APP_HTTPS=true"],
            (await builtIns.ForAsync(app)).Select(b => $"{b.Key}={b.Value}"));

        // Taken off again: its LAN address, and again one deploy applies it.
        await using (var db = rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == app.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ConfigChangePending, false));
        await rig.Domains.RemoveAsync(app.Id, "links.example.com");
        app = await AppAsync(rig, app.Id);
        Assert.True(app.ConfigChangePending);
        Assert.Equal(["APP_URL=http://localhost:9090", "APP_HOST=localhost", "APP_SCHEME=http", "APP_HTTPS=false"],
            (await builtIns.ForAsync(app)).Select(b => $"{b.Key}={b.Value}"));

        // A new web host port is applied the same way.
        await using (var db = rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == app.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ConfigChangePending, false));
        var moved = await rig.Apps.SavePortsAsync(app.Id, [new PortRow { ContainerPort = 9090, Protocol = "tcp", Access = PortAccess.Lan, HostPort = 9191, IsWeb = true }], null);
        Assert.Empty(moved.Problems);
        app = await AppAsync(rig, app.Id);
        Assert.True(app.ConfigChangePending);
        Assert.Equal("http://localhost:9191", (await builtIns.ForAsync(app)).Single(b => b.Key == "APP_URL").Value);

        // An app that isn't a template's is built with its address: a new build, as before, and no built-ins.
        Assert.Null((await rig.Domains.AddAsync(rig.KanbanId, "kanban.example.com")).Error);
        var kanban = await AppAsync(rig, rig.KanbanId);
        Assert.True(kanban.RebuildPending);
        Assert.Empty(await builtIns.ForAsync(kanban));
    }

    [Fact]
    public async Task Two_installs_back_to_back_or_at_once_get_different_host_ports()
    {
        using var rig = new AppsRig();
        var map = new HostPortMap(rig.Network, rig.Db, rig.Domains,
            new HostScanner(rig.Cli, rig.Options, NullLogger<HostScanner>.Instance) { InContainer = false, OnLinux = false }, rig.Settings, rig.Events,
            NullLogger<HostPortMap>.Instance);
        var apps = new AppService(rig.Db, rig.Protector, rig.Cli, rig.Settings, rig.Health, rig.Domains, rig.Options, rig.Restarts, new WatchState(),
            hostPorts: map, network: rig.Network);
        var (installer, _) = InstallerFor(rig, TemplateCatalog.Bundled, apps);
        // Both listen on 80: the first gets it, the second the next one, though the map kept its view of the host meanwhile.
        var first = await installer.InstallAsync(new TemplateRequest("it-tools", null, NoInputs()), "host");
        var second = await installer.InstallAsync(new TemplateRequest("excalidraw", null, NoInputs()), "host");
        Assert.True(first.Ok && second.Ok, first.Message + " " + second.Message);
        // Two at once (two tabs, an agent): one after the other from plan to create.
        var both = await Task.WhenAll(installer.InstallAsync(new TemplateRequest("it-tools", null, NoInputs()), "host"),
            installer.InstallAsync(new TemplateRequest("it-tools", null, NoInputs()), "host"));
        Assert.All(both, b => Assert.True(b.Ok, b.Message));
        var ports = new List<int?>();
        foreach (var made in new[] { first, second, both[0], both[1] }) ports.Add((await AppAsync(rig, made.AppId!.Value)).Ports.Single(p => p.IsWeb).HostPort);
        Assert.Equal(80, ports[0]);
        Assert.Equal(4, ports.Distinct().Count());
        Assert.Equal(["IT-Tools", "IT-Tools 2", "IT-Tools 3"], (await rig.Apps.ListNavAsync()).Select(a => a.Name).Where(n => n.StartsWith("IT-Tools", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Data_an_earlier_install_left_keeps_its_name_taken()
    {
        using var rig = new AppsRig();
        var (installer, _) = InstallerFor(rig, TemplateCatalog.Bundled);
        // Ghost was deleted without its volumes: Compose would mount them again, with new secrets that don't open them.
        rig.Engine.Volumes.Add("gb-ghost_db");
        rig.Engine.Volumes.Add("gb-ghost_content");
        rig.Engine.Volumes.Add("gb-ghost-blog_db");
        var plan = await installer.PlanAsync(new TemplateRequest("ghost", null, NoInputs()), "host");
        Assert.Equal(("Ghost 2", "ghost-2"), (plan.Name, plan.Slug));
        Assert.True(plan.CanInstall, string.Join(" ", plan.Problems));
        // A name typed in whose slug has some is refused, naming them; nothing offers to delete them.
        var typed = await installer.PlanAsync(new TemplateRequest("ghost", "Ghost", NoInputs()), "host");
        Assert.False(typed.CanInstall);
        Assert.Contains(typed.Problems, p => p.Contains("(gb-ghost_content, gb-ghost_db)", StringComparison.Ordinal));
        // A kept folder counts too.
        Directory.CreateDirectory(ComposeReleases.For(rig.Options, "memos").DataDir);
        var memos = await installer.PlanAsync(new TemplateRequest("memos", null, NoInputs()), "host");
        Assert.Equal(("Memos 2", "memos-2"), (memos.Name, memos.Slug));
    }

    [Fact]
    public async Task A_domain_thats_taken_is_refused_before_anything_is_made()
    {
        using var rig = new AppsRig();
        await rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        await rig.AddAsync(new CustomDomain { AppId = rig.KanbanId, Hostname = "taken.example.com" });
        var (installer, _) = InstallerFor(rig, TemplateCatalog.Bundled);
        var plan = await installer.PlanAsync(new TemplateRequest("grafana", null, NoInputs(), "taken.example.com"), "host");
        Assert.Contains("already belongs to Kanban", plan.DomainProblem);
        Assert.False(plan.CanInstall);
        // Nor does it take that domain's address.
        Assert.Equal("http://host:3000", plan.Address.Url);
        var before = await rig.CountAppsAsync();
        var installed = await installer.InstallAsync(new TemplateRequest("grafana", null, NoInputs(), "taken.example.com"), "host");
        Assert.False(installed.Ok);
        Assert.Contains("already belongs to Kanban", installed.Message);
        Assert.Equal(before, await rig.CountAppsAsync());
        // GitHub's webhook host is never an app's either; a free one is.
        await rig.Settings.SetAsync(SettingKeys.GitHubWebhookHost, "hooks.example.com");
        Assert.Contains("GitHub's webhooks", (await installer.PlanAsync(new TemplateRequest("grafana", null, NoInputs(), "hooks.example.com"), "host")).DomainProblem);
        var free = await installer.PlanAsync(new TemplateRequest("grafana", null, NoInputs(), "Dash.Example.com"), "host");
        Assert.Equal((null, "dash.example.com", "https://dash.example.com", true), (free.DomainProblem, free.Domain, free.Address.Url, free.CanInstall));
    }

    [Fact]
    public async Task A_template_open_until_set_up_takes_a_domain_only_behind_cloudflare_access()
    {
        using var rig = new AppsRig();
        await rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        var (installer, _) = InstallerFor(rig, TemplateCatalog.Bundled);
        // Access isn't ready here (no Cloudflare token): rather than publish it open, the plan leaves the domain for later.
        var plan = await installer.PlanAsync(new TemplateRequest("memos", null, NoInputs(), "notes.example.com"), "host");
        Assert.StartsWith(TemplateInstaller.ClaimDomain, plan.DomainProblem);
        Assert.Contains("no Cloudflare token", plan.DomainProblem);
        Assert.False(plan.CanInstall || plan.ProtectDomain);
        var before = await rig.CountAppsAsync();
        Assert.False((await installer.InstallAsync(new TemplateRequest("memos", null, NoInputs(), "notes.example.com"), "host")).Ok);
        Assert.Equal(before, await rig.CountAppsAsync());
        // Without a domain it installs; a template that makes its admin's password takes its domain as it is.
        Assert.True((await installer.PlanAsync(new TemplateRequest("memos", null, NoInputs()), "host")).CanInstall);
        var grafana = await installer.PlanAsync(new TemplateRequest("grafana", null, NoInputs(), "dash.example.com"), "host");
        Assert.Equal(((string?)null, false, true), (grafana.DomainProblem, grafana.ProtectDomain, grafana.CanInstall));
    }

    [Fact]
    public async Task Several_postgres_servers_and_none_chosen_ask_to_choose_one_rather_than_make_another()
    {
        using var rig = new AppsRig();
        var host = new PostgresHost();
        host.Containers.Add(PostgresHost.Server("analytics-db"));
        var servers = new DatabaseServers(new DockerCli(rig.Options) { Runner = host.RunAsync }, rig.Settings, rig.Options, NullLogger<DatabaseServers>.Instance);
        var (installer, _) = InstallerFor(rig, TemplateCatalog.Bundled, servers: servers);
        var plan = await installer.PlanAsync(new TemplateRequest("miniflux", null, NoInputs()), "host");
        Assert.Equal([TemplateInstaller.ChooseServer], plan.Problems);
        Assert.Equal((true, false, (string?)null), (plan.NeedsServerChoice, plan.NeedsServer, plan.Server));
        // Once one is chosen, its database goes there.
        Assert.Null(await servers.SetDefaultAsync("analytics-db"));
        plan = await installer.PlanAsync(new TemplateRequest("miniflux", null, NoInputs()), "host");
        Assert.Equal(("analytics-db", false, true), (plan.Server, plan.NeedsServerChoice, plan.CanInstall));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gitberth.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repo's root wasn't found.");
    }
}
