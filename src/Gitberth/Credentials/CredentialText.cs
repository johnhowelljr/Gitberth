using System.Globalization;

namespace Gitberth.Credentials;

/// <summary>
/// Every sentence the token watch says, in one place: the banners' titles and lines (which notices use too), the top
/// bar's pill, get_health's problem lines, the audit entries, the snackbars of Check the token, and what Gitberth was
/// doing when a service refused a token. Never the token: only whose it is (the login, the user name, the name).
/// </summary>
internal static class CredentialText
{
    // Where each service makes a new token; GitLab's is on Gitberth's own GitLab (GitLabTokens).
    public const string GitHubTokens = "https://github.com/settings/personal-access-tokens";
    public const string CloudflareTokens = "https://dash.cloudflare.com/profile/api-tokens";

    public static string GitLabTokens(string instance) => instance.TrimEnd('/') + "/-/user_settings/personal_access_tokens";

    /// <summary>Where it's dealt with: its section of Settings.</summary>
    public static string Href(CredentialKind kind) => kind switch
    {
        CredentialKind.GitHub => "settings#github",
        CredentialKind.GitLab => "settings#gitlab",
        CredentialKind.Cloudflare => "settings#domains",
        CredentialKind.Registry => "settings#registries",
        _ => "settings#agents",
    };

    /// <summary>That section, as sentences name it.</summary>
    public static string Section(CredentialKind kind) => kind switch
    {
        CredentialKind.GitHub => "Settings › GitHub access",
        CredentialKind.GitLab => "Settings › GitLab",
        CredentialKind.Cloudflare => "Settings › Domains",
        CredentialKind.Registry => "Settings › Registries",
        _ => "Settings › AI agents and API",
    };

    /// <summary>"Dec 1, 2026": a day as UTC has it, which is how GitHub, GitLab and Cloudflare give an expiry.</summary>
    public static string Date(DateTimeOffset at) => at.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    /// <summary>"14:05 UTC".</summary>
    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>"in 3 weeks", "in 6 days", "tomorrow", "today": how far off an expiry is, in whole days rounded up.</summary>
    public static string When(DateTimeOffset expires, DateTimeOffset now)
    {
        var days = ExpiryRule.DaysLeft(expires, now);
        if (days <= 1) return expires.UtcDateTime.Date == now.UtcDateTime.Date ? "today" : "tomorrow";
        return days >= 14 && days % 7 == 0 ? $"in {days / 7} weeks" : $"in {days} days";
    }

    // " for octo": whose token it is, when that's known.
    private static string ForWho(CredentialSubject s) => s.Who is { Length: > 0 } who ? $" for {who}" : "";

    /// <summary>A registry as people name it: Docker Hub, or its host.</summary>
    public static string RegistryName(string? host) => host is null or "docker.io" ? "Docker Hub" : host;

    // ---- Banners and notices ----

    public static string ExpiringTitle(CredentialSubject s, DateTimeOffset now) =>
        $"{Thing(s)} expires {When(s.ExpiresAt!.Value, now)}";

    public static string ExpiredTitle(CredentialSubject s) => $"{Thing(s)} has expired";

    public static string RefusedTitle(CredentialSubject s) => s.Kind switch
    {
        CredentialKind.GitHub => "GitHub refused the token",
        CredentialKind.GitLab => "GitLab refused the token",
        CredentialKind.Cloudflare => "Cloudflare refused Gitberth's API token",
        _ => $"{RegistryName(s.Place)} refused the sign-in",
    };

    // The title's subject: "The GitHub token", "The API token claude-code".
    private static string Thing(CredentialSubject s) => s.Kind switch
    {
        CredentialKind.GitHub => "The GitHub token",
        CredentialKind.GitLab => "The GitLab token",
        CredentialKind.Cloudflare => "The Cloudflare API token",
        CredentialKind.Registry => $"The sign-in for {RegistryName(s.Place)}",
        _ => $"The API token {s.Who}",
    };

    public static string ExpiringText(CredentialSubject s)
    {
        var on = Date(s.ExpiresAt!.Value);
        return s.Kind switch
        {
            CredentialKind.GitHub => $"GitHub stops taking the token{ForWho(s)} on {on}. Then private repos stop deploying, and commit statuses, comments, "
                                     + "webhooks and repo variables stop. Make a new token on GitHub and save it in Settings › GitHub access.",
            CredentialKind.GitLab => $"GitLab stops taking the token{ForWho(s)} on {on}. Then private projects stop deploying, and GitLab hears nothing from "
                                     + "Gitberth. Make a new token on GitLab and save it in Settings › GitLab.",
            CredentialKind.Cloudflare => $"Cloudflare stops taking Gitberth's API token on {on}. Then Gitberth can't add, move or remove domains or change their "
                                         + "routes; the routes already in your tunnel keep working. Make a new token in Cloudflare and save it in Settings › Domains.",
            _ => $"Gitberth stops taking it on {on}. {LastUse(s)}: whatever uses it can't reach Gitberth after that. Make a new token in "
                 + "Settings › AI agents and API and give it to that agent.",
        };
    }

