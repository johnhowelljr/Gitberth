using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gitberth.Data;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Deploy;

/// <summary>
/// Keys the build steps that see secret values to those values. BuildKit leaves a secret mount out of its build cache,
/// so without this a changed secret would reuse layers built with the old one, and a promoted preview's commit (or a
/// branch at production's commit) would reuse layers built with the other scope's value. The build arg that does it
/// (<see cref="DockerfileMarkers.SecretsArg"/>) is an HMAC of the values under this installation's own random key: the
/// image's history shows build args, and a plain hash of a short secret could be guessed from it.
/// </summary>
public static class BuildSecrets
{
    /// <summary>HMAC-SHA256 of the secrets' KEY=value pairs, sorted by name, under <paramref name="key"/>, in hex.</summary>
    public static string Stamp(byte[] key, IEnumerable<EnvValue> secrets)
    {
        // As JSON, so no two sets of values can read as the same text.
        var pairs = JsonSerializer.Serialize(secrets.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}"));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(pairs)));
    }

    /// <summary>This installation's key: made on first use and kept, encrypted, in the Settings table. Never shown.</summary>
    public static async Task<byte[]> KeyAsync(IDbContextFactory<GitberthDb> dbFactory, SecretProtector protector, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.BuildSecretsKey, ct);
            if (row is not null && Decode(protector.TryUnprotect(row.Value)) is { } kept) return kept;
            var key = RandomNumberGenerator.GetBytes(32);
            var value = protector.Protect(Convert.ToBase64String(key));
            if (row is null) db.Settings.Add(new Setting { Key = SettingKeys.BuildSecretsKey, Value = value });
            // One that can't be read any more (the encryption keys were lost) is replaced: builds miss the cache once.
            else row.Value = value;
            try
            {
                await db.SaveChangesAsync(ct);
                return key;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Another build made one at the same moment: that one is read back.
            }
        }
    }

    private static byte[]? Decode(string text)
    {
        try
        {
            return text.Length > 0 ? Convert.FromBase64String(text) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
