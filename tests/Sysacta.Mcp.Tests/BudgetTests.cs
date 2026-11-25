using System.Net;
using System.Text;
using Sysacta.Core.Auditing;
using Sysacta.Core.Tools;
using Sysacta.Infrastructure;

namespace Sysacta.Mcp.Tests;

/// <summary>SEC-05: budgets before the database, and the last-used time only within the client budget.</summary>
public class PreAuthenticationBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string UnknownToken = "sya_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ListBody = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""";

    private static async Task<List<HttpStatusCode>> SendAsync(HttpClient http, int count, Action<HttpRequestMessage>? change = null)
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            using var request = ServerFixture.JsonRpc(ListBody);
            change?.Invoke(request);
            using var response = await http.SendAsync(request, Ct);
            codes.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is { } retry)
            {
                Assert.InRange(retry.Delta!.Value.TotalSeconds, 1, 60);
            }
        }

        return codes;
    }

    [Fact]
    public async Task Unknown_token_flood_beyond_the_address_budget_reaches_neither_database_nor_audit()
    {
        await using var f = await ServerFixture.StartAsync(new ServerEndpointOptions { Port = 0, RequestsPerMinutePerAddress = 5, AuthFailuresPerMinutePerAddress = 100 });
        using var http = f.Http(UnknownToken);

        var codes = await SendAsync(http, 12);

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.Unauthorized, 5), .. Enumerable.Repeat(HttpStatusCode.TooManyRequests, 7)], codes);
        Assert.Equal(5, f.Counted.Lookups);
        Assert.Equal(5, f.Audit.GetStatus().Rows);

        // The next window: the refused requests appear as one aggregated record, and requests are processed again.
        f.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal([HttpStatusCode.Unauthorized], await SendAsync(http, 1));
        var aggregated = Assert.Single(f.Audit.Query(new AuditQuery(Aggregated: true)));
        Assert.Equal(RequestGuards.RequestBudgetExceeded, aggregated.ErrorCode);
        Assert.Equal(7, aggregated.AggregateCount);
        Assert.Null(aggregated.RemoteAddress);
        Assert.Equal(6, f.Counted.Lookups);
    }

    [Fact]
    public async Task Requests_over_the_client_budget_do_not_write_the_last_used_time()
    {
        await using var f = await ServerFixture.StartAsync(new ServerEndpointOptions { Port = 0, RequestsPerMinutePerClient = 3 });
        f.Counted.ForgetLastUse = true; // every request within the budget would write
        using var http = f.Http(f.Issue("audio_get_state").Token);

        var codes = await SendAsync(http, 6);

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.OK, 3), .. Enumerable.Repeat(HttpStatusCode.TooManyRequests, 3)], codes);
        Assert.Equal(3, f.Counted.Uses);
    }

    [Fact]
    public async Task Last_used_time_is_written_at_most_once_a_minute()
    {
        await using var f = await ServerFixture.StartAsync();
        var issued = f.Issue("audio_get_state");
        using var http = f.Http(issued.Token);

        await SendAsync(http, 3);
        Assert.Equal(1, f.Counted.Uses);
        Assert.NotNull(f.Store.GetClient(issued.Client.Id)!.LastUsedAt);

        f.Time.Advance(TimeSpan.FromSeconds(61));
        await SendAsync(http, 2);
        Assert.Equal(2, f.Counted.Uses);
    }

    [Fact]
    public async Task Host_origin_and_malformed_requests_cannot_bypass_the_budget()
    {
        await using var f = await ServerFixture.StartAsync(new ServerEndpointOptions { Port = 0, RequestsPerMinutePerAddress = 4 });
        using var anonymous = f.Http();
        using var valid = f.Http(f.Issue("audio_get_state").Token);

        var codes = new List<HttpStatusCode>();
        codes.AddRange(await SendAsync(anonymous, 2, r => r.Headers.Host = "evil.example"));
        codes.AddRange(await SendAsync(anonymous, 1, r => r.Headers.TryAddWithoutValidation("Origin", "https://evil.example")));
        codes.AddRange(await SendAsync(anonymous, 1, r => r.Headers.TryAddWithoutValidation("Authorization", "Basic dXNlcjpwYXNz")));
        codes.AddRange(await SendAsync(valid, 1));

        Assert.Equal([HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], codes);
    }

    [Fact]
    public async Task Forwarded_headers_do_not_split_the_address_budget()
    {
        await using var f = await ServerFixture.StartAsync(new ServerEndpointOptions { Port = 0, RequestsPerMinutePerAddress = 3 });
        using var http = f.Http(f.Issue("audio_get_state").Token);
        var i = 0;

        var codes = await SendAsync(http, 6, r =>
        {
            i++;
            r.Headers.TryAddWithoutValidation("X-Forwarded-For", $"203.0.113.{i}");
            r.Headers.TryAddWithoutValidation("Forwarded", $"for=198.51.100.{i}");
            r.Headers.TryAddWithoutValidation("X-Real-IP", $"192.0.2.{i}");
        });

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.OK, 3), .. Enumerable.Repeat(HttpStatusCode.TooManyRequests, 3)], codes);
        Assert.Equal(1, f.Host.LastBuiltGuards!.RequestBudget.TrackedKeys);
    }

    [Fact]
    public async Task Authentication_stage_has_a_bounded_number_of_requests_and_no_queue()
    {
        await using var f = await ServerFixture.StartAsync(new ServerEndpointOptions { Port = 0, MaxConcurrentAuthentications = 1 });
        using var gate = new SemaphoreSlim(0);
        f.Counted.LookupGate = gate;
        using var http = f.Http(UnknownToken);

        var first = http.SendAsync(ServerFixture.JsonRpc(ListBody), Ct);
        await Wait.Until(() => f.Counted.Lookups == 1);
        using (var second = await http.SendAsync(ServerFixture.JsonRpc(ListBody), Ct))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
            Assert.NotNull(second.Headers.RetryAfter);
        }

        Assert.Equal(1, f.Counted.Lookups); // refused before the database
        gate.Release();
        using (var response = await first)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        f.Counted.LookupGate = null;
        Assert.Equal([HttpStatusCode.Unauthorized], await SendAsync(http, 1)); // the slot was returned
    }

    [Fact]
    public async Task Several_clients_behind_one_address_work_within_the_budget()
    {
        await using var f = await ServerFixture.StartAsync();
        var clients = Enumerable.Range(0, 4).Select(_ => f.Http(f.Issue("audio_get_state").Token)).ToList();
        try
        {
            foreach (var http in clients)
            {
                Assert.All(await SendAsync(http, 10), c => Assert.Equal(HttpStatusCode.OK, c));
            }
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public async Task Rejections_beyond_the_audit_budget_are_aggregated_with_fixed_values_only()
    {
        const string secret = "SYNTHETICSECRETzq9";
        await using var f = await ServerFixture.StartAsync(new ServerEndpointOptions { Port = 0, AuthFailuresPerMinutePerAddress = 1 });
        using var http = f.Http();

        await SendAsync(http, 3, r => r.Headers.Host = secret.ToLowerInvariant() + ".example");
        await SendAsync(http, 3, r => r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret));
        await f.Host.StopAsync(); // writes the counted rejections

        var all = f.Audit.Query(new AuditQuery());
        var individual = Assert.Single(all, e => e.AggregateCount is null);
        Assert.Equal("HostNotAllowed", individual.ErrorCode);
        var aggregated = all.Where(e => e.AggregateCount is not null).OrderBy(e => e.ErrorCode, StringComparer.Ordinal).ToList();
        Assert.Equal(["HostNotAllowed", "Malformed"], aggregated.Select(e => e.ErrorCode));
        Assert.Equal([2L, 3L], aggregated.Select(e => e.AggregateCount!.Value));
        Assert.All(aggregated, e => Assert.Null(e.Arguments));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(f.Database.Path)!))
        {
            var content = File.ReadAllBytes(file);
            Assert.True(content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret.ToLowerInvariant())) < 0, $"Secret found in {Path.GetFileName(file)}.");
            Assert.True(content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0, $"Secret found in {Path.GetFileName(file)}.");
        }

        using var csv = new StringWriter();
        AuditLog.ExportCsv(all, csv);
        Assert.Contains("aggregate_count", csv.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("::ffff:192.0.2.7", "192.0.2.7")]
    [InlineData("fe80::1%3", "fe80::1")]
    [InlineData("::1", "::1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public void Socket_addresses_are_normalized(string address, string expected) =>
        Assert.Equal(expected, RequestGuards.NormalizeAddress(IPAddress.Parse(address)));
}

public class RequestBudgetUnitTests
{
    [Fact]
    public void Budget_is_per_key_and_total_and_keys_are_bounded_by_the_total()
    {
        var time = new ManualTime();
        var budget = new RequestBudget(perKey: 2, total: 5, time);

        Assert.True(budget.TryAcquire("a", out _));
        Assert.True(budget.TryAcquire("a", out _));
        Assert.False(budget.TryAcquire("a", out var retry)); // per key
        Assert.InRange(retry.TotalSeconds, 0, 60);

        // A refused key does not consume the total: three other keys still fit.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(budget.TryAcquire($"k{i}", out _));
        }

        // The total is used up; new keys are refused and not tracked.
        for (var i = 0; i < 1000; i++)
        {
            Assert.False(budget.TryAcquire($"new{i}", out _));
        }

        Assert.Equal(4, budget.TrackedKeys);

        // The next window forgets every key.
        time.Advance(RequestBudget.Window);
        Assert.Equal(0, budget.TrackedKeys);
        Assert.True(budget.TryAcquire("a", out _));
    }

    [Fact]
    public void Concurrent_acquisitions_never_exceed_the_total()
    {
        var budget = new RequestBudget(perKey: 1_000, total: 500, new ManualTime());
        var granted = 0;
        Parallel.For(0, 4_000, i =>
        {
            if (budget.TryAcquire($"k{i % 7}", out _))
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(500, granted);
    }
}

/// <summary>SEC-06 through the whole pipeline: the reserve for state changes and the refusal when it is gone.</summary>
public class AuditCapacityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task State_change_is_refused_without_side_effect_when_the_audit_log_has_no_room()
    {
        // Two records in total, one of them reserved for state changes; both get used by operations in progress.
        await using var f = await ServerFixture.StartAsync(limits: new AuditLimits(MaxRows: 2, ReservedRows: 1, LowPriorityRows: 1));
        var issued = f.Issue("audio_set_volume", f.Blocking.Name);
        await using var c = await f.ClientAsync(issued.Token);

        var a = c.CallToolAsync(f.Blocking.Name, new Dictionary<string, object?> { ["resource"] = "a" }, cancellationToken: Ct).AsTask();
        var b = c.CallToolAsync(f.Blocking.Name, new Dictionary<string, object?> { ["resource"] = "b" }, cancellationToken: Ct).AsTask();
        await Wait.Until(() => f.Blocking.Started == 2);

        var refused = await c.CallToolAsync("audio_set_volume", new Dictionary<string, object?> { ["level"] = 10 }, cancellationToken: Ct);

        Assert.True(refused.IsError);
        Assert.Contains(ErrorCodes.AuditUnavailable, refused.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, f.AudioDevice.Writes);

        // The operations in progress still get their final records (an update of their intent, no new row).
        f.Blocking.Gates["a"].Release();
        f.Blocking.Gates["b"].Release();
        await Task.WhenAll(a, b);
        var records = f.Audit.Query(new AuditQuery());
        Assert.Equal(2, records.Count);
        Assert.All(records, e => Assert.Equal(AuditOutcome.Success, e.Outcome));
        Assert.Equal(2, f.Audit.GetStatus().Rows);
    }

    [Fact]
    public async Task State_change_is_recorded_when_the_log_is_full_of_ordinary_records()
    {
        await using var f = await ServerFixture.StartAsync(limits: new AuditLimits(MaxRows: 10, ReservedRows: 2, LowPriorityRows: 4));
        var issued = f.Issue("audio_get_state", "audio_set_volume");
        await using var c = await f.ClientAsync(issued.Token);
        for (var i = 0; i < 20; i++)
        {
            await c.CallToolAsync("audio_get_state", new Dictionary<string, object?>(), cancellationToken: Ct);
        }

        Assert.True(f.Audit.GetStatus().Rows <= 8);

        var result = await c.CallToolAsync("audio_set_volume", new Dictionary<string, object?> { ["level"] = 10 }, cancellationToken: Ct);

        Assert.False(result.IsError);
        Assert.Equal(1, f.AudioDevice.Writes);
        Assert.Equal(AuditOutcome.Success, Assert.Single(f.Audit.Query(new AuditQuery(ToolName: "audio_set_volume"))).Outcome);
        Assert.True(f.Audit.GetStatus().EvictedCount > 0);
    }

    [Fact]
    public async Task Rejection_flood_keeps_the_log_and_the_aggregation_bounded()
    {
        await using var f = await ServerFixture.StartAsync(
            new ServerEndpointOptions { Port = 0, AuthFailuresPerMinutePerAddress = 1_000, AuditedRejectionsPerMinute = 10 },
            new AuditLimits(MaxRows: 100, ReservedRows: 10, LowPriorityRows: 15));
        using var http = f.Http("sya_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        for (var minute = 0; minute < 3; minute++)
        {
            for (var i = 0; i < 40; i++)
            {
                (await http.SendAsync(ServerFixture.JsonRpc("{}"), Ct)).Dispose();
            }

            f.Time.Advance(TimeSpan.FromSeconds(61));
        }

        await f.Host.StopAsync();
        var status = f.Audit.GetStatus();
        Assert.True(status.Rows <= 15, $"{status.Rows} rows");
        Assert.Equal(120, f.Counted.Lookups); // every request was within the request budget

        // At most one aggregated record per minute and reason, each counting that minute's unrecorded rejections.
        // Older ones are evicted like any other old rejection record.
        var aggregated = f.Audit.Query(new AuditQuery(Aggregated: true));
        Assert.InRange(aggregated.Count, 1, 3);
        Assert.All(aggregated, e => Assert.Equal(30, e.AggregateCount));
    }
}
