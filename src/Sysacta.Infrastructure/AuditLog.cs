using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sysacta.Core.Auditing;
using static Sysacta.Infrastructure.SysactaDatabase;

namespace Sysacta.Infrastructure;

/// <summary>Filter for <see cref="AuditLog.Query"/>. All conditions are combined with AND.</summary>
public sealed record AuditQuery(
    string? Text = null,
    AuditOutcome? Outcome = null,
    string? ToolName = null,
    string? ClientId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 1000);

/// <summary>SQLite audit log: write/upsert, search, retention and export (spec §11).</summary>
public sealed class AuditLog(SysactaDatabase database) : IAuditLog
{
    public const int DefaultRetentionDays = 30;

    public void Write(AuditEvent auditEvent)
    {
        var e = auditEvent;
        using var c = database.Open();
        Execute(c, """
            INSERT INTO audit_events (request_id, timestamp_utc, client_id, client_name, tool_name, source, sanitized_args, result, error_code, duration_ms, remote_address)
            VALUES ($id, $ts, $cid, $cname, $tool, $src, $args, $res, $err, $dur, $remote)
            ON CONFLICT (request_id) DO UPDATE SET
                result = excluded.result, error_code = excluded.error_code, duration_ms = excluded.duration_ms
            """,
            ("$id", e.RequestId), ("$ts", ToDb(e.Timestamp)), ("$cid", e.ClientId), ("$cname", e.ClientName), ("$tool", e.ToolName),
            ("$src", e.Source), ("$args", e.Arguments), ("$res", OutcomeToDb(e.Outcome)), ("$err", e.ErrorCode), ("$dur", e.DurationMs),
            ("$remote", e.RemoteAddress));
    }

    public IReadOnlyList<AuditEvent> Query(AuditQuery query)
    {
        var sql = new StringBuilder("SELECT request_id, timestamp_utc, client_id, client_name, tool_name, source, sanitized_args, result, error_code, duration_ms, remote_address FROM audit_events WHERE 1 = 1");
        var parameters = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            sql.Append(" AND (tool_name LIKE $q ESCAPE '\\' OR client_name LIKE $q ESCAPE '\\' OR error_code LIKE $q ESCAPE '\\' OR request_id LIKE $q ESCAPE '\\' OR sanitized_args LIKE $q ESCAPE '\\' OR remote_address LIKE $q ESCAPE '\\')");
            var escaped = query.Text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            parameters.Add(("$q", $"%{escaped}%"));
        }

        if (query.Outcome is { } outcome)
        {
            sql.Append(" AND result = $res");
            parameters.Add(("$res", OutcomeToDb(outcome)));
        }

        if (query.ToolName is { } tool)
        {
            sql.Append(" AND tool_name = $tool");
            parameters.Add(("$tool", tool));
        }

        if (query.ClientId is { } client)
        {
            sql.Append(" AND client_id = $cid");
            parameters.Add(("$cid", client));
        }

        if (query.From is { } from)
        {
            sql.Append(" AND timestamp_utc >= $from");
            parameters.Add(("$from", ToDb(from)));
        }

        if (query.To is { } to)
        {
            sql.Append(" AND timestamp_utc < $to");
            parameters.Add(("$to", ToDb(to)));
        }

        sql.Append(" ORDER BY timestamp_utc DESC LIMIT $limit");
        parameters.Add(("$limit", Math.Clamp(query.Limit, 1, 100_000)));

        using var c = database.Open();
        using var cmd = Command(c, sql.ToString(), [.. parameters]);
        using var r = cmd.ExecuteReader();
        var list = new List<AuditEvent>();
        while (r.Read())
        {
            list.Add(new AuditEvent
            {
                RequestId = r.GetString(0),
                Timestamp = FromDb(r.GetString(1)),
                ClientId = r.IsDBNull(2) ? null : r.GetString(2),
                ClientName = r.IsDBNull(3) ? null : r.GetString(3),
                ToolName = r.IsDBNull(4) ? null : r.GetString(4),
                Source = r.GetString(5),
                Arguments = r.IsDBNull(6) ? null : r.GetString(6),
                Outcome = OutcomeFromDb(r.GetString(7)),
                ErrorCode = r.IsDBNull(8) ? null : r.GetString(8),
                DurationMs = r.IsDBNull(9) ? null : r.GetInt64(9),
                RemoteAddress = r.IsDBNull(10) ? null : r.GetString(10),
            });
        }

        return list;
    }

    /// <summary>Deletes records older than <paramref name="retentionDays"/>. Returns the number deleted.</summary>
    public int Purge(int retentionDays, DateTimeOffset now)
    {
        using var c = database.Open();
        return Execute(c, "DELETE FROM audit_events WHERE timestamp_utc < $cutoff", ("$cutoff", ToDb(now.AddDays(-retentionDays))));
    }

    internal static string OutcomeToDb(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.AuthFailed => "auth_failed",
        _ => outcome.ToString().ToLowerInvariant(),
    };

    internal static AuditOutcome OutcomeFromDb(string value) =>
        Enum.GetValues<AuditOutcome>().FirstOrDefault(o => OutcomeToDb(o) == value, AuditOutcome.Unknown);

    // ---- Export ----

    private static readonly string[] Columns =
        ["request_id", "timestamp_utc", "client_id", "client_name", "tool_name", "source", "arguments", "result", "error_code", "duration_ms", "remote_address"];

    private static string?[] Values(AuditEvent e) =>
    [
        e.RequestId, SysactaDatabase.ToDb(e.Timestamp), e.ClientId, e.ClientName, e.ToolName, e.Source, e.Arguments, OutcomeToDb(e.Outcome), e.ErrorCode,
        e.DurationMs?.ToString(CultureInfo.InvariantCulture), e.RemoteAddress,
    ];

    /// <summary>RFC 4180 CSV with formula-injection protection (cells starting with = + - @ tab CR are prefixed with ').</summary>
    public static void ExportCsv(IEnumerable<AuditEvent> events, TextWriter writer)
    {
        writer.Write(string.Join(',', Columns));
        writer.Write("\r\n");
        foreach (var e in events)
        {
            writer.Write(string.Join(',', Values(e).Select(CsvCell)));
            writer.Write("\r\n");
        }
    }

    internal static string CsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static void ExportJson(IEnumerable<AuditEvent> events, Stream stream)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (var e in events)
        {
            writer.WriteStartObject();
            var values = Values(e);
            for (var i = 0; i < Columns.Length; i++)
            {
                writer.WriteString(Columns[i], values[i]);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
