using System.Buffers.Binary;
using System.Text;
using Gitberth.Backup;

namespace Gitberth.Tests;

/// <summary>
/// The backup file format (.gbak): every backup is compressed and encrypted, nothing of what it holds (names included) can
/// be read in it, compressible contents give a much smaller file, and a wrong passphrase or a file changed, cut short or put
/// together wrongly is refused before any of its bytes is used.
/// </summary>
public sealed class BackupFileTests
{
    private const string Passphrase = "correct horse battery staple";

    // Few iterations, small chunks: many chunks for little data, and quick tests. One test checks the defaults themselves.
    private const int Iterations = 1_000;
    private const int Chunk = 4096;

    private sealed record Part(BackupPartKind Kind, string Name, byte[] Content);

    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // Text a database dump or a log looks like: compresses well, but isn't one repeated line.
    private static byte[] DumpLike(int size, string marker = "s3cr3t-api-key")
    {
        var rng = new Random(7);
        var text = new StringBuilder();
        var n = 0;
        while (text.Length < size)
            text.Append("INSERT INTO public.orders (id, customer, note, total) VALUES (").Append(n++).Append(", 'customer-")
                .Append(rng.Next(1000)).Append("', '").Append(marker).Append("', ").Append(rng.Next(100_000) / 100.0).Append(");\n");
        return Encoding.UTF8.GetBytes(text.ToString(0, size));
    }

    private static async Task<(byte[] File, byte[] Plain, Dictionary<string, BackupCodec> Codecs)> WriteAsync(
        IEnumerable<Part> parts, int iterations = Iterations, int chunk = Chunk, string passphrase = Passphrase)
    {
        using var key = BackupKey.ForNewFile(passphrase, iterations, chunk);
        var output = new MemoryStream();
        var plain = new MemoryStream();
        using var writer = new BackupWriter(output, key) { PlainTap = data => plain.Write(data.Span) };
        foreach (var part in parts)
            await writer.WritePartAsync(part.Kind, part.Name, part.Content.Length, new MemoryStream(part.Content), default);
        await writer.FinishAsync(default);
        Assert.Equal(output.Length, writer.BytesWritten);
        return (output.ToArray(), plain.ToArray(), writer.Codecs);
    }

    private static async Task<List<(BackupPartKind Kind, string Name, BackupCodec Codec, long Length, byte[] Content)>> ReadAsync(byte[] file, string passphrase = Passphrase)
    {
        using var reader = await BackupReader.OpenAsync(new MemoryStream(file), passphrase);
        var parts = new List<(BackupPartKind, string, BackupCodec, long, byte[])>();
        while (await reader.NextPartAsync() is { } part)
        {
            var content = new MemoryStream();
            await part.Content.CopyToAsync(content);
            parts.Add((part.Kind, part.Name, part.Codec, part.Length, content.ToArray()));
        }
        return parts;
    }

    // The plaintext a reader hands out, chunk by chunk, until it ends or refuses the file.
    private static async Task<(byte[] Plain, BackupFileException? Refused)> ReadPlainAsync(byte[] file, string passphrase = Passphrase)
    {
        var plain = new MemoryStream();
        try
        {
            using var reader = await BackupReader.OpenAsync(new MemoryStream(file), passphrase);
            var buffer = new byte[1000];
            int n;
            while ((n = await reader.ReadPlainAsync(buffer, default)) > 0) plain.Write(buffer, 0, n);
            return (plain.ToArray(), null);
        }
        catch (BackupFileException ex)
        {
            return (plain.ToArray(), ex);
        }
    }

