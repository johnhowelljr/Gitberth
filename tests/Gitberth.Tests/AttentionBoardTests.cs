using Gitberth.Components.Layout;
using Gitberth.Components.Shared;
using Gitberth.Health;

namespace Gitberth.Tests;

/// <summary>
/// The attention board (Health/AttentionBoard): what Gitberth's own jobs say needs someone, kept in memory, and the three
/// places that show it: the dashboard's banners, the top bar's pill and get_health's problems. An item replaces the one
/// with its key and keeps its place, worst first; setting it again unchanged raises nothing; a dismissal holds while the
/// item says the same thing; a broken page never stops a job; many jobs at once are safe.
/// </summary>
public sealed class AttentionBoardTests
{
    private static AttentionItem Disk(AttentionLevel level = AttentionLevel.Warn, string? dismiss = "low") =>
        new("disk", "disk", level, "Docker's disk is getting low", "12.3 GB free of 64.0 GB.", "settings#disk",
            Actions: [new AttentionAction("See what takes the room", "settings#disk")], Dismiss: dismiss);

    private static AttentionItem Token(string name, AttentionLevel level, string? pill = null, string? dismiss = null) =>
        new($"credential:{name}", "credentials", level, $"The {name} token expires in 6 days", "Make a new token and save it.", $"settings#{name}",
            Dismiss: dismiss) { Pill = pill };

    [Fact]
    public void An_item_is_set_replaced_by_its_key_and_cleared_and_only_a_change_is_raised()
    {
        var board = new AttentionBoard();
        var changes = 0;
        board.Changed += () => changes++;
        board.Set(Disk());
        // The same again, its buttons in a list of their own (a job that looks every minute): no change.
        board.Set(Disk() with { Actions = [new AttentionAction("See what takes the room", "settings#disk")] });
        Assert.Equal(1, changes);
        board.Set(Disk() with { Text = "9.0 GB free of 64.0 GB." });
        Assert.Equal(2, changes);
        Assert.Equal("9.0 GB free of 64.0 GB.", Assert.Single(board.Items).Text);
        Assert.True(board.Clear("disk"));
        Assert.False(board.Clear("disk"));
        Assert.Equal(3, changes);
        Assert.Empty(board.Items);
        Assert.Empty(board.Shown);
    }

    [Fact]
    public void Items_come_worst_first_and_keep_their_place_when_set_again()
    {
        var board = new AttentionBoard();
        board.Set(Token("github", AttentionLevel.Info));
        board.Set(Token("gitlab", AttentionLevel.Warn));
        board.Set(Token("cloudflare", AttentionLevel.Warn));
        board.Set(Disk(AttentionLevel.Critical));
        Assert.Equal(["disk", "credential:gitlab", "credential:cloudflare", "credential:github"], board.Items.Select(i => i.Key));
        // Set again with new words, gitlab stays before cloudflare, which came after it.
        board.Set(Token("gitlab", AttentionLevel.Warn) with { Text = "Tomorrow, then." });
        Assert.Equal(["disk", "credential:gitlab", "credential:cloudflare", "credential:github"], board.Items.Select(i => i.Key));
        // A level that changes moves it: among equals, the one set first comes first.
        board.Set(Token("github", AttentionLevel.Critical));
        Assert.Equal(["credential:github", "disk", "credential:gitlab", "credential:cloudflare"], board.Items.Select(i => i.Key));
    }

    [Fact]
    public void A_dismissal_hides_the_banner_while_the_item_says_the_same_and_it_shows_again_with_a_new_stamp()
    {
        var board = new AttentionBoard();
        var kept = new List<AttentionItem>();
        board.Dismissed += kept.Add;
        board.Set(Token("github", AttentionLevel.Info, dismiss: "3f9c:21"));
        Assert.True(board.Dismiss("credential:github"));
        Assert.Empty(board.Shown);
        Assert.Single(board.Items);
        Assert.True(board.IsDismissed("credential:github"));
        Assert.Equal("3f9c:21", Assert.Single(kept).Dismiss);
        // Set again as it was: still hidden, and told only once.
        board.Set(Token("github", AttentionLevel.Info, dismiss: "3f9c:21"));
        Assert.True(board.Dismiss("credential:github"));
        Assert.Empty(board.Shown);
        Assert.Single(kept);
        // Its next threshold: shown again, and it stays shown.
        board.Set(Token("github", AttentionLevel.Warn, dismiss: "3f9c:7"));
        Assert.Single(board.Shown);
        board.Set(Token("github", AttentionLevel.Info, dismiss: "3f9c:21"));
        Assert.Single(board.Shown);
        Assert.False(board.IsDismissed("credential:github"));
    }

