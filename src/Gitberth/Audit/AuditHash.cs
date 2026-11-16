using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Gitberth.Audit;

/// <summary>
/// The audit log's tamper evidence: each entry's hash is SHA-256 of the hash before it and a canonical form of its own
/// fields, so changing or removing an entry breaks every hash after it. The canonical form is a JSON array of the fields
/// as they're stored, in a fixed order, written the same way every time.
/// </summary>
public static class AuditHash
{
    /// <summary>The hash before the first entry of a log that was never pruned.</summary>
    public static readonly string Genesis = new('0', 64);

    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    /// <summary>The fields an entry is stored with, as the hash covers them.</summary>
    public readonly record struct Row(long Id, long AtMs, string ActorKind, string Actor, string Via, string? Ip, string Action, string Outcome,
        int? AppId, string? AppName, string? Container, string? Target, string Summary, string? Changes, string? Correlation);

    /// <summary>The entry's fields in their fixed order, as one line of JSON.</summary>
    public static string Canonical(in Row row)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, Writer))
        {
            w.WriteStartArray();
            w.WriteNumberValue(row.Id);
            w.WriteNumberValue(row.AtMs);
            w.WriteStringValue(row.ActorKind);
            w.WriteStringValue(row.Actor);
            w.WriteStringValue(row.Via);
            Str(w, row.Ip);
            w.WriteStringValue(row.Action);
            w.WriteStringValue(row.Outcome);
            if (row.AppId is int app) w.WriteNumberValue(app); else w.WriteNullValue();
            Str(w, row.AppName);
            Str(w, row.Container);
            Str(w, row.Target);
            w.WriteStringValue(row.Summary);
            Str(w, row.Changes);
            Str(w, row.Correlation);
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>SHA-256 of the previous hash and the entry's canonical form, as lowercase hex.</summary>
    public static string Of(string previous, in Row row)
    {
        var bytes = Encoding.UTF8.GetBytes(previous + "\n" + Canonical(row));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static void Str(Utf8JsonWriter w, string? value)
    {
        if (value is null) w.WriteNullValue();
        else w.WriteStringValue(value);
    }
}
