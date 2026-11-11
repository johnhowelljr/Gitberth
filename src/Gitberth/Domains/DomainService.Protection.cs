using System.Text.Json;
using System.Text.Json.Nodes;
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
    private (string? Team, string? Problem, bool Transient, DateTime At, string Account)? _team;

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

    /// <summary>
    /// Whether protection is ready, checked with Cloudflare now: the team domain, and that the token may list Access
    /// applications. When it may while an Access application Gitberth made is still to be removed (a gone domain's, or one
    /// no longer protected), the retry tries that once more within 15 minutes: the token may have its permission back,
    /// and nothing else would try before the next sync.
    /// </summary>
    public async Task<ProtectionStatus> ProtectionAsync(CancellationToken ct = default)
    {
        var emails = await EmailsAsync(ct);
        int count;
        bool left;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            count = await db.Domains.CountAsync(d => d.Protected, ct);
            left = (await LeftoversAsync(db, ct)).Count > 0 || await db.Domains.AnyAsync(d => !d.Protected && d.AccessAppId != null, ct);
        }
        using var whileOn = CancellationTokenSource.CreateLinkedTokenSource(ct, _whileOn.Token);
        try
        {
            if (!await EnabledAsync(ct)) return new(false, null, "Domains are turned off.", emails, count);
            if (await settings.GetSecretAsync(SettingKeys.CloudflareToken, ct) is not { } token) return new(false, null, "Gitberth has no Cloudflare token yet.", emails, count);
            var (tunnel, problem) = await ResolveTunnelAsync(connect: false, whileOn.Token);
            if (tunnel is null) return new(false, null, problem, emails, count);
            var (team, teamProblem, _) = await TeamAsync(token, tunnel, fresh: true, whileOn.Token);
            if (team is null) return new(false, null, teamProblem, emails, count);
            await cloudflare.ListAccessAppsAsync(token, tunnel.AccountId, whileOn.Token);
            if (left) _accessRecheck = true;
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
    /// be ready (Settings › Domains › Protection); turning it off never does. Either way the setting is saved even when the
    /// sync can't finish, and then the error says what's still true: turned on, it may still be open; turned off, it's
    /// still behind Access (its rules still check the sign-in, or the Access application Gitberth made is still there),
    /// and what finishes it: the retry every 15 minutes, Fix, or Check again in Settings › Domains › Protection.
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
            var report = await SyncCoreAsync(widen: null, ct);
            // The tunnel wasn't reached: the retry every 15 minutes tries again, once there's a token to try with.
            var again = report.Exposed is null && await TokenSavedAsync(ct) ? $" Gitberth tries again every {RemovalRetry.Every.TotalMinutes:0} minutes." : "";
            if (on) return (NotProtectedYet(report, [hostname]) is { } open ? open + again : null, report);
            // Turned off, its rules keep their Access check until the tunnel takes the change,
            if (NotOpenYet(report, [hostname]) is { } closed) return (closed + again, report);
            // and visitors still sign in until the Access application Gitberth made for it is gone.
            return (await StillSignsInAsync(appId, hostname, report, ct), report);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Why a domain that isn't protected any more still asks (or may still ask) visitors to sign in after a sync: Gitberth
    /// couldn't remove the Access application it made for it. Null when there's none left. Without the sync's note about
    /// it, the sync stopped before it was done, so Access isn't settled and the retry tries again.
    /// </summary>
    private async Task<string?> StillSignsInAsync(int appId, string hostname, SyncReport report, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Domains.AsNoTracking().Where(d => d.AppId == appId && d.Hostname == hostname).Select(d => d.AccessAppId).FirstOrDefaultAsync(ct) is null)
                return null;
        }
        var (said, may) = (StillSignsIn(hostname), MayStillSignIn(hostname));
        return report.Notes.FirstOrDefault(n => n.Problem && (n.Text.StartsWith(said, StringComparison.Ordinal) || n.Text.StartsWith(may, StringComparison.Ordinal)))?.Text
            ?? said + (report.Targets?.ContainsKey(appId) == true ? " Click Fix to try again." : $" Gitberth tries again every {RemovalRetry.Every.TotalMinutes:0} minutes.");
    }

    /// <summary>The start of what's said while a domain no longer protected, or gone, still has the Access application Gitberth made for it.</summary>
    private static string StillSignsIn(string hostname) => $"{hostname} still asks visitors to sign in: Gitberth couldn't remove its Cloudflare Access application.";

    /// <summary>The same, while Gitberth can't see the account's Access applications: that one may be gone already.</summary>
    private static string MayStillSignIn(string hostname) => $"{hostname} may still ask visitors to sign in: Gitberth couldn't remove its Cloudflare Access application or see whether it's gone.";

    /// <summary>
    /// The note for a domain whose Access application Gitberth couldn't remove, so visitors still sign in there, and why.
    /// It's only said to be there when Gitberth just saw it (<paramref name="seen"/>): otherwise it may have been deleted by
    /// hand. When Cloudflare only failed for a moment, the retry tries again every 15 minutes. Otherwise Fix does where the
    /// Domains card has one for it (<paramref name="fix"/>: a domain the app still has, while the app is served); for a
    /// domain that's gone, or whose app isn't served, Check again in Settings › Domains › Protection does once it finds
    /// Access working (<see cref="ProtectionAsync"/>), and so does the next sync. Deleting it by hand is offered only when
    /// Cloudflare refused the removal itself (<paramref name="refused"/>): Gitberth saw the application (and, for a domain
    /// that's gone, found no other tunnel using it), so the next sync notices it's gone.
    /// </summary>
    private static DomainNote StillSignsInNote(string hostname, string? why, bool transient, bool seen, bool fix, bool refused = false) =>
        new(hostname, (seen ? StillSignsIn(hostname) : MayStillSignIn(hostname)) + (why is null ? "" : " " + why)
            + (transient ? $" Gitberth tries again every {RemovalRetry.Every.TotalMinutes:0} minutes."
                : fix ? " Click Fix to try again."
                : (refused ? $" Fix the token's permission, or delete \"{CloudflareApi.AccessAppPrefix}{hostname}\" in Cloudflare Zero Trust." : " Fix the token's permission.")
                    + $" Then click Check again in Settings › Domains › Protection: Gitberth tries again within {RemovalRetry.Every.TotalMinutes:0} minutes."
                    + " It also tries the next time it updates your domains in Cloudflare."),
            Problem: true);

    /// <summary>
    /// Why protecting these domains isn't done after a sync: it didn't get as far as the tunnel, or left one served without
    /// Cloudflare Access. Null when each is behind Access, or out of the tunnel until it can be.
    /// </summary>
    internal static string? NotProtectedYet(SyncReport report, IReadOnlyCollection<string> hostnames)
    {
        var open = hostnames.Where(h => report.Exposed is null || report.Exposed.Contains(h)).Order(StringComparer.Ordinal).ToList();
        if (open.Count == 0) return null;
        var why = report.For(open).Notes.FirstOrDefault(n => n.Problem)?.Text;
        var (names, isnt, it) = open.Count == 1 ? (open[0], "isn't", "it") : (string.Join(", ", open), "aren't", "them");
        return $"{names} {isnt} behind Cloudflare Access yet, so anyone may still open {it}.{(why is null ? "" : " " + why)}";
    }

    /// <summary>
    /// Why taking these domains' protection off isn't done after a sync: it didn't get as far as the tunnel, so their rules
    /// still check the sign-in (or they aren't served yet). Null when the tunnel took the change.
    /// </summary>
    internal static string? NotOpenYet(SyncReport report, IReadOnlyCollection<string> hostnames)
    {
        if (report.Exposed is not null || hostnames.Count == 0) return null;
        var names = hostnames.Order(StringComparer.Ordinal).ToList();
        var why = report.For(names).Notes.FirstOrDefault(n => n.Problem)?.Text;
        var (list, isnt) = names.Count == 1 ? (names[0], "isn't") : (string.Join(", ", names), "aren't");
        return $"{list} {isnt} open to everyone yet: Gitberth couldn't change the tunnel.{(why is null ? "" : " " + why)}";
    }

    /// <summary>Why protection can't work while the account has no team domain: Zero Trust isn't set up, so it has no Access applications either.</summary>
    private const string NoZeroTrust = "Cloudflare Zero Trust isn't set up for this account yet. Open one.dash.cloudflare.com once and choose a team name: that's where visitors sign in.";

    // The team domain, from the last 10 minutes' answer unless asked fresh; a failure is remembered for a minute, with
    // whether it was only Cloudflare failing for a moment (Transient), for the Domains card. A sync asks again after one.
    private async Task<(string? Team, string? Problem, bool Transient)> TeamAsync(string token, TunnelInfo tunnel, bool fresh, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (!fresh && _team is { } known && known.Account == tunnel.AccountId && now - known.At < (known.Team is null ? TimeSpan.FromMinutes(1) : TeamFor))
            return (known.Team, known.Problem, known.Transient);
        try
        {
            var team = await cloudflare.GetAccessTeamDomainAsync(token, tunnel.AccountId, ct);
            var problem = team is null ? NoZeroTrust : null;
            _team = (team, problem, false, now, tunnel.AccountId);
            return (team, problem, false);
        }
        catch (CloudflareApiException ex)
        {
            _team = (null, ex.Message, ex.Transient, now, tunnel.AccountId);
            return (null, ex.Message, ex.Transient);
        }
    }

    /// <summary>
    /// Why the app's domains can't be protected right now, for the Domains card, and whether that's only Cloudflare failing
    /// for a moment; null when they can. Uses the remembered team domain.
    /// </summary>
    private async Task<(string? Problem, bool Transient)> ProtectionProblemAsync(string token, TunnelInfo tunnel, CancellationToken ct)
    {
        if ((await EmailsAsync(ct)).Count == 0) return ("Nobody is allowed in yet: add email addresses in Settings › Domains › Protection.", false);
        var (_, problem, transient) = await TeamAsync(token, tunnel, fresh: false, ct);
        return (problem, transient);
    }

    /// <summary>What a sync does about protection: the access block per protected host, hosts held back, and applications to remove.</summary>
    private sealed record AccessPlan(
        Dictionary<string, TunnelRoutes.Protection> Protect,
        HashSet<string> Withheld,
        HashSet<string> StripAuds,
        List<(AccessApp App, CustomDomain? Domain)> Remove)
    {
        public static AccessPlan None() => new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.Ordinal), []);

        /// <summary>
        /// Cloudflare Access failed only for a moment (busy, or out of reach), so something is left that a later try can
        /// finish: <see cref="RemovalRetry"/> syncs again. A lasting problem (Zero Trust not set up, nobody allowed in, a
        /// token without the permission) waits for a change instead of asking Cloudflare every 15 minutes.
        /// </summary>
        public bool Retry { get; set; }
    }

    /// <summary>
    /// With the lock held, before the tunnel is planned: makes or updates the Access application of every protected
    /// domain, and finds the ones Gitberth made that aren't wanted any more (the domain unprotected, removed, or gone with
    /// one left to remove: <paramref name="leftovers"/>), which go once the tunnel no longer refers to them
    /// (<see cref="RemoveAccessAppsAsync"/>); an account without Zero Trust has none, so there they're only forgotten. The
    /// checks Gitberth put on hosts that aren't protected any more come off, from what it remembers
    /// (<paramref name="checks"/>). Asks Cloudflare nothing when no domain is or was protected.
    /// </summary>
    private async Task<AccessPlan> PlanAccessAsync(string token, TunnelInfo tunnel, List<AppDefinition> apps, List<DomainRemoval> removals,
        IReadOnlyDictionary<int, string> services, IReadOnlyDictionary<string, HashSet<string>> checks, Dictionary<string, string> leftovers,
        List<DomainNote> notes, CancellationToken ct)
    {
        var plan = AccessPlan.None();
        var domains = apps.SelectMany(a => a.Domains.Select(d => (App: a, Domain: d))).ToList();
        var guarded = domains.Where(x => x.Domain.Protected).ToList();
        // Even once its Access application is gone (deleted in the dashboard, say): without one, cloudflared turns everyone away.
        foreach (var (host, auds) in checks)
            if (!guarded.Any(x => string.Equals(x.Domain.Hostname, host, StringComparison.OrdinalIgnoreCase))) plan.StripAuds.UnionWith(auds);
        // A domain that's back (added again, or taken over) takes back the application Gitberth still had to remove for it.
        foreach (var (hostname, id) in leftovers.ToList())
        {
            if (domains.Select(x => x.Domain).FirstOrDefault(d => string.Equals(d.Hostname, hostname, StringComparison.OrdinalIgnoreCase)) is not { } back
                || (back.AccessAppId is { } own && own != id)) continue;
            back.AccessAppId = id;
            leftovers.Remove(hostname);
        }
        var emails = await EmailsAsync(ct);
        if (guarded.Count == 0 && !domains.Any(x => x.Domain.AccessAppId is not null) && leftovers.Count == 0 && (removals.Count == 0 || emails.Count == 0)) return plan;

        // Out of the tunnel until it's behind Access. When Cloudflare only failed for a moment, the retry brings it back.
        void Withhold(string hostname, string why, bool retry)
        {
            plan.Withheld.Add(hostname);
            plan.Retry |= retry;
            notes.Add(new DomainNote(hostname, $"{hostname} isn't served: it's protected, and {why}{(retry ? $" Gitberth tries again every {RemovalRetry.Every.TotalMinutes:0} minutes." : "")}",
                Problem: true));
        }

        // Only a team domain is taken from memory: after a failure, even one a moment ago, a sync asks again, so Fix does.
        var (team, problem, transient) = await TeamAsync(token, tunnel, fresh: _team?.Team is null, ct);
        List<AccessApp> ours;
        try
        {
            ours = team is null ? [] : (await cloudflare.ListAccessAppsAsync(token, tunnel.AccountId, ct))
                .Where(a => a.Name.StartsWith(CloudflareApi.AccessAppPrefix, StringComparison.Ordinal)).ToList();
        }
        catch (CloudflareApiException ex)
        {
            (team, problem, transient, ours) = (null, ex.Message, ex.Transient, []);
        }
        if (team is null)
        {
            // Without Zero Trust the account has no Access applications at all, so none is left to remove.
            if (problem == NoZeroTrust)
            {
                foreach (var (_, d) in domains.Where(x => !x.Domain.Protected)) d.AccessAppId = null;
                leftovers.Clear();
            }
            // An application of a domain no longer protected, or gone, waits too, and visitors may still sign in there meanwhile.
            plan.Retry |= transient;
            foreach (var (_, d) in guarded) Withhold(d.Hostname, $"Cloudflare Access can't be used: {problem}", transient);
            foreach (var (app, d) in domains.Where(x => !x.Domain.Protected && x.Domain.AccessAppId is not null))
                notes.Add(StillSignsInNote(d.Hostname, problem, transient, seen: false, fix: services.ContainsKey(app.Id)));
            foreach (var hostname in leftovers.Keys.Order(StringComparer.Ordinal)) notes.Add(StillSignsInNote(hostname, problem, transient, seen: false, fix: false));
            return plan;
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (app, d) in guarded)
        {
            if (emails.Count == 0)
            {
                Withhold(d.Hostname, "nobody is allowed in yet. Add email addresses in Settings › Domains › Protection.", retry: false);
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
                Withhold(d.Hostname, $"its Access application couldn't be set up: {ex.Message}", ex.Transient);
            }
        }

        // Applications Gitberth made for domains that aren't protected any more, are being removed, or are gone.
        var released = domains.Where(x => !x.Domain.Protected).ToDictionary(x => x.Domain.Hostname, x => x.Domain, StringComparer.OrdinalIgnoreCase);
        var leaving = removals.Select(r => r.Hostname).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var app in ours.Where(a => !wanted.Contains(a.Id)))
        {
            var hostname = app.Name[CloudflareApi.AccessAppPrefix.Length..];
            var domain = released.GetValueOrDefault(hostname) ?? released.Values.FirstOrDefault(d => d.AccessAppId == app.Id);
            if (domain is null && !leaving.Contains(hostname) && !leftovers.ContainsValue(app.Id)) continue;
            plan.Remove.Add((app, domain));
            if (app.Aud.Length > 0) plan.StripAuds.Add(app.Aud);
        }
        // One whose application is gone already (or wanted again) only forgets it.
        foreach (var d in released.Values.Where(d => d.AccessAppId is not null && !ours.Any(a => a.Id == d.AccessAppId))) d.AccessAppId = null;
        foreach (var (hostname, _) in leftovers.Where(l => wanted.Contains(l.Value) || !ours.Any(a => a.Id == l.Value)).ToList()) leftovers.Remove(hostname);
        return plan;
    }

    /// <summary>
    /// Once the tunnel no longer sends anyone through them: the Access applications Gitberth made that aren't wanted any
    /// more. Until one goes, visitors still have to sign in there, so one Cloudflare didn't remove is remembered: on its
    /// domain, which the Domains card then shows as still behind Access, or, for a domain that's gone, with the ones left
    /// to remove (<paramref name="leftovers"/>), which every sync tries until each is gone (the retry, every 15 minutes,
    /// only while Cloudflare fails for a moment). A gone domain's is left alone, and forgotten, once another tunnel serves
    /// that name with it: another Gitberth on the same Cloudflare account took the name over, and the application with
    /// it, since they go by the name. <paramref name="services"/> says which apps are served, so have Fix on their card.
    /// </summary>
    private async Task RemoveAccessAppsAsync(string token, TunnelInfo tunnel, AccessPlan plan, Dictionary<string, string> leftovers,
        IReadOnlyDictionary<int, string> services, List<CloudflareZone> zones, List<DomainNote> notes, CancellationToken ct)
    {
        foreach (var (app, domain) in plan.Remove)
        {
            var hostname = app.Name[CloudflareApi.AccessAppPrefix.Length..];
            // Only past the check that no other tunnel uses it does a failure mean Cloudflare refused the removal itself.
            var removing = false;
            try
            {
                if (domain is null && await UsedElsewhereAsync(token, tunnel, app, hostname, zones, ct))
                {
                    foreach (var (left, _) in leftovers.Where(l => l.Value == app.Id).ToList()) leftovers.Remove(left);
                    notes.Add(new DomainNote(hostname, $"Left \"{app.Name}\" in Cloudflare Access: {hostname} is served through another tunnel now, which uses it."));
                    continue;
                }
                removing = true;
                await cloudflare.DeleteAccessAppAsync(token, tunnel.AccountId, app.Id, hostname, ct);
                if (domain is not null && domain.AccessAppId == app.Id) domain.AccessAppId = null;
                foreach (var (left, _) in leftovers.Where(l => l.Value == app.Id).ToList()) leftovers.Remove(left);
                notes.Add(new DomainNote(hostname, $"Removed the Cloudflare Access application Gitberth made for {hostname}"));
            }
            catch (CloudflareApiException ex)
            {
                if (domain is not null) domain.AccessAppId = app.Id;
                else leftovers[hostname] = app.Id;
                plan.Retry |= ex.Transient;
                notes.Add(StillSignsInNote(hostname, ex.Message, ex.Transient, seen: true, fix: domain is not null && services.ContainsKey(domain.AppId), refused: removing));
            }
        }
    }

    /// <summary>
    /// Whether another tunnel serves the host name with this Access application now: the name's DNS record sends it to a
    /// tunnel other than this one, and that tunnel's rules for it check the application's sign-in. Removing the application
    /// would turn everyone away there. A tunnel Cloudflare won't show Gitberth's token is another account's, which can't
    /// check this account's sign-in.
    /// </summary>
    private async Task<bool> UsedElsewhereAsync(string token, TunnelInfo tunnel, AccessApp app, string hostname, List<CloudflareZone> zones, CancellationToken ct)
    {
        if (app.Aud.Length == 0 || await ZoneAsync(token, tunnel, hostname, zones, ct) is not { } zone) return false;
        foreach (var other in (await cloudflare.GetDnsRecordsAsync(token, zone.Id, hostname, ct)).Select(r => OtherTunnel(r, tunnel)).OfType<string>().Distinct())
        {
            JsonObject config;
            try
            {
                config = await cloudflare.GetTunnelConfigAsync(token, tunnel.AccountId, other, ct);
            }
            catch (CloudflareApiException ex) when (!ex.Transient)
            {
                continue;
            }
            if (TunnelRoutes.AccessChecks(config["ingress"] as JsonArray ?? []).TryGetValue(hostname, out var auds) && auds.Contains(app.Aud)) return true;
        }
        return false;
    }

    /// <summary>The tunnel a DNS record sends its name to, when that's a tunnel other than this one; null otherwise.</summary>
    private static string? OtherTunnel(DnsRecord record, TunnelInfo tunnel)
    {
        const string suffix = ".cfargotunnel.com";
        var target = record.Content.TrimEnd('.');
        return record.Type == "CNAME" && !PointsAtTunnel(record, tunnel) && target.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(target[..^suffix.Length], out var id) ? id.ToString() : null;
    }

    // ---- The Access applications left to remove for domains that are gone ----

    /// <summary>
    /// The Access applications Gitberth made for domains that are gone and still has to remove
    /// (<see cref="SettingKeys.ProtectionLeftovers"/>): application ID by host name.
    /// </summary>
    private static async Task<Dictionary<string, string>> LeftoversAsync(GitberthDb db, CancellationToken ct)
    {
        var leftovers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if ((await db.Settings.FindAsync([SettingKeys.ProtectionLeftovers], ct))?.Value is { } saved
                && JsonSerializer.Deserialize<Dictionary<string, string>>(saved) is { } parsed)
                foreach (var (host, id) in parsed.Where(p => p.Value is { Length: > 0 })) leftovers[host] = id;
        }
        catch (JsonException)
        {
            // Unreadable: nothing is known, so nothing is removed.
        }
        return leftovers;
    }

    /// <summary>Saves them with the context's next save.</summary>
    private static async Task SetLeftoversAsync(GitberthDb db, Dictionary<string, string> leftovers, CancellationToken ct)
    {
        var row = await db.Settings.FindAsync([SettingKeys.ProtectionLeftovers], ct);
        if (leftovers.Count == 0)
        {
            if (row is not null) db.Settings.Remove(row);
            return;
        }
        var value = JsonSerializer.Serialize(leftovers.OrderBy(l => l.Key, StringComparer.Ordinal).ToDictionary(l => l.Key, l => l.Value));
        if (row is null) db.Settings.Add(new Setting { Key = SettingKeys.ProtectionLeftovers, Value = value });
        else row.Value = value;
    }

    // ---- The Access checks Gitberth put in the tunnel ----

    /// <summary>
    /// The audience tags of the Access checks Gitberth put on each host's rules (<see cref="SettingKeys.ProtectionChecks"/>),
    /// so it can take one off a host that isn't protected any more even once the application behind it is gone. A check
    /// Gitberth didn't put there is never taken off.
    /// </summary>
    private async Task<Dictionary<string, HashSet<string>>> ChecksAsync(CancellationToken ct)
    {
        var checks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (await settings.GetAsync(SettingKeys.ProtectionChecks, ct) is { } saved && JsonSerializer.Deserialize<Dictionary<string, string[]>>(saved) is { } parsed)
                foreach (var (host, auds) in parsed)
                    checks[host] = (auds ?? []).OfType<string>().ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Unreadable: nothing is known, so nothing is taken off.
        }
        return checks;
    }

    /// <summary>Saves the checks remembered, when they differ from what's saved.</summary>
    private async Task SaveChecksAsync(Dictionary<string, HashSet<string>> saved, Dictionary<string, HashSet<string>> now, CancellationToken ct)
    {
        if (now.Count == saved.Count && now.All(c => saved.TryGetValue(c.Key, out var auds) && auds.SetEquals(c.Value))) return;
        await settings.SetAsync(SettingKeys.ProtectionChecks, now.Count == 0 ? null
            : JsonSerializer.Serialize(now.OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => c.Value.Order(StringComparer.Ordinal).ToArray())), ct);
    }

    /// <summary>The checks remembered, with the ones this sync puts on protected hosts' rules.</summary>
    private static Dictionary<string, HashSet<string>> WithPlanned(Dictionary<string, HashSet<string>> checks, AccessPlan plan)
    {
        var all = checks.ToDictionary(c => c.Key, c => c.Value.ToHashSet(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        foreach (var (host, protection) in plan.Protect)
        {
            if (!all.TryGetValue(host, out var auds)) all[host] = auds = new HashSet<string>(StringComparer.Ordinal);
            auds.UnionWith(TunnelRoutes.AudsOf(protection.Access));
        }
        return all;
    }

    /// <summary>Of the checks remembered, the ones the tunnel still has on their host's rules: the others need no taking off.</summary>
    private static Dictionary<string, HashSet<string>> StillThere(Dictionary<string, HashSet<string>> checks, JsonArray ingress)
    {
        var there = TunnelRoutes.AccessChecks(ingress);
        return checks
            .Select(c => (Host: c.Key, Auds: c.Value.Where(aud => there.TryGetValue(c.Key, out var on) && on.Contains(aud)).ToHashSet(StringComparer.Ordinal)))
            .Where(c => c.Auds.Count > 0)
            .ToDictionary(c => c.Host, c => c.Auds, StringComparer.OrdinalIgnoreCase);
    }
}
