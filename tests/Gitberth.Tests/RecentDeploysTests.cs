using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Health;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>
/// The dashboard's Recent deploys is every app's, whatever its source: an app made from a registry image shows there as
/// well as a Git app's push, and so do updates and recreating changes of containers Gitberth found running.
/// </summary>
public sealed class RecentDeploysTests : IDisposable
{
    private readonly ImageKit.Db _db = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitberth-recent-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ActivityLog _activity;
    private static readonly DateTime Now = DateTime.UtcNow;

    public RecentDeploysTests()
    {
        Directory.CreateDirectory(_dir);
        _activity = new ActivityLog(_db, new StatsStore(new GitberthOptions { DataDir = _dir }));
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task SeedAsync()
    {
        await using var db = _db.CreateDbContext();
        var orb = new AppDefinition { Name = "orb", Slug = "orb", Source = AppSource.Git };
        var rabbit = new AppDefinition { Name = "rabbitmq", Slug = "rabbitmq", Container = "gb-rabbitmq", Source = AppSource.Registry };
        db.Apps.AddRange(orb, rabbit);
        await db.SaveChangesAsync();
        db.Deployments.AddRange(
            new Deployment { AppId = orb.Id, Trigger = DeployTrigger.Poll, Status = DeployStatus.Succeeded, CommitSha = "abcdef1234", QueuedAt = Now.AddHours(-3), FinishedAt = Now.AddHours(-3) },
            // Scheduled restarts aren't deploys.
            new Deployment { AppId = orb.Id, Trigger = DeployTrigger.Restart, Status = DeployStatus.Succeeded, QueuedAt = Now.AddMinutes(-5), FinishedAt = Now.AddMinutes(-5) },
            // Older than asked for.
            new Deployment { AppId = orb.Id, Trigger = DeployTrigger.Poll, Status = DeployStatus.Succeeded, QueuedAt = Now.AddDays(-40), FinishedAt = Now.AddDays(-40) });
        db.Activity.AddRange(
            new ActivityEntry { ContainerName = "gb-rabbitmq", AppId = rabbit.Id, At = Now.AddMinutes(-10), Kind = ActivityKind.Created, Tone = ActivityTone.Good, Text = "Created from rabbitmq:4-management" },
            // Not a deploy.
            new ActivityEntry { ContainerName = "gb-rabbitmq", AppId = rabbit.Id, At = Now.AddMinutes(-9), Kind = ActivityKind.Live, Tone = ActivityTone.Good, Text = "Memory limit changed live" });
        db.ContainerChanges.AddRange(
            new ContainerChangeEntry { ContainerName = "sample-index", Summary = "memory limit 256 MB", Kind = ChangeKind.Settings, Outcome = ChangeOutcome.Done, OfflineMs = 1200, StartedAt = Now.AddHours(-1), FinishedAt = Now.AddHours(-1) },
            new ContainerChangeEntry { ContainerName = "postgres", Summary = "updated postgres:18 (1a2b3c4 → 5d6e7f8)", Kind = ChangeKind.Update, Outcome = ChangeOutcome.Done, StartedAt = Now.AddHours(-2), FinishedAt = Now.AddHours(-2) },
            // Said by its Created entry.
            new ContainerChangeEntry { ContainerName = "gb-rabbitmq", Summary = "Creating gb-rabbitmq", Kind = ChangeKind.Create, Outcome = ChangeOutcome.Done, StartedAt = Now.AddMinutes(-10), FinishedAt = Now.AddMinutes(-10) },
            new ContainerChangeEntry { ContainerName = "sample-index", Summary = "port 80", Kind = ChangeKind.Ports, Outcome = ChangeOutcome.NothingChanged, StartedAt = Now.AddMinutes(-30), FinishedAt = Now.AddMinutes(-30) });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_registry_app_created_shows_with_every_other_apps_deploys_newest_first()
    {
        await SeedAsync();

        var recent = await _activity.RecentDeploysAsync(Now.AddDays(-30), 50);

        Assert.Equal(["rabbitmq", "sample-index", "postgres", "orb"], recent.Select(r => r.App));
        var created = recent[0];
        Assert.Equal("gb-rabbitmq", created.Key);
        Assert.Equal(ActivityKind.Created, created.Item.Kind);
        Assert.Equal("created from rabbitmq:4-management", created.Item.Text);
        Assert.Equal("apps/gb-rabbitmq", created.Item.Href);
        Assert.Equal(ActivityKind.Change, recent[1].Item.Kind);
        Assert.Equal("settings change: memory limit 256 MB (done, offline 1.2 s)", recent[1].Item.Text);
        Assert.Equal("apps/sample-index/activity", recent[1].Item.Href);
        Assert.Equal(ActivityKind.Updated, recent[2].Item.Kind);
        // An update's summary says "updated" itself: no "update: updated".
        Assert.Equal("updated postgres:18 (1a2b3c4 → 5d6e7f8) (done)", recent[2].Item.Text);
        var push = recent[3];
        Assert.Equal("gb-orb", push.Key);
        Assert.Equal("deployed abcdef1 · new commit", push.Item.Text);
        Assert.Equal(ActivityTone.Good, push.Item.Tone);
        Assert.StartsWith("apps/", push.Item.Href);
        Assert.Contains("/deployments/", push.Item.Href);
    }

    [Fact]
    public async Task Only_whats_asked_for_comes_back()
    {
        await SeedAsync();

        Assert.Equal(["rabbitmq", "sample-index"], (await _activity.RecentDeploysAsync(Now.AddDays(-30), 2)).Select(r => r.App));
        Assert.Equal(["rabbitmq"], (await _activity.RecentDeploysAsync(Now.AddMinutes(-15), 50)).Select(r => r.App));
    }
}
