using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Gitberth.Backup;

/// <summary>
/// Reads a backup file (<see cref="BackupFormat"/>) as a stream: each chunk is opened and its tag checked before any of its
/// bytes is handed on, so a wrong passphrase, or a chunk changed, removed, moved or cut off, is refused
/// (<see cref="BackupFileException"/>) before anything of it is used. Parts come one at a time
/// (<see cref="NextPartAsync"/>); a part not read to its end is skipped when the next is asked for.
/// </summary>
public sealed class BackupReader : IDisposable
{
    /// <summary>What a wrong passphrase is told: the first chunk doesn't open.</summary>
    public const string WrongPassphraseMessage = "This passphrase doesn't open the file.";

    /// <summary>What a damaged file is told.</summary>
    public const string DamagedMessage = "This backup file is damaged: part of it was changed, removed or moved, or it ends early. None of it is used.";

    private readonly Stream _input;
    private readonly AesGcm _aes;
    private readonly byte[] _aad;
    private readonly byte[] _prefix;
    private readonly int _chunkSize;
    private readonly byte[] _sealed;
    private readonly byte[] _plain;
    private readonly byte[] _nonce = new byte[12];
    private readonly byte[] _lengthBuffer = new byte[4];
    private int _plainPos;
    private int _plainLen;
    private uint _counter;
    private bool _sawLast;
    private uint? _nextLength;
    private BackupPart? _current;

    private BackupReader(Stream input, BackupHeader header, BackupKey key, byte[] aad)
    {
        _input = input;
        Header = header;
        _aes = new AesGcm(key.Bytes, BackupFormat.TagBytes);
        _aad = aad;
        _prefix = header.NoncePrefix;
        _chunkSize = header.ChunkSize;
        _sealed = new byte[header.ChunkSize + BackupFormat.TagBytes];
        _plain = new byte[header.ChunkSize];
    }

    public BackupHeader Header { get; }

    /// <summary>The plaintext handed on so far (the parts' headers and frames).</summary>
    public long PlainBytesRead { get; private set; }

    /// <summary>
    /// Opens the file with the passphrase: its header read, the key made, and the first chunk opened, which a wrong
    /// passphrase doesn't.
    /// </summary>
    public static async Task<BackupReader> OpenAsync(Stream input, string passphrase, CancellationToken ct = default)
    {
        var (header, headerBytes) = await ReadHeaderAsync(input, ct);
        using var key = await Task.Run(() => BackupKey.ForFile(header, passphrase), ct);
        return await OpenAsync(input, header, headerBytes, key, ct);
    }

    /// <summary>Opens the file with a key made for it before (the same file read again: its key isn't made twice).</summary>
    public static async Task<BackupReader> OpenAsync(Stream input, BackupKey key, CancellationToken ct = default)
    {
        var (header, headerBytes) = await ReadHeaderAsync(input, ct);
        if (!headerBytes.AsSpan().SequenceEqual(key.Header.ToBytes())) throw Damaged();
        return await OpenAsync(input, header, headerBytes, key, ct);
    }

    /// <summary>The file's header, read from its start.</summary>
    public static async Task<(BackupHeader Header, byte[] Bytes)> ReadHeaderAsync(Stream input, CancellationToken ct = default)
    {
        var headerBytes = new byte[BackupFormat.HeaderBytes];
        var read = await input.ReadAtLeastAsync(headerBytes, headerBytes.Length, throwOnEndOfStream: false, ct);
        if (read < 10) throw new BackupFileException(UploadProblem.NotABackup, "This isn't a Gitberth backup file.");
        return (BackupHeader.Parse(headerBytes.AsSpan(0, read)), headerBytes);
    }

