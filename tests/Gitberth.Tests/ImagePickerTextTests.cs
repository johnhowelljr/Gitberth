using Gitberth.Components.Shared;
using Gitberth.Images;

namespace Gitberth.Tests;

/// <summary>
/// New app's Docker Hub search and the tag picker, in words (ImagePickerText): counts as Docker Hub writes them, when an
/// image was updated, a badge's word and tip, what a chosen tag follows, the highlight's moves, the filter that waits for
/// an edit, a typed reference kept as typed, and the line for a Docker Hub that won't answer with its countdown.
/// </summary>
public sealed class ImagePickerTextTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    // A row as the backend ranks one: its tag, its version, the others naming it (nginx's E1, row A).
    private static TagRow Row(string tag, string? version, string[] also, bool inferred = false, bool pre = false, string? digest = "sha256:" + "a1") =>
        new(tag, version, also, inferred, [""], digest, Now.AddDays(-2), 26_100_000, ["linux/amd64"], pre, true);

    [Fact]
    public void Counts_read_as_docker_hub_writes_them()
    {
        Assert.Equal(("842", "1k", "21.4k", "150k"), (ImagePickerText.Count(842), ImagePickerText.Count(1_000), ImagePickerText.Count(21_427), ImagePickerText.Count(150_400)));
        Assert.Equal(("31M", "1M", "13.6B", "1.2B"), (ImagePickerText.Count(31_000_000), ImagePickerText.Count(999_960), ImagePickerText.Count(13_624_900_407),
            ImagePickerText.Count(1_204_000_000)));
        // Docker Hub's own words win; the basic search's exact count is counted; nothing said, nothing shown.
        Assert.Equal("1B+", ImagePickerText.Pulls(13_624_900_407, "1B+"));
        Assert.Equal("13.6B", ImagePickerText.Pulls(13_624_900_407, null));
        Assert.Null(ImagePickerText.Pulls(null, " "));
        Assert.Equal(("26.1 MB", null), (ImagePickerText.Size(26_100_000), ImagePickerText.Size(null)));
    }

    [Fact]
    public void Updated_says_how_long_ago_and_nothing_when_docker_hub_didnt_say()
    {
        Assert.Equal("Updated 2 days ago", ImagePickerText.Updated(Now.AddDays(-2), Now));
        Assert.Equal("Updated 3 months ago", ImagePickerText.Updated(Now.AddDays(-95), Now));
        Assert.Equal("Updated just now", ImagePickerText.Updated(Now.AddMinutes(-5), Now));
        Assert.Null(ImagePickerText.Updated(null, Now));
        Assert.Equal("2 weeks ago", ImagePickerText.Pushed(Now.AddDays(-15), Now));
        Assert.Null(ImagePickerText.Pushed(null, Now));
    }

    [Fact]
    public void Each_badge_has_its_word_and_its_tip()
    {
        Assert.Equal(("Official", "images.badge.official"), (ImagePickerText.BadgeWord(HubBadge.Official), TipKeys.Badge(HubBadge.Official)));
        Assert.Equal(("Verified Publisher", "images.badge.verified"), (ImagePickerText.BadgeWord(HubBadge.VerifiedPublisher), TipKeys.Badge(HubBadge.VerifiedPublisher)));
        Assert.Equal(("Sponsored OSS", "images.badge.oss"), (ImagePickerText.BadgeWord(HubBadge.SponsoredOss), TipKeys.Badge(HubBadge.SponsoredOss)));
        Assert.Equal((null, null), (ImagePickerText.BadgeWord(HubBadge.None), TipKeys.Badge(HubBadge.None)));
        Assert.Equal(3, TipKeys.Computed.Count(k => k.StartsWith("images.badge.", StringComparison.Ordinal)));
        Assert.Equal(("No arm64 image", "No arm/v7 image", "No image for this host"),
            (ImagePickerText.NoImageFor("arm64"), ImagePickerText.NoImageFor("arm/v7"), ImagePickerText.NoImageFor(null)));
    }

    [Fact]
    public void The_docker_hub_link_is_built_from_the_parsed_name()
    {
        Assert.Equal("https://hub.docker.com/_/nginx", ImagePickerText.HubPage("nginx"));
        Assert.Equal("https://hub.docker.com/r/louislam/uptime-kuma", ImagePickerText.HubPage("louislam/uptime-kuma"));
        Assert.Null(ImagePickerText.HubPage("ghcr.io/owner/app"));
        Assert.Null(ImagePickerText.HubPage("javascript:alert(1)"));
    }

    [Fact]
    public void A_chosen_tag_says_what_it_follows()
    {
        var a = Row("1.31.2", "1.31.2", ["1.31", "1", "latest", "mainline"]);
        Assert.Equal("Follows 1.31, now 1.31.2: patch releases arrive as updates.", ImagePickerText.Follows("1.31", a));
        Assert.Equal("Pinned to 1.31.2: only rebuilds of it arrive as updates.", ImagePickerText.Follows("1.31.2", a));
        Assert.Equal("Follows latest, now 1.31.2: every release arrives as an update, majors included.", ImagePickerText.Follows("latest", a));
        Assert.Equal("Follows mainline, now 1.31.2: every release arrives as an update, majors included.", ImagePickerText.Follows("mainline", a));
        Assert.Equal("Follows 1, now 1.31.2: minor releases and patches arrive as updates, never a new major.", ImagePickerText.Follows("1", a));
        Assert.Equal("Pinned to one exact image: it never updates.", ImagePickerText.Follows("sha256:" + new string('b', 64), a));
        // An empty field is latest.
        Assert.Equal(ImagePickerText.Follows("latest", a), ImagePickerText.Follows("  ", a));

        // Under a variant's chip, the row is shown by that variant's tag.
        var c = Row("1.31.2-alpine", "1.31.2", ["1.31.2-alpine3.24", "1.31-alpine", "alpine"]);
        Assert.Equal("Follows 1.31-alpine, now 1.31.2-alpine: patch releases arrive as updates.", ImagePickerText.Follows("1.31-alpine", c, "1.31.2-alpine"));
        // As many numbers as the version: the same release by another name, so pinned.
        Assert.Equal("Pinned to 1.31.2-alpine3.24: only rebuilds of it arrive as updates.", ImagePickerText.Follows("1.31.2-alpine3.24", c));
        Assert.Equal("Pinned to v3.3.1: only rebuilds of it arrive as updates.", ImagePickerText.Follows("v3.3.1", Row("3.3.1", "3.3.1", ["v3.3.1", "3.3"])));

        // tags/list gives no digests: the floating tag is matched by name. A channel on a row with no version can't say what it names.
        Assert.Equal("Follows 1.27, likely 1.27.3 now: patch releases arrive as updates.",
            ImagePickerText.Follows("1.27", Row("1.27.3", "1.27.3", ["1.27", "1"], inferred: true, digest: null)));
        Assert.Equal("Follows latest: every release arrives as an update, majors included.", ImagePickerText.Follows("latest", Row("latest", null, ["trixie"])));
        Assert.Equal("Pinned to 1.32.0-rc.1, a pre-release: only rebuilds of it arrive as updates.",
            ImagePickerText.Follows("1.32.0-rc.1", Row("1.32.0-rc.1", "1.32.0-rc.1", [], pre: true)));
        Assert.Equal("nightly-2 isn't among the tags listed: Check asks the registry for it.", ImagePickerText.Follows("nightly-2", null));
    }

    [Fact]
    public void A_list_finds_the_row_a_tag_names()
    {
        var a = Row("1.31.2", "1.31.2", ["1.31", "latest"]);
        var c = Row("1.31.2-alpine", "1.31.2", ["1.31-alpine", "alpine"]);
        var bookworm = Row("bookworm", null, []);
        var list = new TagList("nginx", "Docker Hub", true, [new("", "Standard", 1, true), new("alpine", "alpine", 1, false)], [a, c], [bookworm], a, "1.31", 3, 3, false);
        Assert.Same(a, ImagePickerText.RowOf(list, "latest"));
        Assert.Same(c, ImagePickerText.RowOf(list, "1.31.2-alpine"));
        Assert.Same(bookworm, ImagePickerText.RowOf(list, "bookworm"));
        Assert.Null(ImagePickerText.RowOf(list, "1.30"));
        Assert.Null(ImagePickerText.RowOf(null, "latest"));

        // The tag it opened with wins; else the list's default (one part shorter, D1); else latest.
        Assert.Equal("1.27", ImagePickerText.Initial(" 1.27 ", list));
        Assert.Equal("1.31", ImagePickerText.Initial(null, list));
        Assert.Equal("latest", ImagePickerText.Initial(null, list with { Default = null }));
        Assert.Equal("latest", ImagePickerText.Initial("", null));

        Assert.True(ImagePickerText.RunningNow(a, ["sha256:a1", "sha256:zz"]));
        Assert.False(ImagePickerText.RunningNow(a, ["sha256:zz"]));
        Assert.False(ImagePickerText.RunningNow(Row("1.27.3", "1.27.3", [], digest: null), ["sha256:a1"]));
    }

    [Fact]
    public void The_highlight_wraps_round_and_the_filter_waits_for_an_edit()
    {
        Assert.Equal((1, 0, 3, 2), (ImagePickerText.Move(0, 4, 1), ImagePickerText.Move(3, 4, 1), ImagePickerText.Move(0, 4, -1), ImagePickerText.Move(3, 4, -1)));
        // Nothing highlighted yet: ↓ takes the first, ↑ the last; no rows, no highlight.
        Assert.Equal((0, 3, -1), (ImagePickerText.Move(-1, 4, 1), ImagePickerText.Move(-1, 4, -1), ImagePickerText.Move(2, 0, 1)));
        Assert.Equal(0, ImagePickerText.Move(9, 4, 1));

        // The field shows the selection (a chip clicked, the tag preselected) without filtering by it; typed text filters.
        Assert.Null(ImagePickerText.Filter("1.31", edited: false));
        Assert.Equal("1.2", ImagePickerText.Filter(" 1.2 ", edited: true));
        Assert.Null(ImagePickerText.Filter("  ", edited: true));
    }

    [Fact]
    public void A_typed_reference_is_kept_as_typed_and_never_searched()
    {
        Assert.Null(ImagePickerText.Pasted("nginx"));
        Assert.Null(ImagePickerText.Pasted("bitnami/nginx"));
        Assert.Equal(("ghcr.io/owner/app", "1.4"), (ImagePickerText.Pasted("ghcr.io/owner/app:1.4")!.Name, ImagePickerText.Pasted("ghcr.io/owner/app:1.4")!.Tag));
        Assert.Equal("nginx", ImagePickerText.Pasted("nginx:1.27")!.Name);
        Assert.Equal("nginx", ImagePickerText.Pasted("library/nginx:1.27")!.Name);
        // The bug the old row had: a namespace ending in "library" lost it (fooapp).
        Assert.Equal("foolibrary/app", ImagePickerText.Pasted("foolibrary/app:2")!.Name);
        Assert.Equal("registry.e2e.test/team/api", ImagePickerText.Pasted("registry.e2e.test/team/api")!.Name);
        Assert.Equal("localhost:5000/app", ImagePickerText.Pasted("localhost:5000/app")!.Name);
        var digest = "sha256:" + new string('c', 64);
        Assert.Equal(digest, ImagePickerText.Pasted("redis@" + digest)!.Digest);
        Assert.Null(ImagePickerText.Pasted("Nginx:1"));

        Assert.True(ImagePickerText.Searchable("ng"));
        Assert.True(ImagePickerText.Searchable("  bitnami/ngi  "));
        Assert.False(ImagePickerText.Searchable("n"));
        Assert.False(ImagePickerText.Searchable("nginx:1"));
        Assert.False(ImagePickerText.Searchable("redis@sha256"));
        Assert.False(ImagePickerText.Searchable("ghcr.io/o"));
        Assert.False(ImagePickerText.Searchable(new string('a', 101)));

        Assert.Equal("nginx:1.31", ImagePickerText.Reference("nginx", "1.31"));
        Assert.Equal("nginx:latest", ImagePickerText.Reference("nginx", " "));
        Assert.Equal("ghcr.io/o/r@" + digest, ImagePickerText.Reference("ghcr.io/o/r", digest));
        Assert.Equal(("18", digest, "latest"), (ImagePickerText.TagOf(ImageReference.Parse("postgres:18")!), ImagePickerText.TagOf(ImageReference.Parse("redis:7@" + digest)!),
            ImagePickerText.TagOf(ImageReference.Parse("redis")!)));
    }

    [Fact]
    public void A_docker_hub_that_wont_answer_gets_one_line_with_its_countdown()
    {
        const string wait = "Docker Hub asked Gitberth to wait before searching again. Type the image's full name meanwhile.";
        Assert.Equal(wait + " · again in 40 s", ImagePickerText.SearchProblem(LookupProblem.RateLimited, wait, Now.AddSeconds(40), Now));
        Assert.Equal(wait + " · again in 2 min", ImagePickerText.SearchProblem(LookupProblem.RateLimited, wait, Now.AddSeconds(90), Now));
        // The wait is over: the line stays, without the countdown.
        Assert.Equal(wait, ImagePickerText.SearchProblem(LookupProblem.RateLimited, wait, Now.AddSeconds(-1), Now));
        Assert.Equal(("again in 1 s", null), (ImagePickerText.Countdown(Now.AddMilliseconds(200), Now), ImagePickerText.Countdown(null, Now)));
        Assert.Null(ImagePickerText.SearchProblem(LookupProblem.None, "anything", null, Now));
        // Without the search's own sentence, the problem's.
        Assert.StartsWith("Docker Hub can't be reached from this host", ImagePickerText.SearchProblem(LookupProblem.Unreachable, null, null, Now));
        Assert.StartsWith("Docker Hub's search didn't answer properly", ImagePickerText.SearchProblem(LookupProblem.Failed, " ", null, Now));

        Assert.Equal("registry.e2e.test didn't list team/api's tags (HTTP 500). Type the tag instead.",
            ImagePickerText.TagsProblem(LookupProblem.Failed, "registry.e2e.test didn't list team/api's tags (HTTP 500).", null, Now));
        Assert.Equal("Docker Hub asked Gitberth to wait before listing tags again. Type the tag instead. · again in 12 s",
            ImagePickerText.TagsProblem(LookupProblem.RateLimited, "Docker Hub asked Gitberth to wait before listing tags again.", Now.AddSeconds(12), Now));
        Assert.Null(ImagePickerText.TagsProblem(LookupProblem.None, null, null, Now));
        Assert.Equal("Listing nginx's tags…", ImagePickerText.Listing("nginx"));
    }

    [Fact]
    public void The_line_under_the_list_says_how_many_tags_were_read_and_left_out()
    {
        TagList Listed(string registry, bool dates, int read, int total, bool more) => new("nginx", registry, dates, [], [], [], null, null, read, total, more);
        // Older pages left: what was read of the registry's count.
        Assert.Equal("288 of 1,409 tags read, newest first.", ImagePickerText.TagCount(Listed("Docker Hub", true, 288, 1409, more: true)));
        // Every page read: the count, and the tags never listed (nginx's Windows tag and its signature), which can be typed.
        Assert.Equal("130 tags. 2 aren't listed (signatures, CI builds and the like): type one to use it.", ImagePickerText.TagCount(Listed("Docker Hub", true, 128, 130, false)));
        Assert.Equal("10 tags, from registry.e2e.test, which gives no dates. 1 isn't listed (signatures, CI builds and the like): type one to use it.",
            ImagePickerText.TagCount(Listed("registry.e2e.test", false, 9, 10, false)));
        Assert.Equal("21 tags.", ImagePickerText.TagCount(Listed("Docker Hub", true, 21, 21, false)));
    }
}
