using Microsoft.Data.Sqlite;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;

namespace Sysacta.Infrastructure.Tests;

/// <summary>SEC-06: storage limits of the audit log.</summary>
public class AuditCapacityTests
{
    private static AuditEvent Event(string id, DateTimeOffset ts, AuditOutcome outcome = AuditOutcome.Success, string source = "mcp") => new()
    {
        RequestId = id,
        Timestamp = ts,
        ClientId = source == "http" ? null : "c1",
        ToolName = source == "http" ? null : "audio_set_volume",
        Source = source,
        Outcome = outcome,
        Arguments = source == "http" ? """{"method":"POST","path":"/mcp"}""" : """{"level":10}""",
    };

    private static (long Rows, long Bytes) Actual(SysactaDatabase database)
    {
        using var c = database.Open();
        using var cmd = SysactaDatabase.Command(c, "SELECT COUNT(*), COALESCE(SUM(size_bytes), 0) FROM audit_events");
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    private static void AssertUsageMatches(SysactaDatabase database, AuditLog log)
    {
        var status = log.GetStatus();
        Assert.Equal(Actual(database), (status.Rows, status.Bytes));
    }

    [Fact]
    public void Reserve_is_only_for_intents_and_in_progress_intents_are_never_evicted()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var log = new AuditLog(t.Database, new AuditLimits(MaxRows: 3, ReservedRows: 1));
        var now = DateTimeOffset.UtcNow;

        log.Write(Event("p1", now, AuditOutcome.Pending));
        log.Write(Event("p2", now, AuditOutcome.Pending));

        // Ordinary records may not use the reserve, and there is nothing they could evict.
        Assert.Throws<AuditCapacityException>(() => log.Write(Event("n1", now)));
        Assert.Throws<AuditCapacityException>(() => log.Write(Event("h1", now, AuditOutcome.AuthFailed, "http")));

        log.Write(Event("p3", now, AuditOutcome.Pending)); // uses the reserve
        Assert.Throws<AuditCapacityException>(() => log.Write(Event("p4", now, AuditOutcome.Pending)));

        // Final outcomes replace their intent in place, even when the log is full.
        log.Write(Event("p1", now, AuditOutcome.Success) with { DurationMs = 5 });
        Assert.Equal(3, log.GetStatus().Rows);
        Assert.Equal(3, log.GetStatus().FailedWrites);
        AssertUsageMatches(t.Database, log);

        // Once enough intents are finished, they make room for an ordinary record (which must stay below the reserve).
        Assert.Throws<AuditCapacityException>(() => log.Write(Event("n2", now.AddSeconds(1))));
        log.Write(Event("p2", now, AuditOutcome.Success));
        log.Write(Event("n2", now.AddSeconds(1)));
        Assert.Equal(["n2", "p3"], log.Query(new AuditQuery()).Select(e => e.RequestId).Order(StringComparer.Ordinal));
        Assert.Equal(2, log.GetStatus().EvictedCount); // p1 and p2; the refused attempt was rolled back as a whole
    }

    [Fact]
    public void Pending_to_final_is_not_counted_twice()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var log = new AuditLog(t.Database);
        var now = DateTimeOffset.UtcNow;

        log.Write(Event("r1", now, AuditOutcome.Pending));
        var afterPending = log.GetStatus();
        log.Write(Event("r1", now, AuditOutcome.Error) with { ErrorCode = "OS_API_ERROR", DurationMs = 3 });

