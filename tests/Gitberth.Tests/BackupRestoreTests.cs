using Gitberth.Backup;
using Gitberth.Data;
using Gitberth.Domains;
using Gitberth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Restoring: a backup saved on one host and restored onto a fresh one, in the plan's order (networks, volumes, images,
/// database servers with their dumps, then the other containers, then Gitberth's data staged and a restart); a restore
/// that stopped goes on from where it was; a damaged file restores nothing; the staged data replaces the fresh install's
/// at the next start; and the gate holds Gitberth's jobs off meanwhile.
/// </summary>
public sealed class BackupRestoreTests
{
    // The host the backup is made on: a Postgres server with its data volume, a web container with its files, a tunnel,
    // a container on a network of its own, and an image only this host has.
    private static async Task<byte[]> SaveAsync(BackupHost host)
    {
        host.Files("old-machine");
        var engine = host.Engine;
        engine.AddImage("sha256:pg", "postgres:18", "postgres@sha256:" + new string('a', 64));
        engine.AddImage("sha256:web", "nginx:1.29", "nginx@sha256:" + new string('b', 64));
        engine.AddImage("sha256:cf", "cloudflare/cloudflared:latest", "cloudflare/cloudflared@sha256:" + new string('c', 64));
        engine.AddImage("sha256:tool", "backup-agent:1.0");
        foreach (var (reference, digest) in new[] { ("postgres:18", 'a'), ("nginx:1.29", 'b'), ("cloudflare/cloudflared:latest", 'c') })
            engine.Registry[reference] = "sha256:" + new string(digest, 64);
        engine.AddContainer("db", "postgres:18", "sha256:pg", env: ["POSTGRES_PASSWORD=s3cr3t-api-key"], volumes: ["db-data:/var/lib/postgresql"], networks: ["appnet"]);
        engine.AddContainer("web", "nginx:1.29", "sha256:web", volumes: ["web-data:/usr/share/nginx/html"], networks: ["appnet"], hostPort: 8081);
        engine.AddContainer("cloudflare-tunnel", "cloudflare/cloudflared:latest", "sha256:cf", env: ["TUNNEL_TOKEN=tok-9f8e7d6c5b4a"]);
        engine.AddContainer("tool", "backup-agent:1.0", "sha256:tool", running: false);
        engine.AddVolume("web-data", "marker-5be1 web page");
        engine.AddVolume("db-data", "the database's own files");
        var service = host.Backup();
        var items = await service.ItemsAsync();
        var ticket = await service.StartSaveAsync(new BackupChoice(items.Select(i => i.Id).ToHashSet(), false), BackupHost.Passphrase);
        var file = new MemoryStream();
        await service.WriteAsync(service.Claim(ticket.Id)!, file, default);
        return file.ToArray();
    }

    // The fresh host: Gitberth's own container and the base images it has (none of the saved containers).
    private static BackupHost Fresh()
    {
        var host = new BackupHost();
        host.Engine.AddImage("sha256:pg", "postgres:18", "postgres@sha256:" + new string('a', 64));
        File.WriteAllText(Path.Combine(host.Options.KeysDir.EnsureDirectory(), "key-fresh.xml"), "<key id=\"fresh\"/>");
        return host;
    }

    private static int At(List<string> calls, string start)
    {
        var i = calls.FindIndex(c => c.StartsWith(start, StringComparison.Ordinal));
        Assert.True(i >= 0, $"no call starts with \"{start}\": {string.Join(" | ", calls)}");
        return i;
    }

