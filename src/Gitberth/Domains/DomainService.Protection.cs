using System.Text.RegularExpressions;
using Gitberth.Data;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Domains;

/// <summary>Whether domains can be put behind Cloudflare Access, as Settings › Domains › Protection shows it.</summary>
/// <param name="Ready">A protected domain gets its Access application: Zero Trust is on, the token may manage Access, and someone is allowed in.</param>
/// <param name="TeamDomain">Where visitors sign in (acme.cloudflareaccess.com); null when it wasn't found.</param>
/// <param name="Problem">Why it isn't ready; null when it is.</param>
/// <param name="Emails">Who may sign in, as saved.</param>
/// <param name="Protected">How many domains are protected.</param>
public sealed record ProtectionStatus(bool Ready, string? TeamDomain, string? Problem, IReadOnlyList<string> Emails, int Protected);

/// <summary>
/// Deployment protection: a domain marked Protected is served only to people who sign in through Cloudflare Access with
/// one of the email addresses in Settings (a one-time code by email, Cloudflare's default). For each, Gitberth keeps a
/// self-hosted Access application named "Gitberth: &lt;hostname&gt;" and sets the tunnel rule's originRequest.access, so
/// cloudflared checks the sign-in too. A protected domain is never served unprotected: while Access can't be set up for
/// it, its route is left out of the tunnel. Taking protection off (or the domain) removes the application Gitberth made.
/// </summary>
public sealed partial class DomainService
{
    /// <summary>How long the team domain Cloudflare gave is used before it's asked again.</summary>
    private static readonly TimeSpan TeamFor = TimeSpan.FromMinutes(10);
    private (string? Team, string? Problem, DateTime At, string Account)? _team;

    /// <summary>The team name cloudflared wants (acme for acme.cloudflareaccess.com).</summary>
    internal static string TeamName(string teamDomain) => teamDomain.Split('.')[0];

    /// <summary>The email addresses typed, one per line (commas and spaces work too), lower case, each once; and what's wrong with any.</summary>
    internal static (List<string> Emails, string? Problem) ParseEmails(string? text)
    {
        var emails = new List<string>();
        foreach (var raw in (text ?? "").Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var email = raw.ToLowerInvariant();
            if (!EmailShape().IsMatch(email)) return (emails, $"\"{raw}\" isn't an email address.");
            if (!emails.Contains(email)) emails.Add(email);
        }
        return (emails, null);
    }

