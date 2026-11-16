using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Gitberth.Audit;

/// <summary>An exported view of the audit log: CSV for a spreadsheet, JSON Lines (one object per line) for tools.</summary>
public static class AuditExport
{
    public const string CsvHeader = "id,time,actor_kind,actor,via,ip,action,outcome,app_id,app,container,target,summary,changes,correlation,prev_hash,hash";

    private static readonly JsonWriterOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    /// <summary>The file name a download gets: gitberth-audit-20261114-0930.csv.</summary>
    public static string FileName(AuditExportFormat format, DateTime now) =>
        $"gitberth-audit-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.{(format == AuditExportFormat.Csv ? "csv" : "jsonl")}";

    public static string ContentType(AuditExportFormat format) => format == AuditExportFormat.Csv ? "text/csv; charset=utf-8" : "application/x-ndjson; charset=utf-8";

    public static string Time(DateTime at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string CsvLine(AuditRecord r) => string.Join(',',
        Csv(r.Id.ToString(CultureInfo.InvariantCulture)), Csv(Time(r.At)), Csv(r.Actor.Kind.ToString()), Csv(r.Actor.Name), Csv(r.Actor.Via.ToString()),
        Csv(r.Actor.Ip), Csv(r.Action), Csv(r.Outcome.ToString()), Csv(r.AppId?.ToString(CultureInfo.InvariantCulture)), Csv(r.AppName), Csv(r.Container),
        Csv(r.Target), Csv(r.Summary), Csv(ChangesText(r.Changes)), Csv(r.Correlation), Csv(r.PrevHash), Csv(r.Hash));

    public static string JsonLine(AuditRecord r)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, Json))
        {
            w.WriteStartObject();
            w.WriteNumber("id", r.Id);
            w.WriteString("time", Time(r.At));
            w.WriteString("actorKind", r.Actor.Kind.ToString());
            w.WriteString("actor", r.Actor.Name);
            w.WriteString("via", r.Actor.Via.ToString());
            Str(w, "ip", r.Actor.Ip);
            w.WriteString("action", r.Action);
            w.WriteString("outcome", r.Outcome.ToString());
            if (r.AppId is int app) w.WriteNumber("appId", app); else w.WriteNull("appId");
            Str(w, "app", r.AppName);
            Str(w, "container", r.Container);
            Str(w, "target", r.Target);
            w.WriteString("summary", r.Summary);
            w.WriteStartArray("changes");
            foreach (var c in r.Changes)
            {
                w.WriteStartObject();
                w.WriteString("field", c.Field);
                Str(w, "from", c.From);
                Str(w, "to", c.To);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            Str(w, "correlation", r.Correlation);
            w.WriteString("prevHash", r.PrevHash);
            w.WriteString("hash", r.Hash);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>"Memory limit: 512 MB → 1 GB; Restart policy: no → always".</summary>
    public static string ChangesText(IReadOnlyList<AuditChange> changes) =>
        string.Join("; ", changes.Select(c => c.From is null && c.To is null ? c.Field
            : c.From is null ? $"{c.Field}: {c.To}"
            : c.To is null ? $"{c.Field}: {c.From} → (none)"
            : $"{c.Field}: {c.From} → {c.To}"));

    // A field as CSV writes it: quoted when it must be, and never read as a formula by a spreadsheet.
    internal static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static void Str(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }
}