    [Fact]
    public async Task A_backup_comes_back_on_a_fresh_host_in_the_plans_order_and_restarts_Gitberth_on_its_data()
    {
        using var old = new BackupHost();
        var file = await SaveAsync(old);
        using var host = Fresh();
        var restore = host.Restore();
        Assert.Null(restore.CantRestore());
        Assert.Null(restore.UploadedBytes());
        Assert.Equal(file.Length, await restore.ReceiveAsync(new MemoryStream(file), default));
        Assert.Equal(file.Length, restore.UploadedBytes());

        // The wrong passphrase opens nothing; the right one reads the manifest and this host's warnings.
        var wrong = await restore.CheckAsync("not the passphrase at all");
        Assert.Equal(UploadProblem.WrongPassphrase, wrong.Problem);
        Assert.Null(wrong.Summary);
        var check = await restore.CheckAsync(BackupHost.Passphrase);
        Assert.Equal(UploadProblem.None, check.Problem);
        Assert.Equal("homelab", check.Summary!.Host);
        Assert.Contains(check.Summary.Items, i => i.Id == "volume:web-data");
        Assert.Contains(check.Summary.Warnings, w => w.Kind == RestoreWarningKind.TunnelElsewhere && !w.Blocks);
        Assert.DoesNotContain(check.Summary.Warnings, w => w.Blocks);
        // A Gitberth with no apps takes the whole backup; each top-level container is a target of its own, none here.
        Assert.True(check.Everything);
        Assert.Equal(["container:cloudflare-tunnel", "container:db", "container:tool", "container:web"], check.Targets!.Select(t => t.ItemId).Order(StringComparer.Ordinal));
        Assert.Equal(new RestoreTarget("container:web", false, "web2", null), check.Targets!.Single(t => t.ItemId == "container:web"));
        Assert.Empty(host.Engine.Calls);

        await restore.StartAsync(BackupHost.Passphrase);
        Assert.True(host.Gate.Holding);
        await restore.PhaseOne;

        var calls = host.Engine.Calls;
        var status = restore.Status()!;
        Assert.True(status.Active && !status.Finished && status.Phase == 2, string.Join(" | ", restore.LogLines(0)));
        Assert.Equal(0, status.Failed);
        Assert.All(status.Steps.Where(s => s.Id is not ("restart" or "domains" or "finish")), s => Assert.True(s.State is RestoreStepState.Done or RestoreStepState.Skipped, $"{s.Id}: {s.State} {s.Note}"));
        Assert.Equal(1, host.App.Stops);
        // Phase 2 still to run after the restart: the gate stays closed, and so it is when Gitberth starts again.
        Assert.True(host.Gate.Holding);
        Assert.True(new RestoreGate(host.Options).Holding);

        // Networks, then volumes made and filled, images loaded and pulled, the database server with its dump, then the rest.
        Assert.True(At(calls, "network appnet") < At(calls, "volume web-data"));
        Assert.True(At(calls, "volume web-data") < At(calls, "put gitberth-helper-restore-"));
        Assert.True(At(calls, "put gitberth-helper-restore-") < At(calls, "load image tar of backup-agent:1.0"));
        Assert.True(At(calls, "load image tar") < At(calls, "pull docker.io/library/nginx sha256:" + new string('b', 64)));
        Assert.True(At(calls, "pull docker.io/library/nginx") < At(calls, "create db"));
        Assert.True(At(calls, "create db") < At(calls, "start db") && At(calls, "start db") < At(calls, "exec db pg_isready") && At(calls, "exec db pg_isready") < At(calls, "exec db psql"));
        Assert.True(At(calls, "exec db psql") < At(calls, "create cloudflare-tunnel"));
        Assert.True(At(calls, "create cloudflare-tunnel") < At(calls, "create tool") && At(calls, "create tool") < At(calls, "create web"));
        Assert.Contains("start web", calls);
        Assert.DoesNotContain("start tool", calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("pull docker.io/library/postgres", StringComparison.Ordinal));
        Assert.Contains("tag nginx@sha256:" + new string('b', 64) + " nginx:1.29", calls);

        // What came back: the volume's files as saved, the database's volume empty but its dump loaded, the containers as
        // they were (the web one on its network, with its port), and no helper left.
        Assert.Equal(old.Engine.Files["web-data"], host.Engine.Files["web-data"]);
        Assert.False(host.Engine.Files.ContainsKey("db-data"));
        Assert.True(host.Engine.Volumes.ContainsKey("db-data"));
        Assert.Equal(old.Engine.Dump, Assert.Single(host.Engine.Loaded));
        var web = host.Engine.Containers["web"];
        Assert.Equal("nginx:1.29", (string?)web["Config"]!["Image"]);
        Assert.Equal("8081", (string?)web["HostConfig"]!["PortBindings"]!["80/tcp"]![0]!["HostPort"]);
        Assert.NotNull(web["NetworkSettings"]!["Networks"]!["appnet"]);
        Assert.DoesNotContain(host.Engine.Containers.Keys, n => n.StartsWith("gitberth-helper", StringComparison.Ordinal));
        Assert.Equal("yes", (string?)host.Engine.Volumes["web-data"]["Labels"]!["kept"]);

        // Gitberth's data is staged, whole; the log never shows a secret.
        Assert.True(File.Exists(host.Paths.StagedReady));
        foreach (var staged in new[] { "gitberth.db", "keys/key-1.xml", "logs/deploy-1.log", "compose/site/data/notes.txt" })
            Assert.True(File.Exists(Path.Combine(host.Paths.Staged, staged)), staged);
        Assert.False(Directory.Exists(Path.Combine(host.Paths.Staged, "compose", ".run")));
        var log = string.Join("\n", restore.LogLines(0));
        Assert.DoesNotContain("s3cr3t-api-key", log);
        Assert.DoesNotContain("tok-9f8e7d6c5b4a", log);

        // At the next start, before the database is opened: the staged data replaces the fresh install's.
        Assert.NotNull(RestoreBoot.SwapStaged(host.Options));
        Assert.Equal("<key id=\"old-machine\"/>", File.ReadAllText(Path.Combine(host.Options.KeysDir, "key-1.xml")));
        Assert.False(File.Exists(Path.Combine(host.Options.KeysDir, "key-fresh.xml")));
        Assert.True(File.Exists(Path.Combine(host.Paths.Before, "keys", "key-fresh.xml")));
        Assert.True(File.Exists(Path.Combine(host.Paths.Before, "gitberth.db")));
        Assert.Equal("compose data", File.ReadAllText(Path.Combine(host.Options.DataDir, "compose", "site", "data", "notes.txt")));
        Assert.False(Directory.Exists(host.Paths.Staged));
        Assert.Null(RestoreBoot.SwapStaged(host.Options));
        // Its log carries on across the restart.
        Assert.Contains(host.Restore().LogLines(0), l => l.Contains("data from the backup is in place", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_restore_stopped_in_the_middle_goes_on_from_where_it_was()
    {
        using var old = new BackupHost();
        var file = await SaveAsync(old);
        using var host = Fresh();
        var first = host.Restore();
        await first.ReceiveAsync(new MemoryStream(file), default);
        // Gitberth stops while the database server is being made.
        host.Engine.OnCall = call =>
        {
            if (call == "create db") host.App.Stopping.Cancel();
        };
        await first.StartAsync(BackupHost.Passphrase);
        await first.PhaseOne;
        var stopped = first.Status()!;
        Assert.False(stopped.Finished);
        Assert.Equal(RestoreStepState.Done, stopped.Steps.Single(s => s.Id == "volume:web-data").State);
        Assert.Equal(RestoreStepState.Running, stopped.Steps.Single(s => s.Id == "database:db").State);
        Assert.Equal(0, host.App.Stops);
        // Nothing it staged stays meanwhile (the old machine's keys and database, in plain files): it's staged again.
        Assert.False(Directory.Exists(host.Paths.Staged));
        // Gitberth starts again: no restore runs, and the gate is open (phase 1 never finished).
        Assert.False(new RestoreGate(host.Options).Holding);
        var filled = host.Engine.Calls.Count(c => c.StartsWith("put ", StringComparison.Ordinal));
        host.Engine.Calls.Clear();
        host.Engine.OnCall = null;

        var again = new RestoreService(host.Engine, host.Db, host.Options, new RestoreGate(), NullLogger<RestoreService>.Instance, new BackupHost.Lifetime())
        {
            ExitDelay = TimeSpan.Zero, DatabaseReady = TimeSpan.FromSeconds(5), Exit = () => { },
        };
        Assert.Null(again.CantRestore());
        Assert.False(again.Status()!.Active);
        await again.StartAsync(BackupHost.Passphrase);
        await again.PhaseOne;
        var status = again.Status()!;
        Assert.Equal(0, status.Failed);
        Assert.Equal(2, status.Phase);
        // What was done isn't done again: no volume made or filled twice; the database server (made already) gets its dump.
        Assert.Equal(1, filled);
        Assert.DoesNotContain(host.Engine.Calls, c => c.StartsWith("volume ", StringComparison.Ordinal) || c.StartsWith("put ", StringComparison.Ordinal) || c.StartsWith("network ", StringComparison.Ordinal));
        Assert.DoesNotContain("create db", host.Engine.Calls);
        Assert.Contains("exec db psql", host.Engine.Calls);
        Assert.Single(host.Engine.Loaded);
        Assert.Contains("create web", host.Engine.Calls);
        Assert.True(File.Exists(host.Paths.StagedReady));
    }

    [Fact]
    public async Task A_damaged_file_restores_nothing()
    {
        using var old = new BackupHost();
        var file = await SaveAsync(old);
        // One byte changed near the end: the manifest still opens, but the whole file doesn't.
        file[^40] ^= 0x01;
        using var host = Fresh();
        var restore = host.Restore();
        await restore.ReceiveAsync(new MemoryStream(file), default);
        Assert.Equal(UploadProblem.None, (await restore.CheckAsync(BackupHost.Passphrase)).Problem);
        await restore.StartAsync(BackupHost.Passphrase);
        await restore.PhaseOne;

        var status = restore.Status()!;
        Assert.True(status.Finished);
        Assert.Equal(RestoreStepState.Failed, status.Steps.Single(s => s.Id == "check").State);
        Assert.All(status.Steps.Where(s => s.Id != "check"), s => Assert.Equal(RestoreStepState.Waiting, s.State));
        Assert.Empty(host.Engine.Calls);
        Assert.Equal(0, host.App.Stops);
        Assert.False(host.Gate.Holding);
        Assert.False(Directory.Exists(host.Paths.Staged));
        Assert.Contains(restore.LogLines(0), l => l.Contains("nothing was restored", StringComparison.Ordinal));
        // The file stays, to be discarded or tried with another.
        Assert.NotNull(restore.UploadedBytes());
        await restore.DiscardUploadAsync();
        Assert.Null(restore.UploadedBytes());
    }

    [Fact]
    public async Task A_Gitberth_with_apps_takes_picked_items_but_never_the_whole_backup()
    {
        using var host = Fresh();
        await using (var db = host.Db.CreateDbContext())
        {
            db.Apps.Add(new AppDefinition { Name = "site", Slug = "site" });
            await db.SaveChangesAsync();
        }
        var restore = host.Restore();
        Assert.Null(restore.CantRestore());
        Assert.Contains("already has apps", restore.CantRestoreEverything());
        await restore.ReceiveAsync(new MemoryStream("GBAK"u8.ToArray()), default);
        var why = await Assert.ThrowsAsync<InvalidOperationException>(() => restore.StartAsync(BackupHost.Passphrase));
        Assert.Contains("already has apps", why.Message);
        Assert.Equal(UploadProblem.NotABackup, (await restore.CheckAsync(BackupHost.Passphrase)).Problem);
    }

    [Fact]
    public async Task Restoring_everything_after_a_picked_restore_of_the_same_file_restores_everything()
    {
        using var old = new BackupHost();
        var file = await SaveAsync(old);
        using var host = Fresh();
        var restore = host.Restore();
        await restore.ReceiveAsync(new MemoryStream(file), default);
        await restore.StartAsync(BackupHost.Passphrase, [new RestorePick("container:tool", RestoreAction.Restore)]);
        await restore.PhaseOne;
        Assert.True(restore.Status()!.Finished);
        Assert.Contains("create tool", host.Engine.Calls);

        // Not taken for the picked restore going on: a whole restore of its own, from its first step.
        await restore.StartAsync(BackupHost.Passphrase);
        var steps = restore.Status()!.Steps;
        Assert.Contains(steps, s => s.Id == "gitberth");
        Assert.DoesNotContain(steps, s => s.Id.StartsWith("container:tool:", StringComparison.Ordinal));
        await restore.PhaseOne;
        Assert.Equal(RestoreStepState.Done, restore.Status()!.Steps.Single(s => s.Id == "gitberth").State);
        Assert.True(File.Exists(host.Paths.StagedReady));
    }

    [Fact]
    public async Task The_gate_closes_for_a_restore_and_holds_the_domains_sync()
    {
        using var host = Fresh();
        Assert.False(new RestoreGate(host.Options).Holding);
        var plan = new RestorePlanState { PhaseOneDone = true, Steps = [new RestoreStepRecord { Id = "finish", Title = "Finish", Phase = 2 }] };
        plan.Save(host.Paths);
        Assert.True(new RestoreGate(host.Options).Holding);
        plan.Finished = true;
        plan.Save(host.Paths);
        Assert.False(new RestoreGate(host.Options).Holding);

        // While it's closed, a deploy's domains sync and the removals' retry ask nothing of Cloudflare or cloudflared.
        using var rig = new AppsRig();
        var gate = new RestoreGate();
        gate.Close();
        var domains = new DomainService(rig.Db, rig.Settings, new CloudflareApi(new FakeHttp()), new TunnelLocator(rig.Cli), rig.Cli,
            NullLogger<DomainService>.Instance, restoreGate: gate);
        await rig.Settings.SetAsync(SettingKeys.DomainsEnabled, "true");
        Assert.Empty((await domains.SyncAsync()).Notes);
        Assert.Null(await domains.RetryRemovalsAsync());
        Assert.Empty(rig.Engine.Commands);
    }

    [Fact]
    public void The_plan_puts_databases_before_the_containers_that_use_them_and_apps_after_the_restart()
    {
        var manifest = new BackupManifest
        {
            Networks = [new SavedNetwork { Name = "appnet" }],
            Volumes = [new SavedVolume { Name = "db-data" }, new SavedVolume { Name = "web-data", Part = "volume/web-data" }],
            Images = [new SavedImage { Id = "sha256:tool", Tags = ["tool:1"], Part = "image/sha256:tool" }, new SavedImage { Id = "sha256:web", Tags = ["nginx:1.29"] }],
            Databases = [new SavedDatabase("db", "postgres", "postgres", "pg_dumpall -U postgres", "dump/db", ["db-data"])],
            Containers =
            [
                Container("web", "nginx:1.29", ComesBack.Inspect), Container("cache", "redis:8-alpine", ComesBack.Inspect),
                Container("db", "postgres:18", ComesBack.Database), Container("gb-site", "gitberth/site:1d2948c", ComesBack.Deploy),
            ],
            Apps = [new SavedApp(7, "site", "site", "Git", false, true, "main", "1d2948cc693a6d766f2145dc07e52dc2c8f718ae", true)],
        };
        Assert.Equal(
            ["check", "network:appnet", "volume:db-data", "volume:web-data", "image:sha256:tool", "pull:sha256:web", "database:db", "container:cache", "container:web",
                "gitberth", "restart", "deploy:7", "domains", "finish"],
            RestorePlanner.Steps(manifest).Select(s => s.Id));
        Assert.Equal("Deploy site at 1d2948c", RestorePlanner.Steps(manifest).Single(s => s.Id == "deploy:7").Title);
        Assert.All(RestorePlanner.Steps(manifest), s => Assert.Equal(s.Id is "restart" or "deploy:7" or "domains" or "finish" ? 2 : 1, s.Phase));
    }

    private static SavedContainer Container(string name, string image, ComesBack how) => new()
    {
        Name = name, How = how,
        Inspect = new System.Text.Json.Nodes.JsonObject { ["Name"] = "/" + name, ["Config"] = new System.Text.Json.Nodes.JsonObject { ["Image"] = image } },
    };

    [Fact]
    public void A_boot_swap_cut_short_finishes_whats_left_and_never_moves_the_restored_data_aside()
    {
        using var host = Fresh();
        var data = host.Options.DataDir;
        var paths = host.Paths;
        // A crash in the middle of the swap: gitberth.db was moved aside and the restored one moved in; keys are still staged.
        Directory.CreateDirectory(paths.Before);
        File.WriteAllText(Path.Combine(paths.Before, "gitberth.db"), "the fresh install's database");
        File.WriteAllText(Path.Combine(data, "gitberth.db"), "the restored database");
        Directory.CreateDirectory(Path.Combine(paths.Staged, "keys"));
        File.WriteAllText(Path.Combine(paths.Staged, "keys", "key-1.xml"), "<key id=\"old-machine\"/>");
        File.WriteAllText(paths.StagedReady, "now");
        // An earlier restore's leftovers in before/ that this swap has nothing to do with stay.
        File.WriteAllText(Path.Combine(paths.Before, "stats.db"), "an earlier one");

        Assert.NotNull(RestoreBoot.SwapStaged(host.Options));
        Assert.Equal("the restored database", File.ReadAllText(Path.Combine(data, "gitberth.db")));
        Assert.Equal("the fresh install's database", File.ReadAllText(Path.Combine(paths.Before, "gitberth.db")));
        Assert.Equal("an earlier one", File.ReadAllText(Path.Combine(paths.Before, "stats.db")));
        Assert.Equal("<key id=\"old-machine\"/>", File.ReadAllText(Path.Combine(data, "keys", "key-1.xml")));
        Assert.True(File.Exists(Path.Combine(paths.Before, "keys", "key-fresh.xml")));
        Assert.True(Directory.Exists(host.Options.LogsDir));
        Assert.False(Directory.Exists(paths.Staged));
        Assert.Null(RestoreBoot.SwapStaged(host.Options));
    }

    [Fact]
    public async Task What_a_restore_staged_goes_when_its_upload_is_discarded_or_the_restore_ends()
    {
        using var host = Fresh();
        var restore = host.Restore();
        void Stage()
        {
            Directory.CreateDirectory(Path.Combine(host.Paths.Staged, "keys"));
            File.WriteAllText(Path.Combine(host.Paths.Staged, "keys", "key-1.xml"), "<key id=\"old-machine\"/>");
            File.WriteAllText(Path.Combine(host.Paths.Staged, "gitberth.db"), "the old machine's secrets");
        }

        Stage();
        await restore.ReceiveAsync(new MemoryStream("GBAK"u8.ToArray()), default);
        await restore.DiscardUploadAsync();
        Assert.Null(restore.UploadedBytes());
        Assert.False(Directory.Exists(host.Paths.Staged));

        Stage();
        restore.Finish();
        Assert.False(Directory.Exists(host.Paths.Staged));
    }

    [Fact]
    public async Task Nothing_is_ever_written_through_a_link_a_backups_folder_brings()
    {
        var root = Path.Combine(Path.GetTempPath(), "gitberth-extract-" + Guid.NewGuid().ToString("N")[..8]);
        var outside = Path.Combine(Path.GetTempPath(), "gitberth-outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "victim.txt"), "untouched");
        try
        {
            var tar = new MemoryStream();
            await using (var writer = new System.Formats.Tar.TarWriter(tar, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
            {
                System.Formats.Tar.PaxTarEntry File(string name, string text) =>
                    new(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) };
                await writer.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.Directory, "keys/"));
                // A link to a folder outside, then a file under it; a link to a file outside, then a file of its name.
                await writer.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.SymbolicLink, "keys/out") { LinkName = outside });
                await writer.WriteEntryAsync(File("keys/out/planted.txt", "planted"));
                await writer.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.SymbolicLink, "keys/victim") { LinkName = Path.Combine(outside, "victim.txt") });
                await writer.WriteEntryAsync(File("keys/victim", "overwritten"));
                await writer.WriteEntryAsync(File("../escaped.txt", "escaped"));
                await writer.WriteEntryAsync(File("keys/key-1.xml", "<key/>"));
            }
            tar.Position = 0;
            await RestoreService.ExtractAsync(tar, root, default);

            Assert.False(System.IO.File.Exists(Path.Combine(outside, "planted.txt")));
            Assert.Equal("untouched", System.IO.File.ReadAllText(Path.Combine(outside, "victim.txt")));
            Assert.False(System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!, "escaped.txt")));
            Assert.Equal("<key/>", System.IO.File.ReadAllText(Path.Combine(root, "keys", "key-1.xml")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}

internal static class BackupTestPaths
{
    public static string EnsureDirectory(this string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
