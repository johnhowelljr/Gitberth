using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gitberth.Images;

/// <summary>One page of a repository's tags on Docker Hub, as read: the tags and Docker Hub's count of them all.</summary>
internal sealed record HubPage(IReadOnlyList<TagFacts> Tags, int Count, LookupProblem Problem = LookupProblem.None, int Status = 0, DateTimeOffset? RetryAt = null);

/// <summary>A repository's tags on Docker Hub, before ranking: the pages read, the count of all, and whether older pages exist.</summary>
internal sealed record HubTags(IReadOnlyList<TagFacts> Tags, int Total, bool More, LookupProblem Problem = LookupProblem.None, int Status = 0, DateTime? RetryAt = null);

/// <summary>
/// Docker Hub's own APIs, anonymously: its search (v4: badges, pulls, dates and architectures; else the basic v1 search,
/// official images marked only) and a repository's tags (digests, push dates, platforms). Answers are kept ten minutes; a
/// host that asked Gitberth to wait isn't asked again until then, nor one that couldn't be reached for a minute. It never
/// signs in, never follows a redirect, and never puts a response's body in a message or the log.
/// </summary>
internal sealed partial class DockerHubApi(IHttpClientFactory http, TimeProvider time, HostGates gates, ILogger logger)
{
    public const string HubHost = "hub.docker.com";
    public const string IndexHost = "index.docker.io";

    /// <summary>Results a search shows.</summary>
    public const int MaxResults = 25;

    /// <summary>Tags a page holds (Docker Hub's most), and the most pages one listing reads.</summary>
    public const int PageSize = 100;
    public const int MaxPages = 10;

    /// <summary>Platforms read per tag.</summary>
    public const int MaxImages = 64;

    /// <summary>The most a body may hold, decompressed: a search's, a page of tags' (nginx's run to about 474 KB).</summary>
    public const int MaxSearch = 2 << 20;
    public const int MaxTagPage = 4 << 20;

    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TagsTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long an answer is kept; a basic search's less (the full one may be back), a "no such repository" two minutes.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan KeepBasic = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan KeepNotFound = TimeSpan.FromMinutes(2);

    private readonly TimedCache<HubSearch> _searches = new(time, 200);
    private readonly TimedCache<HubPage> _pages = new(time, 500);

    /// <summary>
    /// Docker Hub's search for <paramref name="query"/> (2 to 100 characters, trimmed), in its order: the full search, else
    /// the basic one; a problem comes back in the answer. <paramref name="hostArchitecture"/> (amd64, arm64) marks the
    /// results without an image for this host.
    /// </summary>
    public async Task<HubSearch> SearchAsync(string query, string hostArchitecture, CancellationToken ct)
    {
        var key = query.ToLowerInvariant();
        if (_searches.TryGet(key, out var kept)) return kept with { Query = query };
        // Asked to wait: no request, and not the basic search instead.
        if (gates.HeldUntil(HubHost) is { } held) return Problem(query, new Answer(LookupProblem.RateLimited, RetryAt: held));
        var full = gates.IsDown(HubHost) ? new Answer(LookupProblem.Unreachable)
            : await GetAsync(HubHost, new Uri($"https://{HubHost}/api/search/v4?query={Uri.EscapeDataString(query)}&from=0&size={MaxResults}&type=image"),
                MaxSearch, SearchTimeout, ct, ct);
        if (full.Problem == LookupProblem.None)
        {
            if (ParseFull(full.Json!, query, hostArchitecture) is { } found)
            {
                _searches.Set(key, found, Keep);
                return found;
            }
            full = full with { Problem = LookupProblem.Failed };
        }
        // A search has nothing to be missing: a 404 is the search not answering properly.
        if (full.Problem == LookupProblem.NotFound) full = full with { Problem = LookupProblem.Failed };
        if (full.Problem == LookupProblem.RateLimited) return Problem(query, full);
        if (gates.HeldUntil(IndexHost) is null && !gates.IsDown(IndexHost))
        {
            var basic = await GetAsync(IndexHost, new Uri($"https://{IndexHost}/v1/search?q={Uri.EscapeDataString(query)}&n={MaxResults}"), MaxSearch, SearchTimeout, ct, ct);
            if (basic.Problem == LookupProblem.None && ParseBasic(basic.Json!, query) is { } answer)
            {
                _searches.Set(key, answer, KeepBasic);
                return answer;
            }
        }
        return Problem(query, full);
    }

