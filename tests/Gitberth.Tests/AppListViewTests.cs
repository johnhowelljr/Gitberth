using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Components.Pages;
using Gitberth.Components.Shared;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>
/// What the Apps list, the dashboard and the app page work out to show: search, filters, the groups that fold the
/// sample-* family into stacks, the status vocabulary, logos, and where the old links go.
/// </summary>
public sealed class AppListViewTests
{
    private static AppSummary App(string name, AppState state = AppState.Running, string image = "", AppSource source = AppSource.Existing,
        IReadOnlyList<AddressChip>? chips = null, string? text = null, bool pinned = false, UpdateBadge? update = null) =>
        new(name, null, name, name, source, ContainerRole.Other, ImageOrigin.Local, image.Length == 0 ? $"gitberth-{name}" : image, null, state,
            text ?? state.ToString(), "id-" + name, chips ?? [], null, null, update, pinned, false, null);

    private static AddressChip Chip(int host, int container, AddressChipKind kind = AddressChipKind.Open) =>
        new($"{host}:{container}", $"0.0.0.0:{host}->{container}/tcp", kind, kind == AddressChipKind.Open ? $"http://10.1.1.80:{host}" : $"10.1.1.80:{host}", host, container, "tcp");

    private static List<AppSummary> Host()
    {
        var apps = new List<AppSummary>
        {
            App("postgres", AppState.Healthy, "postgres:18", AppSource.Existing, [new("5432/tcp", "postgres:5432 from containers on gitberth", AddressChipKind.Unpublished, null, null, 5432, "tcp")]),
            App("cloudflare-tunnel", image: "cloudflare/cloudflared:latest"),
            App("gb-kanban", source: AppSource.Git, chips: [Chip(3001, 3000)], pinned: true),
            App("gb-orb", source: AppSource.Git, chips: [Chip(3000, 80)], pinned: true),
        };
        var port = 9120;
        foreach (var n in new[] { "node-express", "node-next", "node-hono", "python-flask", "python-django", "go-chi", "go-gin", "php-laravel", "ruby-rails", "rust-axum", "dotnet-web", "static-site" })
            apps.Add(App($"sample-{n}", n == "go-chi" ? AppState.Crashed : AppState.Running, chips: [Chip(port, 3000)], text: n == "go-chi" ? "Exited (2)" : null));
        apps.Add(App("sample-deno-oak", AppState.Stopped, chips: [Chip(9180, 8000)]));
        apps.Add(App("sample-bun-hono", AppState.Created));
        return apps;
    }

    [Fact]
    public void Search_finds_names_images_ports_and_addresses()
    {
        var apps = Host();
        Assert.Equal(["postgres"], AppSearch.Filter(apps, "postgres").Select(a => a.Key));
        Assert.Equal(["cloudflare-tunnel"], AppSearch.Filter(apps, "cloudflared").Select(a => a.Key));
        Assert.Equal(["postgres"], AppSearch.Filter(apps, "5432").Select(a => a.Key));
        Assert.Equal(["gb-kanban"], AppSearch.Filter(apps, "3001").Select(a => a.Key));
        Assert.Equal(["gb-orb"], AppSearch.Filter(apps, "3000:80").Select(a => a.Key));
        // Every word must match: a family and a runtime.
        Assert.Equal(["sample-python-flask", "sample-python-django"], AppSearch.Filter(apps, "sample python").Select(a => a.Key));
        Assert.Equal(apps.Count, AppSearch.Filter(apps, "  ").Count());
    }

    [Fact]
    public void The_status_filters_split_running_stopped_and_trouble()
    {
        var apps = Host();
        Assert.Equal(15, apps.Count(a => AppFacts.Passes(a, StatusFilter.Running)));
        Assert.Equal(["sample-go-chi", "sample-deno-oak", "sample-bun-hono"], apps.Where(a => AppFacts.Passes(a, StatusFilter.Stopped)).Select(a => a.Key));
        Assert.Equal(["sample-go-chi"], apps.Where(a => AppFacts.Passes(a, StatusFilter.Attention)).Select(a => a.Key));
        Assert.DoesNotContain(apps, a => AppFacts.Passes(a, StatusFilter.Updates));
        var updated = App("redis", update: new UpdateBadge(true, "redis:7 has a newer image", DateTime.UtcNow, null));
        Assert.True(AppFacts.Passes(updated, StatusFilter.Updates));
    }

