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
    /// <summary>Called for every stdout and stderr line, from a background thread.</summary>
    public Action<string>? OnLine { get; init; }
    /// <summary>Keep at most this many chars of stdout/stderr in the result (the tail is kept).</summary>
    public int MaxCapture { get; init; } = 4 * 1024 * 1024;
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
        };
        if (options.WorkingDirectory is not null) psi.WorkingDirectory = options.WorkingDirectory;
        foreach (var arg in args) psi.ArgumentList.Add(arg);
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