        var status = log.GetStatus();
        Assert.Equal(1, status.Rows);
        Assert.Equal(afterPending.Bytes - "pending".Length + "error".Length + "OS_API_ERROR".Length, status.Bytes);
        AssertUsageMatches(t.Database, log);
    }

    [Fact]
    public void Oldest_records_make_room_and_rejections_evict_only_rejections()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var log = new AuditLog(t.Database, new AuditLimits(MaxRows: 50, ReservedRows: 10, LowPriorityRows: 5));
        var start = DateTimeOffset.UtcNow.AddHours(-1);

        for (var i = 0; i < 20; i++)
        {
            log.Write(Event($"tool{i:D3}", start.AddSeconds(i)));
        }

        for (var i = 0; i < 200; i++)
        {
            log.Write(Event($"http{i:D3}", start.AddSeconds(100 + i), AuditOutcome.AuthFailed, "http"));
        }

        var events = log.Query(new AuditQuery(Limit: 1000));
        Assert.Equal(20, events.Count(e => e.Source == "mcp")); // a rejection flood does not evict tool records
        Assert.Equal(5, events.Count(e => e.Source == "http"));
        Assert.Contains(events, e => e.RequestId == "http199");

        for (var i = 20; i < 100; i++)
        {
            log.Write(Event($"tool{i:D3}", start.AddSeconds(1000 + i)));
        }

        Assert.InRange(log.GetStatus().Rows, 30, 40);
        Assert.Contains(log.Query(new AuditQuery(Limit: 1000)), e => e.RequestId == "tool099");
        Assert.DoesNotContain(log.Query(new AuditQuery(Limit: 1000)), e => e.RequestId == "tool000");
        AssertUsageMatches(t.Database, log);
    }

    [Fact]
    public void Byte_limit_applies_independently_of_the_record_limit()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var size = AuditLog.SizeOf(Event("x0000", DateTimeOffset.UnixEpoch));
        var log = new AuditLog(t.Database, new AuditLimits(MaxRows: 1_000, MaxBytes: size * 12, ReservedRows: 0, ReservedBytes: size * 2));

        for (var i = 0; i < 100; i++)
        {
            log.Write(Event($"x{i:D4}", DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var status = log.GetStatus();
        Assert.True(status.Bytes <= size * 10, $"{status.Bytes} bytes");
        Assert.InRange(status.Rows, 1, 10);
        AssertUsageMatches(t.Database, log);
    }

    [Fact]
    public void Retention_and_limits_keep_in_progress_intents_and_recovery_closes_leftovers()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var log = new AuditLog(t.Database, new AuditLimits(MaxRows: 5, ReservedRows: 1));
        var now = DateTimeOffset.UtcNow;
        log.Write(Event("old-pending", now.AddDays(-40), AuditOutcome.Pending));
        log.Write(Event("old-done", now.AddDays(-40)));

        Assert.Equal(1, log.Purge(30, now));
        Assert.Equal(0, log.EnforceLimits());
        Assert.Equal(AuditOutcome.Pending, Assert.Single(log.Query(new AuditQuery())).Outcome);

        // A new process: the intent was left by a crash, so its outcome is unknown.
        Assert.Equal(1, log.RecoverInterrupted());
        var recovered = Assert.Single(log.Query(new AuditQuery()));
        Assert.Equal(AuditOutcome.Unknown, recovered.Outcome);
        Assert.Equal(AuditLog.InterruptedCode, recovered.ErrorCode);
        Assert.Equal(1, log.Purge(30, now));
        AssertUsageMatches(t.Database, log);
    }

    [Fact]
    public async Task Concurrent_writes_maintenance_queries_and_exports_stay_consistent_and_bounded()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var limits = new AuditLimits(MaxRows: 200, ReservedRows: 20, LowPriorityRows: 30);
        var log = new AuditLog(t.Database, limits);
        var start = DateTimeOffset.UtcNow;
        var maxSeen = 0L;
        using var stop = new CancellationTokenSource();

        var writers = Enumerable.Range(0, 6).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 150; i++)
            {
                var id = $"w{w}-{i}";
                var ts = start.AddMilliseconds((w * 1000) + i);
                switch (i % 3)
                {
                    case 0:
                        log.Write(Event(id, ts, AuditOutcome.Pending));
                        log.Write(Event(id, ts, AuditOutcome.Success));
                        break;
                    case 1:
                        log.Write(Event(id, ts));
                        break;
                    default:
                        log.Write(Event(id, ts, AuditOutcome.AuthFailed, "http"));
                        break;
                }
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        var others = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var events = log.Query(new AuditQuery(Limit: 100_000));
                using var csv = new StringWriter();
                AuditLog.ExportCsv(events, csv);
                using var json = new MemoryStream();
                AuditLog.ExportJson(events, json);
                log.Purge(30, start);
                var rows = log.GetStatus().Rows;
                InterlockedMax(ref maxSeen, Math.Max(rows, events.Count));
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(writers);
        await stop.CancelAsync();
        await others;

        Assert.True(maxSeen <= limits.MaxRows, $"{maxSeen} rows seen");
        Assert.True(log.GetStatus().Rows <= limits.MaxRows - limits.ReservedRows);
        Assert.Equal(0, log.GetStatus().FailedWrites);
        Assert.DoesNotContain(log.Query(new AuditQuery(Limit: 100_000)), e => e.Outcome == AuditOutcome.Pending);
        AssertUsageMatches(t.Database, log);
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    [Fact]
    public void Upgrade_from_v1_keeps_data_counts_existing_records_and_trims_an_oversized_log()
    {
        using var t = new TempDatabase();
        t.Database.Migrate([SysactaDatabase.Migrations[0]]);
        var gate = new AuthorizationGate();
        var store = new AccessStore(t.Database, TimeProvider.System, gate);
        store.SyncTools([new Core.Tools.ToolDescriptor("audio_get_state", "audio", "x", "x", Core.Tools.DefaultPolicy.On, true, [])], "0.1.0");
        var issued = store.IssueClient("agent", null, ["audio_get_state"]);
        new SettingsStore(t.Database, TimeProvider.System).Save(new AppSettings { Port = 9100 });
        using (var c = t.Database.Open())
        {
            for (var i = 0; i < 300; i++)
            {
                SysactaDatabase.Execute(c, "INSERT INTO audit_events (request_id, timestamp_utc, source, result, client_name) VALUES ($id, $ts, $src, 'success', 'Agent')",
                    ("$id", $"r{i:D4}"), ("$ts", SysactaDatabase.ToDb(DateTimeOffset.UtcNow.AddSeconds(i))), ("$src", i % 2 == 0 ? "mcp" : "http"));
            }
        }

        Assert.Equal(1, t.Database.Migrate());

        Assert.True(store.IsClientToolAllowed(issued.Client.Id, "audio_get_state"));
        Assert.NotNull(store.FindClientByTokenHash(ClientTokens.Hash(issued.Token)));
        Assert.Equal(9100, new SettingsStore(t.Database, TimeProvider.System).Load().Settings.Port);

        var log = new AuditLog(t.Database, new AuditLimits(MaxRows: 120, ReservedRows: 20, LowPriorityRows: 30));
        Assert.Equal(300, log.GetStatus().Rows);
        AssertUsageMatches(t.Database, log);
        Assert.All(log.Query(new AuditQuery(Limit: 1000)), e => Assert.Null(e.AggregateCount));

        Assert.Equal(200, log.EnforceLimits()); // 120 rejection records beyond 30, then 80 more beyond 100
        Assert.Equal(100, log.GetStatus().Rows);
        Assert.Equal(30, log.Query(new AuditQuery(Limit: 1000)).Count(e => e.Source == "http"));
        Assert.Contains(log.Query(new AuditQuery(Limit: 1000)), e => e.RequestId == "r0299");
        AssertUsageMatches(t.Database, log);
        Assert.True(store.IsClientToolAllowed(issued.Client.Id, "audio_get_state"));
    }

    [Fact]
    public void Physical_size_is_bounded_and_the_wal_is_truncated_after_a_long_reader()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var limits = new AuditLimits(MaxRows: 500, MaxBytes: 256 * 1024, ReservedRows: 50, ReservedBytes: 25 * 1024, LowPriorityRows: 100, LowPriorityBytes: 25 * 1024);
        var log = new AuditLog(t.Database, limits);
        var wal = new FileInfo(t.Database.Path + "-wal");
        var output = TestContext.Current.TestOutputHelper;

        void Report(string phase)
        {
            var s = log.GetStatus();
            wal.Refresh();
            output?.WriteLine($"{phase}: rows={s.Rows} logical={s.Bytes} file={s.FileBytes} free={s.FreeBytes} wal={(wal.Exists ? wal.Length : 0)} evicted={s.EvictedCount}");
        }

        void WriteMany(int from, int count)
        {
            for (var i = from; i < from + count; i++)
            {
                log.Write(Event($"r{i:D6}", DateTimeOffset.UtcNow.AddTicks(i), i % 5 == 0 ? AuditOutcome.AuthFailed : AuditOutcome.Success, i % 5 == 0 ? "http" : "mcp"));
            }
        }

        WriteMany(0, 3_000);
        Report("after 3k writes");
        var fileAfterFirstRound = log.GetStatus().FileBytes;

        // A long read transaction stops checkpoints from resetting the WAL, so it grows while writes continue.
        using (var reader = t.Database.Open())
        using (var tx = reader.BeginTransaction(deferred: true))
        {
            using (var cmd = SysactaDatabase.Command(reader, "SELECT COUNT(*) FROM audit_events"))
            {
                cmd.Transaction = tx;
                cmd.ExecuteScalar();
            }

            WriteMany(3_000, 1_500);
            Report("1.5k writes during a long read");
            tx.Rollback();
        }

        WriteMany(4_500, 3_000);
        Report("3k more writes");

        // Once the reader is gone, the next checkpoint cuts the WAL back to the size limit.
        wal.Refresh();
        Assert.True(wal.Length <= SysactaDatabase.WalSizeLimitBytes, $"WAL {wal.Length} bytes");
        var status = log.GetStatus();
        Assert.True(status.Rows <= limits.MaxRows - limits.ReservedRows);
        Assert.True(status.Bytes <= limits.MaxBytes - limits.ReservedBytes);

        // Freed pages are reused: three times the writes do not make the file grow by more than a little.
        Assert.True(status.FileBytes <= fileAfterFirstRound * 2, $"{status.FileBytes} vs {fileAfterFirstRound}");

        log.Maintain(30, DateTimeOffset.UtcNow);
        Report("after maintenance");
        wal.Refresh();
        Assert.True(!wal.Exists || wal.Length == 0);
    }
}
