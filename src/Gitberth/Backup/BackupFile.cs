using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Gitberth.Backup;

/// <summary>What a part of a backup file holds.</summary>
public enum BackupPartKind : byte
{
    /// <summary>The manifest (JSON): what the backup holds and how to bring it back. Always the first part.</summary>
    Manifest = 1,

    /// <summary>One of Gitberth's own files (a SQLite snapshot of gitberth.db, stats.db, uptime.db or audit.db).</summary>
    DataFile = 2,

    /// <summary>One of Gitberth's own folders as a tar (keys, compose, logs).</summary>
    DataFolder = 3,

    /// <summary>A volume's files as Docker's archive gives them: a tar whose entries start with v/.</summary>
    Volume = 4,

    /// <summary>A database server's dump (pg_dumpall's or mysqldump's SQL).</summary>
    Dump = 5,

    /// <summary>An image only this host had, as docker save writes it.</summary>
    Image = 6,
}

/// <summary>How a part's bytes are kept: compressed, or as they are when they don't shrink (photos, video, archives).</summary>
public enum BackupCodec : byte
{
    Stored = 0,

    /// <summary>Brotli at quality 4 (.NET 10 has no zstd of its own): fast, and close to zstd's ratio on text and databases.</summary>
    Brotli = 1,
}

/// <summary>A backup file that can't be read: why (not a backup, the wrong passphrase, damaged, too new), in one sentence.</summary>
public sealed class BackupFileException(UploadProblem problem, string message) : Exception(message)
{
    public UploadProblem Problem { get; } = problem;
}

/// <summary>
/// The .gbak file format (format 1). A plain header of 43 bytes, then the body: chunks of
/// <c>[u32 sealed length][ciphertext][16-byte tag]</c>, each sealing up to <see cref="ChunkBytes"/> of plaintext with
/// AES-256-GCM. The key comes from the passphrase through PBKDF2-HMAC-SHA256; each chunk's nonce is the header's 7-byte
/// prefix, the chunk's number (u32, big-endian) and a last-chunk flag (1 on the final chunk only); every chunk's additional
/// data is the whole header. So a chunk changed, removed, moved or missing at the end, or a header changed, fails its
/// tag before any of its bytes is used. Integers are big-endian.
/// <para>
/// The plaintext is a sequence of parts, the manifest first. A part's header: u8 kind, u16 name length and the UTF-8 name,
/// u8 codec, u64 original length (0 when unknown); then its bytes as frames <c>[u32 length][bytes]</c>, ending with a
/// frame of length 0. Every part is compressed (Brotli, quality 4) unless its first MiB doesn't shrink by 5%: then it's
/// stored as it is, which costs no CPU.
/// </para>
/// </summary>
public static class BackupFormat
{
    /// <summary>"GBAK", then CR LF, Ctrl-Z, LF: a text transfer or a type command shows it's mangled.</summary>
    public static ReadOnlySpan<byte> Magic => [(byte)'G', (byte)'B', (byte)'A', (byte)'K', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The format this Gitberth writes and reads.</summary>
    public const ushort Version = 1;

    /// <summary>The key derivation: PBKDF2 with HMAC-SHA256.</summary>
    public const byte KdfPbkdf2Sha256 = 1;

    /// <summary>PBKDF2's iterations for a new file (OWASP's figure for HMAC-SHA256).</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>The most iterations a file may ask for: a forged header can't make opening it take hours.</summary>
    public const int MaxIterations = 10_000_000;

    public const int SaltBytes = 16;

    /// <summary>The cipher: AES-256-GCM.</summary>
    public const byte CipherAes256Gcm = 1;

    /// <summary>The plaintext a chunk seals: 1 MiB.</summary>
    public const int ChunkBytes = 1 << 20;

    /// <summary>The largest chunk a file may declare (a reader allocates that much).</summary>
    public const int MaxChunkBytes = 16 << 20;

    public const int NoncePrefixBytes = 7;
    public const int TagBytes = 16;
    public const int KeyBytes = 32;

    /// <summary>The header's size: magic 8, format 2, kdf 1, iterations 4, salt 16, cipher 1, chunk size 4, nonce prefix 7.</summary>
    public const int HeaderBytes = 8 + 2 + 1 + 4 + SaltBytes + 1 + 4 + NoncePrefixBytes;

    /// <summary>The shortest passphrase a save takes.</summary>
    public const int MinPassphrase = 12;

    /// <summary>Brotli's quality for every compressed part, and its window (4 MiB).</summary>
    public const int BrotliQuality = 4;
    public const int BrotliWindow = 22;

    /// <summary>How much of a part is tried before choosing its codec: its first MiB.</summary>
    public const int ProbeBytes = 1 << 20;

    /// <summary>The compressor as the manifest names it.</summary>
    public const string Compression = "brotli quality 4 (parts whose first MiB doesn't shrink by 5% are stored)";

    /// <summary>The largest frame a part may hold.</summary>
    public const int MaxFrameBytes = 4 << 20;

    /// <summary>A backup file's extension.</summary>
    public const string Extension = ".gbak";

    /// <summary>The key for <paramref name="passphrase"/> (NFC-normalized, UTF-8) with the header's salt and iterations.</summary>
    public static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations)
    {
        var secret = Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Chunk <paramref name="counter"/>'s nonce: the prefix, the counter (big-endian) and the last-chunk flag.</summary>
    internal static void Nonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, uint counter, bool last)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[NoncePrefixBytes..], counter);
        nonce[NoncePrefixBytes + 4] = last ? (byte)1 : (byte)0;
    }
}

