using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Gitberth.Security;

/// <summary>Encrypts secrets at rest (GitHub token, env var values) with ASP.NET Core Data Protection.</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Gitberth.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);

    /// <summary>Returns "" when the value can't be decrypted (e.g. the key ring was lost).</summary>
    public string TryUnprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try { return _protector.Unprotect(protectedValue); }
        catch (CryptographicException) { return ""; }
    }
}
