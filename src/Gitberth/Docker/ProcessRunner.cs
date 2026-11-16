using System.Diagnostics;
using System.Text;

namespace Gitberth.Docker;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string ErrorText => (string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr).Trim();
}

public sealed class ProcessOptions
{
    public string? WorkingDirectory { get; init; }
    /// <summary>Extra environment variables for the child process (inherits ours otherwise).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    /// <summary>
    /// Start the child with none of Gitberth's own environment but what a CLI needs to run and reach Docker (PATH, HOME and
    /// the DOCKER_ ones), and <see cref="Environment"/>: for `docker compose`, which fills a Compose file's ${…} from it.
    /// </summary>
    public bool CleanEnvironment { get; init; }
    /// <summary>Called for every stdout and stderr line, from a background thread.</summary>
    public Action<string>? OnLine { get; init; }
    /// <summary>Keep at most this many chars of stdout/stderr in the result (the tail is kept).</summary>
    public int MaxCapture { get; init; } = 4 * 1024 * 1024;
    /// <summary>
    /// Written to the child's standard input (UTF-8, no byte order mark), which is then closed; null leaves it alone. What a
    /// command reads this way (SQL for psql, say) never shows on its command line.
    /// </summary>
    public string? Stdin { get; init; }
}

/// <summary>Runs a CLI (docker, git) with an argument list (no shell), streaming its output.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, ProcessOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ProcessOptions();
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            RedirectStandardInput = options.Stdin is not null,
        };
        // A byte order mark would reach the child as the first characters it reads.
        if (options.Stdin is not null) psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (options.WorkingDirectory is not null) psi.WorkingDirectory = options.WorkingDirectory;
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (options.CleanEnvironment)
        {
            var keep = psi.Environment.Where(e => e.Value is not null && Kept(e.Key)).ToList();
            psi.Environment.Clear();
            foreach (var (key, value) in keep) psi.Environment[key] = value;
        }
        if (options.Environment is not null)
        {
            foreach (var (key, value) in options.Environment) psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new TailBuffer(options.MaxCapture);
        var stderr = new TailBuffer(options.MaxCapture);
        var outClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outClosed.TrySetResult(); return; }
            stdout.AppendLine(e.Data);
            options.OnLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errClosed.TrySetResult(); return; }
            stderr.AppendLine(e.Data);
            options.OnLine?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            if (options.Stdin is { } input) await WriteInputAsync(process, input, ct);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }

        try { await Task.WhenAll(outClosed.Task, errClosed.Task).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch (TimeoutException) { }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // The input, then end of file. A child that ends before it read everything closes the pipe: that's its exit's to say.
    private static async Task WriteInputAsync(Process process, string input, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        catch (IOException)
        {
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
        }
    }

    /// <summary>What a clean environment keeps of Gitberth's own: what the docker CLI needs to run and to reach the engine.</summary>
    internal static bool Kept(string key) =>
        key is "PATH" or "HOME" or "TMPDIR" or "SSL_CERT_FILE" or "SSL_CERT_DIR" or "HTTP_PROXY" or "HTTPS_PROXY" or "NO_PROXY"
        || key.StartsWith("DOCKER_", StringComparison.Ordinal);

    private sealed class TailBuffer(int max)
    {
        private readonly StringBuilder _sb = new();
        private readonly Lock _lock = new();

        public void AppendLine(string line)
        {
            lock (_lock)
            {
                _sb.Append(line).Append('\n');
                if (_sb.Length > max) _sb.Remove(0, _sb.Length - max);
            }
        }

        public override string ToString()
        {
            lock (_lock) return _sb.ToString();
        }
    }
}
