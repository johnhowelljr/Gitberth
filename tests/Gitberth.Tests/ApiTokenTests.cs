using System.Security.Cryptography;
using System.Text;
using Gitberth.Agents;
using Gitberth.Audit;
using Gitberth.Data;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>
/// API tokens: how they're made and kept (gbt_ and 32 random bytes, only the SHA-256 and the first characters stored),
/// what lets a request in (known, not revoked, not expired), what's recorded in the audit log (made, revoked, expired
/// once, never the token), and the throttle on bad ones.
/// </summary>
public sealed class ApiTokenTests : IDisposable
{
    private readonly AppsRig _rig = new();
    private readonly AuditLog _audit = new();
    private readonly ApiTokens _tokens;
    private DateTime _now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    public ApiTokenTests()
    {
        _tokens = new ApiTokens(_rig.Db, _rig.Settings, _audit) { Now = () => _now };
    }

    public void Dispose() => _rig.Dispose();

    private async Task<(TokenView View, string Secret)> MakeAsync(string name = "Claude Code", ApiAccess access = ApiAccess.Read, int? days = null)
    {
        var made = await _tokens.CreateAsync(name, access, days);
        Assert.Null(made.Problem);
        return (made.Token!, made.Secret!);
    }

    [Fact]
    public void A_token_is_gbt_and_32_random_bytes_in_base64url()
    {
        var a = ApiTokens.NewToken();
        var b = ApiTokens.NewToken();
        Assert.StartsWith("gbt_", a);
        Assert.Equal(47, a.Length);
        Assert.NotEqual(a, b);
        Assert.True(ApiTokens.LooksLikeToken(a));
        Assert.All(a[4..], c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.False(ApiTokens.LooksLikeToken("gbt_short"));
        Assert.False(ApiTokens.LooksLikeToken("xyz_" + a[4..]));
        Assert.False(ApiTokens.LooksLikeToken(null));
    }

    [Fact]
    public void The_hash_is_the_tokens_sha256_in_lowercase_hex_and_the_prefix_its_first_eight_characters_after_gbt()
    {
        const string token = "gbt_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdef";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(), ApiTokens.Hash(token));
        Assert.Equal("AbCdEfGh", ApiTokens.PrefixOf(token));
    }

    [Fact]
    public async Task Only_the_hash_and_the_prefix_are_stored_never_the_token()
    {
        var (view, secret) = await MakeAsync();
        await using var db = _rig.Db.CreateDbContext();
        var row = await db.ApiTokens.AsNoTracking().SingleAsync();
        Assert.Equal(ApiTokens.Hash(secret), row.TokenHash);
        Assert.Equal(secret[4..12], row.Prefix);
        Assert.Equal("gbt_" + row.Prefix + "…", view.Shown);
        var stored = string.Join("|", row.Name, row.TokenHash, row.Prefix, row.Access, row.LastUsedIp);
        Assert.DoesNotContain(secret, stored);
        Assert.DoesNotContain(secret[4..], stored);
    }

    [Fact]
    public async Task A_known_token_gets_in_and_an_unknown_one_doesnt()
    {
        var (_, secret) = await MakeAsync(access: ApiAccess.Operate);
        var ok = await _tokens.CheckAsync(secret);
        Assert.NotNull(ok.Token);
        Assert.Equal(ApiAccess.Operate, ok.Token!.Access);
        Assert.Null(ok.Refusal);

        var other = await _tokens.CheckAsync(ApiTokens.NewToken());
        Assert.Null(other.Token);
        Assert.Contains("isn't known", other.Refusal);
        Assert.Null((await _tokens.CheckAsync("Bearer nonsense")).Token);
    }