    [Fact]
    public void A_family_of_three_or_more_folds_into_a_group_with_its_stacks()
    {
        var groups = AppGrouping.Group(Host(), GroupBy.Prefix);
        Assert.Equal(["none", "prefix:sample"], groups.Select(g => g.Id));

        var loose = groups[0];
        Assert.Equal("No group", loose.Title);
        // gb- apps are apps, not a family, however many there are.
        Assert.Equal(["postgres", "cloudflare-tunnel", "gb-kanban", "gb-orb"], loose.Apps.Select(a => a.Key));

        var samples = groups[1];
        Assert.Equal(("sample-*", "Name prefix", 14), (samples.Title, samples.Tag, samples.Apps.Count));
        Assert.Equal((11, 1), (samples.RunningCount, samples.TroubleCount));
        // Stopped on purpose (and never started) can be started together; the crashed one is left to look at.
        Assert.Equal(["sample-deno-oak", "sample-bun-hono"], samples.Startable.Select(a => a.Key));
        Assert.Equal("Node.js", samples.Stacks[0].Title);
        Assert.Equal(["Node.js", "Go", "Python", ".NET", "Bun", "Deno", "PHP", "Ruby", "Rust", "Static"], samples.Stacks.Select(s => s.Title));
    }

    [Fact]
    public void A_small_group_has_no_stacks_and_other_groupings_work()
    {
        var few = Host().Where(a => !a.Key.StartsWith("sample-", StringComparison.Ordinal) || a.Key.Contains("node", StringComparison.Ordinal)).ToList();
        var groups = AppGrouping.Group(few, GroupBy.Prefix);
        Assert.Empty(groups.Single(g => g.Id == "prefix:sample").Stacks);

        var bySource = AppGrouping.Group(Host(), GroupBy.Source);
        Assert.Equal(["Git", "Existing"], bySource.Select(g => g.Title));
        Assert.Single(AppGrouping.Group(Host(), GroupBy.None));
        Assert.Equal(("Node.js", "nodedotjs"), AppGrouping.StackOf("sample-monorepo-vercel"));
        Assert.Equal(("Other", null), AppGrouping.StackOf("postgres"));
    }

    [Fact]
    public void The_list_says_ports_bytes_and_names_the_way_the_design_does()
    {
        Assert.Equal("9120-9180", AppFacts.HostPorts(Host().Where(a => a.Key.StartsWith("sample-", StringComparison.Ordinal))));
        Assert.Null(AppFacts.HostPorts([App("x")]));
        Assert.Equal("10.1.1.80:3001", AppFacts.Shown("http://10.1.1.80:3001/"));
        Assert.Equal("27.3 MiB", AppFacts.Bytes(27.3 * 1024 * 1024));
        Assert.Equal("405 MiB", AppFacts.Bytes(405L * 1024 * 1024));
        Assert.Equal("1.4 GiB", AppFacts.Bytes(1.4 * 1024 * 1024 * 1024));
        Assert.Equal("a, b and c", AppFacts.Names(["a", "b", "c"]));
        Assert.Equal("a, b, c and 2 more", AppFacts.Names(["a", "b", "c", "d", "e"]));
        Assert.Equal("Unless stopped", AppFacts.RestartWords("unless-stopped"));
        // A row shows the web address first, else one it copies, and never an unpublished port.
        Assert.Equal(3001, AppFacts.Primary(Host().Single(a => a.Key == "gb-kanban"))?.HostPort);
        Assert.Null(AppFacts.Primary(Host().Single(a => a.Key == "postgres")));
    }

    [Fact]
    public void Each_state_has_its_shape_word_and_tone()
    {
        Assert.Equal(("gb-st--running", "Running", "good", false), Look(AppState.Running));
        Assert.Equal(("gb-st--healthy", "Healthy", "good", false), Look(AppState.Healthy));
        Assert.Equal(("gb-st--stopped", "Stopped", "neutral", false), Look(AppState.Stopped));
        Assert.Equal(("gb-st--error", "Exited (2)", "serious", true), Look(AppState.Crashed, "Exited (2)"));
        Assert.Equal(("gb-st--unhealthy", "Unhealthy", "serious", true), Look(AppState.Unhealthy));
        Assert.Equal(("gb-st--critical", "Missing", "critical", true), Look(AppState.Missing));
        Assert.Equal(("gb-st--busy", "Deploying", "accent", false), Look(AppState.Deploying));
        // An error is a shape of its own, not just a colour.
        Assert.NotNull(AppStatus.Of(AppState.Crashed).Icon);
        Assert.Equal(["", "gb-w-stop", "gb-w-err"], new[] { AppState.Running, AppState.Stopped, AppState.Crashed }.Select(AppStatus.WaffleClass));
        Assert.Equal(["12s", "48m", "28h", "2d"], new[] { TimeSpan.FromSeconds(12), TimeSpan.FromMinutes(48), TimeSpan.FromHours(28), TimeSpan.FromDays(2.5) }.Select(AppStatus.Short));
        Assert.Equal("for 3 days", AppStatus.Long(TimeSpan.FromDays(3.2)));

        static (string, string, string, bool) Look(AppState state, string? text = null) =>
            AppStatus.Of(state, text) is var l ? (l.Modifier, l.Word, l.Tone, l.Trouble) : default;
    }

