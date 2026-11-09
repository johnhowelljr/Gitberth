using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

public class RestartTests
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    [Theory]
    [InlineData(5, RestartUnit.Minutes, 5, "every 5 minutes")]
    [InlineData(90, RestartUnit.Minutes, 90, "every 90 minutes")]
    [InlineData(1, RestartUnit.Hours, 60, "every hour")]
    [InlineData(6, RestartUnit.Hours, 360, "every 6 hours")]
    [InlineData(1, RestartUnit.Days, 1440, "every day")]
    [InlineData(7, RestartUnit.Days, 10080, "every 7 days")]
    public void An_interval_is_kept_in_minutes_and_read_back_in_its_largest_unit(int amount, RestartUnit unit, int minutes, string text)
    {
        var interval = new RestartInterval(amount, unit);

        Assert.Equal(minutes, interval.TotalMinutes);
        Assert.Equal(text, interval.ToString());
        Assert.Equal(interval, RestartInterval.FromMinutes(minutes));
    }

    [Theory]
    [InlineData(null, RestartUnit.Hours, "whole number")]
    [InlineData(0, RestartUnit.Hours, "whole number")]
    [InlineData(4, RestartUnit.Minutes, "shortest")]
    [InlineData(366, RestartUnit.Days, "longest")]
    [InlineData(int.MaxValue, RestartUnit.Days, "longest")]
    public void Rejects_a_schedule_that_is_missing_too_short_or_too_long(int? amount, RestartUnit unit, string problem) =>
        Assert.Contains(problem, RestartInterval.Problem(amount, unit));

    [Fact]
    public void Accepts_the_shortest_and_longest_schedules()
    {
        Assert.Null(RestartInterval.Problem(5, RestartUnit.Minutes));
        Assert.Null(RestartInterval.Problem(365, RestartUnit.Days));
        Assert.Null(RestartInterval.Problem(8760, RestartUnit.Hours));
    }

    [Fact]
    public void The_settings_form_maps_its_choice_to_an_interval_and_back()
    {
        Assert.Null(new AppSettingsModel().RestartInterval);
        var model = new AppSettingsModel { RestartEvery = RestartChoice.Hours, RestartAmount = 12 };
        Assert.Equal(new RestartInterval(12, RestartUnit.Hours), model.RestartInterval);
        Assert.Equal((RestartChoice.Hours, (int?)12), RestartChoices.From(model.RestartInterval));
        Assert.Equal((RestartChoice.Never, (int?)null), RestartChoices.From(null));
        // No amount yet: it's still a schedule, which validation turns down.
        Assert.NotNull(RestartInterval.Problem(new AppSettingsModel { RestartEvery = RestartChoice.Days }.RestartInterval!.Value.Amount, RestartUnit.Days));
    }

    [Fact]
    public async Task Schedules_are_saved_by_container_name_and_read_back_after_a_restart()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(connection).Options);
        await using (var context = db.CreateDbContext()) await context.Database.EnsureCreatedAsync();
        var settings = new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider()));
        var schedules = new RestartSchedules(settings);

        await schedules.SetAsync("gb-web", new RestartInterval(6, RestartUnit.Hours));
        await schedules.SetAsync("postgres", new RestartInterval(1, RestartUnit.Days));
        await schedules.SetAsync("postgres", null);
        await Assert.ThrowsAsync<ArgumentException>(() => schedules.SetAsync("cache", new RestartInterval(1, RestartUnit.Minutes)));

        var loaded = new RestartSchedules(settings);
        await loaded.LoadAsync();
        Assert.Equal(new RestartInterval(6, RestartUnit.Hours), loaded.For("gb-web"));
        Assert.Null(loaded.For("postgres"));
        Assert.Null(loaded.For("cache"));
        Assert.Equal(["gb-web"], loaded.All.Keys);
    }

    [Fact]
    public void A_damaged_setting_reads_as_no_schedules_and_bad_entries_are_dropped()
    {
        Assert.Empty(RestartSchedules.Parse("not json"));
        Assert.Empty(RestartSchedules.Parse(null));
        Assert.Equal(["a"], RestartSchedules.Parse("""{"a":60,"b":1,"":60}""").Keys);
    }

    [Theory]
    [InlineData("true 2026-11-04T10:00:00.123456789Z\n", "2026-11-04T10:00:00Z")]
    [InlineData("true 2026-11-04T10:00:00Z", "2026-11-04T10:00:00Z")]
    [InlineData("false 2026-11-04T10:00:00.5Z", null)]
    [InlineData("Error: No such container", null)]
    [InlineData("", null)]
    public void Reads_when_a_running_container_started(string output, string? started)
    {
        var parsed = DockerCli.ParseStartedAt(output);

        Assert.Equal(started is null ? null : DateTime.Parse(started, null, System.Globalization.DateTimeStyles.AdjustToUniversal), parsed);
        if (parsed is { } at) Assert.Equal(DateTimeKind.Utc, at.Kind);
    }

    [Fact]
    public async Task A_standalone_container_gets_the_stop_it_asks_for()
    {
        var calls = new List<string>();
        var settings = "";
        var docker = new DockerCli(new GitberthOptions { DataDir = Path.GetTempPath() })
        {
            Runner = (args, _, _) =>
            {
                lock (calls) calls.Add(string.Join(' ', args));
                return Task.FromResult(args[0] == "container" ? new ProcessResult(0, settings + "\n", "") : new ProcessResult(0, "", ""));
            },
        };

        // A database without a stop timeout of its own gets a minute to shut down cleanly; anything else 20 s.
        settings = "null \"postgres:18\"";
        await docker.StopStandaloneAsync("postgres", ContainerService.IsDatabaseImage);
        settings = "null \"nginx:stable-alpine\"";
        await docker.RestartStandaloneAsync("web", ContainerService.IsDatabaseImage);
        // Its own (docker run --stop-timeout) comes first: -1 never kills it, 0 kills it at once.
        settings = "7 \"postgres:18\"";
        await docker.StopStandaloneAsync("pg-old", ContainerService.IsDatabaseImage);
        settings = "-1 \"postgres:18\"";
        await docker.RestartStandaloneAsync("pg-slow", ContainerService.IsDatabaseImage);
        settings = "0 \"redis:7\"";
        await docker.StopStandaloneAsync("cache", ContainerService.IsDatabaseImage);

        Assert.Equal(["stop -t 60 postgres", "restart -t 20 web", "stop -t 7 pg-old", "restart -t -1 pg-slow", "stop -t 0 cache"],
            calls.Where(c => !c.StartsWith("container ", StringComparison.Ordinal)));
        Assert.Equal("container inspect --format {{json .Config.StopTimeout}} {{json .Config.Image}} postgres", calls[0]);
        // One that can't be read stops like anything else.
        Assert.Equal(((int?)null, ""), DockerCli.ParseStopSettings("Error: No such container: gone"));
        Assert.Equal(20, DockerCli.StandaloneStopSeconds(null, database: false));
    }

    [Fact]
    public void A_container_is_due_once_it_has_been_up_for_its_interval_and_wasnt_just_restarted()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var every = TimeSpan.FromHours(6);

        Assert.False(RestartScheduler.IsDue(now.AddHours(-5), every, null, now));
        Assert.True(RestartScheduler.IsDue(now.AddHours(-6), every, null, now));
        // It was restarted an hour ago but its start time didn't change (it didn't come back): not again before the interval passes.
        Assert.False(RestartScheduler.IsDue(now.AddHours(-9), every, now.AddHours(-1), now));
        Assert.True(RestartScheduler.IsDue(now.AddHours(-9), every, now.AddHours(-6), now));
    }
}