    public static string ExpiredText(CredentialSubject s)
    {
        var on = Date(s.ExpiresAt!.Value);
        return s.Kind switch
        {
            CredentialKind.GitHub => $"GitHub stopped taking the token{ForWho(s)} on {on}: private repos don't deploy, and commit statuses, comments, "
                                     + "webhooks and repo variables have stopped. Make a new token on GitHub and save it in Settings › GitHub access.",
            CredentialKind.GitLab => $"GitLab stopped taking the token{ForWho(s)} on {on}: private projects don't deploy, and GitLab hears nothing from "
                                     + "Gitberth. Make a new token on GitLab and save it in Settings › GitLab.",
            CredentialKind.Cloudflare => $"Cloudflare stopped taking Gitberth's API token on {on}: Gitberth can't add, move or remove domains or change their "
                                         + "routes, though the routes already in your tunnel keep working. Make a new token in Cloudflare and save it in Settings › Domains.",
            _ => $"Gitberth stopped taking it on {on}. {LastUse(s)}: whatever used it can't reach Gitberth any more. Make a new token in "
                 + "Settings › AI agents and API, give it to that agent, and revoke this one.",
        };
    }

    // "It was last used on Nov 17 from 192.0.2.10": what tells an API token's owner which agent it is.
    private static string LastUse(CredentialSubject s) =>
        s.LastUsedAt is not { } used ? "It hasn't been used for a while"
        : $"It was last used on {used.ToString("MMM d", CultureInfo.InvariantCulture)}{(s.LastUsedIp is { Length: > 0 } ip ? $" from {ip}" : "")}";

    /// <summary>Why the service refused it, as the sentence after the colon: its own words when it gave them.</summary>
    private static string Why(CredentialSubject s, CredentialFact fact) => s.Kind switch
    {
        CredentialKind.GitLab when fact.RefusedWords is { Length: > 0 } words => words,
        CredentialKind.Cloudflare => "it has expired, or was disabled or deleted.",
        CredentialKind.Registry => "its password or token has expired or was revoked.",
        _ => "it has expired or was revoked.",
    };

    // How Gitberth made sure: checked a token, signed in again to a registry.
    private static string Again(CredentialKind kind) => kind == CredentialKind.Registry ? "Gitberth signed in with it" : "Gitberth checked it";

    public static string RefusedText(CredentialSubject s, CredentialFact fact)
    {
        var at = fact.RefusedAt ?? DateTimeOffset.UtcNow;
        var lead = fact.RefusedDoing is { Length: > 0 } doing ? $"at {Time(at)} {doing}, and again when {Again(s.Kind)}" : $"when {Again(s.Kind)} at {Time(at)}";
        var why = Why(s, fact);
        return s.Kind switch
        {
            CredentialKind.GitHub => $"GitHub refused the token{ForWho(s)} {lead}: {why} Private repos don't deploy, and commit statuses, comments, webhooks "
                                     + "and repo variables have stopped, until you save a new token in Settings › GitHub access.",
            CredentialKind.GitLab => $"GitLab refused the token{ForWho(s)} {lead}: {why} Private projects don't deploy, and GitLab hears nothing from Gitberth, "
                                     + "until you save a new token in Settings › GitLab.",
            CredentialKind.Cloudflare => $"Cloudflare refused Gitberth's API token {lead}: {why} Gitberth can't add, move or remove domains or change their routes "
                                         + "until you save a new token in Settings › Domains; the routes already in your tunnel keep working.",
            _ => $"{RegistryName(s.Place)} refused the sign-in{ForWho(s)} {lead}: {why} Apps on {RegistryName(s.Place)}'s private images can't be pulled or "
                 + "checked for updates until you enter it again in Settings › Registries.",
        };
    }

    /// <summary>The top bar's few words, only for what needs someone now: a refused or expired token.</summary>
    public static string Pill(CredentialSubject s, bool refused) => (s.Kind, refused) switch
    {
        (CredentialKind.GitHub, _) => refused ? "GitHub token refused" : "GitHub token expired",
        (CredentialKind.GitLab, _) => refused ? "GitLab token refused" : "GitLab token expired",
        (CredentialKind.Cloudflare, _) => refused ? "Cloudflare token refused" : "Cloudflare token expired",
        (CredentialKind.Registry, _) => $"{RegistryName(s.Place)} sign-in refused",
        _ => $"API token {s.Who} expired",
    };

