using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gitberth.Railpack;

/// <summary>
/// What railpack prepare's info file (railpack-info.json) says, read into <see cref="RailpackInfo"/>, and what a prepare
/// that failed ends with (<see cref="Classify"/>). Pure. Railpack 0.43.0 writes the logs' keys capitalised (Level, Msg,
/// DocsPath); lower-case ones are read too. A failed prepare's info has its logs only.
/// </summary>
public static partial class RailpackInfoParser
{
    /// <summary>The info file as a JSON object; null when it isn't one.</summary>
    public static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What prepare found: its info file, with what only the plan says (deploy.startCommand, secrets[]). A failed prepare's
    /// info has no provider list: its provider is read from the "Detected Node" log, when there's one.
    /// </summary>
    public static RailpackInfo Parse(JsonObject info, JsonObject? plan)
    {
        var providers = Strings(Get(info, "detectedProviders") as JsonArray).Select(RailpackProviders.Normalize).Where(p => p.Length > 0).Distinct().ToList();
        var logs = Logs(info);
        var provider = providers.FirstOrDefault() ?? DetectedProvider(logs) ?? "";
        var packages = new List<RailpackPackage>();
        if (Get(info, "resolvedPackages") is JsonObject resolved)
            foreach (var (key, node) in resolved)
                if (node is JsonObject p)
                    packages.Add(new RailpackPackage(Text(p, "name") ?? key, Text(p, "requestedVersion"), Text(p, "resolvedVersion") ?? "", Text(p, "source")));
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Get(info, "metadata") is JsonObject meta)
            foreach (var (key, node) in meta)
                if (node is JsonValue v) metadata[key] = v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
        return new RailpackInfo(Text(info, "railpackVersion") ?? RailpackRelease.Version, provider, providers.Count > 0 ? providers : provider.Length > 0 ? [provider] : [],
            packages, metadata, logs, plan is null ? null : RailpackPlanFile.StartCommand(plan), plan is null ? [] : RailpackPlanFile.Secrets(plan));
    }

    /// <summary>The info file's logs, in order: level lower-cased, empty DocsPath as none.</summary>
    public static IReadOnlyList<RailpackLog> Logs(JsonObject info) =>
        Get(info, "logs") is JsonArray logs
            ? logs.OfType<JsonObject>()
                .Select(l => new RailpackLog((Text(l, "Level") ?? "").Trim().ToLowerInvariant(), Text(l, "Msg") ?? Text(l, "Message") ?? "",
                    Text(l, "DocsPath") is { Length: > 0 } docs ? docs : null))
                .ToList()
            : [];

    /// <summary>
    /// What a prepare that exited 1 ends with, from its logs (the first error decides): nothing to build, no start command,
    /// a broken railpack.json (its error is the JSON reader's, after a warning "Failed to read config file"), the network
    /// (the tools' versions couldn't be looked up), or anything else, in Railpack's own words.
    /// </summary>
    /// <param name="output">What prepare printed, for when its info file has no error.</param>
    public static (RailpackFailure Failure, string Why) Classify(IReadOnlyList<RailpackLog> logs, string output)
    {
        var error = logs.FirstOrDefault(l => l.Level == "error" && l.Message.Trim().Length > 0)?.Message;
        if (error is null) return (LooksOffline(output) ? RailpackFailure.Offline : RailpackFailure.Other, LastLine(output) ?? "");
        if (error.Contains("could not determine how to build", StringComparison.OrdinalIgnoreCase)) return (RailpackFailure.NothingFound, FirstLine(error));
        if (error.Contains("No start command", StringComparison.OrdinalIgnoreCase)) return (RailpackFailure.NoStartCommand, FirstLine(error));
        if (logs.Any(l => l.Message.StartsWith("Failed to read config file", StringComparison.OrdinalIgnoreCase))
            || error.StartsWith("hujson:", StringComparison.OrdinalIgnoreCase) || error.Contains("railpack.json", StringComparison.OrdinalIgnoreCase))
            return (RailpackFailure.BadConfig, ConfigWords(error));
        if (LooksOffline(error)) return (RailpackFailure.Offline, NetworkWords(error));
        return (RailpackFailure.Other, FirstLine(error));
    }

    /// <summary>
    /// Whether Railpack's words are a network failure, which a version lookup reports as exit 1 and a mise download as 75:
    /// Go's and mise's (musl: "Try again", not "Temporary failure in name resolution") ways of saying so.
    /// </summary>
    public static bool LooksOffline(string text) => Network().IsMatch(text);

    /// <summary>The words that say what the network did: the most telling line of mise's or Go's, without its logger's prefix.</summary>
    public static string NetworkWords(string text)
    {
        var lines = text.Split('\n').Select(l => Unprefixed(l.Trim())).Where(l => l.Length > 0).ToList();
        var line = lines.LastOrDefault(l => Network().IsMatch(l) && !l.StartsWith("failed to get latest version", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault() ?? "";
        // Go's "…: Get "https://…": dial tcp 127.0.0.1:443: connect: connection refused" ends with what happened.
        if (line.IndexOf("dial tcp", StringComparison.Ordinal) is > 0 and var dial) line = line[dial..];
        return line.Length > 200 ? line[..200].TrimEnd() + "…" : line;
    }

    /// <summary>The provider a log names: "Detected Node" → node; null when none does.</summary>
    internal static string? DetectedProvider(IReadOnlyList<RailpackLog> logs) =>
        logs.Where(l => l.Level == "info").Select(l => DetectedLine().Match(l.Message.Trim())).Where(m => m.Success)
            .Select(m => RailpackProviders.Normalize(m.Groups[1].Value)).FirstOrDefault(p => p.Length > 0);

    // A broken railpack.json's words, without the name of the JSON reader in front of them.
    private static string ConfigWords(string error)
    {
        var line = FirstLine(error);
        return line.StartsWith("hujson:", StringComparison.OrdinalIgnoreCase) ? line["hujson:".Length..].Trim() : line;
    }

    // " ERRO failed to …", "mise ERROR dns error", "mise WARN …": the words after the logger's level.
    private static string Unprefixed(string line)
    {
        foreach (var prefix in new[] { "mise ERROR", "mise WARN", "ERRO", "WARN" })
            if (line.StartsWith(prefix + " ", StringComparison.Ordinal)) return line[(prefix.Length + 1)..].Trim();
        return line;
    }

    private static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    private static string? LastLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    // A property by its name, or by any casing of it.
    private static JsonNode? Get(JsonObject o, string name)
    {
        if (o.TryGetPropertyValue(name, out var exact)) return exact;
        foreach (var (key, value) in o)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private static string? Text(JsonObject o, string name) => Get(o, name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static IEnumerable<string> Strings(JsonArray? array) =>
        array is null ? [] : array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>();

    [GeneratedRegex(@"(?i)timeout|timed out|connection (refused|reset)|no such host|temporary failure|tls handshake|\b50[234]\b|\b429\b|dns error|error sending request|failed to lookup address|client error \(connect\)")]
    private static partial Regex Network();

    [GeneratedRegex(@"^Detected ([A-Za-z0-9+.#-]+)$")]
    private static partial Regex DetectedLine();
}
