using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Docker;
using Gitberth.Security;

namespace Gitberth.Backup;

/// <summary>A file at a destination, as its listing gives it, with its header's first bytes when they were asked for.</summary>
public sealed record DestinationFile(string Name, long Bytes, DateTime? Modified, byte[]? Header = null);

/// <summary>
/// Where automatic backups go: a folder on the host, an SMB share, an FTP server, or an S3-compatible bucket. A backup is
/// streamed to it as it's made (never staged on Gitberth's disk), under a temporary name until it's whole. Only files whose
/// names are Gitberth's own (gitberth-auto-…, gitberth-test-…) are ever written, read or removed.
/// </summary>
public interface IBackupDestination : IAsyncDisposable
{
    /// <summary>"the folder /mnt/backups on this host", "//nas/backups/gitberth", "s3://backups/gitberth at s3.us-west-004.backblazeb2.com".</summary>
    string Where { get; }

    /// <summary>
    /// The file written from <paramref name="content"/>, read to its end. Kept only when the content ended as it should;
    /// otherwise nothing of it stays, and the content's error (or an IOException) is thrown.
    /// </summary>
    Task WriteAsync(string name, Stream content, CancellationToken ct);

    /// <summary>The files at the destination whose names are Gitberth's (gitberth-…); each one's first 64 bytes too with <paramref name="headers"/>.</summary>
    Task<IReadOnlyList<DestinationFile>> ListAsync(bool headers, CancellationToken ct);

    Task ReadAsync(string name, Stream into, CancellationToken ct);

    Task DeleteAsync(string name, CancellationToken ct);
}

/// <summary>A destination's refusal or failure, in a sentence the owner reads (never a secret).</summary>
public sealed class DestinationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The destination automatic backups' settings name, opened for one run (or one test, or one listing): its helper is made
/// when first needed and removed when it's disposed.
/// </summary>
public sealed class AutoDestinations(IBackupEngine engine, DockerCli docker, SecretProtector protector, IHttpClientFactory http, GitberthOptions options)
{
    /// <summary>The helpers' label value: removed at start if one was left behind.</summary>
    public const string HelperKind = "backup-dest";

    /// <summary>
    /// The SMB and FTP clients' image: built once from Alpine with samba-client and curl (it's not Gitberth's to remove;
    /// built again when it's gone).
    /// </summary>
    public const string TransferImage = "gitberth-helper/transfer:1";

    /// <summary>Its Dockerfile.</summary>
    internal const string TransferDockerfile = "FROM alpine:3.21\nRUN apk add --no-cache samba-client curl\nLABEL gitberth.helper=transfer\n";

    /// <summary>The SMB and FTP helper's image, built when this host hasn't got it.</summary>
    internal static async Task EnsureTransferImageAsync(IBackupEngine engine, DockerCli docker, CancellationToken ct)
    {
        if (await engine.InspectImageAsync(TransferImage, ct) is not null) return;
        var built = await docker.RunAsync(["build", "-t", TransferImage, "--label", $"{HostScanHelper.Label}=transfer", "-"],
            new ProcessOptions { Stdin = TransferDockerfile }, ct);
        if (!built.Ok)
            throw new DestinationException($"Gitberth couldn't build its SMB and FTP helper from Alpine (it needs to reach Docker Hub and Alpine's packages): {Helper.FirstLine(built.ErrorText)}.");
    }

    /// <summary>A helper of the transfer image, asleep for its execs, on the default bridge (it reaches the LAN).</summary>
    internal static JsonObject TransferBody() => new()
    {
        ["Image"] = TransferImage,
        ["User"] = "0:0",
        ["Entrypoint"] = new JsonArray("sleep"),
        ["Cmd"] = new JsonArray("3600"),
        ["Labels"] = new JsonObject { [HostScanHelper.Label] = HelperKind },
        ["HostConfig"] = new JsonObject { ["NetworkMode"] = "bridge" },
    };

