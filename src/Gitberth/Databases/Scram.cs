using System.Security.Cryptography;
using System.Text;

namespace Gitberth.Databases;

/// <summary>
/// A Postgres password as the server keeps it: a SCRAM-SHA-256 verifier (RFC 5802 and 7677), worked out here, so
/// <c>CREATE ROLE … PASSWORD 'SCRAM-SHA-256$…'</c> never sends the password itself. Postgres stores a verifier it's given
/// as it is, whatever its password_encryption, and checks a client's proof against it as it would its own.
/// </summary>
public static class Scram
{
    /// <summary>Postgres's own iteration count (scram_iterations' default).</summary>
    public const int Iterations = 4096;

    /// <summary>
    /// <c>SCRAM-SHA-256$&lt;iterations&gt;:&lt;salt&gt;$&lt;StoredKey&gt;:&lt;ServerKey&gt;</c>, base64 each, with a new random 16-byte
    /// salt unless one is given. The password is used as its UTF-8 bytes: Gitberth's own passwords are ASCII letters and
    /// digits, which SASLprep leaves as they are.
    /// </summary>
    public static string Verifier(string password, byte[]? salt = null, int iterations = Iterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        salt ??= RandomNumberGenerator.GetBytes(16);
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        return $"SCRAM-SHA-256${iterations.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{Convert.ToBase64String(salt)}"
            + $"${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }

    /// <summary>
    /// Whether <paramref name="password"/> is the one Postgres keeps for <paramref name="user"/> as <paramref name="stored"/>
    /// (pg_authid.rolpassword): a SCRAM-SHA-256 verifier (its StoredKey and ServerKey worked out again with its salt and
    /// iterations), or an older md5 hash ("md5" + md5(password + user)). False for anything else, an empty one included. A
    /// password that SASLprep would change (non-ASCII) may not match: it's then refused, never let through.
    /// </summary>
    public static bool Checks(string password, string user, string stored)
    {
        if (password.Length == 0 || stored.Length == 0) return false;
        if (Parse(stored) is { } v)
        {
            if (v.Iterations is < 1 or > 10_000_000 || v.Salt.Length == 0) return false;
            var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), v.Salt, v.Iterations, HashAlgorithmName.SHA256, 32);
            var storedKey = SHA256.HashData(HMACSHA256.HashData(salted, "Client Key"u8));
            var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
            return CryptographicOperations.FixedTimeEquals(storedKey, v.StoredKey) & CryptographicOperations.FixedTimeEquals(serverKey, v.ServerKey);
        }
        if (stored.Length == 35 && stored.StartsWith("md5", StringComparison.Ordinal))
        {
            var md5 = "md5" + Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + user)));
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(md5), Encoding.ASCII.GetBytes(stored));
        }
        return false;
    }

    /// <summary>A verifier's parts; null when it isn't one.</summary>
    public static (int Iterations, byte[] Salt, byte[] StoredKey, byte[] ServerKey)? Parse(string verifier)
    {
        const string prefix = "SCRAM-SHA-256$";
        if (!verifier.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = verifier[prefix.Length..].Split('$');
        if (parts.Length != 2) return null;
        var head = parts[0].Split(':');
        var keys = parts[1].Split(':');
        if (head.Length != 2 || keys.Length != 2 || !int.TryParse(head[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) return null;
        try
        {
            return (n, Convert.FromBase64String(head[1]), Convert.FromBase64String(keys[0]), Convert.FromBase64String(keys[1]));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
