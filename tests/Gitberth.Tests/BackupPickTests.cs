using System.Formats.Tar;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Gitberth.Apps;
using Gitberth.Backup;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>
/// Restoring the apps and containers picked from a backup into a Gitberth that has apps: an app read from a file the save
/// made, its secrets encrypted again for this Gitberth; brought back as it was, in place of the one here (its database
/// restored aside first, then swapped in, the old one kept under a _before_ name), or as a copy with databases of its own;
/// each target's conflicts; and the picks checked before anything runs, so nothing here is written over.
/// </summary>
public sealed class BackupPickTests
{
    private const string Secret = "s3cr3t-api-key";
    private const string Token = "tok-9f8e7d6c5b4a";
    private const string Commit = "1d2948cc693a6d766f2145dc07e52dc2c8f718ae";
    private const string Earlier = "0f3e9b7a00000000000000000000000000000000";
    private static readonly JsonSerializerOptions SnapshotJson = new() { Converters = { new JsonStringEnumConverter() } };

    // The host the backup is made on: a Postgres server (db), and the Git app site with its container, a volume of files,
    // a secret, a host port, a domain, a deploy hook, a database on db and two deploys. Returns the file and site's item.
    private static async Task<(byte[] File, string Item)> SaveAsync(BackupHost old)
    {
        var engine = old.Engine;
        Server(engine);
        engine.AddContainer("gb-site", "gitberth/site:1d2948c", "sha256:site", volumes: ["gb-site-data:/app/data"],
            labels: new Dictionary<string, string> { ["gitberth.app"] = "site" }, hostPort: 8081);
        engine.AddVolume("gb-site-data", "marker-5be1 the site's files");
        var protector = old.Protector;
        int id;
        await using (var db = old.Db.CreateDbContext())
        {
            var site = new AppDefinition
            {
                Name = "site", Slug = "site", Owner = "acme", Repo = "site", Branch = "main", CurrentCommit = Commit,
                Ports = [new PortMapping { ContainerPort = 80, HostPort = 8081, IsWeb = true }],
                EnvVars = [new EnvVar { Key = "API_KEY", ProtectedValue = protector.Protect(Secret), IsSecret = true }],
                Volumes = [new VolumeMount { Source = "data", Target = "/app/data" }],
                Domains = [new CustomDomain { Hostname = "site.example.com" }],
            };
            db.Apps.Add(site);
            await db.SaveChangesAsync();
            id = site.Id;
            db.DeployHooks.Add(new DeployHook { AppId = id, Name = "ci", TokenHash = "9a1f", ProtectedToken = protector.Protect(Token) });
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                AppId = id, Kind = DatabaseKind.Postgres, Server = "db", Name = "site", Role = "site", ProtectedPassword = protector.Protect(Secret), Variable = "DATABASE_URL",
            });
            db.Deployments.Add(new Deployment { AppId = id, Trigger = DeployTrigger.Created, Status = DeployStatus.Succeeded, CommitSha = Earlier });
            db.Deployments.Add(new Deployment { AppId = id, Trigger = DeployTrigger.Webhook, Status = DeployStatus.Succeeded, CommitSha = Commit });
            await db.SaveChangesAsync();
        }
        return (await FileAsync(old), $"app:{id}");
    }

    private static void Server(BackupTestEngine engine)
    {
        engine.AddImage("sha256:pg", "postgres:18", "postgres@sha256:" + new string('a', 64));
        engine.Registry["postgres:18"] = "sha256:" + new string('a', 64);
        engine.AddContainer("db", "postgres:18", "sha256:pg", env: ["POSTGRES_PASSWORD=" + Secret], volumes: ["db-data:/var/lib/postgresql"]);
    }

    private static async Task<byte[]> FileAsync(BackupHost old)
    {
        var service = old.Backup();
        var items = await service.ItemsAsync();
        var ticket = await service.StartSaveAsync(new BackupChoice(items.Select(i => i.Id).ToHashSet(), false), BackupHost.Passphrase);
        var file = new MemoryStream();
        await service.WriteAsync(service.Claim(ticket.Id)!, file, default);
        return file.ToArray();
    }

    // The Gitberth it's restored into: an app of its own (blog, so IDs differ), the same Postgres server running, its image.
    private static async Task<BackupHost> HereAsync(Func<GitberthDb, BackupHost, Task>? more = null)
    {
        var host = new BackupHost();
        host.Engine.AddImage("sha256:pg", "postgres:18", "postgres@sha256:" + new string('a', 64));
        host.Engine.AddContainer("db", "postgres:18", "sha256:pg", env: ["POSTGRES_PASSWORD=" + Secret], volumes: ["db-data:/var/lib/postgresql"]);
        await using var db = host.Db.CreateDbContext();
        db.Apps.Add(new AppDefinition { Name = "blog", Slug = "blog" });
        await db.SaveChangesAsync();
        if (more is not null) await more(db, host);
        return host;
    }

    // site is here: running on its volume (other files now), its own database on db, other settings. Returns its ID.
    private static async Task<int> SiteHereAsync(GitberthDb db, BackupHost here)
    {
        here.Engine.AddContainer("gb-site", "gitberth/site:9c41d07", "sha256:site", volumes: ["gb-site-data:/app/data"],
            labels: new Dictionary<string, string> { ["gitberth.app"] = "site" }, hostPort: 8081);
        here.Engine.AddVolume("gb-site-data", "the files it has now");
        here.Engine.PgNames.AddRange(["d:site", "r:site"]);
        var site = new AppDefinition
        {
            Name = "site", Slug = "site", Owner = "acme", Repo = "site", Branch = "main", CurrentCommit = "9c41d07" + new string('0', 33),
            Ports = [new PortMapping { ContainerPort = 80, HostPort = 8081, IsWeb = true }],
            EnvVars = [new EnvVar { Key = "OLD_KEY", ProtectedValue = here.Protector.Protect("old") }],
        };
        db.Apps.Add(site);
        await db.SaveChangesAsync();
        db.ManagedDatabases.Add(new ManagedDatabase { AppId = site.Id, Kind = DatabaseKind.Postgres, Server = "db", Name = "site", Role = "site", Variable = "DATABASE_URL" });
        await db.SaveChangesAsync();
        return site.Id;
    }

    private static async Task<(RestoreStatus Status, string Log)> RunAsync(RestoreService restore, int failed, params RestorePick[] picks)
    {
        await restore.StartAsync(BackupHost.Passphrase, picks);
        await restore.PhaseOne;
        var log = string.Join("\n", restore.LogLines(0));
        var status = restore.Status()!;
        Assert.True(status.Finished && status.Failed == failed, log);
        Assert.DoesNotContain(Secret, log);
        Assert.DoesNotContain(Token, log);
        return (status, log);
    }

    private static string[] Stages(RestoreStatus status, string item) => [.. status.Steps.Select(s => s.Id[(item.Length + 1)..])];

    private static void NoHelperLeft(BackupHost host)
    {
        Assert.DoesNotContain(host.Engine.Containers.Keys, n => n.StartsWith("gitberth-helper", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Engine.Volumes.Keys, n => n.StartsWith("gitberth-helper", StringComparison.Ordinal));
        var read = Path.Combine(host.Paths.RestoreDir, "read");
        Assert.False(Directory.Exists(read) && Directory.EnumerateFileSystemEntries(read).Any(), "the saved data read for its apps stayed");
    }

    // The database restored aside: its name, from the script that made it.
    private static string Aside(BackupHost host, string name)
    {
        var made = Assert.Single(host.Engine.Scripts, s => s.Contains("CREATE DATABASE", StringComparison.Ordinal));
        var temp = Regex.Match(made, $"CREATE DATABASE \"({name}_restoring_[0-9a-f]{{6}})\"").Groups[1].Value;
        Assert.NotEmpty(temp);
        return temp;
    }

    [Fact]
    public async Task A_deleted_app_comes_back_as_it_was_with_its_files_secrets_database_and_deploys()
    {
        using var old = new BackupHost(migrated: true);
        var (file, item) = await SaveAsync(old);
        using var host = await HereAsync();
        var restore = host.Restore();
        await restore.ReceiveAsync(new MemoryStream(file), default);

        // Opened: the whole backup isn't offered here; site isn't here, and its copy would be site2; db is.
        var check = await restore.CheckAsync(BackupHost.Passphrase);
        Assert.Equal(UploadProblem.None, check.Problem);
        Assert.False(check.Everything);
        Assert.Equal(new RestoreTarget(item, false, "site2", null), check.Targets!.Single(t => t.ItemId == item));
        Assert.Equal(new RestoreTarget("container:db", true, "db2", null), check.Targets!.Single(t => t.ItemId == "container:db"));
        Assert.Empty(host.Engine.Calls);
        NoHelperLeft(host);

        var (status, _) = await RunAsync(restore, 0, new RestorePick(item, RestoreAction.Restore));
        Assert.Equal(["images", "database", "volumes", "app", "deploy", "domains"], Stages(status, item));
        Assert.All(status.Steps, s => Assert.StartsWith("site: ", s.Title, StringComparison.Ordinal));
        Assert.Equal(1, status.Phase);
        Assert.Equal(0, host.App.Stops);
        Assert.False(host.Gate.Holding || host.Gate.Holds(new AppDefinition { Slug = "site" }));
        Assert.NotNull(restore.UploadedBytes());

        // Its rows, under a new ID: its settings, its secret and hook token readable here alone, its database, its deploys.
        await using var db = host.Db.CreateDbContext();
        var site = await db.Apps.Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Domains).Include(a => a.Volumes).SingleAsync(a => a.Slug == "site");
        Assert.NotEqual(int.Parse(item["app:".Length..]), site.Id);
        Assert.Equal(("site", "acme/site", "main", Commit), (site.Name, $"{site.Owner}/{site.Repo}", site.Branch, site.CurrentCommit));
        Assert.Null(site.Container);
        Assert.Equal(8081, Assert.Single(site.Ports).HostPort);
        Assert.Equal("site.example.com", Assert.Single(site.Domains).Hostname);
        Assert.Equal(("data", "/app/data"), (Assert.Single(site.Volumes).Source, site.Volumes[0].Target));
        var secret = Assert.Single(site.EnvVars);
        Assert.Equal(Secret, host.Protector.TryUnprotect(secret.ProtectedValue));
        Assert.Equal("", old.Protector.TryUnprotect(secret.ProtectedValue));
        Assert.Equal(Token, host.Protector.TryUnprotect((await db.DeployHooks.SingleAsync(h => h.AppId == site.Id)).ProtectedToken));
        Assert.Equal([Earlier, Commit], await db.Deployments.Where(d => d.AppId == site.Id).OrderBy(d => d.Id).Select(d => d.CommitSha).ToListAsync());
        var database = await db.ManagedDatabases.SingleAsync(d => d.AppId == site.Id);
        Assert.Equal(("db", "site", "site", "DATABASE_URL"), (database.Server, database.Name, database.Role, database.Variable));
        Assert.Equal(Secret, host.Protector.TryUnprotect(database.ProtectedPassword));

        // Its files in a new volume; its database made with its role, restored aside from db's dump by a throwaway server,
        // then given its name.
        Assert.Equal(old.Engine.Files["gb-site-data"], host.Engine.Files["gb-site-data"]);
        var temp = Aside(host, "site");
        var made = host.Engine.Scripts.Single(s => s.Contains("CREATE DATABASE", StringComparison.Ordinal));
        Assert.StartsWith("db: CREATE ROLE \"site\" LOGIN PASSWORD 'SCRAM-SHA-256$", made, StringComparison.Ordinal);
        Assert.Contains($"CREATE DATABASE \"{temp}\" OWNER \"site\";", made, StringComparison.Ordinal);
        var swap = Assert.Single(host.Engine.Scripts, s => s.Contains("BEGIN;", StringComparison.Ordinal));
        Assert.Contains($"ALTER DATABASE \"{temp}\" RENAME TO \"site\";", swap, StringComparison.Ordinal);
        Assert.DoesNotContain("_before_", string.Concat(host.Engine.Scripts), StringComparison.Ordinal);
        Assert.Equal(old.Engine.Dump, Assert.Single(host.Engine.Loaded));
        Assert.Equal(KeyValuePair.Create($"db/{temp}", "PGDMP site --role=site"), Assert.Single(host.Engine.Restored));
        var helper = host.Engine.Calls.Single(c => c.StartsWith("create gitberth-helper-restore-db-", StringComparison.Ordinal))["create ".Length..];
        Assert.Contains($"exec {helper} pg_dump", host.Engine.Calls);
        Assert.Contains($"rm {helper}", host.Engine.Calls);
        Assert.Contains($"volume-rm {helper}-data", host.Engine.Calls);
        NoHelperLeft(host);
        // Nothing else was touched: no container stopped or removed but the helpers.
        Assert.DoesNotContain(host.Engine.Calls, c => c.StartsWith("stop ", StringComparison.Ordinal) || c.StartsWith("rm ", StringComparison.Ordinal) && !c.StartsWith("rm gitberth-helper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replacing_an_app_swaps_its_database_in_one_go_keeping_the_old_one_and_refills_its_volume()
    {
        using var old = new BackupHost(migrated: true);
        var (file, item) = await SaveAsync(old);
        var currentId = 0;
        using var host = await HereAsync(async (db, here) => currentId = await SiteHereAsync(db, here));
        var restore = host.Restore();
        await restore.ReceiveAsync(new MemoryStream(file), default);
        // It's here, on its own port and domain: nothing in the way.
        Assert.Equal(new RestoreTarget(item, true, "site2", null), (await restore.CheckAsync(BackupHost.Passphrase)).Targets!.Single(t => t.ItemId == item));

        var (status, log) = await RunAsync(restore, 0, new RestorePick(item, RestoreAction.Replace));
        Assert.Equal(["stop", "images", "database", "volumes", "app", "deploy", "domains"], Stages(status, item));

        // Stopped first; its volume emptied, then filled from the file.
        var calls = host.Engine.Calls;
        Assert.Contains("stop gb-site", calls);
        var emptied = calls.FindIndex(c => c.StartsWith("wait gitberth-helper-restore-", StringComparison.Ordinal));
        Assert.True(emptied > calls.IndexOf("stop gb-site") && emptied < calls.FindIndex(c => c.StartsWith("put ", StringComparison.Ordinal) && c.EndsWith("(gb-site-data)", StringComparison.Ordinal)));
        Assert.Equal(old.Engine.Files["gb-site-data"], host.Engine.Files["gb-site-data"]);
        Assert.DoesNotContain("start gb-site", calls);

        // Restored aside (its role is there: not touched yet), then in one transaction: the database here kept under its
        // _before_ name, the restored one under its name, its role's password the saved one. The log says what was kept.
        var temp = Aside(host, "site");
        Assert.Equal($"db: CREATE DATABASE \"{temp}\" OWNER \"site\";\nREVOKE CONNECT, TEMPORARY ON DATABASE \"{temp}\" FROM PUBLIC;\n",
            host.Engine.Scripts.Single(s => s.Contains("CREATE DATABASE", StringComparison.Ordinal)));
        var swap = Assert.Single(host.Engine.Scripts, s => s.Contains("BEGIN;", StringComparison.Ordinal));
        var before = Regex.Match(swap, "BEGIN;\nALTER DATABASE \"site\" RENAME TO \"(site_before_[0-9]{12})\";\n"
            + $"ALTER DATABASE \"{temp}\" RENAME TO \"site\";\nALTER ROLE \"site\" LOGIN PASSWORD 'SCRAM-SHA-256\\$[^']+';\nCOMMIT;\n$").Groups[1].Value;
        Assert.NotEmpty(before);
        Assert.Contains($"site: the database site here is kept as {before}.", log, StringComparison.Ordinal);
        Assert.Equal("PGDMP site --role=site", host.Engine.Restored[$"db/{temp}"]);

        // Its rows: the same ID, the saved settings in place of its own, the saved deploys added to its history.
        await using var db = host.Db.CreateDbContext();
        var site = await db.Apps.Include(a => a.EnvVars).Include(a => a.Domains).SingleAsync(a => a.Slug == "site");
        Assert.Equal(currentId, site.Id);
        Assert.Equal(Commit, site.CurrentCommit);
        Assert.Equal("API_KEY", Assert.Single(site.EnvVars).Key);
        Assert.Equal("site.example.com", Assert.Single(site.Domains).Hostname);
        var database = await db.ManagedDatabases.SingleAsync(d => d.AppId == currentId);
        Assert.Equal(Secret, host.Protector.TryUnprotect(database.ProtectedPassword));
        Assert.Contains(Commit, await db.Deployments.Where(d => d.AppId == currentId).Select(d => d.CommitSha).ToListAsync());
        Assert.Equal(2, await db.Apps.CountAsync());
        NoHelperLeft(host);
    }

    [Fact]
    public async Task A_replace_whose_restore_fails_leaves_the_app_as_it_was_and_running()
    {
        using var old = new BackupHost(migrated: true);
        var (file, item) = await SaveAsync(old);
        var currentId = 0;
        using var host = await HereAsync(async (db, here) => currentId = await SiteHereAsync(db, here));
        var restore = host.Restore();
        await restore.ReceiveAsync(new MemoryStream(file), default);
        var files = host.Engine.Files["gb-site-data"];
        // pg_restore refuses, as it does for an extension the role may not create.
        host.Engine.Failing["exec db pg_restore"] = "pg_restore: error: could not execute query: ERROR:  permission denied to create extension";

        var (status, log) = await RunAsync(restore, 1, new RestorePick(item, RestoreAction.Replace));
        Assert.Equal(RestoreStepState.Failed, status.Steps.Single(s => s.Id == $"{item}:database").State);
        Assert.All(status.Steps.Where(s => s.Id.EndsWith(":volumes", StringComparison.Ordinal) || s.Id.EndsWith(":app", StringComparison.Ordinal)
            || s.Id.EndsWith(":deploy", StringComparison.Ordinal) || s.Id.EndsWith(":domains", StringComparison.Ordinal)), s => Assert.Equal(RestoreStepState.Skipped, s.State));

        // Its database here wasn't touched (no rename, no password changed); the one restored aside went; its files are as
        // they were, and it runs again.
        var temp = Aside(host, "site");
        Assert.DoesNotContain(host.Engine.Scripts, s => s.Contains("RENAME", StringComparison.Ordinal) || s.Contains("ALTER ROLE", StringComparison.Ordinal));
        Assert.Contains(host.Engine.Scripts, s => s.Contains($"DROP DATABASE IF EXISTS \"{temp}\";", StringComparison.Ordinal) && !s.Contains("DROP ROLE", StringComparison.Ordinal));
        Assert.Same(files, host.Engine.Files["gb-site-data"]);
        Assert.True(host.Engine.Calls.IndexOf("start gb-site") > host.Engine.Calls.IndexOf("stop gb-site"));
        Assert.Contains("site: gb-site is started again, as it was.", log, StringComparison.Ordinal);
        await using var db = host.Db.CreateDbContext();
        var site = await db.Apps.Include(a => a.EnvVars).SingleAsync(a => a.Id == currentId);
        Assert.Equal("OLD_KEY", Assert.Single(site.EnvVars).Key);
        Assert.Equal("site", (await db.ManagedDatabases.SingleAsync(d => d.AppId == currentId)).Name);
        NoHelperLeft(host);
    }

    [Fact]
    public async Task A_copy_gets_its_own_name_volume_database_and_port_and_leaves_the_original_alone()
    {
        using var old = new BackupHost(migrated: true);
        var (file, item) = await SaveAsync(old);
        byte[]? originalFiles = null;
        using var host = await HereAsync(async (db, here) =>
        {
            // The original is here: running on its port and volume, its database on db, its domain.
            here.Engine.AddContainer("gb-site", "gitberth/site:1d2948c", "sha256:site", volumes: ["gb-site-data:/app/data"],
                labels: new Dictionary<string, string> { ["gitberth.app"] = "site" }, hostPort: 8081);
            here.Engine.AddVolume("gb-site-data", "the original's files");
            originalFiles = here.Engine.Files["gb-site-data"];
            here.Engine.PgNames.AddRange(["d:site", "r:site"]);
            db.Apps.Add(new AppDefinition
            {
                Name = "site", Slug = "site", Ports = [new PortMapping { ContainerPort = 80, HostPort = 8081, IsWeb = true }],
                Domains = [new CustomDomain { Hostname = "site.example.com" }],
            });
            await db.SaveChangesAsync();
        });
        var restore = host.Restore();
        await restore.ReceiveAsync(new MemoryStream(file), default);
        var target = (await restore.CheckAsync(BackupHost.Passphrase)).Targets!.Single(t => t.ItemId == item);
        Assert.Equal(new RestoreTarget(item, true, "site2", null), target);

        var (status, log) = await RunAsync(restore, 0, new RestorePick(item, RestoreAction.Copy, target.CopyName));
        // No hooks, no domains: nothing of the original's is stopped, and no domains step.
        Assert.Equal(["images", "database", "volumes", "app", "deploy"], Stages(status, item));
        Assert.All(status.Steps, s => Assert.StartsWith("site2: ", s.Title, StringComparison.Ordinal));

        await using var db = host.Db.CreateDbContext();
        var copy = await db.Apps.Include(a => a.Ports).Include(a => a.Domains).Include(a => a.EnvVars).Include(a => a.Volumes).SingleAsync(a => a.Slug == "site2");
        Assert.Equal(("site2", "gb-site2", Commit), (copy.Name, copy.ContainerName, copy.CurrentCommit));
        Assert.Equal(8082, Assert.Single(copy.Ports).HostPort);
        Assert.Contains("port 8081 was taken, so it's 8082", log, StringComparison.Ordinal);
        Assert.Empty(copy.Domains);
        Assert.Equal(PreviewMode.Off, copy.PreviewMode);
        Assert.Equal("data", Assert.Single(copy.Volumes).Source);
        Assert.False(await db.DeployHooks.AnyAsync(h => h.AppId == copy.Id));
        Assert.Equal(Secret, host.Protector.TryUnprotect(Assert.Single(copy.EnvVars).ProtectedValue));
        Assert.Equal([Earlier, Commit], await db.Deployments.Where(d => d.AppId == copy.Id).OrderBy(d => d.Id).Select(d => d.CommitSha).ToListAsync());

        // A database and role of its own, named after it, with a new password, holding the saved database's data.
        var database = await db.ManagedDatabases.SingleAsync(d => d.AppId == copy.Id);
        Assert.Equal(("db", "site2", "site2", "DATABASE_URL"), (database.Server, database.Name, database.Role, database.Variable));
        Assert.NotEqual(Secret, host.Protector.TryUnprotect(database.ProtectedPassword));
        var temp = Aside(host, "site2");
        Assert.StartsWith("db: CREATE ROLE \"site2\" LOGIN", host.Engine.Scripts.Single(s => s.Contains("CREATE DATABASE", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains($"ALTER DATABASE \"{temp}\" RENAME TO \"site2\";", host.Engine.Scripts.Single(s => s.Contains("BEGIN;", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal(KeyValuePair.Create($"db/{temp}", "PGDMP site --role=site2"), Assert.Single(host.Engine.Restored));
        Assert.DoesNotContain("_before_", string.Concat(host.Engine.Scripts), StringComparison.Ordinal);

        // Its files in a volume of its own; the original's volume, container and rows as they were.
        Assert.Equal(old.Engine.Files["gb-site-data"], host.Engine.Files["gb-site2-data"]);
        Assert.Same(originalFiles, host.Engine.Files["gb-site-data"]);
        Assert.DoesNotContain(host.Engine.Calls, c => c.Contains("gb-site ", StringComparison.Ordinal) || c.EndsWith(" gb-site", StringComparison.Ordinal) || c.EndsWith("(gb-site-data)", StringComparison.Ordinal));
        var original = await db.Apps.Include(a => a.Ports).Include(a => a.Domains).SingleAsync(a => a.Slug == "site");
        Assert.Equal(8081, Assert.Single(original.Ports).HostPort);
        Assert.Single(original.Domains);
        NoHelperLeft(host);
    }

    [Fact]
    public async Task A_copy_of_an_image_app_takes_its_own_database_url_and_label_not_the_originals()
    {
        // An image app (shop) whose container has its DATABASE_URL in its environment, as Gitberth puts it there, and its
        // saved settings the same.
        using var old = new BackupHost(migrated: true);
        Server(old.Engine);
        old.Engine.AddImage("sha256:shop", "nginx:1.29", "nginx@sha256:" + new string('b', 64));
        old.Engine.Registry["nginx:1.29"] = "sha256:" + new string('b', 64);
        var url = $"postgres://shop:{Secret}@db:5432/shop";
        old.Engine.AddContainer("gb-shop", "nginx:1.29", "sha256:shop", env: ["DATABASE_URL=" + url, "MODE=live"],
            labels: new Dictionary<string, string> { ["gitberth.app"] = "shop" }, hostPort: 8085);
        var snapshot = new AppSnapshot(new ContainerSettings
        {
            Image = "nginx:1.29", Env = [new EnvSetting("DATABASE_URL", url, Secret: true), new EnvSetting("MODE", "live")],
            Labels = new Dictionary<string, string> { ["gitberth.app"] = "shop" },
        }, []);
        int id;
        await using (var db = old.Db.CreateDbContext())
        {
            var shop = new AppDefinition
            {
                Name = "shop", Slug = "shop", Source = AppSource.Registry, ImageRef = "nginx:1.29",
                SpecProtected = old.Protector.Protect(JsonSerializer.Serialize(snapshot, SnapshotJson)),
            };
            db.Apps.Add(shop);
            await db.SaveChangesAsync();
            id = shop.Id;
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                AppId = id, Kind = DatabaseKind.Postgres, Server = "db", Name = "shop", Role = "shop", ProtectedPassword = old.Protector.Protect(Secret), Variable = "DATABASE_URL",
            });
            await db.SaveChangesAsync();
        }
        var file = await FileAsync(old);
        using var host = await HereAsync();
        host.Engine.AddImage("sha256:shop", "nginx:1.29", "nginx@sha256:" + new string('b', 64));
        const string copyUrl = "postgres://shop2:copy-pw@db:5432/shop2";
        var restore = host.Restore(values: (_, _) => Task.FromResult<IReadOnlyList<ProvidedVariable>>([new ProvidedVariable("DATABASE_URL", copyUrl, "the copy's")]));
        await restore.ReceiveAsync(new MemoryStream(file), default);
        await RunAsync(restore, 0, new RestorePick($"app:{id}", RestoreAction.Copy, "shop2"));

        // Its container: its own database's URL, the rest as saved, and its own slug on gitberth.app.
        var made = host.Engine.Containers["gb-shop2"];
        var env = made["Config"]!["Env"]!.AsArray().Select(e => (string)e!).ToList();
        Assert.Contains("DATABASE_URL=" + copyUrl, env);
        Assert.Contains("MODE=live", env);
        Assert.DoesNotContain(env, e => e.Contains("shop:" + Secret, StringComparison.Ordinal));
        Assert.Equal("shop2", (string?)made["Config"]!["Labels"]!["gitberth.app"]);
        // Its saved settings the same, so it's made so again later.
        await using var db2 = host.Db.CreateDbContext();
        var copy = await db2.Apps.SingleAsync(a => a.Slug == "shop2");
        var saved = JsonSerializer.Deserialize<AppSnapshot>(host.Protector.TryUnprotect(copy.SpecProtected!), SnapshotJson)!;
        Assert.Equal(copyUrl, saved.Settings.Env.Single(e => e.Key == "DATABASE_URL").Value);
        Assert.Equal("shop2", saved.Settings.Labels["gitberth.app"]);
    }

    // A backup's manifest and apps, as a file holds them: the app site (its container with port 8081, its volume, its
    // domain) and the container web (port 8090, its volume); with server, the Postgres server db (its dump, its data volume)
    // and site's database on it.
    private static (BackupManifest Manifest, Dictionary<int, SavedAppRows> Saved) Backup(bool server = false)
    {
        var inspects = new BackupTestEngine();
        var manifest = new BackupManifest
        {
            Items =
            [
                new BackupItem("app:7", BackupItemKind.App, "site", null, null, false, true, null),
                new BackupItem("volume:gb-site-data", BackupItemKind.Volume, "gb-site-data", null, null, false, true, "app:7"),
                new BackupItem("container:web", BackupItemKind.Container, "web", null, null, false, true, null),
                new BackupItem("volume:web-data", BackupItemKind.Volume, "web-data", null, null, false, true, "container:web"),
            ],
            Containers =
            [
                new SavedContainer { Name = "gb-site", How = ComesBack.Deploy, Item = "app:7", Running = true, Inspect = inspects.AddContainer("gb-site", "gitberth/site:1d2948c", "sha256:site", hostPort: 8081) },
                new SavedContainer { Name = "web", How = ComesBack.Inspect, Item = "container:web", Running = true, Inspect = inspects.AddContainer("web", "nginx:1.29", "sha256:web", hostPort: 8090) },
            ],
            Volumes =
            [
                new SavedVolume { Name = "gb-site-data", Users = ["gb-site"], Part = "volume/gb-site-data" },
                new SavedVolume { Name = "web-data", Users = ["web"], Part = "volume/web-data" },
            ],
        };
        var site = new AppDefinition
        {
            Id = 7, Name = "site", Slug = "site", Ports = [new PortMapping { ContainerPort = 80, HostPort = 8081 }], Domains = [new CustomDomain { Hostname = "site.example.com" }],
        };
        var rows = new SavedAppRows { App = site };
        if (server)
        {
            manifest.Items.Add(new BackupItem("container:db", BackupItemKind.Container, "db", null, null, false, true, null));
            manifest.Containers.Add(new SavedContainer
            {
                Name = "db", How = ComesBack.Inspect, Item = "container:db", Running = true, Inspect = inspects.AddContainer("db", "postgres:18", "sha256:pg", volumes: ["db-data:/var/lib/postgresql"]),
            });
            manifest.Volumes.Add(new SavedVolume { Name = "db-data", Users = ["db"] });
            manifest.Databases.Add(new SavedDatabase("db", "postgres", "postgres", "pg_dumpall -U postgres", "dump/db", ["db-data"]));
            rows.Databases.Add(new ManagedDatabase { Kind = DatabaseKind.Postgres, Server = "db", Name = "site", Role = "site", Variable = "DATABASE_URL" });
        }
        return (manifest, new Dictionary<int, SavedAppRows> { [7] = rows });
    }

    private static async Task<string> RefusedAsync(RestoreService restore, BackupManifest manifest, Dictionary<int, SavedAppRows> saved, params RestorePick[] picks) =>
        (await Assert.ThrowsAsync<InvalidOperationException>(() => restore.PlanAsync(manifest, saved, picks, default))).Message;

    [Fact]
    public async Task Each_target_says_if_its_here_its_copys_name_and_the_first_conflict()
    {
        using var host = new BackupHost();
        var restore = host.Restore();
        var (manifest, saved) = Backup();
        async Task<RestoreTarget> Target(string id) => (await restore.TargetsAsync(manifest, saved, default)).Single(t => t.ItemId == id);

        Assert.Equal(["app:7", "container:web"], (await restore.TargetsAsync(manifest, saved, default)).Select(t => t.ItemId));
        Assert.Equal(new RestoreTarget("app:7", false, "site2", null), await Target("app:7"));
        Assert.Equal(new RestoreTarget("container:web", false, "web2", null), await Target("container:web"));

        // A volume of its name is here (nothing uses it): it's kept as it is.
        host.Engine.AddVolume("gb-site-data", "left behind");
        Assert.Equal("The volume gb-site-data is here already: it's kept as it is.", (await Target("app:7")).Note);

        // Another app has its domain: left off (said before the volume).
        await using (var db = host.Db.CreateDbContext())
        {
            db.Apps.Add(new AppDefinition { Name = "blog", Slug = "blog", Domains = [new CustomDomain { Hostname = "site.example.com" }] });
            await db.SaveChangesAsync();
        }
        Assert.Equal("site.example.com is another app's here: it's left off.", (await Target("app:7")).Note);

        // A container has its port: it gets a free one (said first).
        host.Engine.AddContainer("proxy", "nginx:1.29", "sha256:web", hostPort: 8081);
        Assert.Equal("Port 8081 is taken here: it gets a free one.", (await Target("app:7")).Note);

        // A container has its name, and no app: it's here, and it can't be restored as itself.
        host.Engine.AddContainer("gb-site", "nginx:1.29", "sha256:web");
        var orphan = await Target("app:7");
        Assert.True(orphan.Here);
        Assert.StartsWith("A container called gb-site is here, and no app has it", orphan.Note, StringComparison.Ordinal);
        Assert.Equal("A container called gb-site is here: restore site as a copy, or remove that container first.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("app:7", RestoreAction.Restore)));

        // web is here on its port; its copy's name skips one taken.
        host.Engine.AddContainer("web", "nginx:1.29", "sha256:web", hostPort: 8090);
        host.Engine.AddContainer("web2", "nginx:1.29", "sha256:web");
        Assert.Equal(new RestoreTarget("container:web", true, "web3", null), await Target("container:web"));
    }

    [Fact]
    public async Task Picks_are_checked_before_anything_runs()
    {
        using var host = new BackupHost();
        var restore = host.Restore();
        var (manifest, saved) = Backup();
        await using (var db = host.Db.CreateDbContext())
        {
            db.Apps.Add(new AppDefinition { Name = "site", Slug = "site" });
            await db.SaveChangesAsync();
        }
        Task<string> Refused(params RestorePick[] picks) => RefusedAsync(restore, manifest, saved, picks);

        Assert.Equal("Pick at least one app or container to restore.", await Refused());
        Assert.Equal("volume:gb-site-data isn't an app or container of this backup.", await Refused(new RestorePick("volume:gb-site-data", RestoreAction.Restore)));
        Assert.Equal("site is here: replace it, or restore it as a copy.", await Refused(new RestorePick("app:7", RestoreAction.Restore)));
        Assert.Equal("web isn't here to replace: restore it as it was.", await Refused(new RestorePick("container:web", RestoreAction.Replace)));
        Assert.Equal("site is picked twice.", await Refused(new RestorePick("app:7", RestoreAction.Replace), new RestorePick("app:7", RestoreAction.Copy, "site3")));
        Assert.Equal("Site is taken here: give the copy of site another name.", await Refused(new RestorePick("app:7", RestoreAction.Copy, "Site")));
        Assert.StartsWith("The copy of site: ", await Refused(new RestorePick("app:7", RestoreAction.Copy, " ")), StringComparison.Ordinal);
        Assert.Equal("site3 is taken here: give the copy of web another name.",
            await Refused(new RestorePick("app:7", RestoreAction.Copy, "site3"), new RestorePick("container:web", RestoreAction.Copy, "site3")));
        Assert.StartsWith("The copy of web: ", await Refused(new RestorePick("container:web", RestoreAction.Copy, "web/2")), StringComparison.Ordinal);

        // What passes: each pick with what it's called here, its containers and volumes renamed for a copy.
        var plans = await restore.PlanAsync(manifest, saved, [new RestorePick("app:7", RestoreAction.Copy, "Site 3"), new RestorePick("container:web", RestoreAction.Copy, "web2")], default);
        Assert.Equal([("Site 3", "site-3"), ("web2", "web2")], plans.Select(p => (p.Name, p.Slug)));
        Assert.Equal(["gb-site-3", "gb-site-3-data"], new[] { plans[0].Rename("gb-site"), plans[0].Rename("gb-site-data") });
        Assert.Equal(["web2", "web2-data"], new[] { plans[1].Rename("web"), plans[1].Rename("web-data") });
        var replace = Assert.Single(await restore.PlanAsync(manifest, saved, [new RestorePick("app:7", RestoreAction.Replace)], default));
        Assert.Equal(("gb-site", "gb-site-data"), (replace.Rename("gb-site"), replace.Rename("gb-site-data")));
        Assert.Empty(host.Engine.Calls);

        // Without a file there's nothing to restore from.
        Assert.Equal("No file was uploaded.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => restore.StartAsync(BackupHost.Passphrase, [new RestorePick("app:7", RestoreAction.Replace)]))).Message);
    }

    [Fact]
    public async Task A_database_servers_dump_never_goes_over_data_thats_here()
    {
        using var host = new BackupHost();
        var restore = host.Restore();
        var (manifest, saved) = Backup(server: true);

        // Its data volume is here (its container gone): restoring it as it was would load the whole dump over that data.
        host.Engine.AddVolume("db-data", "the server's files now");
        Assert.Equal("db's data volume db-data is here: restoring it would load its whole dump over that data. Restore it as a copy instead.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("container:db", RestoreAction.Restore)));
        // Its target says so, and offers it as here, so a copy can be picked.
        Assert.Equal(new RestoreTarget("container:db", true, "db2", "Its data volume db-data is here: it can only come back as a copy."),
            (await restore.TargetsAsync(manifest, saved, default)).Single(t => t.ItemId == "container:db"));
        // It's here: replacing it would load the dump over every database it has.
        host.Engine.AddContainer("db", "postgres:18", "sha256:pg", volumes: ["db-data:/var/lib/postgresql"]);
        Assert.StartsWith("db is a database server: replacing it would load its whole dump over the databases it has now.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("container:db", RestoreAction.Replace)), StringComparison.Ordinal);
        // A copy gets a data volume of its own.
        var copy = Assert.Single(await restore.PlanAsync(manifest, saved, [new RestorePick("container:db", RestoreAction.Copy, "db2")], default));
        Assert.Equal("db2-data", copy.Rename("db-data"));
    }

    [Fact]
    public async Task Restore_and_copy_never_write_into_data_thats_here_or_share_the_hosts_folders()
    {
        using var host = new BackupHost();
        var restore = host.Restore();
        var (manifest, saved) = Backup();

        // A volume with the copy's name is here (an app removed with its volumes kept, say).
        host.Engine.AddVolume("gb-site2-data", "an earlier site2's");
        Assert.Equal("A volume called gb-site2-data is here already: give the copy of site another name, or remove that volume first.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("app:7", RestoreAction.Copy, "site2")));

        // A container that writes to a folder of the host: a copy would share it. Read-only, or Docker's socket, it may.
        var web = manifest.Containers.Single(c => c.Name == "web").Inspect;
        var mounts = (JsonArray)web["Mounts"]!;
        mounts.Add(new JsonObject { ["Type"] = "bind", ["Source"] = "/var/run/docker.sock", ["Destination"] = "/var/run/docker.sock", ["RW"] = true });
        mounts.Add(new JsonObject { ["Type"] = "bind", ["Source"] = "/srv/web-conf", ["Destination"] = "/etc/nginx", ["RW"] = false });
        Assert.Single(await restore.PlanAsync(manifest, saved, [new RestorePick("container:web", RestoreAction.Copy, "web2")], default));
        mounts.Add(new JsonObject { ["Type"] = "bind", ["Source"] = "/srv/web", ["Destination"] = "/usr/share/nginx/html", ["RW"] = true });
        Assert.Equal("web writes to the host's folder /srv/web: a copy would share it with the original, so it can't be copied.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("container:web", RestoreAction.Copy, "web2")));

        // Restore keeps a volume that's here as it is: not when another container here uses it.
        host.Engine.AddVolume("gb-site-data", "someone else's");
        Assert.Single(await restore.PlanAsync(manifest, saved, [new RestorePick("app:7", RestoreAction.Restore)], default));
        host.Engine.AddContainer("other", "nginx:1.29", "sha256:web", volumes: ["gb-site-data:/data"]);
        Assert.Equal("The volume gb-site-data is here and other uses it: restore site as a copy instead.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("app:7", RestoreAction.Restore)));
        Assert.Equal(new RestoreTarget("app:7", true, "site2", "The volume gb-site-data is here and other uses it: it can only come back as a copy."),
            (await restore.TargetsAsync(manifest, saved, default)).Single(t => t.ItemId == "app:7"));

        // Replace empties its own volumes: never one another container uses too.
        await using (var db = host.Db.CreateDbContext())
        {
            db.Apps.Add(new AppDefinition { Name = "site", Slug = "site" });
            await db.SaveChangesAsync();
        }
        Assert.Equal("The volume gb-site-data is here and other uses it too: replacing site would empty it under other. Restore it as a copy instead.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("app:7", RestoreAction.Replace)));
        Assert.Empty(host.Engine.Calls);
    }

    [Fact]
    public async Task A_restored_compose_app_keeps_the_data_folder_thats_here_and_a_replaced_one_takes_the_saved_one()
    {
        using var host = new BackupHost();
        var restore = host.Restore();
        var folder = Path.Combine(host.Options.DataDir, "compose", "site");
        static MemoryStream Tar()
        {
            var tar = new MemoryStream();
            using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "compose/site/data/"));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "compose/site/data/notes.txt") { DataStream = new MemoryStream("saved data"u8.ToArray()) });
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "compose/site/releases/1/compose.yaml") { DataStream = new MemoryStream("services: {}"u8.ToArray()) });
            }
            tar.Position = 0;
            return tar;
        }
        // Removed with its data kept, then written to since the backup.
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(Path.Combine(folder, "data", "notes.txt"), "newer data");

        var said = await restore.ComposeFilesAsync(new AppDefinition { Slug = "site" }, "site", RestoreAction.Restore, Tar(), default);
        Assert.Equal("newer data", File.ReadAllText(Path.Combine(folder, "data", "notes.txt")));
        Assert.Equal("services: {}", File.ReadAllText(Path.Combine(folder, "releases", "1", "compose.yaml")));
        Assert.Contains("the data/ folder there is kept as it was", said, StringComparison.Ordinal);

        await restore.ComposeFilesAsync(new AppDefinition { Slug = "site" }, "site", RestoreAction.Replace, Tar(), default);
        Assert.Equal("saved data", File.ReadAllText(Path.Combine(folder, "data", "notes.txt")));
    }

    [Fact]
    public async Task An_apps_database_needs_its_server_and_dump_and_is_never_another_apps()
    {
        using var host = new BackupHost();
        var restore = host.Restore();
        var (manifest, saved) = Backup(server: true);

        // Its server isn't here (and no pick before it brings it back).
        Assert.Equal("site's database is on db, which isn't running here: restore or start db first.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("app:7", RestoreAction.Restore)));
        // Picked before it, as it was, the server comes back first.
        Assert.Equal(2, (await restore.PlanAsync(manifest, saved, [new RestorePick("container:db", RestoreAction.Restore), new RestorePick("app:7", RestoreAction.Restore)], default)).Count);

        host.Engine.AddContainer("db", "postgres:18", "sha256:pg", volumes: ["db-data:/var/lib/postgresql"]);
        Assert.Single(await restore.PlanAsync(manifest, saved, [new RestorePick("app:7", RestoreAction.Restore)], default));

        // Another app here has a database or role of its name on that server: it isn't taken over. A copy gets its own.
        await using (var db = host.Db.CreateDbContext())
        {
            var blog = new AppDefinition { Name = "Blog 2", Slug = "blog-2" };
            db.Apps.Add(blog);
            await db.SaveChangesAsync();
            db.ManagedDatabases.Add(new ManagedDatabase { AppId = blog.Id, Kind = DatabaseKind.Postgres, Server = "db", Name = "blog_2", Role = "site", Variable = "DATABASE_URL" });
            await db.SaveChangesAsync();
        }
        Assert.Equal("The database site on db (or its role) is Blog 2's here: restore site as a copy instead.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("app:7", RestoreAction.Restore)));
        Assert.Equal(new RestoreTarget("app:7", true, "site2", "Its database site on db (or its role) is Blog 2's here: it can only come back as a copy."),
            (await restore.TargetsAsync(manifest, saved, default)).Single(t => t.ItemId == "app:7"));
        Assert.Single(await restore.PlanAsync(manifest, saved, [new RestorePick("app:7", RestoreAction.Copy, "site2")], default));

        // The backup has no dump of the server.
        manifest.Databases.Clear();
        Assert.Equal("The backup has no dump of db, so site's database can't come back.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("app:7", RestoreAction.Copy, "site2")));
    }

    [Fact]
    public async Task A_copy_is_named_as_New_app_names_an_app()
    {
        using var host = new BackupHost();
        var (manifest, saved) = Backup();
        // A preview's name is taken.
        await using (var db = host.Db.CreateDbContext())
        {
            var parent = new AppDefinition { Name = "blog", Slug = "blog" };
            db.Apps.Add(parent);
            await db.SaveChangesAsync();
            db.Apps.Add(new AppDefinition { Name = "blog · PR #2", Slug = "site2", ParentAppId = parent.Id });
            await db.SaveChangesAsync();
        }
        Assert.Equal("site2 is taken here: give the copy of site another name.",
            await RefusedAsync(host.Restore(), manifest, saved, new RestorePick("app:7", RestoreAction.Copy, "site2")));
        // New app's own check: a removed domain's route still going to the name, say.
        var restore = host.Restore(chooseSlug: (name, _) => Task.FromResult(name == "site3"
            ? new NetworkName("site3-2", "a removed domain still goes to site3") : new NetworkName(AppService.Slugify(name), null)));
        Assert.Equal("site3 is taken here (a removed domain still goes to site3): give the copy of site another name.",
            await RefusedAsync(restore, manifest, saved, new RestorePick("app:7", RestoreAction.Copy, "site3")));
        Assert.Single(await restore.PlanAsync(manifest, saved, [new RestorePick("app:7", RestoreAction.Copy, "site4")], default));
    }

    [Fact]
    public void A_copy_takes_the_first_free_number_and_its_volumes_take_its_slug()
    {
        Assert.Equal("site2", RestoreService.CopyName("site", _ => false));
        Assert.Equal("site4", RestoreService.CopyName("site", n => n is "site2" or "site3"));
        Assert.Equal("gb-site2-data", RestoreService.RenameVolume("gb-site-data", "site", "site2", "gb-site", "gb-site2"));
        Assert.Equal("gb-site2_pgdata", RestoreService.RenameVolume("gb-site_pgdata", "site", "site2", "gb-site_web", "gb-site2_web"));
        Assert.Equal("gb-site2-uploads", RestoreService.RenameVolume("uploads", "site", "site2", "gb-site", "gb-site2"));
        var anonymous = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));
        Assert.Equal("gb-site2-0123456789ab", RestoreService.RenameVolume(anonymous, "site", "site2", "gb-site", "gb-site2"));
    }

    [Fact]
    public void A_copys_settings_take_its_own_database_urls_and_slug()
    {
        var settings = new ContainerSettings
        {
            Env = [new EnvSetting("DATABASE_URL", "from the image", FromImage: true), new EnvSetting("DATABASE_URL", "the original's", Secret: true), new EnvSetting("MODE", "live")],
            Labels = new Dictionary<string, string> { ["gitberth.app"] = "site", ["other"] = "site" },
        };
        var copy = RestoreService.ForCopy(settings, [new ProvidedVariable("DATABASE_URL", "the copy's", "x"), new ProvidedVariable("REDIS_URL", "its redis", "y")], "site2");
        Assert.Equal([("DATABASE_URL", "from the image"), ("DATABASE_URL", "the copy's"), ("MODE", "live"), ("REDIS_URL", "its redis")], copy.Env.Select(e => (e.Key, e.Value)));
        Assert.True(copy.Env[1].Secret && copy.Env[3].Secret);
        Assert.Equal(("site2", "site"), (copy.Labels["gitberth.app"], copy.Labels["other"]));
    }
}
