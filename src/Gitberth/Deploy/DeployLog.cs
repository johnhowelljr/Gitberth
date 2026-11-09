namespace Gitberth.Deploy;

public static class DeployPaths
{
    public static string LogFile(GitberthOptions options, int deploymentId) => Path.Combine(options.LogsDir, $"deploy-{deploymentId}.log");

    public static string DockerfileCopy(GitberthOptions options, int deploymentId) => Path.Combine(options.LogsDir, $"deploy-{deploymentId}.Dockerfile");

    public static List<string> ReadTail(string path, int maxLines)
    {
        var tail = new Queue<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > maxLines) tail.Dequeue();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return tail.ToList();
    }

    public static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes a clone; git marks pack files read-only, which blocks deletion on Windows.</summary>
    public static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Writes a deploy's log to disk and to its <see cref="LiveLog"/>, masking secret values.</summary>
public sealed class DeployLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly LiveLog _live;
    private string[] _secrets = [];
    private readonly Lock _lock = new();
    private bool _disposed;

    public DeployLog(string path, LiveLog live, IEnumerable<string> secrets)
    {
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        _live = live;
        Mask(secrets);
    }

    /// <summary>Adds values to hide, for secrets only known once the deploy has started.</summary>
    public void Mask(IEnumerable<string> secrets)
    {
        lock (_lock) _secrets = _secrets.Concat(secrets.Where(s => s.Length >= 4)).Distinct().OrderByDescending(s => s.Length).ToArray();
    }

    public void Step(string message) => Write("==> " + message);

    public void Line(string line) => Write(line);

    private void Write(string text)
    {
        string stamped;
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var secret in _secrets) text = text.Replace(secret, Security.Masking.Mask, StringComparison.Ordinal);
            stamped = $"{DateTime.Now:HH:mm:ss} {text}";
            _writer.WriteLine(stamped);
        }
        _live.Append(stamped);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _writer.Dispose();
        }
    }
}