    [GeneratedRegex(@"^[^@\s]+@[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex EmailShape();

    private async Task<List<string>> EmailsAsync(CancellationToken ct) => ParseEmails(await settings.GetAsync(SettingKeys.ProtectionEmails, ct)).Emails;

    /// <summary>Whether protection is ready, checked with Cloudflare now: the team domain, and that the token may list Access applications.</summary>
    public async Task<ProtectionStatus> ProtectionAsync(CancellationToken ct = default)
    {
        var emails = await EmailsAsync(ct);
        int count;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            count = await db.Domains.CountAsync(d => d.Protected, ct);
        using var whileOn = CancellationTokenSource.CreateLinkedTokenSource(ct, _whileOn.Token);
        try
        {
            if (!await EnabledAsync(ct)) return new(false, null, "Domains are turned off.", emails, count);
            if (await settings.GetSecretAsync(SettingKeys.CloudflareToken, ct) is not { } token) return new(false, null, "Gitberth has no Cloudflare token yet.", emails, count);
            var (tunnel, problem) = await ResolveTunnelAsync(connect: false, whileOn.Token);
            if (tunnel is null) return new(false, null, problem, emails, count);
            var (team, teamProblem) = await TeamAsync(token, tunnel, fresh: true, whileOn.Token);
            if (team is null) return new(false, null, teamProblem, emails, count);
            await cloudflare.ListAccessAppsAsync(token, tunnel.AccountId, whileOn.Token);
            return emails.Count == 0
                ? new(false, team, "Add the email addresses of the people who may sign in.", emails, count)
                : new(true, team, null, emails, count);
        }
        catch (CloudflareApiException ex)
        {
            return new(false, null, ex.Message, emails, count);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, null, TurnedOff, emails, count);
        }
    }

    /// <summary>
    /// Saves who may sign in to protected domains and brings every protected domain's Access application up to date. An
    /// empty list isn't saved while a domain is protected: that domain would go offline.
    /// </summary>
    public async Task<(string? Error, SyncReport Report)> SaveProtectionEmailsAsync(string? text, CancellationToken ct = default)
    {
        var (emails, problem) = ParseEmails(text);
        if (problem is not null) return (problem, SyncReport.Nothing);
        await _gate.WaitAsync(ct);
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (emails.Count == 0 && await db.Domains.AnyAsync(d => d.Protected, ct))
                    return ("Some domains are protected, so somebody has to be allowed in. Turn protection off on those domains first.", SyncReport.Nothing);
            }
            await settings.SetAsync(SettingKeys.ProtectionEmails, emails.Count == 0 ? null : string.Join('\n', emails), ct);
            return (null, await EnabledAsync(ct) && await TokenSavedAsync(ct) ? await SyncCoreAsync(widen: null, ct) : SyncReport.Nothing);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Puts a domain behind Cloudflare Access, or takes it out, and routes it that way. Turning it on needs protection to
    /// be ready (Settings › Domains › Protection); turning it off never does.
    /// </summary>
    public async Task<(string? Error, SyncReport Report)> SetProtectedAsync(int appId, string hostname, bool on, CancellationToken ct = default)
    {
        if (on && await ProtectionAsync(ct) is { Ready: false } status)
            return ($"{hostname} can't be protected yet: {status.Problem} Set it up in Settings › Domains › Protection.", SyncReport.Nothing);
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnabledAsync(ct)) return (TurnedOff, SyncReport.Nothing);
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var row = await db.Domains.FirstOrDefaultAsync(d => d.AppId == appId && d.Hostname == hostname, ct);
                if (row is null) return ($"{hostname} isn't one of this app's domains.", SyncReport.Nothing);
                if (row.Protected == on) return (null, SyncReport.Nothing);
                row.Protected = on;
                await db.SaveChangesAsync(ct);
            }
            return (null, await SyncCoreAsync(widen: null, ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    // The team domain, from the last 10 minutes' answer unless asked fresh; a failure is remembered for a minute.
    private async Task<(string? Team, string? Problem)> TeamAsync(string token, TunnelInfo tunnel, bool fresh, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (!fresh && _team is { } known && known.Account == tunnel.AccountId && now - known.At < (known.Team is null ? TimeSpan.FromMinutes(1) : TeamFor))
            return (known.Team, known.Problem);
        try
        {
            var team = await cloudflare.GetAccessTeamDomainAsync(token, tunnel.AccountId, ct);
            var problem = team is null
                ? "Cloudflare Zero Trust isn't set up for this account yet. Open one.dash.cloudflare.com once and choose a team name: that's where visitors sign in."
                : null;
            _team = (team, problem, now, tunnel.AccountId);
            return (team, problem);
        }
        catch (CloudflareApiException ex)
        {
            _team = (null, ex.Message, now, tunnel.AccountId);
            return (null, ex.Message);
        }
    }

    /// <summary>Why the app's domains can't be protected right now, for the Domains card; null when they can. Uses the remembered team domain.</summary>
    private async Task<string?> ProtectionProblemAsync(string token, TunnelInfo tunnel, CancellationToken ct)
    {
        if ((await EmailsAsync(ct)).Count == 0) return "Nobody is allowed in yet: add email addresses in Settings › Domains › Protection.";
        return (await TeamAsync(token, tunnel, fresh: false, ct)).Problem;
    }

    /// <summary>What a sync does about protection: the access block per protected host, hosts held back, and applications to remove.</summary>
    private sealed record AccessPlan(
        Dictionary<string, TunnelRoutes.Protection> Protect,
        HashSet<string> Withheld,
        HashSet<string> StripAuds,
        List<(AccessApp App, CustomDomain? Domain)> Remove)
    {
        public static AccessPlan None() => new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.Ordinal), []);
    }