    [Fact]
    public void Only_an_item_that_allows_it_is_dismissed_and_clearing_it_forgets_the_dismissal()
    {
        var board = new AttentionBoard();
        board.Set(Token("github", AttentionLevel.Critical, pill: "GitHub token refused"));
        Assert.False(board.Dismiss("credential:github"));
        Assert.False(board.Dismiss("no-such-item"));
        Assert.Single(board.Shown);

        board.Set(Disk(dismiss: "low"));
        Assert.True(board.Dismiss("disk"));
        Assert.DoesNotContain(board.Shown, i => i.Key == "disk");
        board.Clear("disk");
        // It came back: a new time, so it shows.
        board.Set(Disk(dismiss: "low"));
        Assert.Contains(board.Shown, i => i.Key == "disk");
    }

    [Fact]
    public void A_dismissal_kept_across_a_restart_is_given_back_and_one_for_another_stamp_is_let_go()
    {
        var board = new AttentionBoard();
        board.KeepDismissed("credential:github", "3f9c:21");
        board.Set(Token("github", AttentionLevel.Info, dismiss: "3f9c:21"));
        Assert.Empty(board.Shown);
        Assert.Single(board.Items);

        var other = new AttentionBoard();
        other.Set(Token("github", AttentionLevel.Warn, dismiss: "3f9c:7"));
        other.KeepDismissed("credential:github", "3f9c:21");
        Assert.Single(other.Shown);
    }

    [Fact]
    public void Replace_makes_a_sources_items_exactly_these_in_one_change()
    {
        var board = new AttentionBoard();
        board.Set(Disk());
        board.Replace("credentials", [Token("github", AttentionLevel.Warn), Token("gitlab", AttentionLevel.Info)]);
        var changes = 0;
        board.Changed += () => changes++;
        board.Replace("credentials", [Token("gitlab", AttentionLevel.Info), Token("cloudflare", AttentionLevel.Critical)]);
        Assert.Equal(1, changes);
        Assert.Equal(["credential:cloudflare", "disk", "credential:gitlab"], board.Items.Select(i => i.Key));
        // The same again changes nothing; none at all leaves the other sources' items.
        board.Replace("credentials", [Token("gitlab", AttentionLevel.Info), Token("cloudflare", AttentionLevel.Critical)]);
        Assert.Equal(1, changes);
        board.Replace("credentials", []);
        Assert.Equal(["disk"], board.Items.Select(i => i.Key));
        Assert.Throws<ArgumentException>(() => board.Replace("credentials", [Disk()]));
    }

    [Fact]
    public void Agents_hear_of_every_item_from_warn_up_dismissed_or_not()
    {
        var board = new AttentionBoard();
        board.Set(Token("github", AttentionLevel.Info));
        board.Set(Disk(dismiss: "low"));
        board.Set(Token("gitlab", AttentionLevel.Critical) with { Problem = "GitLab refused the token: save a new one in Settings › GitLab." });
        board.Dismiss("disk");
        Assert.Equal(["GitLab refused the token: save a new one in Settings › GitLab.", "Docker's disk is getting low: 12.3 GB free of 64.0 GB."], board.Problems);
    }

