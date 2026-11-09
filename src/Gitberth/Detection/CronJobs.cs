using System.Text.Json;

namespace Gitberth.Detection;

/// <summary>A cron job from vercel.json: a GET of <paramref name="Path"/> at the times <paramref name="Schedule"/> gives.</summary>
public sealed record CronJob(string Path, string Schedule);

/// <summary>
/// The crons in an app's vercel.json. They're read when the image is built and travel with it as a label
/// (<see cref="Docker.ImageLabels.Crons"/>), so a rollback runs the jobs of the image it goes back to.
/// </summary>
public static class CronJobs
{
    /// <summary>More than this are left out. It also keeps the label far below what one argument of a command line can carry.</summary>
    public const int MaxJobs = 100;

    /// <summary>
    /// What to label an image built from the clone at <paramref name="repoRoot"/> with: null when the app in
    /// <paramref name="appDir"/> has no cron jobs. Each job that can't be used is left out with a warning for the deploy log.
    /// </summary>
    public static (string? LabelJson, IReadOnlyList<string> Warnings) ForBuild(string repoRoot, string appDir)
    {
        var (jobs, warnings) = Read(repoRoot, appDir);
        return (jobs.Count == 0 ? null : ToLabel(jobs), warnings);
    }

    /// <summary>The label value: compact JSON, [{"path":"/api/x","schedule":"0 6 * * *"}].</summary>
    public static string ToLabel(IEnumerable<CronJob> jobs) =>
        JsonSerializer.Serialize(jobs.Select(j => new { path = j.Path, schedule = j.Schedule }));

    /// <summary>The jobs in a label value. Each is checked again, so one an older Gitberth wrote can't stop the others.</summary>
    public static IReadOnlyList<CronJob> FromLabel(string? json)
    {
        var jobs = new List<CronJob>();
        if (string.IsNullOrWhiteSpace(json)) return jobs;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return jobs;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var job = new CronJob(Text(entry, "path") ?? "", Text(entry, "schedule") ?? "");
                if (jobs.Count < MaxJobs && Problem(job.Path, job.Schedule) is null && !jobs.Contains(job)) jobs.Add(job);
            }
        }
        catch (JsonException)
        {
        }
        return jobs;
    }

    internal static (List<CronJob> Jobs, List<string> Warnings) Read(string repoRoot, string appDir)
    {
        var jobs = new List<CronJob>();
        var warnings = new List<string>();
        if (ProjectAnalyzer.NormalizeDir(appDir) is not { } dir || Find(repoRoot, dir, warnings) is not (var config, var file)
            || !config.TryGetProperty("crons", out var crons)) return (jobs, warnings);
        if (crons.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"\"crons\" in {file} isn't a list, so none of it is scheduled.");
            return (jobs, warnings);
        }
        var number = 0;
        foreach (var entry in crons.EnumerateArray())
        {
            number++;
            var (path, schedule) = (Text(entry, "path"), Text(entry, "schedule"));
            if (Problem(path, schedule) is { } problem)
            {
                warnings.Add($"The cron job {path ?? $"#{number}"} in {file} isn't scheduled. {problem}");
                continue;
            }
            var job = new CronJob(path!, schedule!);
            if (jobs.Contains(job))
            {
                warnings.Add($"The cron job {path} ({schedule}) is listed twice in {file}. It runs once.");
            }
            else if (jobs.Count == MaxJobs)
            {
                warnings.Add($"{file} lists more than {MaxJobs} cron jobs. Only the first {MaxJobs} are scheduled.");
                break;
            }
            else
            {
                jobs.Add(job);
            }
        }
        return (jobs, warnings);
    }

    /// <summary>
    /// The vercel.json that configures the app in <paramref name="dir"/>: the folder's own, or else the repo root's when
    /// its rootDirectory points at the folder, as Kanban's does. One whose rootDirectory names another folder belongs to
    /// that folder, as for <see cref="VercelConfig.Load"/>.
    /// </summary>
    private static (JsonElement Config, string File)? Find(string repoRoot, string dir, List<string> warnings)
    {
        var folder = dir.Length == 0 ? repoRoot : Path.Combine(repoRoot, dir.Replace('/', Path.DirectorySeparatorChar));
        var name = RepoFiles.Join(dir, "vercel.json");
        if (File.Exists(Path.Combine(folder, "vercel.json")))
        {
            if (VercelConfig.Read(folder) is not { } own)
            {
                warnings.Add($"{name} couldn't be read as JSON, so Gitberth can't schedule any cron jobs from it.");
                return null;
            }
            if (VercelConfig.RootDirectory(own) is null) return (own, name);
        }
        return dir.Length > 0 && VercelConfig.Read(repoRoot) is { } root && VercelConfig.RootDirectory(root) == dir ? (root, "vercel.json") : null;
    }

    /// <summary>Why a job can't be scheduled, as a sentence, or null when it can.</summary>
    private static string? Problem(string? path, string? schedule) =>
        path is null ? "It has no path."
        : !path.StartsWith('/') ? "Its path must start with /."
        : path.Length > 512 ? "Its path is longer than 512 characters."
        : path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? "Its path can't contain spaces."
        : schedule is null ? "It has no schedule."
        : schedule.Length > 256 ? "Its schedule is longer than 256 characters."
        : CronSchedule.TryParse(schedule, out _, out var error) ? null
        : error;

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && v.GetString()!.Trim() is { Length: > 0 } s ? s : null;
}
