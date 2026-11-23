using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Gitberth.Backup;

/// <summary>
/// Writes a backup file (<see cref="BackupFormat"/>) to a stream as it's made, nothing of it on disk: the header, then each
/// part compressed and framed, sealed in chunks of AES-256-GCM. Every write is asynchronous (it may be a download's
/// response). <see cref="FinishAsync"/> seals the last chunk: a file whose writer never finished is refused when read.
/// </summary>
public sealed class BackupWriter : IDisposable
{
    private readonly Stream _output;
    private readonly AesGcm _aes;
    private readonly byte[] _aad;
    private readonly byte[] _prefix;
    private readonly byte[] _chunk;
    private readonly byte[] _sealed;
    private readonly byte[] _nonce = new byte[12];
    private int _filled;
    private uint _counter;
    private bool _started;
    private bool _finished;
    private bool _inPart;

    /// <param name="key">The file's key and header: one key for one file, never two (its nonces would repeat).</param>
    public BackupWriter(Stream output, BackupKey key)
    {
        _output = output;
        _aes = new AesGcm(key.Bytes, BackupFormat.TagBytes);
        _aad = key.Header.ToBytes();
        _prefix = key.Header.NoncePrefix;
        _chunk = new byte[key.Header.ChunkSize];
        _sealed = new byte[4 + key.Header.ChunkSize + BackupFormat.TagBytes];
    }

    /// <summary>The file's bytes written so far.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>Each part's codec as it was chosen, by name (for the tests and the log).</summary>
    public Dictionary<string, BackupCodec> Codecs { get; } = new(StringComparer.Ordinal);

    /// <summary>The plaintext as it's sealed (tests only: what a damaged file must never hand out).</summary>
    internal Action<ReadOnlyMemory<byte>>? PlainTap { get; init; }

    /// <summary>
    /// One part: its bytes are whatever <paramref name="write"/> writes to the stream it's given (asynchronously), compressed
    /// unless they don't shrink.
    /// </summary>
    /// <param name="length">Its original length when known, else 0.</param>
    public async Task WritePartAsync(BackupPartKind kind, string name, long length, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        if (_finished) throw new InvalidOperationException("The backup file is finished.");
        if (_inPart) throw new InvalidOperationException("A part is being written already.");
        _inPart = true;
        try
        {
            await StartAsync(ct);
            await using var sink = new PartSink(this, kind, name, Math.Max(0, length));
            await write(sink, ct);
            await sink.CompleteAsync(ct);
            Codecs[name] = sink.Codec;
        }
        finally
        {
            _inPart = false;
        }
    }

    /// <summary>One part from a stream, read to its end.</summary>
    public Task WritePartAsync(BackupPartKind kind, string name, long length, Stream content, CancellationToken ct) =>
        WritePartAsync(kind, name, length, (sink, token) => content.CopyToAsync(sink, 128 * 1024, token), ct);

    /// <summary>Seals the last chunk: the file is whole.</summary>
    public async Task FinishAsync(CancellationToken ct)
    {
        if (_finished) return;
        if (_inPart) throw new InvalidOperationException("A part is being written.");
        await StartAsync(ct);
        await SealAsync(last: true, ct);
        _finished = true;
        await _output.FlushAsync(ct);
    }

