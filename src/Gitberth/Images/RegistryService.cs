using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Security;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Images;

/// <summary>
/// Sign-ins for registries, for pulls and update checks: one per registry host, verified with Docker (POST /auth) before
/// it's saved, the secret stored encrypted (<see cref="SecretProtector"/>) and never handed back. Docker gets it per call
/// in X-Registry-Auth; it's never logged, put in a plan or written to the journal. docker.io, index.docker.io and
/// registry-1.docker.io are the one Docker Hub.
/// </summary>
public sealed partial class RegistryService
{
    /// <summary>Docker Hub's sign-in address, as docker login sends it.</summary>
    internal const string DockerHubServer = "https://index.docker.io/v1/";

    private readonly IDbContextFactory<GitberthDb> _dbFactory;
    private readonly SecretProtector _protector;
    private readonly IImageEngine _engine;

    public RegistryService(IDbContextFactory<GitberthDb> dbFactory, SecretProtector protector, DockerApi api)
        : this(dbFactory, protector, (IImageEngine)api)
    {
    }

    /// <summary>Signs in through <paramref name="engine"/>: a stand-in, in tests.</summary>
    internal RegistryService(IDbContextFactory<GitberthDb> dbFactory, SecretProtector protector, IImageEngine engine)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _engine = engine;
    }

    /// <summary>Every saved sign-in, by host; one whose secret can't be read any more is marked as needing it again.</summary>
    public async Task<IReadOnlyList<RegistryInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Registries.AsNoTracking().ToListAsync(ct);
        return rows.OrderBy(r => r.Host, StringComparer.Ordinal)
            .Select(r => new RegistryInfo(r.Id, r.Host, r.Username, r.CreatedAt, r.VerifiedAt) { NeedsSecret = _protector.TryUnprotect(r.ProtectedSecret).Length == 0 })
            .ToList();
    }

    /// <summary>
    /// Verified with POST /auth before it's saved; the secret is stored encrypted and never returned. Editing a saved one
    /// without a new secret keeps (and verifies) the saved secret. Nothing is saved when the registry refuses it.
    /// </summary>
    public async Task<RegistrySaved> SaveAsync(RegistryInput input, CancellationToken ct = default)
    {
        if (NormalizeHost(input.Host) is not { } host)
            return new RegistrySaved(false, "Give the registry's address, such as ghcr.io or registry.lan:5000.", null);
        var username = input.Username?.Trim() ?? "";
        if (username.Length == 0) return new RegistrySaved(false, "Give the user name it signs in with.", null);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        RegistryCredential? row = null;
        if (input.Id is int id && (row = await db.Registries.FirstOrDefaultAsync(r => r.Id == id, ct)) is null)
            return new RegistrySaved(false, "That sign-in was removed meanwhile. Add it again.", null);
        if (await db.Registries.AnyAsync(r => r.Host == host && r.Id != (row == null ? 0 : row.Id), ct))
            return new RegistrySaved(false, $"There's a sign-in for {NameOf(host)} already: change that one.", null);

        var secret = input.Secret ?? "";
        if (secret.Length == 0)
        {
            if (row is null) return new RegistrySaved(false, "Give its password or access token.", null);
            secret = _protector.TryUnprotect(row.ProtectedSecret);
            if (secret.Length == 0) return new RegistrySaved(false, "Give its password or access token again: the saved one can't be read any more.", null);
        }

        try
        {
            await _engine.AuthAsync(new JsonObject { ["username"] = username, ["password"] = secret, ["serveraddress"] = ServerAddress(host) }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new RegistrySaved(false, $"{NameOf(host)} refused the sign-in, so nothing was saved. Docker said: {ex.Message}", null);
        }

        if (row is null) db.Registries.Add(row = new RegistryCredential());
        row.Host = host;
        row.Username = username;
        row.ProtectedSecret = _protector.Protect(secret);
        row.VerifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // What the registry refused before may be private to this sign-in: the next look asks again.
        await db.ImageChecks.Where(c => c.Error == ImageOrigins.OnlyHere && c.Reference.StartsWith(host + "/")).ExecuteDeleteAsync(ct);
        return new RegistrySaved(true, $"Signed in to {NameOf(host)} as {username}. Saved.", row.Id);
    }

    /// <summary>Removes the sign-in, and takes it off the apps that named it (they go back to the one for their host, if any).</summary>
    public async Task RemoveAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Apps.Where(a => a.RegistryId == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.RegistryId, (int?)null), ct);
        await db.Registries.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>X-Registry-Auth for the reference (base64url JSON), or null for none.</summary>
    /// <param name="registryId">
    /// The sign-in an app names, used only when it's for the reference's registry (an app whose image moved to another
    /// registry never sends it there); else the one for the reference's registry.
    /// </param>
    internal async Task<string?> AuthHeaderAsync(ImageReference reference, int? registryId = null, CancellationToken ct = default) =>
        await SignInAsync(reference, registryId, ct) is { } signIn ? Header(signIn.Username, signIn.Secret, ServerAddress(reference.Registry)) : null;

    /// <summary>
    /// The sign-in for the reference's registry, chosen as <see cref="AuthHeaderAsync"/> chooses it, for asking the registry
    /// itself (New app reads an image's config before it's pulled); null for none, or one whose secret can't be read.
    /// </summary>
    internal async Task<RegistrySignIn?> SignInAsync(ImageReference reference, int? registryId = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        RegistryCredential? row = null;
        if (registryId is int id) row = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.Host == reference.Registry, ct);
        row ??= await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Host == reference.Registry, ct);
        if (row is null) return null;
        var secret = _protector.TryUnprotect(row.ProtectedSecret);
        return secret.Length == 0 ? null : new RegistrySignIn(row.Username, secret);
    }

    /// <summary>X-Registry-Auth as the docker CLI writes it: {"username","password","serveraddress"} in URL-safe base64.</summary>
    internal static string Header(string username, string password, string serverAddress)
    {
        var json = new JsonObject { ["username"] = username, ["password"] = password, ["serveraddress"] = serverAddress }.ToJsonString();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The address Docker signs in to: Docker Hub's v1 address, or the host itself.</summary>
    internal static string ServerAddress(string host) => host == ImageReference.DockerHub ? DockerHubServer : host;

    /// <summary>
    /// A registry's host as it's stored: lower case, no scheme or path; Docker Hub's names (docker.io, index.docker.io,
    /// registry-1.docker.io, hub.docker.com) as docker.io. Null for one that isn't a host.
    /// </summary>
    internal static string? NormalizeHost(string? text)
    {
        var host = text?.Trim().ToLowerInvariant() ?? "";
        foreach (var scheme in new[] { "https://", "http://" })
            if (host.StartsWith(scheme, StringComparison.Ordinal)) host = host[scheme.Length..];
        var slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];
        if (host is "docker.io" or "index.docker.io" or "registry-1.docker.io" or "hub.docker.com" or "registry.hub.docker.com") return ImageReference.DockerHub;
        return host.Length > 0 && Host().IsMatch(host) ? host : null;
    }

    private static string NameOf(string host) => host == ImageReference.DockerHub ? "Docker Hub" : host;

    [GeneratedRegex(@"^(?:(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)(?:\.(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?))*|\[[0-9a-f:.]+\])(?::[0-9]{1,5})?$")]
    private static partial Regex Host();
}