    private static async Task<BackupReader> OpenAsync(Stream input, BackupHeader header, byte[] headerBytes, BackupKey key, CancellationToken ct)
    {
        var reader = new BackupReader(input, header, key, headerBytes);
        try
        {
            await reader.FillAsync(ct);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the whole file and checks every chunk, using nothing of it: a restore does this first, so a damaged file is
    /// refused before anything is restored. Returns the parts' names and kinds.
    /// </summary>
    public static async Task<IReadOnlyList<(BackupPartKind Kind, string Name)>> VerifyAsync(Stream input, string passphrase, CancellationToken ct = default)
    {
        using var reader = await OpenAsync(input, passphrase, ct);
        return await VerifyAsync(reader, ct);
    }

    /// <summary>As <see cref="VerifyAsync(Stream, string, CancellationToken)"/>, with the file's key made before.</summary>
    public static async Task<IReadOnlyList<(BackupPartKind Kind, string Name)>> VerifyAsync(Stream input, BackupKey key, CancellationToken ct = default)
    {
        using var reader = await OpenAsync(input, key, ct);
        return await VerifyAsync(reader, ct);
    }

    private static async Task<IReadOnlyList<(BackupPartKind Kind, string Name)>> VerifyAsync(BackupReader reader, CancellationToken ct)
    {
        var parts = new List<(BackupPartKind, string)>();
        while (await reader.NextPartAsync(ct) is { } part)
        {
            parts.Add((part.Kind, part.Name));
            await part.SkipAsync(ct);
        }
        return parts;
    }

    /// <summary>
    /// The next part, or null after the last. The one before it is skipped (read and checked to its end) when it wasn't
    /// read to its end. Its <see cref="BackupPart.Content"/> is its original bytes, decompressed.
    /// </summary>
    public async Task<BackupPart?> NextPartAsync(CancellationToken ct = default)
    {
        if (_current is not null)
        {
            await _current.SkipAsync(ct);
            _current.Close();
            _current = null;
        }
        var first = new byte[1];
        if (await ReadPlainAsync(first, ct) == 0) return null;
        var kind = (BackupPartKind)first[0];
        var lengthBytes = new byte[2];
        await ReadPlainExactlyAsync(lengthBytes, ct);
        var nameBytes = new byte[BinaryPrimitives.ReadUInt16BigEndian(lengthBytes)];
        await ReadPlainExactlyAsync(nameBytes, ct);
        var rest = new byte[1 + 8];
        await ReadPlainExactlyAsync(rest, ct);
        var codec = (BackupCodec)rest[0];
        if (codec is not (BackupCodec.Stored or BackupCodec.Brotli))
            throw new BackupFileException(UploadProblem.TooNew, "This backup holds data compressed in a way this Gitberth doesn't know. Update Gitberth, then restore it.");
        var length = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(rest.AsSpan(1)), long.MaxValue);
        return _current = new BackupPart(this, kind, Encoding.UTF8.GetString(nameBytes), codec, length);
    }

    public void Dispose()
    {
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_plain);
    }

    // ---- the plaintext: chunks opened one at a time ----