    // ---- get_health ----

    public static string ExpiringProblem(CredentialSubject s, DateTimeOffset now) =>
        $"{ExpiringTitle(s, now)} ({Date(s.ExpiresAt!.Value)}): {Fix(s.Kind)}.";

    public static string ExpiredProblem(CredentialSubject s) =>
        $"{Thing(s)}{ForWho(s, s.Kind)} expired on {Date(s.ExpiresAt!.Value)}: {Fix(s.Kind)}.";

    public static string RefusedProblem(CredentialSubject s, CredentialFact fact) =>
        $"{RefusedTitle(s)}: {Why(s, fact)} {char.ToUpperInvariant(Fix(s.Kind)[0])}{Fix(s.Kind)[1..]}.";

    // Only GitHub's and GitLab's tokens have a login to name; the rest are named by their title already.
    private static string ForWho(CredentialSubject s, CredentialKind kind) => kind is CredentialKind.GitHub or CredentialKind.GitLab ? ForWho(s) : "";

    private static string Fix(CredentialKind kind) => kind switch
    {
        CredentialKind.Registry => "enter its password again in Settings › Registries",
        CredentialKind.ApiToken => "make a new one in Settings › AI agents and API",
        _ => $"save a new one in {Section(kind)}",
    };

    // ---- The audit log ----

    /// <summary>The entry's action: settings.github.token.refused, domains.token.expired, settings.registry.accepted.</summary>
    public static string Action(CredentialKind kind, string what) => kind switch
    {
        CredentialKind.GitHub => $"settings.github.token.{what}",
        CredentialKind.GitLab => $"settings.gitlab.token.{what}",
        CredentialKind.Cloudflare => $"domains.token.{what}",
        _ => $"settings.registry.{what}",
    };

    /// <summary>What the entry is about, as the page that saves it names it.</summary>
    public static string Target(CredentialSubject s) => s.Kind switch
    {
        CredentialKind.GitHub => "GitHub token",
        CredentialKind.GitLab => "GitLab token",
        CredentialKind.Cloudflare => "Cloudflare token",
        CredentialKind.Registry => RegistryName(s.Place),
        _ => s.Who ?? "API token",
    };

    public static string AuditExpired(CredentialSubject s)
    {
        var on = Date(s.ExpiresAt!.Value);
        return s.Kind switch
        {
            CredentialKind.GitHub => $"The GitHub token{ForWho(s)} expired on {on}: private repos don't deploy until a new one is saved.",
            CredentialKind.GitLab => $"The GitLab token{ForWho(s)} expired on {on}: private projects don't deploy, and GitLab hears nothing from Gitberth, until a new one is saved.",
            _ => $"Gitberth's Cloudflare API token expired on {on}: domains can't be added, moved or removed until a new one is saved.",
        };
    }

    public static string AuditRefused(CredentialSubject s, CredentialFact fact)
    {
        var lead = fact.RefusedDoing is { Length: > 0 } doing
            ? $"{doing}, and again when it {(s.Kind == CredentialKind.Registry ? "signed in" : "checked")}"
            : $"when {Again(s.Kind)}";
        return s.Kind switch
        {
            CredentialKind.GitHub => $"GitHub refused the token{ForWho(s)} {lead}: {Why(s, fact)}",
            CredentialKind.GitLab => $"GitLab refused the token{ForWho(s)} {lead}: {Why(s, fact)}",
            CredentialKind.Cloudflare => $"Cloudflare refused Gitberth's API token {lead}: {Why(s, fact)}",
            _ => $"{RegistryName(s.Place)} refused the sign-in{ForWho(s)} {lead}: {Why(s, fact)}",
        };
    }

    public static string AuditAccepted(CredentialSubject s) => s.Kind switch
    {
        CredentialKind.GitHub => $"GitHub takes the token{ForWho(s)} again.",
        CredentialKind.GitLab => $"GitLab takes the token{ForWho(s)} again.",
        CredentialKind.Cloudflare => "Cloudflare takes Gitberth's API token again.",
        _ => $"{RegistryName(s.Place)} takes the sign-in{ForWho(s)} again.",
    };

    // ---- Check the token ----

    public static string Took(CredentialSubject s, DateTimeOffset? expires)
    {
        var until = expires is { } at ? $"it expires on {Date(at)}" : "it doesn't expire";
        return s.Kind switch
        {
            CredentialKind.GitHub => s.Who is { Length: > 0 } who ? $"GitHub took the token: it's {who}'s, and {until}." : $"GitHub took the token, and {until}.",
            _ => $"Cloudflare took the token, and {until}.",
        };
    }