    /// <summary>Stands in for a destination in tests.</summary>
    internal Func<AutoBackupConfig, IBackupDestination>? Open { get; init; }

    /// <summary>The destination of these settings; its passwords are read with SecretProtector.</summary>
    /// <exception cref="DestinationException">It can't be used as set (the settings' own check says why), or Gitberth can't reach it from here.</exception>
    public async Task<IBackupDestination> OpenAsync(AutoBackupConfig c, CancellationToken ct)
    {
        if (Open is { } open) return open(c);
        switch (c.Where)
        {
            case AutoDestinationKind.Folder:
                if (AutoBackupRules.FolderProblem(c.Folder) is { } bad) throw new DestinationException(bad);
                return new FolderDestination(engine, await OwnImageAsync(ct), HostFolders.Clean(c.Folder!.Trim())!);
            case AutoDestinationKind.Smb:
                if (AutoBackupRules.SmbProblem(c.SmbShare, c.SmbUser, c.SmbDomain) is { } wrong) throw new DestinationException(wrong);
                var (service, folder) = AutoBackupRules.SmbTarget(c.SmbShare)!.Value;
                return new SmbDestination(engine, docker, service, folder, c.SmbUser ?? "", Secret(c.SmbPassword), c.SmbDomain);
            case AutoDestinationKind.Ftp:
                if (AutoBackupRules.FtpProblem(c.FtpAddress, c.FtpUser) is { } off) throw new DestinationException(off);
                return new FtpDestination(engine, docker, AutoBackupRules.Ftp(c.FtpAddress)!, c.FtpUser!.Trim(), Secret(c.FtpPassword));
            default:
                if (AutoBackupRules.S3Problem(c) is { } problem) throw new DestinationException(problem);
                return new S3Destination(http.CreateClient(S3Client.ClientName), S3Target.Of(c), c.S3AccessKey!.Trim(), Secret(c.S3Secret));
        }
    }

    private string Secret(string? protectedValue) => protectedValue is { Length: > 0 } p ? protector.TryUnprotect(p) : "";

    private async Task<string> OwnImageAsync(CancellationToken ct) =>
        options.OwnContainer is { } self && await engine.InspectContainerAsync(self, ct) is { } me && BackupInventory.Str(me, "Image") is { Length: > 0 } image
            ? image
            : throw new DestinationException("A folder on the host is written by a helper of Gitberth's own image, and Gitberth isn't running in a container here.");

    /// <summary>Where the settings send backups, in a few words.</summary>
    public static string Describe(AutoBackupConfig c) => c.Where switch
    {
        AutoDestinationKind.Folder => $"the folder {c.Folder} on this host",
        AutoDestinationKind.Smb => c.SmbShare ?? "an SMB share",
        AutoDestinationKind.Ftp => AutoBackupRules.Ftp(c.FtpAddress)?.Display ?? "an FTP server",
        _ => S3Target.Describe(c),
    };

    /// <summary>Whether a file name is one Gitberth may write, read or remove at a destination.</summary>
    internal static bool Ours(string name) =>
        name.Length <= 120 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
        && (AutoBackupRules.Parse(name) is not null || name.StartsWith("gitberth-test-", StringComparison.Ordinal) && !name.Contains(".."));

    internal static void CheckName(string name)
    {
        if (!Ours(name)) throw new DestinationException($"{name} isn't one of Gitberth's own files.");
    }
}

/// <summary>
/// A stream read to its end by someone else (Docker's exec feeds it): whether it ended as it should, or why not.
/// </summary>
internal sealed class EndStream(Stream inner) : Stream
{
    /// <summary>It was read to its end without an error.</summary>
    public bool Ended { get; private set; }

