using Gitberth.Images;

namespace Gitberth.Tests;

/// <summary>
/// The tag picker's ranking (TagRanking) and its views (TagViews): each worked example of the design (3.6, after the
/// research's 3.3) as a test, tags as the probes of 2026-11-22 saw them (some names illustrative), on linux/amd64 unless
/// said. Capital letters in the comments are digests.
/// </summary>
public sealed class TagRankingTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly HostPlatform Amd64 = HostPlatform.Of("linux", "x86_64");
    private static readonly HostPlatform Arm64 = HostPlatform.Of("linux", "aarch64");
    private static readonly string[] Both = ["linux/amd64", "linux/arm64/v8"];

    // One image's tags: they share a digest, pushed so many hours ago, for amd64 and arm64 unless said.
    private static IEnumerable<TagFacts> Image(char digest, int hoursAgo, params string[] tags) => On(Both, digest, hoursAgo, tags);

    private static IEnumerable<TagFacts> On(string[] platforms, char digest, int hoursAgo, params string[] tags) =>
        tags.Select(t => new TagFacts(t, "sha256:" + new string(digest, 64), Now.AddHours(-hoursAgo), 26_000_000, platforms));

    private static TagList Rank(IEnumerable<TagFacts> tags, HostPlatform? host = null, string image = "nginx")
    {
        var list = tags.ToList();
        return TagRanking.Rank(image, "Docker Hub", list, host ?? Amd64, list.Count, false);
    }

    private static List<string> Tags(IEnumerable<TagChoice> choices) => choices.Select(c => c.Tag).ToList();

    private static TagChoice Choice(TagList list, string? family, string tag, bool pre = false) =>
        Assert.Single(TagViews.For(list, family, pre), c => c.Tag == tag);

    // E1: nginx, the stable line real (R8), the 1.31.x mainline names illustrative.
    private static TagList Nginx() => Rank(
        Image('a', 2, "latest", "mainline", "1", "1.31", "1.31.2")
            .Concat(Image('b', 1, "stable", "1.30", "1.30.7"))
            .Concat(Image('c', 2, "alpine", "mainline-alpine", "1.31-alpine", "1.31.2-alpine", "1.31.2-alpine3.24"))
            .Concat(Image('d', 1, "stable-alpine", "stable-alpine3.24", "1.30.7-alpine", "1.30.7-alpine3.24"))
            .Concat(Image('e', 300, "1.31.1"))
            .Concat(Image('f', 300, "1.31.1-alpine")));

    [Fact]
    public void E1_nginx_is_newest_stable_first_by_variant_with_its_floating_tags()
    {
        var list = Nginx();

        Assert.Equal(["Standard", "alpine"], list.Families.Select(f => f.Label));
        Assert.Equal([new TagFamily("", "Standard", 3, true), new TagFamily("alpine", "alpine", 3, false)], list.Families);
        var standard = TagViews.For(list, "", false);
        Assert.Equal(["1.31.2", "1.31.1", "1.30.7"], Tags(standard));
        Assert.Equal(["1.31", "1", "latest", "mainline"], standard[0].Follows);
        Assert.True(standard[0].Recommended);
        Assert.Equal(["1.30", "stable"], standard[2].Follows);
        var alpine = TagViews.For(list, "alpine", false);
        Assert.Equal(["1.31.2-alpine", "1.31.1-alpine", "1.30.7-alpine"], Tags(alpine));
        Assert.Equal(["1.31-alpine", "alpine", "mainline-alpine"], alpine[0].Follows);
        Assert.Equal(["stable-alpine"], alpine[2].Follows);
        Assert.Equal("1.31", list.Default);
        Assert.Equal("1.31.2", list.Recommended!.Tag);

        // A row is one image: its tag, its version, its other tags (versions longest first, then channels), its variants.
        var c = list.Versions.Single(r => r.Tag == "1.31.2-alpine");
        Assert.Equal("1.31.2", c.Version);
        Assert.Equal(["1.31.2-alpine3.24", "1.31-alpine", "alpine", "mainline-alpine"], c.Also);
        Assert.Equal(["alpine", "alpine3.24"], c.Families);
        Assert.Equal(("sha256:" + new string('c', 64), 26_000_000L), (c.Digest, c.Size));
        Assert.Equal(Both, c.Platforms);
        Assert.Equal((false, true, false), (c.PreRelease, c.FitsHost, c.Inferred));
        // All: the newest version first, the default family first on a tie; nothing in Other.
        Assert.Equal(["1.31.2", "1.31.2-alpine", "1.31.1", "1.31.1-alpine", "1.30.7", "1.30.7-alpine"], Tags(TagViews.For(list, null, false)));
        Assert.Empty(list.Other);
        Assert.Equal((19, 19, true, false), (list.Read, list.Total, list.Dates, list.More));
    }

    // E2: postgres, the real first 30 tags by last_updated (R11): push order misleads, and latest names no version here.
    [Fact]
    public void E2_postgres_recommends_latest_with_no_version_and_keeps_channels_in_other()
    {
        var list = Rank(Image('1', 1, "15.20-trixie", "15.20", "15-trixie", "15")
            .Concat(Image('2', 2, "16.16-alpine3.24", "16.16-alpine", "16-alpine3.24", "16-alpine"))
            .Concat(Image('3', 3, "16.16-alpine3.23", "16-alpine3.23"))
            .Concat(Image('4', 4, "17.12-alpine3.24", "17.12-alpine", "17-alpine3.24", "17-alpine"))
            .Concat(Image('5', 5, "17.12-alpine3.23", "17-alpine3.23"))
            .Concat(Image('6', 6, "18.7-alpine3.23", "18-alpine3.23"))
            .Concat(Image('7', 7, "17.12-trixie", "17.12", "17-trixie", "17"))
            .Concat(Image('8', 8, "18.7-alpine3.24", "18.7-alpine", "18-alpine3.24", "18-alpine"))
            .Concat(Image('9', 9, "trixie", "latest"))
            .Concat(Image('a', 10, "bookworm"))
            .Concat(Image('b', 11, "alpine3.24")), image: "postgres");

        Assert.Equal(30, list.Read);
        Assert.Equal("latest", list.Recommended!.Tag);
        Assert.Null(list.Recommended.Version);
        Assert.Equal(["trixie"], list.Recommended.Also);
        Assert.Equal("latest", list.Default);
        Assert.Equal(["17.12", "15.20"], Tags(TagViews.For(list, "", false)));
        Assert.Equal(["17"], Choice(list, "", "17.12").Follows);
        // Newest push first: latest's row, then bookworm, then alpine3.24, none of which names a version.
        Assert.Equal(["latest", "bookworm", "alpine3.24"], list.Other.Select(r => r.Tag));
        Assert.Same(list.Recommended, list.Other[0]);
        Assert.Equal(["18.7-alpine", "17.12-alpine", "16.16-alpine"], Tags(TagViews.For(list, "alpine", false)));
        Assert.Equal(["18.7-alpine3.23", "17.12-alpine3.23", "16.16-alpine3.23"], Tags(TagViews.For(list, "alpine3.23", false)));
        // trixie's rows are all Standard's, and alpine3.24's all alpine's: no chips of their own.
        Assert.Equal(["", "alpine", "alpine3.23"], list.Families.Select(f => f.Name));
    }

    // E3: node (real, R12, first 30): twelve 27.0.0 alphas pushed last, and LTS codenames as channels.
    [Fact]
    public void E3_node_hides_alphas_unless_asked_and_follows_codenames_in_each_variant()
    {
        string[] variants = ["trixie-slim", "trixie", "bookworm-slim", "bookworm", "alpine3.24", "alpine3.23"];
        var alphas = variants.SelectMany((v, i) => new[]
        {
            new TagFacts($"27.0.0-alpha.0.1.1-{v}", "sha256:" + new string((char)('a' + i), 63) + "1", Now.AddHours(-1), 50_000_000, Both),
            new TagFacts($"27.0.0-alpha.0.1.0-{v}", "sha256:" + new string((char)('a' + i), 63) + "0", Now.AddHours(-2), 50_000_000, Both),
        });
        var list = Rank(alphas
            .Concat(Image('1', 3, "jod-alpine3.24", "jod-alpine", "22.23.5-alpine3.24", "22.23.5-alpine", "22.23-alpine3.24", "22.23-alpine", "22-alpine3.24", "22-alpine"))
            .Concat(Image('2', 4, "krypton-trixie-slim", "24.22.3-trixie-slim", "24.22-trixie-slim", "24-trixie-slim"))
            .Concat(Image('3', 5, "krypton", "krypton-bookworm", "24.22.3", "24.22.3-bookworm", "24.22", "24.22-bookworm")), image: "node");

        Assert.Equal(30, list.Read);
        Assert.Equal(["24.22.3"], Tags(TagViews.For(list, "", false)));
        Assert.Equal(["24.22", "krypton"], Choice(list, "", "24.22.3").Follows);
        var slim = Choice(list, "trixie-slim", "24.22.3-trixie-slim");
        Assert.Equal(["24.22-trixie-slim", "24-trixie-slim", "krypton-trixie-slim"], slim.Follows);
        Assert.Equal(["22.23-alpine", "22-alpine", "jod-alpine"], Choice(list, "alpine", "22.23.5-alpine").Follows);
        // The alphas only when asked: newest first, above the release they lead to.
        Assert.Equal(["27.0.0-alpha.0.1.1-trixie-slim", "27.0.0-alpha.0.1.0-trixie-slim", "24.22.3-trixie-slim"], Tags(TagViews.For(list, "trixie-slim", true)));
        Assert.All(list.Versions.Where(r => r.Tag.StartsWith("27.", StringComparison.Ordinal)), r => Assert.True(r.PreRelease));
        Assert.Equal(["", "alpine", "trixie-slim"], list.Families.Select(f => f.Name));
        Assert.Equal("24.22", list.Default);
    }

    // E4: python (real, R25): a glued alpha, rc as a token, and Windows-only tags.
    [Fact]
    public void E4_python_puts_3_15_2_first_flags_3_16_0a2_and_hides_windows()
    {
        string[] linuxAndWindows = ["linux/amd64", "linux/arm64/v8", "windows/amd64"];
        var list = Rank(On(linuxAndWindows, 'a', 1, "latest", "3", "3.15", "3.15.2")
            .Concat(Image('b', 1, "trixie", "3-trixie", "3.15-trixie", "3.15.2-trixie"))
            .Concat(On(linuxAndWindows, 'c', 2, "3.14", "3.14.10"))
            .Concat(Image('d', 2, "3.14-trixie", "3.14.10-trixie"))
            .Concat(Image('e', 3, "3.13", "3.13.18", "3.13-trixie", "3.13.18-trixie"))
            .Concat(Image('f', 4, "3.12", "3.12.16", "3.12-trixie", "3.12.16-trixie"))
            .Concat(Image('0', 5, "3.11", "3.11.18", "3.11-trixie", "3.11.18-trixie"))
            .Concat(Image('1', 6, "3.16.0a2", "3.16-rc"))
            .Concat(Image('2', 6, "3.16.0a2-alpine", "3.16-rc-alpine"))
            .Concat(Image('3', 6, "3.16.0a2-trixie", "3.16-rc-trixie"))
            .Concat(On(["windows/amd64"], '4', 7, "windowsservercore-ltsc2025", "3.15.2-windowsservercore-ltsc2025")), image: "python");

        Assert.Equal(["3.15.2", "3.14.10", "3.13.18", "3.12.16", "3.11.18"], Tags(TagViews.For(list, "", false)));
        Assert.Equal(["3.15", "3", "latest"], Choice(list, "", "3.15.2").Follows);
        var pre = TagViews.For(list, "", true);
        Assert.Equal(("3.16.0a2", "3.16.0a2"), (pre[0].Tag, pre[0].Row.Version));
        Assert.Equal(["3.16-rc"], pre[0].Follows);
        Assert.True(pre[0].Row.PreRelease);
        Assert.DoesNotContain(list.Versions.Concat(list.Other), r => r.Tag.Contains("windowsservercore", StringComparison.Ordinal)
            || r.Also.Any(a => a.Contains("windowsservercore", StringComparison.Ordinal)));
        Assert.Equal(["3.15.2-trixie", "3.14.10-trixie", "3.13.18-trixie", "3.12.16-trixie", "3.11.18-trixie"], Tags(TagViews.For(list, "trixie", false)));
        Assert.Equal(["3.15-trixie", "3-trixie", "trixie"], Choice(list, "trixie", "3.15.2-trixie").Follows);
        Assert.Equal(["", "trixie"], list.Families.Select(f => f.Name));
        Assert.Equal("3.15", list.Default);
        Assert.True(list.Recommended!.FitsHost);
    }

    // E5: alpine (real, R24): semver releases and date snapshots, edge on one of them.
    [Fact]
    public void E5_alpine_ranks_semver_and_keeps_dates_in_other_with_edge_a_pre_release()
    {
        var list = Rank(Image('a', 5, "latest", "3", "3.24", "3.24.4")
            .Concat(Image('b', 6, "3.23.8", "3.23")).Concat(Image('c', 6, "3.22.8", "3.22")).Concat(Image('d', 6, "3.21.10", "3.21"))
            .Concat(Image('e', 6, "3.20.10", "3.20"))
            .Concat(Image('f', 50, "3.24.3")).Concat(Image('0', 90, "3.24.2")).Concat(Image('1', 130, "3.24.1")).Concat(Image('2', 170, "3.24.0"))
            .Concat(Image('3', 1, "edge", "20260805"))
            .Concat(Image('4', 400, "20260127")).Concat(Image('5', 900, "20251224")), image: "alpine");

        Assert.Equal(["3.24.4", "3.24.3", "3.24.2", "3.24.1", "3.24.0", "3.23.8", "3.22.8", "3.21.10", "3.20.10"], Tags(TagViews.For(list, "", false)));
        Assert.Equal(["3.24", "3", "latest"], Choice(list, "", "3.24.4").Follows);
        Assert.Equal(["20260805", "20260127", "20251224"], list.Other.Select(r => r.Tag));
        var edge = list.Other[0];
        Assert.True(edge.PreRelease);
        Assert.Equal(["edge"], edge.Also);
        Assert.False(list.Other[1].PreRelease);
        Assert.Equal("3.24", list.Default);
    }

    // E6: linuxserver/plex (real, R22): a build number after the version, and copies for one architecture.
    [Fact]
    public void E6_plex_orders_by_build_and_hides_single_arch_copies()
    {
        var list = Rank(Image('a', 1, "latest", "1.43.5", "version-1.43.5.11079-b3b13e506", "1.43.5.11079-b3b13e506-ls333")
            .Concat(Image('b', 200, "1.43.5.11079-b3b13e506-ls332"))
            .Concat(On(["linux/amd64"], 'c', 1, "amd64-latest", "amd64-1.43.5", "amd64-version-1.43.5.11079-b3b13e506", "amd64-1.43.5.11079-b3b13e506-ls333"))
            .Concat(On(["linux/arm64/v8"], 'd', 1, "arm64v8-latest", "arm64v8-1.43.5", "arm64v8-version-1.43.5.11079-b3b13e506", "arm64v8-1.43.5.11079-b3b13e506-ls333"))
            .Concat(On(["linux/amd64"], 'e', 200, "amd64-1.43.5.11079-b3b13e506-ls332")), image: "linuxserver/plex");

        Assert.Equal(["1.43.5.11079-b3b13e506-ls333", "1.43.5.11079-b3b13e506-ls332"], Tags(TagViews.For(list, "", false)));
        Assert.Equal(["version-1.43.5.11079-b3b13e506", "1.43.5", "latest"], Choice(list, "", "1.43.5.11079-b3b13e506-ls333").Follows);
        Assert.Equal("1.43.5.11079-ls333", list.Versions[0].Version);
        Assert.Equal(5, list.Read);
        Assert.DoesNotContain(list.Versions.Concat(list.Other).SelectMany(r => r.Also.Prepend(r.Tag)), t => t.StartsWith("amd64-", StringComparison.Ordinal) || t.StartsWith("arm64v8-", StringComparison.Ordinal));
        Assert.Equal("1.43.5", list.Default);
    }

    // E7 (illustrative, distinct digests): a release above its rcs (rc.10 above rc.2), betas, alphas and dev builds.
    [Fact]
    public void E7_pre_releases_go_by_stage_then_number_below_their_release()
    {
        var list = Rank(new[] { "1.28.0", "1.28.0-rc.10", "1.28.0-rc.2", "1.28.0-beta.3", "1.28.0-alpha.1", "1.28.0-dev", "1.27.9" }
            .SelectMany((t, i) => Image((char)('a' + i), i + 1, t)));

        Assert.Equal(["1.28.0", "1.28.0-rc.10", "1.28.0-rc.2", "1.28.0-beta.3", "1.28.0-alpha.1", "1.28.0-dev", "1.27.9"], Tags(TagViews.For(list, "", true)));
        Assert.Equal(["1.28.0", "1.27.9"], Tags(TagViews.For(list, "", false)));
        Assert.Equal("1.28.0", list.Default);
    }

    // E8 (traefik-style, illustrative): the same versions with and without a v.
    [Fact]
    public void E8_traefik_shows_the_version_without_its_v_and_follows_the_shorter_ones()
    {
        var list = Rank(Image('a', 1, "latest", "v3", "v3.3", "3.3", "v3.3.1", "3.3.1")
            .Concat(Image('b', 30, "v3.3.0", "3.3.0"))
            .Concat(Image('c', 40, "v2.11", "2.11", "v2.11.18", "2.11.18"))
            .Concat(Image('d', 2, "v3.4.0-rc1", "3.4.0-rc1")), image: "traefik");

        Assert.Equal(["3.3.1", "3.3.0", "2.11.18"], Tags(TagViews.For(list, "", false)));
        // The tags that follow it first; the same version with a v at the end.
        Assert.Equal(["3.3", "v3.3", "v3", "latest", "v3.3.1"], Choice(list, "", "3.3.1").Follows);
        Assert.Equal(["2.11", "v2.11", "v2.11.18"], Choice(list, "", "2.11.18").Follows);
        var rc = Assert.Single(list.Versions, r => r.PreRelease);
        Assert.Equal("3.4.0-rc1", rc.Tag);
        Assert.Equal(["v3.4.0-rc1"], rc.Also);
        Assert.Equal("3.3", list.Default);
    }

    // E9 (Home Assistant-style, illustrative): calendar versions with glued betas, and beta and dev channels.
    [Fact]
    public void E9_calver_puts_releases_first_and_merges_betas_when_asked()
    {
        var list = Rank(Image('a', 2, "latest", "stable", "2025.1.1").Concat(Image('b', 20, "2025.1.0")).Concat(Image('c', 1, "beta", "2025.2.0b1"))
            .Concat(Image('d', 30, "2025.1.0b5")).Concat(Image('e', 1, "dev")).Concat(Image('f', 60, "2024.12.5")), image: "homeassistant/home-assistant");

        Assert.Equal(["2025.1.1", "2025.1.0", "2024.12.5"], Tags(TagViews.For(list, "", false)));
        Assert.Equal(["2025.2.0b1", "2025.1.1", "2025.1.0", "2025.1.0b5", "2024.12.5"], Tags(TagViews.For(list, "", true)));
        Assert.Equal(["beta"], TagViews.For(list, "", true)[0].Follows);
        // dev names no version: a pre-release in Other.
        var dev = Assert.Single(list.Other);
        Assert.Equal(("dev", true), (dev.Tag, dev.PreRelease));
        Assert.Equal("2025.1.1", list.Default);
    }

    // E10 (illustrative): MinIO's dated releases, one variant.
    [Fact]
    public void E10_release_dates_are_the_primary_scheme_with_a_cpuv1_chip()
    {
        var list = Rank(Image('a', 1, "latest", "RELEASE.2025-01-20T14-49-07Z").Concat(Image('b', 1, "RELEASE.2025-01-20T14-49-07Z-cpuv1"))
            .Concat(Image('c', 800, "RELEASE.2024-12-18T13-15-44Z")), image: "minio/minio");

        Assert.Equal(["RELEASE.2025-01-20T14-49-07Z", "RELEASE.2024-12-18T13-15-44Z"], Tags(TagViews.For(list, "", false)));
        Assert.Equal(["RELEASE.2025-01-20T14-49-07Z-cpuv1"], Tags(TagViews.For(list, "cpuv1", false)));
        Assert.Equal(["Standard", "cpuv1"], list.Families.Select(f => f.Label));
        Assert.Equal("2025-01-20T14-49-07Z", list.Versions[0].Version);
        Assert.Equal("RELEASE.2025-01-20T14-49-07Z", list.Default);
    }

    // E11 (illustrative): commit, pull request and signature tags among the releases.
    [Fact]
    public void E11_ci_noise_is_hidden_and_main_is_a_pre_release()
    {
        var list = Rank(Image('a', 1, "latest", "1.4", "1.4.0").Concat(Image('b', 50, "1.3.2")).Concat(Image('c', 1, "sha-4f9c2e1"))
            .Concat(Image('d', 1, "4f9c2e1d")).Concat(Image('e', 2, "pr-123")).Concat(Image('f', 1, "main"))
            .Concat(Image('0', 1, "sha256-" + new string('7', 64) + ".sig")).Concat(Image('1', 100, "20250120")), image: "acme/api");

        Assert.Equal(["1.4.0", "1.3.2"], Tags(TagViews.For(list, "", false)));
        // An all-digit tag is never a commit: 20250120 is a date, in Other.
        Assert.Equal(["main", "20250120"], list.Other.Select(r => r.Tag));
        Assert.True(list.Other[0].PreRelease);
        Assert.Equal(6, list.Read);
        Assert.Equal("1.4", list.Default);
    }

    // E12 (illustrative): a registry's tags/list, lexical, without digests or dates.
    [Fact]
    public void E12_without_digests_floating_versions_are_inferred_and_latest_names_nothing_known()
    {
        var list = TagRanking.Rank("registry.e2e.test/team/api", "registry.e2e.test",
            new[] { "1", "1.26", "1.26.5", "1.27", "1.27.2", "1.27.3", "1.28.0-rc1", "latest", "stable" }.Select(t => new TagFacts(t, null, null, null, null)).ToList(),
            Amd64, 9, false);

        Assert.Equal(["1.27.3", "1.27.2", "1.26.5"], Tags(TagViews.For(list, "", false)));
        var top = list.Versions.Single(r => r.Tag == "1.27.3");
        Assert.True(top.Inferred);
        Assert.Equal(["1.27", "1"], top.Also);
        Assert.Equal(["1.27", "1"], Choice(list, "", "1.27.3").Follows);
        var older = list.Versions.Single(r => r.Tag == "1.26.5");
        Assert.True(older.Inferred);
        Assert.Equal(["1.26"], older.Also);
        Assert.False(list.Versions.Single(r => r.Tag == "1.27.2").Inferred);
        Assert.Equal(["latest", "stable"], list.Other.Select(r => r.Tag));
        Assert.Equal("1.28.0-rc1", Assert.Single(list.Versions, r => r.PreRelease).Tag);
        Assert.Same(top, list.Recommended);
        Assert.Equal("1.27", list.Default);
        Assert.False(list.Dates);
        Assert.All(list.Versions.Concat(list.Other), r => Assert.Equal(((string?)null, true, 0), (r.Digest, r.FitsHost, r.Platforms.Count)));
    }

    // E13 (distroless-style names, illustrative): channels only.
    [Fact]
    public void E13_channels_only_recommend_latest_and_list_the_rest_in_other()
    {
        var list = Rank(Image('a', 1, "latest").Concat(Image('b', 1, "nonroot")).Concat(Image('c', 1, "debug")).Concat(Image('d', 1, "debug-nonroot")),
            image: "gcr.io/distroless/static");

        Assert.Empty(list.Versions);
        Assert.Empty(list.Families);
        Assert.Equal("latest", list.Recommended!.Tag);
        Assert.Equal("latest", list.Default);
        // As old as each other: by name.
        Assert.Equal(["debug", "debug-nonroot", "latest", "nonroot"], list.Other.Select(r => r.Tag));
        Assert.All(list.Other, r => Assert.False(r.PreRelease));
    }

    [Fact]
    public void An_arm64_host_sees_an_amd64_only_tag_marked_and_isnt_recommended_it()
    {
        var list = Rank(On(["linux/amd64"], 'a', 1, "latest", "1.2", "1.2.0").Concat(Image('b', 30, "1.1", "1.1.0")), Arm64, "acme/api");

        var amd64Only = list.Versions.Single(r => r.Tag == "1.2.0");
        Assert.False(amd64Only.FitsHost);
        Assert.Equal(["1.2.0", "1.1.0"], Tags(TagViews.For(list, "", false)));
        Assert.Equal("1.1.0", list.Recommended!.Tag);
        Assert.Equal("1.1", list.Default);
        Assert.True(list.Versions.Single(r => r.Tag == "1.1.0").FitsHost);
    }

    [Theory]
    [InlineData("linux/arm64/v8", "aarch64", true)]
    [InlineData("linux/arm64", "aarch64", true)]
    [InlineData("linux/arm/v6", "armv7l", true)]
    [InlineData("linux/arm/v7", "armv7l", true)]
    [InlineData("linux/arm/v7", "armv6l", false)]
    [InlineData("linux/amd64", "aarch64", false)]
    [InlineData("windows/amd64", "x86_64", false)]
    public void A_platform_fits_the_host_by_os_architecture_and_variant(string platform, string uname, bool fits) =>
        Assert.Equal(fits, TagRanking.Fits(platform, HostPlatform.Of("linux", uname)));

    // R10: the tag to preselect.
    [Fact]
    public void Nothing_stable_and_no_latest_preselects_nothing()
    {
        var list = Rank(Image('a', 1, "2.0.0-rc.1").Concat(Image('b', 2, "2.0.0-beta.2")));

        Assert.Null(list.Recommended);
        Assert.Null(list.Default);
        Assert.Empty(list.Families);
        Assert.Empty(TagViews.For(list, "", false));
        Assert.Equal(2, TagViews.For(list, "", true).Count);
    }

    // R11: chips with a stable row, a family that adds no row dropped, the default first, then by rows, at most 12.
    [Fact]
    public void At_most_twelve_chips_the_default_first_then_by_rows()
    {
        var tags = Image('a', 1, "latest", "2.0.0").Concat(Image('b', 2, "1.0.0"));
        var families = Enumerable.Range(0, 15).Select(i => $"v{(char)('a' + i)}x").ToList();
        // Each variant a row of its own; the last three have two.
        tags = tags.Concat(families.SelectMany((f, i) => new[] { new TagFacts($"2.0.0-{f}", $"sha256:{i:x2}{new string('0', 62)}", Now, null, Both) }));
        tags = tags.Concat(families.Skip(12).Select((f, i) => new TagFacts($"1.0.0-{f}", $"sha256:{i:x2}{new string('1', 62)}", Now, null, Both)));

        var list = Rank(tags);

        Assert.Equal(12, list.Families.Count);
        Assert.Equal(("", "Standard", 2, true), (list.Families[0].Name, list.Families[0].Label, list.Families[0].Rows, list.Families[0].Default));
        Assert.Equal(["vmx", "vnx", "vox"], list.Families.Skip(1).Take(3).Select(f => f.Name));
        Assert.Equal(2, list.Families[1].Rows);
        Assert.All(list.Families.Skip(4), f => Assert.Equal(1, f.Rows));
    }

    // R12: what's typed searches every row, in every family; a row shows as the family of what matched.
    [Fact]
    public void A_filter_searches_every_variant_and_other_tags_and_labels_a_row_by_what_matched()
    {
        var list = Rank(Image('a', 2, "latest", "mainline", "1", "1.31", "1.31.2")
            .Concat(Image('b', 1, "stable", "1.30", "1.30.7"))
            .Concat(Image('c', 2, "alpine", "mainline-alpine", "1.31-alpine", "1.31.2-alpine", "1.31.2-alpine3.24"))
            .Concat(Image('d', 1, "stable-alpine", "stable-alpine3.24", "1.30.7-alpine", "1.30.7-alpine3.24"))
            .Concat(Image('9', 3, "1.32.0-rc.1"))
            .Concat(Image('8', 900, "bookworm-perl")));

        var alpine3 = TagViews.For(list, "", false, "ALPINE3");
        Assert.Equal(["1.31.2-alpine3.24", "1.30.7-alpine3.24"], Tags(alpine3));
        Assert.Equal(["stable-alpine3.24"], alpine3[1].Follows);
        // A family's filter is every family's; a pre-release that matches is shown, marked; so is a tag in Other.
        var rc = Assert.Single(TagViews.For(list, "alpine", false, " rc "));
        Assert.Equal(("1.32.0-rc.1", true), (rc.Tag, rc.Row.PreRelease));
        Assert.Equal(["bookworm-perl"], Tags(TagViews.For(list, "", false, "perl")));
        Assert.Equal(["1.31.2", "1.31.2-alpine", "1.30.7", "1.30.7-alpine"], Tags(TagViews.For(list, null, false, "1.3")).Where(t => !t.Contains("rc", StringComparison.Ordinal)).ToList());
        Assert.Empty(TagViews.For(list, null, true, "no-such"));
    }

    // A picker opened with a tag (pasted, a Popular tile, the app's own) opens on that tag's family, where its row shows it.
    [Theory]
    [InlineData("1.31-alpine", "alpine", true)]
    [InlineData("alpine", "alpine", true)]
    [InlineData("stable-alpine", "alpine", true)]
    [InlineData("1.31.1-alpine", "alpine", true)]
    [InlineData("latest", "", true)]
    [InlineData("1.30", "", true)]
    // alpine3.24 has no chip: the shortest chip its image is listed under (as 1.31.2-alpine).
    [InlineData("1.31.2-alpine3.24", "alpine", false)]
    // Not among the tags read (an older one): by its name.
    [InlineData("1.29.8-alpine", "alpine", false)]
    [InlineData("1.29.8", "", false)]
    [InlineData("1.29.8-perl", null, false)]
    public void A_picker_opened_on_a_tag_opens_on_its_family(string tag, string? family, bool listed)
    {
        var list = Nginx();

        Assert.Equal(family, TagViews.FamilyOf(list, tag));
        Assert.Equal(listed, TagViews.For(list, family, false).Any(c => c.Tag == tag || c.Follows.Contains(tag)));
    }

    [Fact]
    public void A_tag_read_twice_counts_once_and_a_name_docker_wouldnt_take_is_left_out()
    {
        var tags = Image('a', 1, "latest", "1.2.0").Concat(Image('a', 1, "1.2.0")).Concat(Image('b', 2, "-bad", ".bad", new string('x', 129))).ToList();

        var list = TagRanking.Rank("acme/api", "Docker Hub", tags, Amd64, 400, true);

        Assert.Equal((2, 400, true), (list.Read, list.Total, list.More));
        Assert.Equal(["latest"], Assert.Single(list.Versions).Also);
    }

    [Theory]
    // A Windows name without platforms is hidden on a Linux host.
    [InlineData("ltsc2022", null, true)]
    [InlineData("3.12-windowsservercore-1809", null, true)]
    [InlineData("nanoserver-ltsc2025", null, true)]
    [InlineData("3.12-windowsservercore-1809", "windows/amd64", true)]
    // Another architecture stays (marked); another OS goes.
    [InlineData("3.12", "linux/s390x", false)]
    [InlineData("3.12", "windows/amd64", true)]
    [InlineData("cache", null, true)]
    [InlineData("buildcache-main", null, true)]
    [InlineData("mr-7", null, true)]
    [InlineData("sha-1234567", null, true)]
    [InlineData("1234567", null, false)]
    [InlineData("deadbee", null, true)]
    [InlineData("sha256-" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null, true)]
    [InlineData("1.2.3.att", null, true)]
    public void Signatures_ci_tags_and_other_oses_are_hidden(string tag, string? platform, bool hidden)
    {
        var facts = new TagFacts(tag, null, null, null, platform is null ? null : [platform]);

        Assert.Equal(hidden, TagRanking.Hidden(facts, new HashSet<string> { tag }, Amd64));
    }
}