    // The file's chunks, each [u32 length][ciphertext][tag] as it is in the file.
    private static List<byte[]> Chunks(byte[] file)
    {
        var chunks = new List<byte[]>();
        var at = BackupFormat.HeaderBytes;
        while (at < file.Length)
        {
            var size = 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at)) + BackupFormat.TagBytes;
            chunks.Add(file[at..(at + size)]);
            at += size;
        }
        Assert.Equal(file.Length, at);
        return chunks;
    }

    private static byte[] Join(byte[] file, IEnumerable<byte[]> chunks) => [.. file[..BackupFormat.HeaderBytes], .. chunks.SelectMany(c => c)];

    private static async Task<(byte[] File, byte[] Plain)> SampleAsync()
    {
        var (file, plain, _) = await WriteAsync(
        [
            new(BackupPartKind.Manifest, "manifest.json", Encoding.UTF8.GetBytes("""{"format":1,"host":"homelab"}""")),
            new(BackupPartKind.Dump, "dump/postgres", DumpLike(60_000)),
            new(BackupPartKind.Volume, "volume/gb-site-data", Random(48_000, 1)),
        ]);
        return (file, plain);
    }

    // Refused, and nothing it handed out came from the damaged chunk or after it: a prefix of the plaintext of the chunks before.
    private static void RefusedBeforeUse(byte[] originalPlain, (byte[] Plain, BackupFileException? Refused) read, int firstBadChunk)
    {
        Assert.NotNull(read.Refused);
        Assert.NotEqual(UploadProblem.None, read.Refused.Problem);
        Assert.True(read.Plain.Length <= firstBadChunk * Chunk, $"{read.Plain.Length} bytes handed out, but chunk {firstBadChunk} is the damaged one");
        Assert.Equal(originalPlain[..read.Plain.Length], read.Plain);
    }

    [Fact]
    public async Task Parts_come_back_as_they_went_in_over_many_chunks_compressed_or_stored()
    {
        var parts = new List<Part>
        {
            new(BackupPartKind.Manifest, "manifest.json", Encoding.UTF8.GetBytes("""{"format":1,"items":[]}""")),
            new(BackupPartKind.DataFile, "data/gitberth.db", DumpLike(150_000)),
            new(BackupPartKind.DataFolder, "data/keys", []),
            new(BackupPartKind.Volume, "volume/gb-photos-media", Random(300_000, 2)),
            new(BackupPartKind.Dump, "dump/postgres", DumpLike(2_500_000)),
            new(BackupPartKind.Image, "image/sha256:abc", Random(10, 3)),
        };
        var (file, _, codecs) = await WriteAsync(parts);
        Assert.True(Chunks(file).Count > 50);

        var read = await ReadAsync(file);
        Assert.Equal(parts.Select(p => (p.Kind, p.Name)), read.Select(r => (r.Kind, r.Name)));
        for (var i = 0; i < parts.Count; i++)
        {
            Assert.Equal(parts[i].Content, read[i].Content);
            Assert.Equal(parts[i].Content.Length, read[i].Length);
            Assert.Equal(codecs[parts[i].Name], read[i].Codec);
        }
        // Text and databases are compressed; random bytes (media) are stored as they are; so are empty or tiny parts.
        Assert.Equal(BackupCodec.Brotli, codecs["data/gitberth.db"]);
        Assert.Equal(BackupCodec.Brotli, codecs["dump/postgres"]);
        Assert.Equal(BackupCodec.Stored, codecs["volume/gb-photos-media"]);
        Assert.Equal(BackupCodec.Stored, codecs["data/keys"]);
    }

    [Fact]
    public async Task A_new_file_has_the_formats_header_and_default_key_and_chunks()
    {
        using var key = BackupKey.ForNewFile(Passphrase);
        Assert.Equal(600_000, key.Header.Iterations);
        Assert.Equal(1 << 20, key.Header.ChunkSize);
        var header = key.Header.ToBytes();
        Assert.True(header.Length <= 64);
        Assert.Equal("GBAK\r\n\u001a\n"u8.ToArray(), header[..8]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8)));
        Assert.Equal(1, header[10]);
        Assert.Equal(600_000u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(11)));
        Assert.Equal(1, header[31]);
        Assert.Equal(1u << 20, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32)));

        // 3 MiB over the default 1 MiB chunks, with the default key.
        var content = Random(3 * (1 << 20) + 5, 4);
        var output = new MemoryStream();
        using (var writer = new BackupWriter(output, key))
        {
            await writer.WritePartAsync(BackupPartKind.Volume, "volume/v", content.Length, new MemoryStream(content), default);
            await writer.FinishAsync(default);
        }
        var file = output.ToArray();
        Assert.Equal(header, file[..BackupFormat.HeaderBytes]);
        Assert.Equal(4, Chunks(file).Count);
        var read = await ReadAsync(file);
        Assert.Equal(content, Assert.Single(read).Content);
    }

    [Fact]
    public async Task A_wrong_passphrase_is_refused_before_anything_is_read()
    {
        var (file, _) = await SampleAsync();
        var refused = await Assert.ThrowsAsync<BackupFileException>(() => BackupReader.OpenAsync(new MemoryStream(file), "a wrong passphrase!"));
        Assert.Equal(UploadProblem.WrongPassphrase, refused.Problem);
        var read = await ReadPlainAsync(file, "a wrong passphrase!");
        Assert.Empty(read.Plain);
        await Assert.ThrowsAsync<BackupFileException>(() => BackupReader.VerifyAsync(new MemoryStream(file), "a wrong passphrase!"));
    }

    [Fact]
    public async Task One_changed_byte_anywhere_is_refused_before_its_chunk_is_used()
    {
        var (file, plain) = await SampleAsync();
        var chunks = Chunks(file);
        Assert.True(chunks.Count >= 10);
        var starts = new List<int>();
        var at = BackupFormat.HeaderBytes;
        foreach (var c in chunks)
        {
            starts.Add(at);
            at += c.Length;
        }

        // In the header (every chunk's additional data), and in chunks at the start, the middle and the end: their length,
        // ciphertext and tag.
        var positions = new List<(int Offset, int FirstBadChunk)> { (12, 0), (20, 0), (33, 0), (40, 0) };
        foreach (var i in new[] { 0, 1, chunks.Count / 2, chunks.Count - 2, chunks.Count - 1 })
        {
            positions.Add((starts[i] + 3, i));
            positions.Add((starts[i] + 4, i));
            positions.Add((starts[i] + chunks[i].Length / 2, i));
            positions.Add((starts[i] + chunks[i].Length - 1, i));
        }
        foreach (var (offset, bad) in positions)
        {
            var changed = (byte[])file.Clone();
            changed[offset] ^= 0x01;
            RefusedBeforeUse(plain, await ReadPlainAsync(changed), bad);
            await Assert.ThrowsAsync<BackupFileException>(() => BackupReader.VerifyAsync(new MemoryStream(changed), Passphrase));
        }

        // The magic itself: not a backup at all.
        var magic = (byte[])file.Clone();
        magic[0] = (byte)'X';
        Assert.Equal(UploadProblem.NotABackup, (await Assert.ThrowsAsync<BackupFileException>(() => BackupReader.OpenAsync(new MemoryStream(magic), Passphrase))).Problem);
    }

    [Fact]
    public async Task A_removed_reordered_or_missing_chunk_is_refused_before_its_bytes_are_used()
    {
        var (file, plain) = await SampleAsync();
        var chunks = Chunks(file);
        var middle = chunks.Count / 2;

        // Removed: the one at the start, in the middle, the last.
        foreach (var i in new[] { 0, middle, chunks.Count - 1 })
            RefusedBeforeUse(plain, await ReadPlainAsync(Join(file, chunks.Where((_, n) => n != i))), i);

        // Two swapped.
        var swapped = chunks.ToList();
        (swapped[middle], swapped[middle + 1]) = (swapped[middle + 1], swapped[middle]);
        RefusedBeforeUse(plain, await ReadPlainAsync(Join(file, swapped)), middle);

        // Cut short at a chunk's end, inside a chunk, inside a length; and something after the last chunk.
        RefusedBeforeUse(plain, await ReadPlainAsync(Join(file, chunks.Take(middle))), middle - 1);
        RefusedBeforeUse(plain, await ReadPlainAsync(file[..^7]), chunks.Count - 1);
        RefusedBeforeUse(plain, await ReadPlainAsync(Join(file, chunks.Take(middle)).Concat(chunks[middle][..2]).ToArray()), middle - 1);
        RefusedBeforeUse(plain, await ReadPlainAsync([.. file, 0, 0, 0, 0, 1, 2, 3]), chunks.Count - 1);
        RefusedBeforeUse(plain, await ReadPlainAsync([.. file, 9]), chunks.Count - 1);
        RefusedBeforeUse(plain, await ReadPlainAsync(file[..(BackupFormat.HeaderBytes + 2)]), 0);

        // A chunk of another file made with the same passphrase doesn't fit either.
        var (other, _) = await SampleAsync();
        var mixed = chunks.ToList();
        mixed[middle] = Chunks(other)[middle];
        RefusedBeforeUse(plain, await ReadPlainAsync(Join(file, mixed)), middle);

        // The whole file, read through, is fine.
        var whole = await ReadPlainAsync(file);
        Assert.Null(whole.Refused);
        Assert.Equal(plain, whole.Plain);
    }

    [Fact]
    public async Task Nothing_of_the_contents_or_their_names_can_be_read_in_the_file()
    {
        const string marker = "s3cr3t-api-key";
        var media = Random(200_000, 5);
        var names = new[] { "volume/gb-kanban-uploads-7f3a", "dump/postgres-main-db", "data/gitberth.db", "image/sha256:feedface" };
        var manifest = Encoding.UTF8.GetBytes("""{"format":1,"gitberthVersion":"1.7.0","host":"homelab-server","items":[{"id":"app:7","title":"kanban"}]}""");
        var (file, _, codecs) = await WriteAsync(
        [
            new(BackupPartKind.Manifest, "manifest.json", manifest),
            new(BackupPartKind.Volume, names[0], media),
            new(BackupPartKind.Dump, names[1], DumpLike(100_000, marker)),
            new(BackupPartKind.DataFile, names[2], Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("token=tok-9f8e7d6c5b4a;", 500)))),
            new(BackupPartKind.Image, names[3], Encoding.UTF8.GetBytes(marker)),
        ]);
        Assert.Equal(BackupCodec.Stored, codecs[names[0]]);

        static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
        foreach (var text in names.Concat([marker, "tok-9f8e7d6c5b4a", "homelab-server", "gitberthVersion", "kanban", "manifest.json", "INSERT INTO"]))
        {
            Assert.False(Contains(file, Encoding.UTF8.GetBytes(text)), $"\"{text}\" can be read in the file");
            Assert.False(Contains(file, Encoding.Unicode.GetBytes(text)), $"\"{text}\" (UTF-16) can be read in the file");
        }
        // Not even stored (uncompressed) bytes: no 16 bytes of the media part are in the file.
        for (var at = 0; at + 16 <= media.Length; at += 997)
            Assert.False(Contains(file, media[at..(at + 16)]), $"the media part's bytes at {at} can be read in the file");
        // The plain header is all there is to read.
        Assert.False(Contains(file.AsSpan(BackupFormat.HeaderBytes).ToArray(), "GBAK"u8.ToArray()));
    }

    [Fact]
    public async Task Compressible_contents_give_a_much_smaller_file_and_media_isnt_made_bigger()
    {
        var dump = DumpLike(4 << 20);
        var (file, _, codecs) = await WriteAsync([new(BackupPartKind.Dump, "dump/postgres", dump)], chunk: BackupFormat.ChunkBytes);
        Assert.Equal(BackupCodec.Brotli, codecs["dump/postgres"]);
        Assert.True(file.Length < dump.Length / 5, $"{file.Length} bytes for {dump.Length} of text: not much smaller");

        var media = Random(2 << 20, 6);
        var (stored, _, mediaCodecs) = await WriteAsync([new(BackupPartKind.Volume, "volume/media", media)], chunk: BackupFormat.ChunkBytes);
        Assert.Equal(BackupCodec.Stored, mediaCodecs["volume/media"]);
        Assert.True(stored.Length < media.Length * 1.01, $"{stored.Length} bytes for {media.Length} of media");
        Assert.Equal(media, Assert.Single(await ReadAsync(stored)).Content);
    }

    [Fact]
    public async Task A_file_is_checked_whole_and_its_parts_listed()
    {
        var (file, _) = await SampleAsync();
        var parts = await BackupReader.VerifyAsync(new MemoryStream(file), Passphrase);
        Assert.Equal(["manifest.json", "dump/postgres", "volume/gb-site-data"], parts.Select(p => p.Name));

        // A file whose writer never finished (a save that failed half-way) has no last chunk: refused.
        using var key = BackupKey.ForNewFile(Passphrase, Iterations, Chunk);
        var output = new MemoryStream();
        using (var writer = new BackupWriter(output, key))
            await writer.WritePartAsync(BackupPartKind.Dump, "dump/x", 0, new MemoryStream(DumpLike(50_000)), default);
        Assert.Equal(UploadProblem.Damaged, (await Assert.ThrowsAsync<BackupFileException>(() => BackupReader.VerifyAsync(new MemoryStream(output.ToArray()), Passphrase))).Problem);
    }

    [Fact]
    public void A_header_from_a_newer_Gitberth_or_no_backup_at_all_is_told_apart()
    {
        using var key = BackupKey.ForNewFile(Passphrase, Iterations, Chunk);
        var header = key.Header.ToBytes();
        var newer = (byte[])header.Clone();
        newer[9] = BackupFormat.Newest + 1;
        Assert.Equal(UploadProblem.TooNew, Assert.Throws<BackupFileException>(() => BackupHeader.Parse(newer)).Problem);
        Assert.Equal(UploadProblem.NotABackup, Assert.Throws<BackupFileException>(() => BackupHeader.Parse("PK\u0003\u0004 a zip file"u8)).Problem);
        var greedy = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(greedy.AsSpan(11), uint.MaxValue);
        Assert.Equal(UploadProblem.Damaged, Assert.Throws<BackupFileException>(() => BackupHeader.Parse(greedy)).Problem);
        Assert.Equal(key.Header.Salt, BackupHeader.Parse(header).Salt);
    }
}
