using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sysacta.Core.Auditing;
using static Sysacta.Infrastructure.SysactaDatabase;

namespace Sysacta.Infrastructure;

/// <summary>Filter for <see cref="AuditLog.Query"/>. All conditions are combined with AND.</summary>
/// <param name="Aggregated">true = only aggregated records, false = only individual records, null = both.</param>
public sealed record AuditQuery(
    string? Text = null,
    AuditOutcome? Outcome = null,
    string? ToolName = null,
    string? ClientId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 1000,
    bool? Aggregated = null);

/// <summary>
/// Storage limits of the audit log (SEC-06), in records and in logical bytes (<see cref="AuditLog.SizeOf"/>).
/// </summary>
/// <param name="ReservedRows">
/// Part of <paramref name="MaxRows"/> only the intent record of a state change (<see cref="AuditOutcome.Pending"/>) may
/// use. Every other record is written only while the log stays below <c>MaxRows - ReservedRows</c>.
/// </param>
/// <param name="LowPriorityRows">
/// Records of HTTP rejections (source <c>http</c>, including aggregated ones) kept at most; older ones make room for newer
/// ones, so a flood of rejected requests never evicts tool records.
/// </param>
public sealed record AuditLimits(
    long MaxRows = 100_000,
    long MaxBytes = 64L * 1024 * 1024,
    long ReservedRows = 1_000,
    long ReservedBytes = 64L * 1024 * 1024 / 10,
    long LowPriorityRows = 10_000,
    long LowPriorityBytes = 64L * 1024 * 1024 / 10);

/// <summary>Current usage of the audit log, for the console.</summary>
/// <param name="EvictedCount">Records deleted to stay within the limits (not counting retention), since the database was created or upgraded.</param>
/// <param name="FailedWrites">Records that could not be written since this process started (full log or database error).</param>
/// <param name="FileBytes">Size of the database file (all data, not only the audit log), and of its WAL file.</param>
public sealed record AuditStatus(
    long Rows,
    long Bytes,
    long EvictedCount,
    DateTimeOffset? LastEvictedAt,
    long FailedWrites,
    long FileBytes,
    long FreeBytes,
    long WalBytes,
    AuditLimits Limits);

/// <summary>A record does not fit the audit log's limits even after evicting the oldest records allowed.</summary>
public sealed class AuditCapacityException() : InvalidOperationException("The audit log is full.");

/// <summary>SQLite audit log: write/upsert within storage limits, search, retention and export (spec §11, SEC-06).</summary>
/// <remarks>
/// A write checks and changes the totals in <c>audit_usage</c> inside one <c>BEGIN IMMEDIATE</c> transaction, so
/// concurrent writers cannot overshoot a limit. When a record does not fit, the oldest records that are not
/// <see cref="AuditOutcome.Pending"/> are deleted in a bounded batch (a ring buffer); if that is not enough the write
/// throws. Pending records (operations in progress) are never deleted by the limits or the retention; leftovers of a
/// crash are closed by <see cref="RecoverInterrupted"/> at startup.
/// </remarks>
public sealed class AuditLog(SysactaDatabase database, AuditLimits? limits = null) : IAuditLog
{
    public const int DefaultRetentionDays = 30;

    /// <summary>Error code that <see cref="RecoverInterrupted"/> gives to an operation the previous process did not finish.</summary>
    public const string InterruptedCode = "INTERRUPTED";

    /// <summary>Added to every record's logical size for its integer columns, record header and index entries.</summary>
    internal const int RowOverheadBytes = 128;

    /// <summary>Records deleted at most per batch (per write, or per transaction during maintenance).</summary>
    private const int EvictionBatch = 1_000;
    private const int MaxEvictionBatchesPerWrite = 4;

    private long _failedWrites;

    public AuditLimits Limits { get; } = limits ?? new AuditLimits();