    /// <summary>
    /// The repository's tags, newest push first: page 1, then the next pages up to <paramref name="pages"/> (and Docker Hub's
    /// count) at once; with <paramref name="name"/> (Docker Hub's substring filter) page 1 only. A later page that fails is
    /// left out, and More says so. Platforms and sizes are read for <paramref name="host"/>.
    /// </summary>
    public async Task<HubTags> TagsAsync(ImageReference reference, int pages, string? name, HostPlatform host, CancellationToken ct)
    {
        var parts = reference.Repository.Split('/');
        if (parts.Length != 2) return new HubTags([], 0, false, LookupProblem.NotFound, 404);
        pages = name is null ? Math.Clamp(pages, 1, MaxPages) : 1;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TagsTimeout);
        var first = await PageAsync(parts[0], parts[1], 1, name, host, budget.Token, ct);
        if (first.Problem != LookupProblem.None) return new HubTags([], 0, false, first.Problem, first.Status, first.RetryAt?.UtcDateTime);
        var available = Math.Max(1, (first.Count + PageSize - 1) / PageSize);
        var last = Math.Min(pages, available);
        var rest = await Task.WhenAll(Enumerable.Range(2, Math.Max(0, last - 1)).Select(p => PageAsync(parts[0], parts[1], p, name, host, budget.Token, ct)));
        var tags = first.Tags.Concat(rest.Where(p => p.Problem == LookupProblem.None).SelectMany(p => p.Tags)).ToList();
        return new HubTags(tags, Math.Max(first.Count, tags.Count), available > last || rest.Any(p => p.Problem != LookupProblem.None));
    }

    private async Task<HubPage> PageAsync(string ns, string repo, int page, string? name, HostPlatform host, CancellationToken budget, CancellationToken caller)
    {
        var key = $"{ns}/{repo}|{page.ToString(CultureInfo.InvariantCulture)}|{name}";
        if (_pages.TryGet(key, out var kept)) return kept;
        if (gates.HeldUntil(HubHost) is { } held) return new HubPage([], 0, LookupProblem.RateLimited, 0, held);
        if (gates.IsDown(HubHost)) return new HubPage([], 0, LookupProblem.Unreachable);
        var url = $"https://{HubHost}/v2/namespaces/{Uri.EscapeDataString(ns)}/repositories/{Uri.EscapeDataString(repo)}/tags" +
            $"?page={page.ToString(CultureInfo.InvariantCulture)}&page_size={PageSize}&ordering=last_updated" + (name is null ? "" : "&name=" + Uri.EscapeDataString(name));
        var answer = await GetAsync(HubHost, new Uri(url), MaxTagPage, PageTimeout, budget, caller);
        var read = answer.Problem != LookupProblem.None ? new HubPage([], 0, answer.Problem, answer.Status, answer.RetryAt)
            : ParseTags(answer.Json!, host) ?? new HubPage([], 0, LookupProblem.Failed, answer.Status);
        if (read.Problem == LookupProblem.None) _pages.Set(key, read, Keep);
        else if (read.Problem == LookupProblem.NotFound) _pages.Set(key, read, KeepNotFound);
        return read;
    }

    private sealed record Answer(LookupProblem Problem, int Status = 0, JsonNode? Json = null, DateTimeOffset? RetryAt = null);

    // One GET, anonymous, as Gitberth: its answer read within the size cap, or why not. A host that couldn't be reached
    // (DNS, connect, TLS, a timeout) is down for a minute; one that answered 429 is held as it says.
    private async Task<Answer> GetAsync(string host, Uri url, int max, TimeSpan timeout, CancellationToken budget, CancellationToken caller)
    {
        IDisposable slot;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(budget))
        {
            // Its turn is waited for no longer than the request itself may take (nor than the listing's time).
            wait.CancelAfter(timeout);
            try
            {
                slot = await gates.SlotAsync(host, wait.Token);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                // The time ran out while it waited its turn: nothing was asked, so the host isn't taken for down.
                return new Answer(LookupProblem.Unreachable);
            }
        }
        using var held = slot;
        // What the requests ahead of it heard while it waited: a host that asked Gitberth to wait, or couldn't be reached,
        // isn't asked now.
        if (gates.HeldUntil(host) is { } until) return new Answer(LookupProblem.RateLimited, RetryAt: until);
        if (gates.IsDown(host)) return new Answer(LookupProblem.Unreachable);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(budget);
        timer.CancelAfter(timeout);
        try
        {
            using var client = http.CreateClient(ImageService.HubClient);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(HostGates.UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token);
            gates.Up(host);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = gates.Hold(host, HostGates.RetryAt(response.Headers, gates.Now));
                logger.LogInformation("{Host} asked Gitberth to wait until {Until:u}", host, wait);
                return new Answer(LookupProblem.RateLimited, status, RetryAt: wait);
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return new Answer(LookupProblem.NotFound, status);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("{Host} answered {Status}", host, status);
                return new Answer(LookupProblem.Failed, status);
            }
            if (HostGates.LowUntil(response.Headers, gates.Now) is { } low) gates.Hold(host, low);
            if (await HostGates.ReadAsync(response.Content, max, timer.Token) is not { } body)
            {
                logger.LogDebug("{Host}'s answer was too big or not readable", host);
                return new Answer(LookupProblem.Failed, status);
            }
            try
            {
                return JsonNode.Parse(body) is { } json ? new Answer(LookupProblem.None, status, json) : new Answer(LookupProblem.Failed, status);
            }
            catch (JsonException)
            {
                return new Answer(LookupProblem.Failed, status);
            }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException or IOException)
        {
            // The listing's own time running out (pages cut short) isn't the host failing: only its own errors and its
            // request's timeout leave it alone for a minute.
            if (ex is not OperationCanceledException || !budget.IsCancellationRequested) gates.Down(host);
            logger.LogDebug("{Host} couldn't be reached ({Error})", host, ex.GetType().Name);
            return new Answer(LookupProblem.Unreachable);
        }
    }

    private static HubSearch Problem(string query, Answer answer) =>
        new(query, [], 0, false, answer.Problem, LookupText.Search(answer.Problem, answer.Status), answer.RetryAt?.UtcDateTime);

    // ---- reading ----

    /// <summary>The full search's answer (v4); null when it isn't one.</summary>
    internal static HubSearch? ParseFull(JsonNode json, string query, string hostArchitecture)
    {
        if (json is not JsonObject root || root["results"] is not JsonArray results) return null;
        var images = new List<HubImage>();
        // The results asked for (25) and no more, whatever else the answer holds.
        foreach (var item in results.Take(MaxResults).OfType<JsonObject>())
        {
            // Archived repositories, and Docker Hardened Images (dhi.io, which needs a sign-in), aren't offered.
            if (Bool(item["archived"]) == true || Str(item["type"]) is { Length: > 0 } type && type != "image" || Str(item["badge"]) == "hardened") continue;
            if (NameOf(Str(item["id"])) is not { } name) continue;
            var architectures = (item["architectures"] as JsonArray ?? []).OfType<JsonObject>().Select(a => Str(a["name"])?.Trim().ToLowerInvariant())
                .OfType<string>().Where(a => a.Length is > 0 and <= 32 && a != "unknown").Distinct(StringComparer.Ordinal).Take(32).ToList();
            var (pulls, pullsText) = Pulls(item["raw_pull_count"], item["pull_count"]);
            images.Add(new HubImage(name, Clean(Str(item["short_description"])), BadgeOf(Str(item["badge"])), Int(item["star_count"]), pulls, pullsText,
                Date(item["updated_at"]), architectures, architectures.Count == 0 || architectures.Contains(hostArchitecture)));
        }
        return new HubSearch(query, images, Count(root["total"]), false);
    }

    /// <summary>The basic search's answer (v1): official images marked, no dates or architectures; null when it isn't one.</summary>
    internal static HubSearch? ParseBasic(JsonNode json, string query)
    {
        if (json is not JsonObject root || root["results"] is not JsonArray results) return null;
        var images = new List<HubImage>();
        foreach (var item in results.Take(MaxResults).OfType<JsonObject>())
        {
            if (NameOf(Str(item["name"])) is not { } name) continue;
            images.Add(new HubImage(name, Clean(Str(item["description"])), Bool(item["is_official"]) == true ? HubBadge.Official : HubBadge.None,
                Int(item["star_count"]), Long(item["pull_count"]), null, null, [], true));
        }
        return new HubSearch(query, images, Count(root["num_results"]), true);
    }

    /// <summary>A page of the tags API; null when it isn't one. Attestations (os unknown) and what isn't an image are left out.</summary>
    internal static HubPage? ParseTags(JsonNode json, HostPlatform host)
    {
        if (json is not JsonObject root || root["results"] is not JsonArray results) return null;
        var tags = new List<TagFacts>();
        // A page holds 100 at most: an answer with more is read no further (what's kept and ranked stays bounded).
        foreach (var item in results.Take(PageSize).OfType<JsonObject>())
        {
            if (Str(item["name"]) is not { } name || !TagRanking.IsTagName(name)) continue;
            // Docker Hub also keeps plugins and other artifacts: only images are tags to run.
            if (Str(item["content_type"]) is { Length: > 0 } type && type != "image") continue;
            var digest = Str(item["digest"]) is { } d && DigestPattern().IsMatch(d) ? d : null;
            var images = (item["images"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxImages)
                .Select(i => (Os: Lower(i["os"]), Arch: Lower(i["architecture"]), Variant: Lower(i["variant"]), Size: Long(i["size"])))
                .Where(i => Token(i.Os) && i.Os != "unknown" && Token(i.Arch) && i.Arch != "unknown" && (i.Variant.Length == 0 || Token(i.Variant)))
                .Select(i => (Platform: i.Variant.Length > 0 ? $"{i.Os}/{i.Arch}/{i.Variant}" : $"{i.Os}/{i.Arch}", i.Variant, i.Size))
                .ToList();
            var platforms = images.Select(i => i.Platform).Distinct(StringComparer.Ordinal).ToList();
            var size = images.Count == 0 ? Long(item["full_size"])
                : images.Where(i => TagRanking.Fits(i.Platform, host)).OrderBy(i => i.Variant == (host.Variant ?? "") ? 0 : 1).Select(i => i.Size).FirstOrDefault();
            tags.Add(new TagFacts(name, digest, Date(item["tag_last_pushed"]) ?? Date(item["last_updated"]), size, platforms.Count == 0 ? null : platforms));
        }
        return new HubPage(tags, Count(root["count"]));
    }

    /// <summary>A repository's name as it's pulled (nginx, bitnami/nginx) from Docker Hub's id (library/nginx); null for one Docker wouldn't take.</summary>
    internal static string? NameOf(string? id)
    {
        var name = id?.Trim() ?? "";
        if (name.StartsWith("library/", StringComparison.Ordinal)) name = name["library/".Length..];
        return name.Count(c => c == '/') <= 1 && ImageReference.Parse(name) is { IsDockerHub: true, Tag: null, Digest: null } parsed && parsed.Name == name ? name : null;
    }

    /// <summary>
    /// Text from Docker Hub as plain text on one line: control and bidi-override characters taken out, spaces run together,
    /// at most <paramref name="max"/> characters (the last an ellipsis when it was longer).
    /// </summary>
    internal static string Clean(string? text, int max = 300)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var c in text)
        {
            if (c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏' or '؜') continue;
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
            if (sb.Length > max) break;
        }
        if (sb.Length <= max) return sb.ToString();
        var cut = max - 1;
        if (char.IsHighSurrogate(sb[cut - 1])) cut--;
        return sb.ToString(0, cut).TrimEnd() + "…";
    }

    private static HubBadge BadgeOf(string? badge) => badge switch
    {
        "official" => HubBadge.Official,
        "verified_publisher" => HubBadge.VerifiedPublisher,
        "open_source" => HubBadge.SponsoredOss,
        _ => HubBadge.None,
    };

    // The full search gives pull_count as Docker Hub's words ("1B+", or "842" when small) and raw_pull_count exactly;
    // the basic one pull_count as a number.
    private static (long? Pulls, string? Text) Pulls(JsonNode? raw, JsonNode? count)
    {
        var exact = Long(raw);
        string? text = null;
        if (count is JsonValue value && value.TryGetValue<string>(out var words))
        {
            text = Clean(words, 16);
            if (exact is null && long.TryParse(words.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)) exact = number;
        }
        else exact ??= Long(count);
        return (exact, string.IsNullOrEmpty(text) ? null : text);
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static string Lower(JsonNode? node) => Str(node)?.Trim().ToLowerInvariant() ?? "";

    // A platform's part as registries write one (linux, arm64, v8): a short lower-case word, or it isn't taken.
    private static bool Token(string text) =>
        text.Length is > 0 and <= 32 && text.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');

    private static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : null;

    private static long? Long(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var l)) return l >= 0 ? l : null;
        return value.TryGetValue<double>(out var d) && d >= 0 && d < long.MaxValue ? (long)d : null;
    }

    private static int Int(JsonNode? node) => (int)Math.Min(Long(node) ?? 0, int.MaxValue);

    private static int Count(JsonNode? node) => Int(node);

    private static DateTime? Date(JsonNode? node) =>
        Str(node) is { Length: > 0 } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.UtcDateTime : null;

    [GeneratedRegex("^[a-z0-9]+(?:[.+_-][a-z0-9]+)*:[a-zA-Z0-9=_-]{32,256}$")]
    private static partial Regex DigestPattern();
}