/// <summary>A backup file's plain header: how its key is made and how its body is sealed.</summary>
public sealed class BackupHeader
{
    public required int Iterations { get; init; }
    public required byte[] Salt { get; init; }
    public required int ChunkSize { get; init; }
    public required byte[] NoncePrefix { get; init; }

    /// <summary>A new file's header: a fresh salt and nonce prefix.</summary>
    public static BackupHeader New(int iterations = BackupFormat.DefaultIterations, int chunkSize = BackupFormat.ChunkBytes) => new()
    {
        Iterations = iterations,
        Salt = RandomNumberGenerator.GetBytes(BackupFormat.SaltBytes),
        ChunkSize = chunkSize,
        NoncePrefix = RandomNumberGenerator.GetBytes(BackupFormat.NoncePrefixBytes),
    };

    /// <summary>The header's bytes, as written at the start of the file and given to every chunk as its additional data.</summary>
    public byte[] ToBytes()
    {
        var b = new byte[BackupFormat.HeaderBytes];
        var s = b.AsSpan();
        BackupFormat.Magic.CopyTo(s);
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], BackupFormat.Version);
        s[10] = BackupFormat.KdfPbkdf2Sha256;
        BinaryPrimitives.WriteUInt32BigEndian(s[11..], (uint)Iterations);
        Salt.CopyTo(s[15..]);
        s[31] = BackupFormat.CipherAes256Gcm;
        BinaryPrimitives.WriteUInt32BigEndian(s[32..], (uint)ChunkSize);
        NoncePrefix.CopyTo(s[36..]);
        return b;
    }

    /// <summary>The header at the start of a file; a <see cref="BackupFileException"/> says why it isn't one Gitberth reads.</summary>
    public static BackupHeader Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 10 || !b[..8].SequenceEqual(BackupFormat.Magic))
            throw new BackupFileException(UploadProblem.NotABackup, "This isn't a Gitberth backup file.");
        var version = BinaryPrimitives.ReadUInt16BigEndian(b[8..]);
        if (version > BackupFormat.Version)
            throw new BackupFileException(UploadProblem.TooNew, "This backup was made by a newer Gitberth. Update Gitberth, then restore it.");
        if (version != BackupFormat.Version || b.Length < BackupFormat.HeaderBytes)
            throw new BackupFileException(UploadProblem.NotABackup, "This isn't a Gitberth backup file.");
        if (b[10] != BackupFormat.KdfPbkdf2Sha256 || b[31] != BackupFormat.CipherAes256Gcm)
            throw new BackupFileException(UploadProblem.TooNew, "This backup is encrypted in a way this Gitberth doesn't know. Update Gitberth, then restore it.");
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(b[11..]);
        var chunk = BinaryPrimitives.ReadUInt32BigEndian(b[32..]);
        if (iterations is 0 or > BackupFormat.MaxIterations || chunk is 0 or > BackupFormat.MaxChunkBytes)
            throw new BackupFileException(UploadProblem.Damaged, "This backup's header is damaged.");
        return new BackupHeader
        {
            Iterations = (int)iterations,
            Salt = b[15..31].ToArray(),
            ChunkSize = (int)chunk,
            NoncePrefix = b[36..43].ToArray(),
        };
    }
}

/// <summary>
/// A backup's key and header: made once from the passphrase (PBKDF2, which takes a moment on purpose), kept only for one
/// save or one restore, and wiped when it's done.
/// </summary>
public sealed class BackupKey : IDisposable
{
    private readonly byte[] _key;

    private BackupKey(BackupHeader header, byte[] key)
    {
        Header = header;
        _key = key;
    }

    public BackupHeader Header { get; }

    internal ReadOnlySpan<byte> Bytes => _key;

    /// <summary>A key for a new file: a new header, and the passphrase's key under it.</summary>
    public static BackupKey ForNewFile(string passphrase, int iterations = BackupFormat.DefaultIterations, int chunkSize = BackupFormat.ChunkBytes)
    {
        var header = BackupHeader.New(iterations, chunkSize);
        return new BackupKey(header, BackupFormat.DeriveKey(passphrase, header.Salt, header.Iterations));
    }

    /// <summary>The passphrase's key for a file with this header.</summary>
    public static BackupKey ForFile(BackupHeader header, string passphrase) =>
        new(header, BackupFormat.DeriveKey(passphrase, header.Salt, header.Iterations));

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