    public void Write(AuditEvent auditEvent)
    {
        try
        {
            WriteCore(auditEvent);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failedWrites);
            throw;
        }
    }

    private void WriteCore(AuditEvent e)
    {
        var size = SizeOf(e);
        using var c = database.Open();
        using var tx = c.BeginTransaction(); // BEGIN IMMEDIATE: check and write are one step for concurrent writers

        // The final outcome of a recorded intent replaces it in place; it never needs new room.
        if (ExecuteTx(c, tx, "UPDATE audit_events SET result = $res, error_code = $err, duration_ms = $dur, size_bytes = $size WHERE request_id = $id AND result = 'pending'",
                ("$id", e.RequestId), ("$res", OutcomeToDb(e.Outcome)), ("$err", e.ErrorCode), ("$dur", e.DurationMs), ("$size", size)) == 0)
        {
            // A rejection record makes room only among rejection records, never by evicting tool records.
            var lowPriority = e.Source == "http";
            if (lowPriority)
            {
                MakeRoom(c, tx, "http", "http", Limits.LowPriorityRows, Limits.LowPriorityBytes, size);
            }

            var reserved = e.Outcome == AuditOutcome.Pending;
            MakeRoom(c, tx, "all", lowPriority ? "http" : "all",
                reserved ? Limits.MaxRows : Limits.MaxRows - Limits.ReservedRows,
                reserved ? Limits.MaxBytes : Limits.MaxBytes - Limits.ReservedBytes,
                size);

            ExecuteTx(c, tx, """
                INSERT INTO audit_events (request_id, timestamp_utc, client_id, client_name, tool_name, source, sanitized_args, result, error_code, duration_ms, remote_address, aggregate_count, size_bytes)
                VALUES ($id, $ts, $cid, $cname, $tool, $src, $args, $res, $err, $dur, $remote, $agg, $size)
                """,
                ("$id", e.RequestId), ("$ts", ToDb(e.Timestamp)), ("$cid", e.ClientId), ("$cname", e.ClientName), ("$tool", e.ToolName),
                ("$src", e.Source), ("$args", e.Arguments), ("$res", OutcomeToDb(e.Outcome)), ("$err", e.ErrorCode), ("$dur", e.DurationMs),
                ("$remote", e.RemoteAddress), ("$agg", e.AggregateCount), ("$size", size));
        }

        tx.Commit();
    }

    /// <summary>
    /// Ensures one more record of <paramref name="size"/> bytes fits within the limits of <paramref name="scope"/>,
    /// evicting the oldest non-pending records of <paramref name="evictScope"/> in at most a few bounded batches. Evicts a little more than
    /// needed (1 % of the limit) so that a full log does not evict on every write.
    /// </summary>
    private static void MakeRoom(SqliteConnection c, SqliteTransaction tx, string scope, string evictScope, long maxRows, long maxBytes, long size)
    {
        for (var batch = 0; ; batch++)
        {
            var (rows, bytes) = Usage(c, tx, scope);
            if (rows + 1 <= maxRows && bytes + size <= maxBytes)
            {
                return;
            }

            if (batch == MaxEvictionBatchesPerWrite)
            {
                throw new AuditCapacityException();
            }

            var excessRows = rows + 1 - (maxRows - maxRows / 100);
            var averageSize = rows > 0 ? Math.Max(1, bytes / rows) : 1;
            var excessBytes = bytes + size - (maxBytes - maxBytes / 100);
            var count = Math.Clamp(Math.Max(excessRows, (excessBytes + averageSize - 1) / averageSize), 1, EvictionBatch);
            if (Evict(c, tx, evictScope, count) == 0)
            {
                throw new AuditCapacityException(); // everything left is in progress
            }
        }
    }

    private static (long Rows, long Bytes) Usage(SqliteConnection c, SqliteTransaction? tx, string scope)
    {
        using var cmd = Command(c, "SELECT row_count, byte_count FROM audit_usage WHERE scope = $s", ("$s", scope));
        cmd.Transaction = tx;
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : throw new InvalidOperationException("The audit usage table is missing.");
    }

    /// <summary>Deletes the oldest <paramref name="count"/> non-pending records of <paramref name="scope"/> (by index, no table scan).</summary>
    private static int Evict(SqliteConnection c, SqliteTransaction tx, string scope, long count)
    {
        var deleted = ExecuteTx(c, tx, scope == "http"
            ? "DELETE FROM audit_events WHERE rowid IN (SELECT rowid FROM audit_events WHERE source = 'http' AND result <> 'pending' ORDER BY timestamp_utc LIMIT $n)"
            : "DELETE FROM audit_events WHERE rowid IN (SELECT rowid FROM audit_events WHERE result <> 'pending' ORDER BY timestamp_utc LIMIT $n)",
            ("$n", count));
        if (deleted > 0)
        {
            ExecuteTx(c, tx, "UPDATE audit_usage SET evicted_count = evicted_count + $d, last_evicted_at = $at WHERE scope = 'all'",
                ("$d", deleted), ("$at", ToDb(DateTimeOffset.UtcNow)));
        }

        return deleted;
    }

    /// <summary>Logical size of a record: <see cref="RowOverheadBytes"/> + the UTF-8 bytes of its text columns (as in migration v2).</summary>
    public static long SizeOf(AuditEvent e) =>
        RowOverheadBytes + new[] { e.RequestId, ToDb(e.Timestamp), e.ClientId, e.ClientName, e.ToolName, e.Source, e.Arguments, OutcomeToDb(e.Outcome), e.ErrorCode, e.RemoteAddress }
            .Sum(v => v is null ? 0 : Encoding.UTF8.GetByteCount(v));

    public IReadOnlyList<AuditEvent> Query(AuditQuery query)
    {
        var sql = new StringBuilder("SELECT request_id, timestamp_utc, client_id, client_name, tool_name, source, sanitized_args, result, error_code, duration_ms, remote_address, aggregate_count FROM audit_events WHERE 1 = 1");
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

        if (query.Aggregated is { } aggregated)
        {
            sql.Append(aggregated ? " AND aggregate_count IS NOT NULL" : " AND aggregate_count IS NULL");
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
                AggregateCount = r.IsDBNull(11) ? null : r.GetInt64(11),
            });
        }

        return list;
    }

    /// <summary>
    /// Deletes records older than <paramref name="retentionDays"/>, in short transactions so that writers are not
    /// blocked for long. Pending records are kept. Returns the number deleted.
    /// </summary>
    public int Purge(int retentionDays, DateTimeOffset now)
    {
        var total = 0;
        int deleted;
        do
        {
            using var c = database.Open();
            deleted = Execute(c,
                "DELETE FROM audit_events WHERE rowid IN (SELECT rowid FROM audit_events WHERE timestamp_utc < $cutoff AND result <> 'pending' ORDER BY timestamp_utc LIMIT $n)",
                ("$cutoff", ToDb(now.AddDays(-retentionDays))), ("$n", EvictionBatch * 5));
            total += deleted;
        }
        while (deleted > 0);

        return total;
    }

    /// <summary>
    /// Brings an over-full log (e.g. an upgraded database, or lowered limits) below the limits for ordinary records,
    /// in short transactions. Returns the number of records evicted.
    /// </summary>
    public long EnforceLimits()
    {
        long total = 0;
        foreach (var (scope, maxRows, maxBytes) in new[]
        {
            ("http", Limits.LowPriorityRows, Limits.LowPriorityBytes),
            ("all", Limits.MaxRows - Limits.ReservedRows, Limits.MaxBytes - Limits.ReservedBytes),
        })
        {
            while (true)
            {
                using var c = database.Open();
                using var tx = c.BeginTransaction();
                var (rows, bytes) = Usage(c, tx, scope);
                if (rows <= maxRows && bytes <= maxBytes)
                {
                    break;
                }

                var averageSize = rows > 0 ? Math.Max(1, bytes / rows) : 1;
                var count = Math.Clamp(Math.Max(rows - maxRows, (bytes - maxBytes + averageSize - 1) / averageSize), 1, EvictionBatch * 5);
                var deleted = Evict(c, tx, scope, count);
                tx.Commit();
                if (deleted == 0)
                {
                    break;
                }

                total += deleted;
            }
        }

        return total;
    }

    /// <summary>
    /// Closes intent records left by a previous process that ended before writing the final outcome: they become
    /// <see cref="AuditOutcome.Unknown"/> with <see cref="InterruptedCode"/>, since the change may or may not have
    /// happened. Call only at startup, before any tool can run (the console is single-instance). Returns the number closed.
    /// </summary>
    public int RecoverInterrupted()
    {
        using var c = database.Open();
        return Execute(c, "UPDATE audit_events SET result = $res, error_code = $err WHERE result = 'pending'",
            ("$res", OutcomeToDb(AuditOutcome.Unknown)), ("$err", InterruptedCode));
    }

    /// <summary>
    /// Periodic maintenance (startup and every few hours): retention, limits, then the physical file. SQLite reuses the
    /// pages of deleted records, so the file stays near its largest size; it is compacted (VACUUM) only when at least
    /// a quarter of it, and 16 MiB, is free. The WAL is checkpointed and truncated. Failures are left to the next run.
    /// </summary>
    public void Maintain(int retentionDays, DateTimeOffset now)
    {
        Purge(retentionDays, now);
        EnforceLimits();
        using var c = database.Open();
        var pageSize = Convert.ToInt64(Scalar(c, "PRAGMA page_size;"), CultureInfo.InvariantCulture);
        var pages = Convert.ToInt64(Scalar(c, "PRAGMA page_count;"), CultureInfo.InvariantCulture);
        var free = Convert.ToInt64(Scalar(c, "PRAGMA freelist_count;"), CultureInfo.InvariantCulture);
        if (free * pageSize >= 16L * 1024 * 1024 && free * 4 >= pages)
        {
            Execute(c, "VACUUM;");
        }

        Execute(c, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public AuditStatus GetStatus()
    {
        using var c = database.Open();
        using var cmd = Command(c, "SELECT row_count, byte_count, evicted_count, last_evicted_at FROM audit_usage WHERE scope = 'all'");
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            throw new InvalidOperationException("The audit usage table is missing.");
        }

        var pageSize = Convert.ToInt64(Scalar(c, "PRAGMA page_size;"), CultureInfo.InvariantCulture);
        var pages = Convert.ToInt64(Scalar(c, "PRAGMA page_count;"), CultureInfo.InvariantCulture);
        var free = Convert.ToInt64(Scalar(c, "PRAGMA freelist_count;"), CultureInfo.InvariantCulture);
        var wal = new FileInfo(database.Path + "-wal");
        return new AuditStatus(
            r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : FromDb(r.GetString(3)),
            Interlocked.Read(ref _failedWrites), pages * pageSize, free * pageSize, wal.Exists ? wal.Length : 0, Limits);
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
        ["request_id", "timestamp_utc", "client_id", "client_name", "tool_name", "source", "arguments", "result", "error_code", "duration_ms", "remote_address", "aggregate_count"];

    private static string?[] Values(AuditEvent e) =>
    [
        e.RequestId, SysactaDatabase.ToDb(e.Timestamp), e.ClientId, e.ClientName, e.ToolName, e.Source, e.Arguments, OutcomeToDb(e.Outcome), e.ErrorCode,
        e.DurationMs?.ToString(CultureInfo.InvariantCulture), e.RemoteAddress, e.AggregateCount?.ToString(CultureInfo.InvariantCulture),
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
