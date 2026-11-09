using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Network;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>docker update for a container's limits and restart policy: what it sends, what it can't do live, and when it doesn't.</summary>
public sealed class ContainerUpdaterTests
{
    private static readonly IReadOnlySet<SettingField> Memory = new HashSet<SettingField> { SettingField.Memory };
    private static readonly IReadOnlySet<SettingField> Cpu = new HashSet<SettingField> { SettingField.Cpu };
    private static readonly IReadOnlySet<SettingField> Pids = new HashSet<SettingField> { SettingField.PidsLimit };

    private static ContainerSettings Limits(long? memory = null, long? swap = null, double? cpus = null, long? pids = null) =>
        new() { Image = "postgres:18", Resources = new ResourceSetting(memory, null, swap, cpus, null, null, pids) };

    private static JsonObject HostConfig(long memory = 0, long swap = 0, long quota = 0) =>
        new() { ["Memory"] = memory, ["MemorySwap"] = swap, ["CpuQuota"] = quota, ["CpuPeriod"] = quota > 0 ? 100000 : 0 };

    [Fact]
    public void Sends_swap_twice_the_new_limit_unless_one_is_set_and_keeps_unlimited_swap()
    {
        Assert.Equal("""{"Memory":536870912,"MemorySwap":1073741824}""", ContainerUpdater.Body(Limits(512L << 20), Memory, HostConfig(1L << 30, 2L << 30)).ToJsonString());
        Assert.Equal("""{"Memory":536870912,"MemorySwap":805306368}""", ContainerUpdater.Body(Limits(512L << 20, 768L << 20), Memory, HostConfig()).ToJsonString());
        Assert.Equal("""{"Memory":536870912,"MemorySwap":-1}""", ContainerUpdater.Body(Limits(512L << 20), Memory, HostConfig(1L << 30, -1)).ToJsonString());
        Assert.Equal("""{"Memory":536870912,"MemorySwap":-1}""", ContainerUpdater.Body(Limits(512L << 20, -1), Memory, HostConfig()).ToJsonString());
    }

    [Fact]
    public void Sends_only_the_fields_given()
    {
        var all = Limits(512L << 20, cpus: 1.5, pids: 200) with { Restart = new RestartSetting("on-failure", 5) };

        Assert.Equal("""{"NanoCpus":1500000000}""", ContainerUpdater.Body(all, Cpu, null).ToJsonString());
        Assert.Equal("""{"PidsLimit":200}""", ContainerUpdater.Body(all, Pids, null).ToJsonString());
        Assert.Equal("""{"RestartPolicy":{"Name":"on-failure","MaximumRetryCount":5}}""",
            ContainerUpdater.Body(all, new HashSet<SettingField> { SettingField.RestartPolicy }, null).ToJsonString());
        // No PID limit is -1: Docker takes it away live.
        Assert.Equal("""{"PidsLimit":-1}""", ContainerUpdater.Body(Limits(), Pids, null).ToJsonString());
    }

    [Fact]
    public void Taking_a_limit_away_or_moving_a_quota_to_cpus_cant_be_done_live()
    {
        // docker update reads 0 as "unchanged", and refuses CPUs next to a quota it can't clear.
        Assert.False(ContainerUpdater.CanApplyLive(Limits(1L << 30), Limits(), Memory, HostConfig(1L << 30)));
        Assert.False(ContainerUpdater.CanApplyLive(Limits(cpus: 2), Limits(), Cpu, HostConfig()));
        Assert.False(ContainerUpdater.CanApplyLive(Limits(cpus: 0.5), Limits(cpus: 1), Cpu, HostConfig(quota: 50000)));
        // Raising, lowering, setting one anew, and taking the PID limit away are.
        Assert.True(ContainerUpdater.CanApplyLive(Limits(1L << 30), Limits(2L << 30), Memory, HostConfig(1L << 30)));
        Assert.True(ContainerUpdater.CanApplyLive(Limits(1L << 30), Limits(512L << 20), Memory, HostConfig(1L << 30)));
        Assert.True(ContainerUpdater.CanApplyLive(Limits(), Limits(512L << 20), Memory, HostConfig()));
        Assert.True(ContainerUpdater.CanApplyLive(Limits(cpus: 1), Limits(cpus: 2), Cpu, HostConfig()));
        Assert.True(ContainerUpdater.CanApplyLive(Limits(pids: 200), Limits(), Pids, HostConfig()));
        // Anything but limits and the restart policy isn't live at all.
        Assert.False(ContainerUpdater.CanApplyLive(Limits(), Limits(), new HashSet<SettingField> { SettingField.Env }, HostConfig()));
    }

    [Fact]
    public async Task Updates_a_running_container_and_refuses_a_busy_one()
    {
        var clock = new FakeClock();
        var engine = new FakeEngine(clock);
        var inspect = DockerFixtures.Inspect("sample-index");
        inspect["State"]!["Running"] = true;
        engine.Add(inspect);
        var locks = new ContainerLocks();
        var journal = new MemoryJournal();
        var host = new FakeHost(engine);
        var changer = new PortChanger(engine, journal, locks, host, host, host, host, host, new GitberthOptions { DataDir = Path.GetTempPath() },
            NullLogger<PortChanger>.Instance, clock);
        var updater = new ContainerUpdater(engine, locks, new Lazy<PortChanger>(changer), null!, NullLogger<ContainerUpdater>.Instance);

        var done = await updater.ApplyAsync("sample-index", Limits(128L << 20), Memory);

        Assert.True(done.Ok, done.Message);
        Assert.Equal("Changed the memory limit. No restart.", done.Message);
        Assert.Equal(128L << 20, ContainerPorts.Number(engine.Get("sample-index").HostConfig, "Memory"));

        // Something holds its lock: refused, nothing sent.
        var calls = engine.Calls.Count;
        using (await locks.AcquireAsync("sample-index"))
        {
            var held = await updater.ApplyAsync("sample-index", Limits(256L << 20), Memory);
            Assert.Equal((false, "Gitberth is already changing sample-index. Try again when that's done."), (held.Ok, held.Message));
        }
        // A change to it waits for its first start: refused too.
        await journal.AddAsync(new ContainerChange
        {
            ContainerName = "sample-index", Summary = "", OldId = "0", OldName = "sample-index_old_0", TempName = "sample-index_new_0", OldRestartPolicy = "{}",
            Step = ChangeStep.WaitingForStart, Kind = ChangeKind.Settings,
        }, default);
        var busy = await updater.ApplyAsync("sample-index", Limits(256L << 20), Memory);
        Assert.False(busy.Ok);
        Assert.Equal("sample-index hasn't started since its last settings change. Start it, or put the original back, before changing it again.", busy.Message);
        // And a limit taken away isn't sent at all.
        Assert.False((await updater.ApplyToAsync("sample-index", Limits(), Memory)).Ok);
        Assert.Equal(calls, engine.Calls.Count);
    }
}