    /// <summary>What reading it threw.</summary>
    public Exception? Failed { get; private set; }

    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            var n = await inner.ReadAsync(buffer, ct);
            if (n == 0 && buffer.Length > 0) Ended = true;
            BytesRead += n;
            return n;
        }
        catch (Exception ex)
        {
            Failed ??= ex;
            throw;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>The content's own error when it had one, else that it ended early.</summary>
    public Exception Why() => Failed is { } failed
        ? failed is IOException or InvalidOperationException ? failed : new IOException(failed.Message, failed)
        : new IOException("The backup ended before it was whole.");
}

/// <summary>A helper container kept for one destination's calls: made and started when first asked for, removed when disposed.</summary>
internal sealed class Helper(IBackupEngine engine, Func<string, JsonObject> body) : IAsyncDisposable
{
    private string? _name;

    public async Task<string> NameAsync(CancellationToken ct)
    {
        if (_name is not null) return _name;
        var name = HostFolders.HelperName(AutoDestinations.HelperKind);
        await engine.CreateContainerAsync(name, body(name), ct);
        _name = name;
        await engine.StartContainerAsync(name, ct);
        return name;
    }

    public async Task<ExecResult> ExecAsync(IReadOnlyList<string> command, Stream? stdin, Stream? stdout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null) =>
        await engine.ExecAsync(await NameAsync(ct), command, env, stdin, stdout, ct);

    public async ValueTask DisposeAsync()
    {
        if (_name is null) return;
        try
        {
            await engine.RemoveContainerAsync(_name, force: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Left behind, it's removed at the next start (BackupService.RecoverAsync).
        }
        _name = null;
    }

    public static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } line
            ? line.Length > 300 ? line[..300] : line
            : "it said nothing";
}

/// <summary>
/// A folder on the Docker host: a helper of Gitberth's own image mounts it at /dest (Docker makes the folder when it isn't
/// there) and writes, lists, reads and removes files there. Files are written with mode 600.
/// </summary>
internal sealed class FolderDestination : IBackupDestination
{
    private readonly Helper _helper;
    private readonly string _folder;

    public FolderDestination(IBackupEngine engine, string image, string folder)
    {
        _folder = folder;
        _helper = new Helper(engine, _ => HostFolders.Body(image, [$"{folder}:/dest"], AutoDestinations.HelperKind, ["sleep", "3600"]));
    }

    public string Where => $"the folder {_folder} on this host";

    public async Task WriteAsync(string name, Stream content, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        var fed = new EndStream(content);
        var r = await _helper.ExecAsync(["sh", "-c", "umask 077; cat > \"/dest/$1.part\"", "sh", name], fed, null, ct);
        if (!r.Ok || !fed.Ended)
        {
            await _helper.ExecAsync(["rm", "-f", "--", $"/dest/{name}.part"], null, null, CancellationToken.None);
            throw r.Ok ? fed.Why() : new DestinationException($"Writing to {_folder} failed: {Helper.FirstLine(r.StdErr)}.");
        }
        var moved = await _helper.ExecAsync(["mv", "-f", "--", $"/dest/{name}.part", $"/dest/{name}"], null, null, ct);
        if (!moved.Ok) throw new DestinationException($"Writing to {_folder} failed: {Helper.FirstLine(moved.StdErr)}.");
    }

    private const string ListScript =
        "cd /dest || exit 3; for f in gitberth-*; do [ -f \"$f\" ] || continue; " +
        "printf '%s\\t%s\\t%s\\t' \"$f\" \"$(stat -c %s \"$f\")\" \"$(stat -c %Y \"$f\")\"; " +
        "if [ \"$1\" = 1 ]; then head -c 64 \"$f\" | base64 | tr -d '\\n'; fi; printf '\\n'; done";

    public async Task<IReadOnlyList<DestinationFile>> ListAsync(bool headers, CancellationToken ct)
    {
        var output = new MemoryStream();
        var r = await _helper.ExecAsync(["sh", "-c", ListScript, "sh", headers ? "1" : "0"], null, output, ct);
        if (!r.Ok) throw new DestinationException($"Listing {_folder} failed: {Helper.FirstLine(r.StdErr)}.");
        return ParseList(Encoding.UTF8.GetString(output.ToArray()));
    }