    public static string RefusedNow(CredentialSubject s) => s.Kind switch
    {
        CredentialKind.GitHub => "GitHub refused the token: it has expired or was revoked. Save a new one.",
        _ => "Cloudflare refused the token: it has expired, or was disabled or deleted. Save a new one.",
    };

    public static string CouldNotAsk(CredentialSubject s, string? why) =>
        $"Couldn't ask {s.Kind switch { CredentialKind.GitHub => "GitHub", CredentialKind.GitLab => "GitLab", _ => "Cloudflare" }}: {FirstSentence(why) ?? "it didn't answer."} Nothing changed.";

    // A check's warning was written for a save ("Saved, but api.github.com couldn't be reached…"): its first sentence, as a
    // reason of its own.
    private static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith("Saved, but ", StringComparison.Ordinal)) s = s["Saved, but ".Length..];
        var end = s.IndexOf(". ", StringComparison.Ordinal);
        s = end > 0 ? s[..(end + 1)] : s;
        return s.EndsWith('.') ? s : s + ".";
    }

    // ---- What Gitberth was doing ----

    /// <summary>
    /// What a call to GitHub's or GitLab's API was for, from its address, as the sentence goes on: "while Gitberth was
    /// setting a commit status on acme/web". The address never carries a token.
    /// </summary>
    public static string ApiDoing(string kind, HttpMethod method, Uri? uri)
    {
        var gitLab = kind == CredentialWatch.GitLabKey;
        var path = (uri?.AbsolutePath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (gitLab)
        {
            // GitLab's API is under /api/v4/, maybe below the path a self-hosted GitLab is served at.
            var v4 = Array.IndexOf(path, "v4");
            path = v4 >= 1 && path[v4 - 1] == "api" ? path[(v4 + 1)..] : [];
        }
        string Of(string repo, string[] rest) => rest switch
        {
            ["statuses", ..] => $"setting a commit status on {repo}",
            ["hooks", ..] => $"setting up {(gitLab ? "the project hook" : "the webhook")} on {repo}",
            ["issues", ..] or ["merge_requests", _, "notes", ..] => $"commenting on {(gitLab ? "a merge request" : "a pull request")} of {repo}",
            ["actions", "variables", ..] or ["variables", ..] => method == HttpMethod.Get ? $"reading {repo}'s variables" : $"saving {repo}'s variables",
            ["pulls", ..] or ["merge_requests", ..] => $"reading {repo}'s {(gitLab ? "merge requests" : "pull requests")}",
            _ => $"reading {repo}",
        };
        var doing = path switch
        {
            ["user", ..] or ["personal_access_tokens", ..] => "checking the token",
            ["projects", var project, .. var rest] when gitLab => Of(project, rest),
            ["repos", var owner, var name, .. var rest] when !gitLab => Of($"{owner}/{name}", rest),
            _ => $"talking to {(gitLab ? "GitLab" : "GitHub")}",
        };
        return $"while Gitberth was {doing}";
    }

    /// <summary>What a git command was doing: "while Gitberth was cloning acme/web".</summary>
    public static string GitDoing(string command, string repo) => command switch
    {
        "clone" => $"while Gitberth was cloning {repo}",
        "fetch" => $"while Gitberth was fetching {repo}",
        "ls-remote" => $"while Gitberth was looking at {repo}",
        _ => $"while Gitberth was reading {repo}",
    };

    /// <summary>What a Cloudflare call was for, from its own words ("read the tunnel's routes").</summary>
    public static string CloudflareDoing(string doing) => $"when Gitberth tried to {doing}";

    /// <summary>An update check that a registry refused: "when Gitberth checked ghcr.io/acme/web:latest for an update".</summary>
    public static string RegistryDoing(string reference) => $"when Gitberth checked {reference} for an update";

    /// <summary>
    /// GitLab's own word on a refused token, from its 401's WWW-Authenticate (error_description="Token is expired…" or
    /// "Token was revoked…"); null when it gave none.
    /// </summary>
    public static string? GitLabWords(IEnumerable<string>? challenges)
    {
        foreach (var challenge in challenges ?? [])
        {
            if (challenge.Contains("Token was revoked", StringComparison.OrdinalIgnoreCase)) return "GitLab says it was revoked.";
            if (challenge.Contains("Token is expired", StringComparison.OrdinalIgnoreCase)) return "GitLab says it has expired.";
        }
        return null;
    }
}
