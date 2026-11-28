using System.Security.Cryptography;
using System.Text;

namespace Gitberth.Backup;

/// <summary>
/// The key pair automatic backups are sealed to, as Gitberth keeps it (Settings, in clear: none of it opens a file): its
/// ID, its public key, and its private key sealed with the owner's passphrase (PBKDF2 of this salt and iterations, then
/// AES-256-GCM). Gitberth never has the passphrase after the pair is made, so it can make a file but never open one.
/// </summary>
/// <param name="Id">The pair's ID in hex: the first <see cref="BackupFormat.KeyIdBytes"/> bytes of SHA-256 of its public key.</param>
/// <param name="PublicKey">Its public key (SubjectPublicKeyInfo, P-256), base64.</param>
/// <param name="SealedPrivateKey">Its private key (PKCS #8) sealed with the passphrase: nonce, ciphertext and tag, base64.</param>
/// <param name="Salt">PBKDF2's salt for the passphrase, base64.</param>
public sealed record SealedKeyPair(string Id, string PublicKey, string SealedPrivateKey, string Salt, int Iterations, DateTime MadeAt)
{
    /// <summary>The ID as the owner sees it next to each file: its first 8 hex digits.</summary>
    public string ShortId => SealedKeys.ShortId(Convert.FromHexString(Id));
}