    /// <summary>name, size, modified (seconds since 1970) and the header's first bytes in base64, a tab between each.</summary>
    internal static List<DestinationFile> ParseList(string text)
    {
        var files = new List<DestinationFile>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = line.Split('\t');
            if (cols.Length < 3 || !AutoDestinations.Ours(cols[0]) || !long.TryParse(cols[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) continue;
            DateTime? modified = long.TryParse(cols[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? DateTime.UnixEpoch.AddSeconds(seconds) : null;
            byte[]? header = null;
            if (cols.Length > 3 && cols[3].Length > 0)
                try
                {
                    header = Convert.FromBase64String(cols[3].Trim());
                }
                catch (FormatException)
                {
                }
            files.Add(new DestinationFile(cols[0], size, modified, header));
        }
        return files;
    }

    public async Task ReadAsync(string name, Stream into, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        var r = await _helper.ExecAsync(["cat", "--", $"/dest/{name}"], null, into, ct);
        if (!r.Ok) throw new DestinationException($"Reading {name} from {_folder} failed: {Helper.FirstLine(r.StdErr)}.");
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        var r = await _helper.ExecAsync(["rm", "-f", "--", $"/dest/{name}"], null, null, ct);
        if (!r.Ok) throw new DestinationException($"Removing {name} from {_folder} failed: {Helper.FirstLine(r.StdErr)}.");
    }

    public ValueTask DisposeAsync() => _helper.DisposeAsync();
}

/// <summary>
/// An SMB share, through smbclient in a helper (<see cref="AutoDestinations.TransferImage"/>, built once). The credentials go
/// in an auth file inside the helper (mode 600), never on a command line; every name in smbclient's commands is one
/// <see cref="AutoBackupRules.SmbSafe"/> allows, inside double quotes.
/// </summary>
internal sealed partial class SmbDestination : IBackupDestination
{
    private readonly IBackupEngine _engine;
    private readonly DockerCli _docker;
    private readonly string _service;
    private readonly string? _folder;
    private readonly string _user;
    private readonly string _password;
    private readonly string? _domain;
    private readonly Helper _helper;
    private bool _ready;

    public SmbDestination(IBackupEngine engine, DockerCli docker, string service, string? folder, string user, string password, string? domain)
    {
        (_engine, _docker, _service, _folder, _user, _password, _domain) = (engine, docker, service, folder, user, password, domain);
        _helper = new Helper(engine, _ => AutoDestinations.TransferBody());
    }

    public string Where => _folder is null ? _service : $"{_service}/{_folder}";

    // The image once, the helper, then its auth file: username, password and domain, one per line.
    private async Task ReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        if (string.IsNullOrEmpty(_user)) throw new DestinationException("Enter the user name for the share.");
        if (AutoBackupRules.SmbPasswordProblem(_password) is { } bad) throw new DestinationException(bad);
        await AutoDestinations.EnsureTransferImageAsync(_engine, _docker, ct);
        var auth = $"username={_user}\npassword={_password}\n" + (string.IsNullOrEmpty(_domain) ? "" : $"domain={_domain}\n");
        var r = await _helper.ExecAsync(["sh", "-c", "umask 077; cat > /tmp/auth"], new MemoryStream(Encoding.UTF8.GetBytes(auth)), null, ct);
        if (!r.Ok) throw new DestinationException($"The SMB helper didn't start: {Helper.FirstLine(r.StdErr)}.");
        _ready = true;
    }

    // smbclient's arguments: the service, the auth file, the folder to start in; then the commands.
    private List<string> Args(bool quiet, string commands, bool inFolder = true)
    {
        var args = new List<string> { "smbclient", _service, "-A", "/tmp/auth" };
        if (quiet) args.Add("-E");
        if (inFolder && _folder is not null) args.AddRange(["-D", _folder]);
        args.AddRange(["-c", commands]);
        return args;
    }

    private static string Quote(string name) => AutoBackupRules.SmbSafe(name) ? $"\"{name}\"" : throw new DestinationException($"{name} can't be sent to the share.");

    // A failed command: smbclient's exit code, or an NT_STATUS error it printed.
    private DestinationException? Failure(ExecResult r, string? stdout, string what)
    {
        var said = (r.StdErr + "\n" + stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Contains("NT_STATUS_", StringComparison.Ordinal));
        if (r.Ok && said is null) return null;
        return new DestinationException($"{what} on {Where} failed: {Explain(said ?? Helper.FirstLine(r.StdErr))}.");
    }

    private static string Explain(string said) =>
        said.Contains("NT_STATUS_LOGON_FAILURE", StringComparison.Ordinal) ? "the share refused the user name or password"
        : said.Contains("NT_STATUS_BAD_NETWORK_NAME", StringComparison.Ordinal) ? "the server has no share by that name"
        : said.Contains("NT_STATUS_ACCESS_DENIED", StringComparison.Ordinal) ? "the user may not write there"
        : said.Contains("NT_STATUS_OBJECT_NAME_NOT_FOUND", StringComparison.Ordinal) || said.Contains("NT_STATUS_OBJECT_PATH_NOT_FOUND", StringComparison.Ordinal)
            ? "the folder isn't on the share"
        : said.Contains("NT_STATUS_IO_TIMEOUT", StringComparison.Ordinal) || said.Contains("NT_STATUS_HOST_UNREACHABLE", StringComparison.Ordinal)
            || said.Contains("NT_STATUS_CONNECTION_REFUSED", StringComparison.Ordinal) || said.Contains("Connection to", StringComparison.Ordinal)
            ? "the server didn't answer"
        : said.TrimEnd('.');

    // The folder made on the share when it isn't there, a level at a time (an error for one that's there is no matter).
    private async Task MakeFolderAsync(CancellationToken ct)
    {
        if (_folder is null) return;
        var parts = _folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var commands = string.Join("; ", parts.Select((_, i) => "mkdir " + Quote(string.Join('/', parts.Take(i + 1)))));
        await _helper.ExecAsync(Args(quiet: false, commands, inFolder: false), null, Stream.Null, ct);
    }

    public async Task WriteAsync(string name, Stream content, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        await ReadyAsync(ct);
        await MakeFolderAsync(ct);
        var fed = new EndStream(content);
        var stdout = new MemoryStream();
        var r = await _helper.ExecAsync(Args(quiet: true, "put - " + Quote(name + ".part")), fed, stdout, ct);
        var failed = Failure(r, Encoding.UTF8.GetString(stdout.ToArray()), "Writing");
        if (failed is not null || !fed.Ended)
        {
            await _helper.ExecAsync(Args(quiet: false, "del " + Quote(name + ".part")), null, Stream.Null, CancellationToken.None);
            throw (Exception?)failed ?? fed.Why();
        }
        stdout.SetLength(0);
        var renamed = await _helper.ExecAsync(Args(quiet: false, $"rename {Quote(name + ".part")} {Quote(name)}"), null, stdout, ct);
        if (Failure(renamed, Encoding.UTF8.GetString(stdout.ToArray()), "Writing") is { } notRenamed) throw notRenamed;
    }

    // smbclient's ls: "  name   A   12345  Fri Nov 27 10:00:05 2026".
    [GeneratedRegex(@"^\s+(?<name>\S+)\s+(?<attr>[A-Za-z]*)\s+(?<size>\d+)\s+(?<date>\w{3}\s+\w{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}\s+\d{4})\s*$")]
    private static partial Regex LsLine();

    internal static List<DestinationFile> ParseLs(string text)
    {
        var files = new List<DestinationFile>();
        foreach (var line in text.Split('\n'))
        {
            var m = LsLine().Match(line.TrimEnd('\r'));
            if (!m.Success || m.Groups["attr"].Value.Contains('D') || !AutoDestinations.Ours(m.Groups["name"].Value)) continue;
            var date = string.Join(' ', m.Groups["date"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            DateTime? modified = DateTime.TryParseExact(date, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
            files.Add(new DestinationFile(m.Groups["name"].Value, long.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture), modified));
        }
        return files;
    }

    private const string HeadScript =
        "for f in \"$@\"; do printf '%s\\t' \"$f\"; smbclient \"$SVC\" -A /tmp/auth -E ${DIR:+-D} ${DIR:+\"$DIR\"} -c \"get \\\"$f\\\" -\" 2>/dev/null " +
        "| head -c 64 | base64 | tr -d '\\n'; printf '\\n'; done";

    public async Task<IReadOnlyList<DestinationFile>> ListAsync(bool headers, CancellationToken ct)
    {
        await ReadyAsync(ct);
        var stdout = new MemoryStream();
        var r = await _helper.ExecAsync(Args(quiet: false, "ls"), null, stdout, ct);
        var text = Encoding.UTF8.GetString(stdout.ToArray());
        if (Failure(r, text, "Listing") is { } failed) throw failed;
        var files = ParseLs(text);
        var gbak = files.Where(f => f.Name.EndsWith(BackupFormat.Extension, StringComparison.Ordinal)).Select(f => f.Name).ToList();
        if (!headers || gbak.Count == 0) return files;
        var heads = new MemoryStream();
        var env = new Dictionary<string, string> { ["SVC"] = _service, ["DIR"] = _folder ?? "" };
        await _helper.ExecAsync(["sh", "-c", HeadScript, "sh", .. gbak], null, heads, ct, env);
        var byName = FolderDestination.ParseList(string.Join('\n', Encoding.UTF8.GetString(heads.ToArray()).Split('\n')
                .Select(l => l.Split('\t') is { Length: 2 } c ? $"{c[0]}\t0\t\t{c[1]}" : "")))
            .ToDictionary(f => f.Name, f => f.Header, StringComparer.Ordinal);
        return files.Select(f => f with { Header = byName.GetValueOrDefault(f.Name) }).ToList();
    }

    public async Task ReadAsync(string name, Stream into, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        await ReadyAsync(ct);
        var r = await _helper.ExecAsync(Args(quiet: true, $"get {Quote(name)} -"), null, into, ct);
        if (Failure(r, null, "Reading " + name) is { } failed) throw failed;
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        await ReadyAsync(ct);
        var stdout = new MemoryStream();
        var r = await _helper.ExecAsync(Args(quiet: false, "del " + Quote(name)), null, stdout, ct);
        if (Failure(r, Encoding.UTF8.GetString(stdout.ToArray()), "Removing " + name) is { } failed) throw failed;
    }

    public ValueTask DisposeAsync() => _helper.DisposeAsync();
}

/// <summary>An FTP server's folder, as the settings give it: ftp://server[:port]/folder, or ftps:// for TLS from the first byte.</summary>
/// <param name="Folder">The folder under the user's own (decoded, segments joined by /); empty for the user's own.</param>
public sealed record FtpTarget(string Scheme, string Host, int? Port, string Folder)
{
    /// <summary>"ftp://nas/backups/gitberth", as the card and the log show it (never with a user or password).</summary>
    public string Display => $"{Scheme}://{Host}{(Port is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : "")}/{Folder}";

    /// <summary>The folder's address as curl takes it: each part percent-encoded, a slash at its end.</summary>
    public string Url => $"{Scheme}://{Host}{(Port is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : "")}/"
        + string.Concat(Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => Uri.EscapeDataString(s) + "/"));
}

/// <summary>
/// An FTP server, through curl in the transfer helper (<see cref="AutoDestinations.TransferImage"/>): TLS when the server
/// offers it (curl --ssl), from the first byte with ftps://. The user name and password go in a netrc file inside the helper
/// (mode 600), never on a command line. A backup streams up from curl's standard input under a temporary name and is
/// renamed once it's whole; only Gitberth's own files are listed, read or removed (their names are its own, so nothing a
/// command is given comes from anywhere else).
/// </summary>
internal sealed partial class FtpDestination : IBackupDestination
{
    private readonly IBackupEngine _engine;
    private readonly DockerCli _docker;
    private readonly FtpTarget _target;
    private readonly string _user;
    private readonly string _password;
    private readonly Helper _helper;
    private bool _ready;

    public FtpDestination(IBackupEngine engine, DockerCli docker, FtpTarget target, string user, string password)
    {
        (_engine, _docker, _target, _user, _password) = (engine, docker, target, user, password);
        _helper = new Helper(engine, _ => AutoDestinations.TransferBody());
    }

    public string Where => _target.Display;

    /// <summary>The netrc line curl reads: the server, then the user and password as quoted strings (backslash and quote escaped).</summary>
    internal static string Netrc(string host, string user, string password) =>
        $"machine {host} login {Quoted(user)} password {Quoted(password)}\n";

    private static string Quoted(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private async Task ReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        if (AutoBackupRules.FtpPasswordProblem(_password) is { } bad) throw new DestinationException(bad);
        await AutoDestinations.EnsureTransferImageAsync(_engine, _docker, ct);
        var r = await _helper.ExecAsync(["sh", "-c", "umask 077; cat > /tmp/netrc"], new MemoryStream(Encoding.UTF8.GetBytes(Netrc(_target.Host, _user, _password))), null, ct);
        if (!r.Ok) throw new DestinationException($"The FTP helper didn't start: {Helper.FirstLine(r.StdErr)}.");
        _ready = true;
    }

    // curl's arguments: quiet but for errors, the netrc file, TLS when the server offers it, then what's asked.
    private static List<string> Curl(params string[] more) =>
        ["curl", "-sS", "--netrc-file", "/tmp/netrc", "--ssl", "--connect-timeout", "20", .. more];

    private DestinationException? Failure(ExecResult r, string what) =>
        r.Ok ? null : new DestinationException($"{what} on {Where} failed: {Explain(r.ExitCode, Helper.FirstLine(r.StdErr))}.");

    /// <summary>curl's exit code as the owner reads it.</summary>
    internal static string Explain(int code, string said) => code switch
    {
        6 => "the server's name doesn't resolve here",
        7 => "the server didn't answer",
        9 or 78 => "the folder isn't there, or the user may not use it",
        25 => "the server refused the file",
        28 => "the server didn't answer in time",
        35 => "TLS with the server failed",
        60 => "the server's TLS certificate isn't one this host trusts",
        64 => "the server wants TLS, and it failed",
        67 => "the server refused the user name or password",
        18 or 56 => "the connection broke",
        _ => CurlPrefix().Replace(said, "").Trim().TrimEnd('.'),
    };

    // "curl: (99) " before what curl says.
    [GeneratedRegex(@"^curl:\s*(\(\d+\)\s*)?")]
    private static partial Regex CurlPrefix();

    public async Task WriteAsync(string name, Stream content, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        await ReadyAsync(ct);
        var fed = new EndStream(content);
        var r = await _helper.ExecAsync(Curl("--ftp-create-dirs", "-T", "-", _target.Url + name + ".part"), fed, null, ct);
        var failed = Failure(r, "Writing");
        if (failed is not null || !fed.Ended)
        {
            await _helper.ExecAsync(Curl("-o", "/dev/null", "-Q", $"-DELE {name}.part", _target.Url), null, null, CancellationToken.None);
            throw (Exception?)failed ?? fed.Why();
        }
        var renamed = await _helper.ExecAsync(Curl("-o", "/dev/null", "-Q", $"-RNFR {name}.part", "-Q", $"-RNTO {name}", _target.Url), null, null, ct);
        if (Failure(renamed, "Writing") is { } notRenamed) throw notRenamed;
    }

    // A listing's line: Unix's ("-rw-r--r-- 1 ftp ftp 16439 Nov 28 10:20 name") or Windows' ("11-28-26 10:20AM 16439 name").
    [GeneratedRegex(@"^(?<type>[-dlbcps])[rwxsStT-]{9}\S*\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+\w{3}\s+\d{1,2}\s+(?:\d{1,2}:\d{2}|\d{4})\s+(?<name>.+)$")]
    private static partial Regex UnixLine();

    [GeneratedRegex(@"^\d{2}-\d{2}-\d{2,4}\s+\d{1,2}:\d{2}[AP]M\s+(?<size><DIR>|\d+)\s+(?<name>.+)$")]
    private static partial Regex WindowsLine();

    internal static List<DestinationFile> ParseList(string text)
    {
        var files = new List<DestinationFile>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = UnixLine().Match(line);
            if (m.Success && m.Groups["type"].Value == "-" && AutoDestinations.Ours(m.Groups["name"].Value))
            {
                files.Add(new DestinationFile(m.Groups["name"].Value, long.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture), null));
                continue;
            }
            m = WindowsLine().Match(line);
            if (m.Success && m.Groups["size"].Value != "<DIR>" && AutoDestinations.Ours(m.Groups["name"].Value))
                files.Add(new DestinationFile(m.Groups["name"].Value, long.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture), null));
        }
        return files;
    }