    [Fact]
    public async Task A_revoked_token_is_refused_and_its_revoke_is_audited_by_name_never_the_token()
    {
        var (view, secret) = await MakeAsync("ci", ApiAccess.Admin);
        Assert.True(await _tokens.RevokeAsync(view.Id));
        Assert.False(await _tokens.RevokeAsync(view.Id));
        var check = await _tokens.CheckAsync(secret);
        Assert.Null(check.Token);
        Assert.Equal("The token ci was revoked.", check.Refusal);

        var entries = _audit.Entries.Where(e => e.Action.StartsWith("auth.token.", StringComparison.Ordinal)).ToList();
        Assert.Equal(["auth.token.created", "auth.token.revoked"], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal("ci", e.Target));
        Assert.Contains("Admin access", entries[0].Summary);
        Assert.All(entries, e => Assert.DoesNotContain(secret[4..], e.Summary + string.Join("", e.Changes.Select(c => c.From + c.To))));
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_its_expiry_is_recorded_once()
    {
        var (_, secret) = await MakeAsync("nightly", days: 30);
        Assert.NotNull((await _tokens.CheckAsync(secret)).Token);

        _now = _now.AddDays(31);
        var check = await _tokens.CheckAsync(secret);
        Assert.Null(check.Token);
        Assert.Contains("expired", check.Refusal);
        Assert.Equal(0, await _tokens.NoteExpiredAsync());
        await _tokens.CheckAsync(secret);

        var expired = _audit.Entries.Where(e => e.Action == "auth.token.expired").ToList();
        Assert.Single(expired);
        Assert.Equal("nightly", expired[0].Target);
        Assert.Equal(AuditActorKind.Gitberth, expired[0].Actor.Kind);
        Assert.False((await _tokens.ListAsync()).Single().ActiveAt(_now));
    }

    [Fact]
    public async Task A_token_that_never_expires_has_no_expiry_and_one_for_30_days_expires_in_30()
    {
        var (forever, _) = await MakeAsync("forever");
        var (month, _) = await MakeAsync("month", days: 30);
        Assert.Null(forever.ExpiresAt);
        Assert.Equal(_now.AddDays(30), month.ExpiresAt);
    }

    [Fact]
    public async Task A_name_in_use_or_without_letters_is_refused()
    {
        await MakeAsync("Claude Code");
        Assert.NotNull((await _tokens.CreateAsync("claude code", ApiAccess.Read, null)).Problem);
        Assert.NotNull((await _tokens.CreateAsync("  ", ApiAccess.Read, null)).Problem);
        Assert.NotNull((await _tokens.CreateAsync("a|b", ApiAccess.Read, null)).Problem);
        Assert.NotNull((await _tokens.CreateAsync(new string('x', 51), ApiAccess.Read, null)).Problem);
    }

    [Fact]
    public async Task Last_used_is_written_at_most_once_a_minute_per_token()
    {
        var (view, _) = await MakeAsync();
        await _tokens.UsedAsync(view.Id, "10.0.0.5");
        var first = (await _tokens.ListAsync()).Single();
        Assert.Equal(_now, first.LastUsedAt);
        Assert.Equal("10.0.0.5", first.LastUsedIp);

        _now = _now.AddSeconds(30);
        await _tokens.UsedAsync(view.Id, "10.0.0.5");
        Assert.Equal(first.LastUsedAt, (await _tokens.ListAsync()).Single().LastUsedAt);

        _now = _now.AddSeconds(31);
        await _tokens.UsedAsync(view.Id, "10.0.0.5");
        Assert.Equal(_now, (await _tokens.ListAsync()).Single().LastUsedAt);
    }

    [Fact]
    public void Bad_tokens_from_an_address_are_throttled_after_ten_in_fifteen_minutes_and_audited_once_a_minute()
    {
        var throttle = new ApiTokenThrottle();
        var now = DateTime.UtcNow;
        for (var i = 0; i < ApiTokenThrottle.MaxFailures; i++)
        {
            Assert.False(throttle.Blocked("10.0.0.9", now));
            throttle.Failed("10.0.0.9", now);
        }
        Assert.True(throttle.Blocked("10.0.0.9", now));
        Assert.False(throttle.Blocked("10.0.0.10", now));
        Assert.False(throttle.Blocked("10.0.0.9", now + ApiTokenThrottle.Window));

        Assert.True(throttle.ShouldAudit("10.0.0.9", now));
        Assert.False(throttle.ShouldAudit("10.0.0.9", now.AddSeconds(59)));
        Assert.True(throttle.ShouldAudit("10.0.0.9", now.AddSeconds(61)));
    }

    [Fact]
    public void A_callers_access_lets_it_call_its_level_and_below()
    {
        var operate = new ApiCaller(1, "ops", ApiAccess.Operate, AuditVia.Mcp, null);
        Assert.True(operate.May(ApiAccess.Read));
        Assert.True(operate.May(ApiAccess.Operate));
        Assert.False(operate.May(ApiAccess.Admin));
        var back = ApiCaller.From(operate.ToPrincipal());
        Assert.Equal(operate, back);
        Assert.Equal(new AuditActor(AuditActorKind.ApiToken, "ops", AuditVia.Mcp, null), operate.Actor);
        Assert.Equal("the token ops", AuditActors.Describe(operate.Actor));
    }
}