/// <summary>
/// The sentences a search or a tag list's problem comes with: one sentence, the registry's name and at most a status
/// code, never a response's body, a sign-in or a token. The UI shows them as they are, and counts down to RetryAt itself.
/// </summary>
internal static class LookupText
{
    public static string Search(LookupProblem problem, int status) => problem switch
    {
        LookupProblem.Unreachable => "Docker Hub can't be reached from this host, so there's no search. Type the image's full name, like nginx:1.27 or ghcr.io/owner/app:1.4.",
        LookupProblem.RateLimited => "Docker Hub asked Gitberth to wait before searching again. Type the image's full name meanwhile.",
        _ => $"Docker Hub's search didn't answer properly{Code(status)}. Type the image's full name meanwhile.",
    };

    /// <param name="registry">"Docker Hub" or the registry's host.</param>
    /// <param name="image">As people write it: nginx, ghcr.io/o/r.</param>
    public static string Tags(LookupProblem problem, string registry, string image, int status) => problem switch
    {
        LookupProblem.NotFound when registry == "Docker Hub" =>
            $"Docker Hub has no public image called {image}: check the name, or add a Docker Hub sign-in in Settings › Registries if it's private.",
        LookupProblem.NotFound => $"{registry} has no image called {image}: check the name.",
        LookupProblem.NeedsSignIn => $"{registry} wants a sign-in to list {image}'s tags: add one in Settings › Registries.",
        LookupProblem.Unreachable => $"{registry} can't be reached from this host, so its tags can't be listed.",
        LookupProblem.RateLimited => $"{registry} asked Gitberth to wait before listing tags again.",
        _ => $"{registry} didn't list {image}'s tags{Code(status)}.",
    };

    // The status code, when it says something: not for an answer that came but couldn't be read.
    private static string Code(int status) => status is >= 100 and (< 200 or >= 300) ? $" (HTTP {status.ToString(CultureInfo.InvariantCulture)})" : "";
}