    /// <summary>
    /// With the lock held, before the tunnel is planned: makes or updates the Access application of every protected
    /// domain, and finds the ones Gitberth made that aren't wanted any more (the domain unprotected or removed), which go
    /// once the tunnel no longer refers to them (<see cref="RemoveAccessAppsAsync"/>). Asks Cloudflare nothing when no
    /// domain is or was protected.
    /// </summary>
    private async Task<AccessPlan> PlanAccessAsync(string token, TunnelInfo tunnel, List<AppDefinition> apps, List<DomainRemoval> removals,
        IReadOnlyDictionary<int, string> services, List<DomainNote> notes, CancellationToken ct)
    {
        var plan = AccessPlan.None();
        var domains = apps.SelectMany(a => a.Domains.Select(d => (App: a, Domain: d))).ToList();
        var guarded = domains.Where(x => x.Domain.Protected).ToList();
        var emails = await EmailsAsync(ct);
        if (guarded.Count == 0 && !domains.Any(x => x.Domain.AccessAppId is not null) && (removals.Count == 0 || emails.Count == 0)) return plan;

        void Withhold(string hostname, string why)
        {
            plan.Withheld.Add(hostname);
            notes.Add(new DomainNote(hostname, $"{hostname} isn't served: it's protected, and {why}", Problem: true));
        }

        var (team, problem) = await TeamAsync(token, tunnel, fresh: false, ct);
        List<AccessApp> ours;
        try
        {
            ours = team is null ? [] : (await cloudflare.ListAccessAppsAsync(token, tunnel.AccountId, ct))
                .Where(a => a.Name.StartsWith(CloudflareApi.AccessAppPrefix, StringComparison.Ordinal)).ToList();
        }
        catch (CloudflareApiException ex)
        {
            (team, problem, ours) = (null, ex.Message, []);
        }
        if (team is null)
        {
            foreach (var (_, d) in guarded) Withhold(d.Hostname, $"Cloudflare Access can't be used: {problem}");
            return plan;
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (app, d) in guarded)
        {
            if (emails.Count == 0)
            {
                Withhold(d.Hostname, "nobody is allowed in yet. Add email addresses in Settings › Domains › Protection.");
                continue;
            }
            var existing = ours.FirstOrDefault(a => a.Id == d.AccessAppId) ?? ours.FirstOrDefault(a => a.Name == CloudflareApi.AccessAppPrefix + d.Hostname);
            try
            {
                var current = existing;
                if (existing is { Emails: null }) current = await cloudflare.GetAccessAppAsync(token, tunnel.AccountId, existing.Id, ct) ?? existing;
                AccessApp made;
                if (existing is null)
                {
                    made = await cloudflare.SaveAccessAppAsync(token, tunnel.AccountId, null, d.Hostname, emails, ct);
                    notes.Add(new DomainNote(d.Hostname, $"Made the Cloudflare Access application for {d.Hostname}"));
                }
                else if (current!.Domain != d.Hostname || current.Emails is null || !current.Emails.SequenceEqual(emails.Order(StringComparer.Ordinal)) || current.Aud.Length == 0)
                {
                    made = await cloudflare.SaveAccessAppAsync(token, tunnel.AccountId, existing.Id, d.Hostname, emails, ct);
                    notes.Add(new DomainNote(d.Hostname, $"Updated who may sign in to {d.Hostname}"));
                }
                else
                {
                    made = current;
                }
                if (made.Aud.Length == 0) throw new CloudflareApiException($"Cloudflare didn't give {d.Hostname}'s Access application an audience tag.");
                d.AccessAppId = made.Id;
                wanted.Add(made.Id);
                List<string> owned = [.. new[] { services.GetValueOrDefault(app.Id), d.Service }.OfType<string>()];
                plan.Protect[d.Hostname] = new TunnelRoutes.Protection(TunnelRoutes.AccessBlock(TeamName(team), made.Aud), owned);
            }
            catch (CloudflareApiException ex)
            {
                Withhold(d.Hostname, $"its Access application couldn't be set up: {ex.Message}");
            }
        }

        // Applications Gitberth made for domains that aren't protected any more, or are being removed.
        var released = domains.Where(x => !x.Domain.Protected).ToDictionary(x => x.Domain.Hostname, x => x.Domain, StringComparer.OrdinalIgnoreCase);
        var leaving = removals.Select(r => r.Hostname).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var app in ours.Where(a => !wanted.Contains(a.Id)))
        {
            var hostname = app.Name[CloudflareApi.AccessAppPrefix.Length..];
            var domain = released.GetValueOrDefault(hostname) ?? released.Values.FirstOrDefault(d => d.AccessAppId == app.Id);
            if (domain is null && !leaving.Contains(hostname)) continue;
            plan.Remove.Add((app, domain));
            if (app.Aud.Length > 0) plan.StripAuds.Add(app.Aud);
        }
        // One whose application is gone already only forgets it.
        foreach (var d in released.Values.Where(d => d.AccessAppId is not null && !ours.Any(a => a.Id == d.AccessAppId))) d.AccessAppId = null;
        return plan;
    }

    /// <summary>Once the tunnel no longer sends anyone through them: the Access applications Gitberth made that aren't wanted any more.</summary>
    private async Task RemoveAccessAppsAsync(string token, TunnelInfo tunnel, AccessPlan plan, List<DomainNote> notes, CancellationToken ct)
    {
        foreach (var (app, domain) in plan.Remove)
        {
            var hostname = app.Name[CloudflareApi.AccessAppPrefix.Length..];
            try
            {
                await cloudflare.DeleteAccessAppAsync(token, tunnel.AccountId, app.Id, hostname, ct);
                if (domain is not null) domain.AccessAppId = null;
                notes.Add(new DomainNote(hostname, $"Removed the Cloudflare Access application Gitberth made for {hostname}"));
            }
            catch (CloudflareApiException ex)
            {
                notes.Add(new DomainNote(hostname, $"{hostname}: {ex.Message} Gitberth tries again with the next change.", Problem: true));
            }
        }
    }
}