/// <summary>
/// Format 2's keys (<see cref="BackupFormat"/>): a key pair made once from the owner's passphrase, and each file's own
/// random key, wrapped for that pair with ECDH-ES (a new ephemeral key per file) and HKDF-SHA256. Opening a file takes its
/// passphrase: it unseals the private key, which unwraps the file's key.
/// </summary>
public static class SealedKeys
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    // What the sealed private key and a file's wrapped key are bound to, besides their key pair or header.
    private static readonly byte[] SealedFor = Encoding.ASCII.GetBytes("gitberth sealed backup key v1");
    private static readonly byte[] WrapInfo = Encoding.ASCII.GetBytes("gitberth backup file key v2");

    /// <summary>"3f9c2ab1": a key pair's ID as the owner sees it.</summary>
    public static string ShortId(ReadOnlySpan<byte> id) => Convert.ToHexStringLower(id)[..Math.Min(8, id.Length * 2)];

    /// <summary>
    /// A new key pair for <paramref name="passphrase"/> (12 characters at least): the private key sealed with it, never
    /// kept in clear. Takes a moment on purpose (PBKDF2).
    /// </summary>
    /// <exception cref="ArgumentException">The passphrase is too short.</exception>
    public static SealedKeyPair Make(string passphrase, int iterations = BackupFormat.DefaultIterations, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < BackupFormat.MinPassphrase)
            throw new ArgumentException($"The passphrase needs {BackupFormat.MinPassphrase} characters at least.", nameof(passphrase));
        using var pair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = pair.ExportSubjectPublicKeyInfo();
        var id = IdOf(publicKey);
        var salt = RandomNumberGenerator.GetBytes(BackupFormat.SaltBytes);
        var secret = pair.ExportPkcs8PrivateKey();
        var kek = BackupFormat.DeriveKey(passphrase, salt, iterations);
        try
        {
            return new SealedKeyPair(Convert.ToHexStringLower(id), Convert.ToBase64String(publicKey), Convert.ToBase64String(Seal(kek, secret, Bound(id))),
                Convert.ToBase64String(salt), iterations, now ?? DateTime.UtcNow);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Whether the passphrase opens the pair (the private key unseals): to check it before something relies on it.</summary>
    public static bool Opens(SealedKeyPair pair, string passphrase)
    {
        try
        {
            var secret = Unseal(Convert.FromBase64String(pair.SealedPrivateKey), Convert.FromBase64String(pair.Salt), pair.Iterations,
                Convert.FromHexString(pair.Id), passphrase);
            CryptographicOperations.ZeroMemory(secret);
            return true;
        }
        catch (BackupFileException)
        {
            return false;
        }
    }

    /// <summary>A new file's header and its random key, wrapped for the pair: no passphrase needed.</summary>
    internal static (BackupHeader Header, byte[] Key) NewFile(SealedKeyPair pair, int chunkSize)
    {
        var id = Convert.FromHexString(pair.Id);
        var recipient = Convert.FromBase64String(pair.PublicKey);
        if (!IdOf(recipient).AsSpan().SequenceEqual(id)) throw new CryptographicException("The key pair's ID isn't its public key's.");
        var fileKey = RandomNumberGenerator.GetBytes(BackupFormat.KeyBytes);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        using var them = ECDiffieHellman.Create();
        them.ImportSubjectPublicKeyInfo(recipient, out _);
        var shared = ephemeral.DeriveRawSecretAgreement(them.PublicKey);
        var draft = new BackupHeader
        {
            Version = BackupFormat.Sealed,
            Iterations = pair.Iterations,
            Salt = Convert.FromBase64String(pair.Salt),
            ChunkSize = chunkSize,
            NoncePrefix = RandomNumberGenerator.GetBytes(BackupFormat.NoncePrefixBytes),
            KeyId = id,
            SealedPrivateKey = Convert.FromBase64String(pair.SealedPrivateKey),
            EphemeralPublicKey = ephemeralPublic,
            WrappedKey = [],
        };
        var wrapKey = WrapKey(shared, ephemeralPublic, recipient);
        try
        {
            var header = new BackupHeader
            {
                Version = draft.Version, Iterations = draft.Iterations, Salt = draft.Salt, ChunkSize = draft.ChunkSize, NoncePrefix = draft.NoncePrefix,
                KeyId = draft.KeyId, SealedPrivateKey = draft.SealedPrivateKey, EphemeralPublicKey = draft.EphemeralPublicKey,
                // Bound to the header's fixed part: its chunk size, nonce prefix, salt and key ID can't be swapped.
                WrappedKey = Seal(wrapKey, fileKey, draft.FixedBytes()),
            };
            return (header, fileKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    /// <summary>
    /// A format 2 file's key from its passphrase. A passphrase that doesn't unseal the private key is WrongPassphrase; a
    /// header whose parts don't belong together (a changed byte) is Damaged.
    /// </summary>
    internal static byte[] FileKey(BackupHeader header, string passphrase)
    {
        if (header.Version != BackupFormat.Sealed || header.KeyId is not { } id || header.SealedPrivateKey is not { } sealedKey
            || header.EphemeralPublicKey is not { } ephemeralPublic || header.WrappedKey is not { } wrapped)
            throw BackupReader.Damaged();
        var secret = Unseal(sealedKey, header.Salt, header.Iterations, id, passphrase);
        try
        {
            using var mine = ECDiffieHellman.Create();
            byte[] recipient;
            try
            {
                mine.ImportPkcs8PrivateKey(secret, out _);
                recipient = mine.ExportSubjectPublicKeyInfo();
            }
            catch (CryptographicException)
            {
                throw BackupReader.Damaged();
            }
            if (!IdOf(recipient).AsSpan().SequenceEqual(id)) throw BackupReader.Damaged();
            using var them = ECDiffieHellman.Create();
            try
            {
                them.ImportSubjectPublicKeyInfo(ephemeralPublic, out _);
            }
            catch (CryptographicException)
            {
                throw BackupReader.Damaged();
            }
            var shared = mine.DeriveRawSecretAgreement(them.PublicKey);
            var wrapKey = WrapKey(shared, ephemeralPublic, recipient);
            try
            {
                return Open(wrapKey, wrapped, header.FixedBytes()) ?? throw BackupReader.Damaged();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(shared);
                CryptographicOperations.ZeroMemory(wrapKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] Unseal(byte[] sealedKey, byte[] salt, int iterations, byte[] id, string passphrase)
    {
        var kek = BackupFormat.DeriveKey(passphrase, salt, iterations);
        try
        {
            return Open(kek, sealedKey, Bound(id)) ?? throw new BackupFileException(UploadProblem.WrongPassphrase, BackupReader.WrongPassphraseMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] IdOf(byte[] publicKey) => SHA256.HashData(publicKey)[..BackupFormat.KeyIdBytes];

    private static byte[] Bound(byte[] id) => [.. SealedFor, .. id];

    // HKDF-SHA256 of the ECDH secret, salted with both public keys: the wrapping key for one file.
    private static byte[] WrapKey(byte[] shared, byte[] ephemeralPublic, byte[] recipient) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, BackupFormat.KeyBytes, [.. ephemeralPublic, .. recipient], WrapInfo);

    // AES-256-GCM: nonce, ciphertext, tag.
    private static byte[] Seal(byte[] key, byte[] plain, byte[] aad)
    {
        var output = new byte[NonceBytes + plain.Length + TagBytes];
        RandomNumberGenerator.Fill(output.AsSpan(0, NonceBytes));
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(output.AsSpan(0, NonceBytes), plain, output.AsSpan(NonceBytes, plain.Length), output.AsSpan(NonceBytes + plain.Length), aad);
        return output;
    }

    // Null when the tag doesn't match: the wrong key, or a changed byte.
    private static byte[]? Open(byte[] key, byte[] sealedBytes, byte[] aad)
    {
        if (sealedBytes.Length < NonceBytes + TagBytes) return null;
        var length = sealedBytes.Length - NonceBytes - TagBytes;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagBytes);
        try
        {
            aes.Decrypt(sealedBytes.AsSpan(0, NonceBytes), sealedBytes.AsSpan(NonceBytes, length), sealedBytes.AsSpan(NonceBytes + length), plain, aad);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(plain);
            return null;
        }
    }
}