    internal async ValueTask<int> ReadPlainAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length == 0) return 0;
        while (_plainPos == _plainLen)
            if (!await FillAsync(ct)) return 0;
        var n = Math.Min(buffer.Length, _plainLen - _plainPos);
        _plain.AsMemory(_plainPos, n).CopyTo(buffer);
        _plainPos += n;
        PlainBytesRead += n;
        return n;
    }

    internal async ValueTask ReadPlainExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (buffer.Length > 0)
        {
            var n = await ReadPlainAsync(buffer, ct);
            // The plaintext ended inside a part: only a writer that never finished does that, and its file has no last chunk.
            if (n == 0) throw Damaged();
            buffer = buffer[n..];
        }
    }

    // The next chunk, opened and checked; false after the last. Whether a chunk is the last is read from the file itself
    // (nothing follows it), and its nonce says so too: a file cut short at a chunk's end, or with something after its
    // last chunk, fails that chunk's tag.
    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        if (_sawLast) return false;
        uint length;
        if (_nextLength is { } known)
        {
            length = known;
        }
        else
        {
            if (await _input.ReadAtLeastAsync(_lengthBuffer, 4, throwOnEndOfStream: false, ct) < 4) throw Damaged();
            length = BinaryPrimitives.ReadUInt32BigEndian(_lengthBuffer);
        }
        if (length > _chunkSize) throw Damaged();
        var size = (int)length + BackupFormat.TagBytes;
        if (await _input.ReadAtLeastAsync(_sealed.AsMemory(0, size), size, throwOnEndOfStream: false, ct) < size) throw Damaged();
        var ahead = await _input.ReadAtLeastAsync(_lengthBuffer, 4, throwOnEndOfStream: false, ct);
        bool last;
        switch (ahead)
        {
            case 0:
                last = true;
                _nextLength = null;
                break;
            case 4:
                last = false;
                _nextLength = BinaryPrimitives.ReadUInt32BigEndian(_lengthBuffer);
                break;
            default:
                throw Damaged();
        }
        BackupFormat.Nonce(_nonce, _prefix, _counter, last);
        try
        {
            _aes.Decrypt(_nonce, _sealed.AsSpan(0, (int)length), _sealed.AsSpan((int)length, BackupFormat.TagBytes), _plain.AsSpan(0, (int)length), _aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            // The first chunk not opening is nearly always the wrong passphrase (or a changed header), unless it opens as
            // the other kind of chunk: then the key is right, and the file was cut short after it or added to.
            if (_counter != 0) throw Damaged();
            BackupFormat.Nonce(_nonce, _prefix, _counter, !last);
            try
            {
                _aes.Decrypt(_nonce, _sealed.AsSpan(0, (int)length), _sealed.AsSpan((int)length, BackupFormat.TagBytes), _plain.AsSpan(0, (int)length), _aad);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new BackupFileException(UploadProblem.WrongPassphrase, WrongPassphraseMessage);
            }
            CryptographicOperations.ZeroMemory(_plain);
            throw Damaged();
        }
        _counter++;
        _sawLast = last;
        _plainPos = 0;
        _plainLen = (int)length;
        return true;
    }

    internal static BackupFileException Damaged() => new(UploadProblem.Damaged, DamagedMessage);
}

/// <summary>One part of a backup file, as it's read: its content is the original bytes, decompressed.</summary>
public sealed class BackupPart
{
    private readonly FrameSource _frames;

    internal BackupPart(BackupReader reader, BackupPartKind kind, string name, BackupCodec codec, long length)
    {
        Kind = kind;
        Name = name;
        Codec = codec;
        Length = length;
        _frames = new FrameSource(reader);
        Content = codec == BackupCodec.Brotli ? new BrotliStream(_frames, CompressionMode.Decompress, leaveOpen: true) : _frames;
    }

    public BackupPartKind Kind { get; }
    public string Name { get; }
    public BackupCodec Codec { get; }

    /// <summary>Its original length as saved; 0 when it wasn't known.</summary>
    public long Length { get; }

    /// <summary>The part's bytes, read once, to their end.</summary>
    public Stream Content { get; }

    /// <summary>Reads (and checks) what's left of the part, using none of it.</summary>
    public async Task SkipAsync(CancellationToken ct = default)
    {
        var buffer = new byte[64 * 1024];
        while (await _frames.ReadAsync(buffer, ct) > 0)
        {
        }
    }

    // The decompressor's native state goes with the part.
    internal void Close()
    {
        if (Content is BrotliStream brotli) brotli.Dispose();
    }

    /// <summary>The frames of one part, as a stream that ends at its frame of length 0.</summary>
    private sealed class FrameSource(BackupReader reader) : Stream
    {
        private readonly byte[] _length = new byte[4];
        private int _left;
        private bool _ended;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            while (_left == 0)
            {
                if (_ended) return 0;
                await reader.ReadPlainExactlyAsync(_length, ct);
                var length = BinaryPrimitives.ReadUInt32BigEndian(_length);
                if (length > BackupFormat.MaxFrameBytes) throw BackupReader.Damaged();
                if (length == 0) _ended = true;
                _left = (int)length;
            }
            var n = await reader.ReadPlainAsync(buffer[..Math.Min(buffer.Length, _left)], ct);
            if (n == 0) throw BackupReader.Damaged();
            _left -= n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