    private const string HeadScript =
        "for f in \"$@\"; do printf '%s\\t' \"$f\"; curl -sS --netrc-file /tmp/netrc --ssl --connect-timeout 20 -r 0-63 \"$URL$f\" 2>/dev/null " +
        "| head -c 64 | base64 | tr -d '\\n'; printf '\\n'; done";

    public async Task<IReadOnlyList<DestinationFile>> ListAsync(bool headers, CancellationToken ct)
    {
        await ReadyAsync(ct);
        var stdout = new MemoryStream();
        var r = await _helper.ExecAsync(Curl(_target.Url), null, stdout, ct);
        // A folder that isn't there yet holds nothing of Gitberth's.
        if (r.ExitCode is 9 or 78) return [];
        if (Failure(r, "Listing") is { } failed) throw failed;
        var files = ParseList(Encoding.UTF8.GetString(stdout.ToArray()));
        var gbak = files.Where(f => f.Name.EndsWith(BackupFormat.Extension, StringComparison.Ordinal)).Select(f => f.Name).ToList();
        if (!headers || gbak.Count == 0) return files;
        var heads = new MemoryStream();
        await _helper.ExecAsync(["sh", "-c", HeadScript, "sh", .. gbak], null, heads, ct, new Dictionary<string, string> { ["URL"] = _target.Url });
        var byName = FolderDestination.ParseList(string.Join('\n', Encoding.UTF8.GetString(heads.ToArray()).Split('\n')
                .Select(l => l.Split('\t') is { Length: 2 } c ? $"{c[0]}\t0\t\t{c[1]}" : "")))
            .ToDictionary(f => f.Name, f => f.Header, StringComparer.Ordinal);
        return files.Select(f => f with { Header = byName.GetValueOrDefault(f.Name) }).ToList();
    }

    public async Task ReadAsync(string name, Stream into, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        await ReadyAsync(ct);
        var r = await _helper.ExecAsync(Curl(_target.Url + name), null, into, ct);
        if (Failure(r, "Reading " + name) is { } failed) throw failed;
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        await ReadyAsync(ct);
        var r = await _helper.ExecAsync(Curl("-o", "/dev/null", "-Q", $"-DELE {name}", _target.Url), null, null, ct);
        if (Failure(r, "Removing " + name) is { } failed) throw failed;
    }

    public ValueTask DisposeAsync() => _helper.DisposeAsync();
}