    [Fact]
    public void Logos_follow_the_framework_then_the_image_then_the_name()
    {
        Assert.Equal("postgresql", LogoCatalog.Resolve("postgres:18", "postgres-before-gitberth").Slug);
        Assert.Equal("cloudflare", LogoCatalog.Resolve("cloudflare/cloudflared:latest", "cloudflare-tunnel").Slug);
        Assert.Equal("nginx", LogoCatalog.Resolve("nginx:stable-alpine", "sample-index").Slug);
        // A Git app: what Gitberth detected in its repository.
        Assert.Equal("nextdotjs", LogoCatalog.Resolve("gitberth/kanban:425df8a", "gb-kanban", "NextJs").Slug);
        Assert.Equal("html5", LogoCatalog.Resolve("gitberth/orb:1d2948c", "gb-orb", "Static").Slug);
        // The samples: a framework in the name wins over the runtime.
        Assert.Equal("express", LogoCatalog.Resolve("gitberth-sample-node-express", "sample-node-express").Slug);
        Assert.Equal("hono", LogoCatalog.Resolve("gitberth-sample-deno-hono", "sample-deno-hono").Slug);
        Assert.Equal("reactrouter", LogoCatalog.Resolve("gitberth-sample-node-react-router", "sample-node-react-router").Slug);
        Assert.Equal("vuedotjs", LogoCatalog.Resolve("gitberth-sample-node-vite-vue", "sample-node-vite-vue").Slug);
        Assert.Equal("nodedotjs", LogoCatalog.Resolve("gitberth-sample-node-hapi", "sample-node-hapi").Slug);
        Assert.Equal("go", LogoCatalog.Resolve("gitberth-sample-go-echo", "sample-go-echo").Slug);
        Assert.Equal("dotnet", LogoCatalog.Resolve("mcr.microsoft.com/dotnet/sdk:10.0", "boring_keldysh").Slug);
        // Gitberth's own container.
        Assert.True(LogoCatalog.Resolve("gitberth:local", "gitberth").Gitberth);
        Assert.True(LogoCatalog.Resolve("anything", "x", self: true).Gitberth);
        // Anything else: two letters on a hue the name always gets.
        var unknown = LogoCatalog.Resolve("acme/thing-box:1", "tb");
        Assert.Null(unknown.Slug);
        Assert.Equal("TB", unknown.Monogram);
        Assert.Equal(unknown, LogoCatalog.Resolve("acme/thing-box:1", "tb"));
        // A name the catalogue knows still gets its mark, even inside a longer image name.
        Assert.Equal("watchtower", LogoCatalog.Resolve("containrrr/watchtower-x:1", "wt").Slug);
        Assert.Equal("SO", LogoCatalog.Monogram("socat"));
        Assert.Equal("MA", LogoCatalog.Monogram("my-app"));
        Assert.True(LogoCatalog.Resolve(null, "").Cube);
        Assert.Equal("cloudflared", LogoCatalog.Repository("cloudflare/cloudflared:latest"));
        Assert.Equal("postgres", LogoCatalog.Repository("docker.io/library/postgres@sha256:abc"));
        Assert.Null(LogoCatalog.Repository("sha256:3a4264520e12"));
        // Every mark the rules can pick is in the catalogue.
        Assert.All(new[] { "postgresql", "nextdotjs", "html5", "nodedotjs", "python", "go", "dotnet", "openjdk", "docker", "redis", "mysql" },
            slug => Assert.NotNull(LogoCatalog.Find(slug)));
    }

    [Fact]
    public void Sparklines_are_drawn_in_their_box()
    {
        Assert.Equal("", SparkPath.Line([1], null, 58, 18));
        Assert.Equal("M0,17 L29,9 L58,1", SparkPath.Line([0, 5, 10], null, 58, 18));
        Assert.Equal("M0,17 L29,9 L58,1 L58,18 L0,18 Z", SparkPath.Area([0, 5, 10], null, 58, 18));
        // A fixed top: 10 of 20 is half way.
        Assert.Equal("M0,9 L58,9", SparkPath.Line([10, 10], 20, 58, 18));
        // Only the last 30 readings.
        Assert.Equal(30, SparkPath.Line(Enumerable.Range(0, 100).Select(i => (double)i).ToList(), null, 58, 18).Split(' ').Length);
    }

    [Fact]
    public void Old_container_links_go_to_the_app_page()
    {
        Assert.Equal("apps", ContainerRedirect.Target(null, null));
        Assert.Equal("apps/postgres", ContainerRedirect.Target("postgres", null));
        Assert.Equal("apps/postgres", ContainerRedirect.Target("postgres", "overview"));
        Assert.Equal("apps/postgres/network", ContainerRedirect.Target("postgres", "network"));
        Assert.Equal("apps/my%20app/logs", ContainerRedirect.Target("my app", "logs"));
    }
}
