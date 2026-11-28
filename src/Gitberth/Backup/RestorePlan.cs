using System.Globalization;
using System.Text;
using System.Text.Json;
using Gitberth.Services;

namespace Gitberth.Backup;

/// <summary>One step of a restore as plan.json keeps it.</summary>
public sealed class RestoreStepRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int Phase { get; set; }
    public RestoreStepState State { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// A restore's plan and where it stands (/data/restore/plan.json): each step is written down as it ends, so a restore that
/// stopped (Gitberth restarted in the middle) runs again from here and skips what's done.
/// </summary>
public sealed class RestorePlanState
{
    public DateTime StartedAt { get; set; }

    /// <summary>The backup's machine and moment: a plan is resumed only for the same backup.</summary>
    public string Host { get; set; } = "";

    public DateTime SavedAt { get; set; }

    /// <summary>The first phase ended: Gitberth's data is staged, and the restart puts it in place.</summary>
    public bool PhaseOneDone { get; set; }

    /// <summary>The restore is over: every step done, or it stopped at one it couldn't get past.</summary>
    public bool Finished { get; set; }

    /// <summary>The volumes this restore made (a volume that was here before it is kept as it is, never filled).</summary>
    public List<string> MadeVolumes { get; set; } = [];

    public List<RestoreStepRecord> Steps { get; set; } = [];

    public int Phase => PhaseOneDone ? 2 : 1;

    public static RestorePlanState New(BackupManifest manifest, DateTime now) => new()
    {
        StartedAt = now,
        Host = manifest.Host,
        SavedAt = manifest.SavedAt,
        Steps = RestorePlanner.Steps(manifest).Select(s => new RestoreStepRecord { Id = s.Id, Title = s.Title, Phase = s.Phase }).ToList(),
    };

    /// <summary>Whether this plan is <paramref name="manifest"/>'s backup's.</summary>
    public bool Of(BackupManifest manifest) => Host == manifest.Host && SavedAt == manifest.SavedAt;

    public RestoreStepRecord? Step(string id) => Steps.FirstOrDefault(s => s.Id == id);

    public RestoreStatus View(bool active) => new(active, Finished, Phase,
        Steps.Select(s => new RestoreStepView(s.Id, s.Title, s.State, s.Note)).ToList(), Steps.Count(s => s.State == RestoreStepState.Failed), StartedAt);

    private static readonly JsonSerializerOptions Json = new(BackupManifest.Json) { WriteIndented = true };

    public static RestorePlanState? Load(BackupPaths paths)
    {
        try
        {
            return File.Exists(paths.Plan) ? JsonSerializer.Deserialize<RestorePlanState>(File.ReadAllText(paths.Plan), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Written whole to a new file, then moved over the old one: a stop in the middle never leaves half a plan.</summary>
    public void Save(BackupPaths paths)
    {
        Directory.CreateDirectory(paths.RestoreDir);
        var next = paths.Plan + ".new";
        File.WriteAllText(next, JsonSerializer.Serialize(this, Json));
        File.Move(next, paths.Plan, overwrite: true);
    }
}

/// <summary>A restore's steps from a backup's manifest, in the order they run.</summary>
public static class RestorePlanner
{
    public static List<PlannedStep> Steps(BackupManifest m)
    {
        var steps = new List<PlannedStep> { new("check", "Check the whole file", 1) };
        steps.AddRange(m.Networks.Select(n => new PlannedStep("network:" + n.Name, "Network " + n.Name, 1)));
        steps.AddRange(m.Volumes.Select(v => new PlannedStep("volume:" + v.Name, "Volume " + v.Name, 1)));
        steps.AddRange(m.HostFolders.Where(f => f.Part is not null).Select(f => new PlannedStep(HostFolders.ItemId(f.Path), "Host folder " + f.Path, 1)));
        foreach (var image in m.Images)
            steps.Add(image.Part is not null
                ? new PlannedStep("image:" + image.Id, "Image " + (image.Tags.FirstOrDefault() ?? image.Id), 1)
                : new PlannedStep("pull:" + image.Id, "Pull " + (image.Tags.FirstOrDefault() ?? image.Digest ?? image.Id), 1));
        steps.AddRange(m.Databases.Select(d => new PlannedStep("database:" + d.Container, $"Database {d.Container}: its dump loaded", 1)));
        steps.AddRange(Created(m).Select(c => new PlannedStep("container:" + c.Name, "Container " + c.Name, 1)));
        steps.Add(new PlannedStep("gitberth", "Gitberth's data", 1));
        steps.Add(new PlannedStep("restart", "Restart Gitberth on the saved data", 2));
        foreach (var app in m.Apps)
            steps.Add(new PlannedStep("deploy:" + app.Id.ToString(CultureInfo.InvariantCulture),
                app.Compose && !app.FromRepo ? "Start " + app.Name : app.Commit is { Length: >= 7 } sha ? $"Deploy {app.Name} at {sha[..7]}" : "Deploy " + app.Name, 2));
        steps.Add(new PlannedStep("domains", "Domains", 2));
        steps.Add(new PlannedStep("finish", "Finish", 2));
        return steps;
    }

    /// <summary>The containers created from their inspect, databases (and anything like one) first, then the rest, each by name.</summary>
    public static IEnumerable<SavedContainer> Created(BackupManifest m) =>
        m.Containers.Where(c => c.How == ComesBack.Inspect)
            .OrderBy(c => ContainerService.IsDatabase(BackupInventory.Str(c.Inspect["Config"], "Image"), c.Inspect["Config"]) ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.Ordinal);
}

/// <summary>
/// The restore's log: /data/restore/restore.log, kept across the restart, its lines also in memory for the live view.
/// Every secret it was told about is masked before a line is written.
/// </summary>
public sealed class RestoreLog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<string> _lines = [];
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);

    public RestoreLog(BackupPaths paths)
    {
        _path = paths.Log;
        try
        {
            if (File.Exists(_path)) _lines.AddRange(File.ReadAllLines(_path));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A value never written: a database's password, a token in a container's settings.</summary>
    public void Secret(string? value)
    {
        if (value is { Length: >= 4 })
            lock (_gate) _secrets.Add(value);
    }

    public void Line(string text)
    {
        var line = $"{DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {Mask(text)}";
        lock (_gate)
        {
            _lines.Add(line);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line + "\n", Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A new restore's log starts empty.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }
    }

    public IReadOnlyList<string> From(int from)
    {
        lock (_gate) return _lines.Skip(Math.Max(0, from)).ToList();
    }

    private string Mask(string text)
    {
        lock (_gate)
            foreach (var secret in _secrets.OrderByDescending(s => s.Length))
                text = text.Replace(secret, "••••", StringComparison.Ordinal);
        return text;
    }
}