    [Fact]
    public void The_pill_shows_the_worst_item_that_asks_for_one_and_how_many_more()
    {
        Assert.Null(AttentionPill.Look([]));
        var board = new AttentionBoard();
        board.Set(Disk());
        Assert.Empty(board.Pills);

        board.Set(Token("github", AttentionLevel.Critical, pill: "GitHub token refused"));
        var one = AttentionPill.Look(board.Pills)!;
        Assert.Equal(("GitHub token refused", "settings#github", "critical", 0, "GitHub token refused"), (one.Words, one.Href, one.Tone, one.More, one.Label));
        Assert.Equal("The github token expires in 6 days", one.Detail);

        // Two: the worst one's words, and the dashboard, which has every banner.
        board.Set(Token("cloudflare", AttentionLevel.Critical, pill: "Cloudflare token expired"));
        var two = AttentionPill.Look(board.Pills)!;
        Assert.Equal(("GitHub token refused", "", 1, "GitHub token refused, and 1 more"), (two.Words, two.Href, two.More, two.Label));
        Assert.Equal("The github token expires in 6 days · The cloudflare token expires in 6 days", two.Detail);

        // A dismissed item's pill goes with its banner.
        board.Set(Token("gitlab", AttentionLevel.Warn, pill: "GitLab token expires", dismiss: "x:7"));
        Assert.Equal(3, board.Pills.Count);
        board.Dismiss("credential:gitlab");
        Assert.Equal(2, board.Pills.Count);
    }

    [Fact]
    public void A_banner_shows_its_items_buttons_but_one_that_leads_where_it_already_is()
    {
        var item = Token("github", AttentionLevel.Warn) with
        {
            Actions = [new AttentionAction("Make a new token", "https://example.com/tokens"), new AttentionAction("Open Settings", "settings#github")],
        };
        Assert.Equal(["Make a new token", "Open Settings"], AttentionBanners.ActionsFor(item, null).Select(a => a.Label));
        Assert.Equal(["Make a new token"], AttentionBanners.ActionsFor(item, "settings#github").Select(a => a.Label));
        Assert.Empty(AttentionBanners.ActionsFor(Token("gitlab", AttentionLevel.Info), null));
        Assert.True(item.Actions![0].External);
        Assert.False(item.Actions[1].External);
        Assert.Equal(("critical", "warn", "info"),
            (AttentionBanners.Tone(AttentionLevel.Critical), AttentionBanners.Tone(AttentionLevel.Warn), AttentionBanners.Tone(AttentionLevel.Info)));
    }

    [Fact]
    public void An_item_needs_a_key_a_source_and_a_title_and_at_most_two_buttons()
    {
        var board = new AttentionBoard();
        Assert.Throws<ArgumentException>(() => board.Set(Disk() with { Key = "" }));
        Assert.Throws<ArgumentException>(() => board.Set(Disk() with { Source = "" }));
        Assert.Throws<ArgumentException>(() => board.Set(Disk() with { Title = " " }));
        Assert.Throws<ArgumentException>(() => board.Set(Disk() with { Actions = [new("One", "a"), new("Two", "b"), new("Three", "c")] }));
        Assert.Empty(board.Items);
    }

    [Fact]
    public void A_page_that_fails_to_follow_the_board_never_stops_the_job_that_set_the_item()
    {
        var board = new AttentionBoard();
        var heard = 0;
        board.Changed += () => throw new InvalidOperationException("a page whose connection just dropped");
        board.Changed += () => heard++;
        board.Dismissed += _ => throw new InvalidOperationException("the job couldn't save it");
        board.Set(Disk());
        Assert.True(board.Dismiss("disk"));
        Assert.Equal(2, heard);
        Assert.Empty(board.Shown);
    }

    [Fact]
    public async Task Many_jobs_set_and_clear_at_once_while_pages_read()
    {
        var board = new AttentionBoard();
        var reads = 0;
        board.Changed += () => Interlocked.Add(ref reads, board.Shown.Count >= 0 ? 1 : 0);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(job => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                board.Set(new AttentionItem($"job{job}:{i % 10}", $"job{job}", (AttentionLevel)(i % 3), $"Item {i}", "Something to say."));
                if (i % 7 == 0) board.Clear($"job{job}:{(i + 3) % 10}");
            }
        })));
        // Each job only touched its own keys, one after the other: the board ends as each would have alone.
        var left = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            left.Add($"{i % 10}");
            if (i % 7 == 0) left.Remove($"{(i + 3) % 10}");
        }
        for (var job = 0; job < 8; job++)
            Assert.Equal(left.Order().Select(k => $"job{job}:{k}"), board.Items.Where(i => i.Source == $"job{job}").Select(i => i.Key).Order());
        // Worst first throughout, and nothing dismissed.
        Assert.Equal(board.Items.Select(i => i.Level).OrderByDescending(l => l), board.Items.Select(i => i.Level));
        Assert.Equal(board.Items, board.Shown);
        Assert.True(reads > 0);
    }
}