    public void Dispose()
    {
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_chunk);
    }

    private async ValueTask StartAsync(CancellationToken ct)
    {
        if (_started) return;
        _started = true;
        await _output.WriteAsync(_aad, ct);
        BytesWritten += _aad.Length;
    }

    // Plaintext into the chunk being gathered; a full chunk is sealed once more comes, so the last one is always known.
    internal async ValueTask PlainAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        PlainTap?.Invoke(data);
        while (data.Length > 0)
        {
            if (_filled == _chunk.Length) await SealAsync(last: false, ct);
            var n = Math.Min(data.Length, _chunk.Length - _filled);
            data.Span[..n].CopyTo(_chunk.AsSpan(_filled));
            _filled += n;
            data = data[n..];
        }
    }

    private async ValueTask SealAsync(bool last, CancellationToken ct)
    {
        BackupFormat.Nonce(_nonce, _prefix, _counter, last);
        BinaryPrimitives.WriteUInt32BigEndian(_sealed, (uint)_filled);
        _aes.Encrypt(_nonce, _chunk.AsSpan(0, _filled), _sealed.AsSpan(4, _filled), _sealed.AsSpan(4 + _filled, BackupFormat.TagBytes), _aad);
        var size = 4 + _filled + BackupFormat.TagBytes;
        await _output.WriteAsync(_sealed.AsMemory(0, size), ct);
        BytesWritten += size;
        _filled = 0;
        _counter = _counter == uint.MaxValue ? throw new InvalidOperationException("The backup file is too large.") : _counter + 1;
    }

    /// <summary>
    /// A part's bytes as they come: the first MiB is held to choose the codec (Brotli unless it doesn't shrink by 5%), then
    /// the part's header goes out and everything after it, compressed or not, in frames.
    /// </summary>
    private sealed class PartSink(BackupWriter writer, BackupPartKind kind, string name, long length) : Stream
    {
        private byte[]? _probe = new byte[BackupFormat.ProbeBytes];
        private int _probed;
        private FrameSink? _frames;
        private BrotliStream? _brotli;

        public BackupCodec Codec { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_probe is not null)
            {
                var n = Math.Min(buffer.Length, _probe.Length - _probed);
                buffer.Span[..n].CopyTo(_probe.AsSpan(_probed));
                _probed += n;
                buffer = buffer[n..];
                if (_probed < _probe.Length) return;
                await DecideAsync(ct);
            }
            if (buffer.Length > 0) await TargetWriteAsync(buffer, ct);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        // Every write is asynchronous: the file may be a download's response, which refuses synchronous writes.
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("A backup part is written asynchronously.");

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public async Task CompleteAsync(CancellationToken ct)
        {
            if (_probe is not null) await DecideAsync(ct);
            if (_brotli is not null)
            {
                await _brotli.DisposeAsync();
                _brotli = null;
            }
            await _frames!.CompleteAsync(ct);
        }

        private ValueTask TargetWriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
            _brotli is not null ? _brotli.WriteAsync(data, ct) : _frames!.WriteAsync(data, ct);

        // Brotli when the first MiB (or the whole part, when it's smaller) shrinks by 5% at least; stored otherwise.
        private async Task DecideAsync(CancellationToken ct)
        {
            var probe = _probe!;
            _probe = null;
            Codec = Shrinks(probe.AsSpan(0, _probed)) ? BackupCodec.Brotli : BackupCodec.Stored;

            var nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > ushort.MaxValue) throw new ArgumentException("A part's name is too long.", nameof(name));
            var header = new byte[1 + 2 + nameBytes.Length + 1 + 8];
            header[0] = (byte)kind;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(1), (ushort)nameBytes.Length);
            nameBytes.CopyTo(header, 3);
            header[3 + nameBytes.Length] = (byte)Codec;
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(4 + nameBytes.Length), (ulong)length);
            await writer.PlainAsync(header, ct);

            _frames = new FrameSink(writer);
            if (Codec == BackupCodec.Brotli)
                _brotli = new BrotliStream(_frames, new BrotliCompressionOptions { Quality = BackupFormat.BrotliQuality }, leaveOpen: true);
            if (_probed > 0) await TargetWriteAsync(probe.AsMemory(0, _probed), ct);
        }

        internal static bool Shrinks(ReadOnlySpan<byte> sample)
        {
            if (sample.Length == 0) return false;
            var room = new byte[BrotliEncoder.GetMaxCompressedLength(sample.Length)];
            return BrotliEncoder.TryCompress(sample, room, out var written, BackupFormat.BrotliQuality, BackupFormat.BrotliWindow)
                && written <= sample.Length * 0.95;
        }

        // A part that failed half-way writes nothing more: its compressor's last bytes are dropped, not sent after the error.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _frames?.Abandon();
                _brotli?.Dispose();
                _brotli = null;
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            _frames?.Abandon();
            if (_brotli is not null) await _brotli.DisposeAsync();
            _brotli = null;
            await base.DisposeAsync();
        }
    }

    /// <summary>A part's (compressed) bytes as frames [u32 length][bytes], up to 256 KiB each, then a frame of length 0.</summary>
    private sealed class FrameSink(BackupWriter writer) : Stream
    {
        private const int FrameSize = 256 * 1024;
        private readonly byte[] _frame = new byte[4 + FrameSize];
        private int _filled;
        private bool _abandoned;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <summary>From now on what comes is dropped (the part is over: completed, or failed).</summary>
        public void Abandon() => _abandoned = true;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_abandoned) return;
            while (buffer.Length > 0)
            {
                var n = Math.Min(buffer.Length, FrameSize - _filled);
                buffer.Span[..n].CopyTo(_frame.AsSpan(4 + _filled));
                _filled += n;
                buffer = buffer[n..];
                if (_filled == FrameSize) await EmitAsync(ct);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_abandoned) throw new NotSupportedException("A backup part is written asynchronously.");
        }

        // Brotli flushes only what it has; the frames go out when full, or at the end.
        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public async Task CompleteAsync(CancellationToken ct)
        {
            if (_filled > 0) await EmitAsync(ct);
            await writer.PlainAsync(new byte[4], ct);
            _abandoned = true;
        }

        private async ValueTask EmitAsync(CancellationToken ct)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_frame, (uint)_filled);
            await writer.PlainAsync(_frame.AsMemory(0, 4 + _filled), ct);
            _filled = 0;
        }
    }
}
