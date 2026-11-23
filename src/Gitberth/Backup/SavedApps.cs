using System.Security.Cryptography;
using Gitberth.Data;
using Gitberth.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Backup;

/// <summary>One app as a backup's own database has it, its secrets re-encrypted for this Gitberth.</summary>
internal sealed class SavedAppRows
{
    /// <summary>The row, with its ports, variables, volumes, domains and networks; its IDs are the saved Gitberth's.</summary>
    public required AppDefinition App { get; init; }

    public List<DeployHook> Hooks { get; init; } = [];
    public List<ManagedDatabase> Databases { get; init; } = [];
    public List<Deployment> Deployments { get; init; } = [];
}

/// <summary>
/// The apps a backup holds, read from its own data (P3): its gitberth.db and key ring are taken out of the file into
/// /data/restore/read/, a copy of that database is migrated to this Gitberth's version, and each app's rows are read. Every
/// protected value is decrypted with the saved key ring and encrypted again with this Gitberth's protector; none is ever
/// logged. The folder goes whatever happens.
/// </summary>
internal static class SavedApps
{
    /// <summary>The apps (previews left out), by their saved ID.</summary>
    public static async Task<Dictionary<int, SavedAppRows>> ReadAsync(Stream file, BackupKey key, string readRoot, SecretProtector here, CancellationToken ct)
    {
        var dir = Path.Combine(readRoot, Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        try
        {
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, "gitberth.db");
            using (var reader = await BackupReader.OpenAsync(file, key, ct))
            {
                bool database = false, keys = false;
                while (!(database && keys) && await reader.NextPartAsync(ct) is { } part)
                {
                    if (part.Kind == BackupPartKind.DataFile && part.Name == "data/gitberth.db")
                    {
                        await using var copy = new FileStream(dbPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                        await part.Content.CopyToAsync(copy, ct);
                        database = true;
                    }
                    else if (part.Kind == BackupPartKind.DataFolder && part.Name == "data/keys")
                    {
                        await RestoreService.ExtractAsync(part.Content, dir, ct);
                        keys = true;
                    }
                }
                if (!database) throw new InvalidOperationException("This backup holds no Gitberth database to read its apps from.");
            }

            var saved = new SecretProtector(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "keys")), b => b.SetApplicationName("Gitberth")));
            string Again(string value) => string.IsNullOrEmpty(value) ? value : here.Protect(saved.TryUnprotect(value));
            string? AgainOrNull(string? value) => value is null ? null : Again(value);

            var options = new DbContextOptionsBuilder<GitberthDb>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()).Options;
            await using var db = new GitberthDb(options);
            await db.Database.MigrateAsync(ct);
            var apps = await db.Apps.AsNoTracking().Where(a => a.ParentAppId == null)
                .Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Domains).Include(a => a.Networks)
                .AsSplitQuery().ToListAsync(ct);
            var ids = apps.Select(a => a.Id).ToHashSet();
            var hooks = (await db.DeployHooks.AsNoTracking().ToListAsync(ct)).Where(h => ids.Contains(h.AppId)).ToLookup(h => h.AppId);
            var databases = (await db.ManagedDatabases.AsNoTracking().ToListAsync(ct)).Where(d => ids.Contains(d.AppId)).ToLookup(d => d.AppId);
            var deployments = (await db.Deployments.AsNoTracking().ToListAsync(ct)).Where(d => ids.Contains(d.AppId)).ToLookup(d => d.AppId);

            var result = new Dictionary<int, SavedAppRows>();
            foreach (var app in apps)
            {
                app.SpecProtected = AgainOrNull(app.SpecProtected);
                app.ComposeYamlProtected = AgainOrNull(app.ComposeYamlProtected);
                foreach (var variable in app.EnvVars) variable.ProtectedValue = Again(variable.ProtectedValue);
                var rows = new SavedAppRows
                {
                    App = app,
                    Hooks = hooks[app.Id].ToList(),
                    Databases = databases[app.Id].ToList(),
                    Deployments = deployments[app.Id].OrderBy(d => d.Id).ToList(),
                };
                foreach (var hook in rows.Hooks) hook.ProtectedToken = Again(hook.ProtectedToken);
                foreach (var database in rows.Databases) database.ProtectedPassword = Again(database.ProtectedPassword);
                result[app.Id] = rows;
            }
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
