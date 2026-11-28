using System.Text.Json;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Notification;
using Sysacta.Core.Tools.Power;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

/// <summary>Phase 1G notification_send with a fake adapter (no notification is ever shown) and a manual monotonic clock.</summary>
public class NotificationToolTests
{
    private const string Name = "notification_send";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Clock : TimeProvider
    {
        private long _now = 1_000_000;
        public TimeSpan WallOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _now);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + WallOffset;
        public void Advance(double milliseconds) => Interlocked.Add(ref _now, (long)(milliseconds * TimeSpan.TicksPerMillisecond));
    }

    private sealed record Setup(Harness H, FakeNotifications Os, Clock Time, Caller Caller)
    {
        public Task<ToolResult> Send(string title = "title", string body = "body", Caller? caller = null) =>
            H.Executor.ExecuteAsync(caller ?? Caller, Name, TestJson.Args(new { title, body }), Ct);

        public Task<ToolResult> Raw(string json) => H.Executor.ExecuteAsync(Caller, Name, TestJson.Raw(json), Ct);
    }

    private static Setup Create()
    {
        var os = new FakeNotifications();
        var time = new Clock();
        var h = new Harness(_ => [new NotificationSendTool(os, time), new PowerLockTool(new FakePower())]);
        var (caller, _) = h.Store.AddClient("c1", Name, "power_lock");
        return new(h, os, time, caller);
    }

    private static long RetryAfter(ToolResult r)
    {
        Assert.Equal((ToolStatus.Error, ErrorCodes.Busy, true), (r.Status, r.Error!.Code, r.Error.Retryable));
        Assert.False((bool)r.Data!["accepted"]!);
        return (long)r.Data["retry_after_ms"]!;
    }

    // ---- descriptor, schema and text contract ------------------------------------------------------------------

    [Fact]
    public void Descriptor_and_schemas_are_strict_and_expose_only_title_and_body()
    {
        var s = Create();
        var tool = s.H.Executor.Registry.Find(Name)!;
        var d = tool.Descriptor;
        Assert.Equal((DefaultPolicy.On, false, ToolCategories.Notification, null, false), (d.DefaultPolicy, d.ReadOnly, d.Category, d.Transition, d.LocalOnly));
        Assert.Empty(d.AuditedArguments);
        Assert.Equal(TimeSpan.FromSeconds(5), d.EffectiveTimeout);

        var input = tool.InputSchema;
        Assert.False(input.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["title", "body"], input.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["body", "title"], input.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
        foreach (var (name, max) in new[] { ("title", 64), ("body", 256) })
        {
            var p = input.GetProperty("properties").GetProperty(name);
            Assert.Equal(("string", 1, max), (p.GetProperty("type").GetString(), p.GetProperty("minLength").GetInt32(), p.GetProperty("maxLength").GetInt32()));
        }

        var data = tool.OutputSchema.GetProperty("properties").GetProperty("data").GetProperty("properties");
        Assert.Equal(["accepted", "retry_after_ms"], data.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"title":"t"}""")]
    [InlineData("""{"body":"b"}""")]
    [InlineData("""{"title":null,"body":"b"}""")]
    [InlineData("""{"title":"t","body":null}""")]
    [InlineData("""{"title":1,"body":"b"}""")]
    [InlineData("""{"title":"t","body":["b"]}""")]
    [InlineData("""{"title":"","body":"b"}""")]
    [InlineData("""{"title":"t","body":""}""")]
    [InlineData("""{"title":"   ","body":"b"}""")]
    [InlineData("""{"title":"　","body":"b"}""")]
    [InlineData("""{"title":"t","body":" \n\r\n "}""")]
    [InlineData("""{"title":"‍‌","body":"b"}""")]
    [InlineData("""{"title":"t","body":"b","image":"x"}""")]
    [InlineData("""{"title":"t","body":"b","launch":"x"}""")]
    [InlineData("""{"Title":"t","body":"b"}""")]
    [InlineData("""{"title":"a\nb","body":"b"}""")]
    [InlineData("""{"title":"a\r\nb","body":"b"}""")]
    [InlineData("""{"title":"t","body":"a\rb"}""")]
    [InlineData("""{"title":"t","body":"a\r"}""")]
    [InlineData("""{"title":"t","body":"a\tb"}""")]
    [InlineData("""{"title":"a\u0000b","body":"b"}""")]
    [InlineData("""{"title":"t","body":"a\u0007b"}""")]
    [InlineData("""{"title":"t","body":"a\u001bb"}""")]
    [InlineData("""{"title":"t","body":"a\u007fb"}""")]
    [InlineData("""{"title":"t","body":"a\u0085b"}""")]
    [InlineData("""{"title":"t","body":"a\u2028b"}""")]
    [InlineData("""{"title":"t","body":"a\u2029b"}""")]
    [InlineData("""{"title":"t","body":"a\u202Eb"}""")]
    [InlineData("""{"title":"t","body":"a\u2066b"}""")]
    [InlineData("""{"title":"a\u200Fb","body":"b"}""")]
    [InlineData("""{"title":"a\u061Cb","body":"b"}""")]
    [InlineData("""{"title":"a\u200Bb","body":"b"}""")]
    [InlineData("""{"title":"a\uFEFFb","body":"b"}""")]
    [InlineData("""{"title":"a\uFFFEb","body":"b"}""")]
    [InlineData("""{"title":"a\uFDD0b","body":"b"}""")]
    [InlineData("""{"title":"a\uD83F\uDFFFb","body":"b"}""")]
    [InlineData("""{"title":"a\uD800b","body":"b"}""")]
    [InlineData("""{"title":"t","body":"a\uDC00"}""")]
    [InlineData("""{"title":"a\uDB40\uDC61","body":"b"}""")] // tag character without a flag
    [InlineData("""{"title":"\uD83C\uDFF4\uDB40\uDC67","body":"b"}""")] // unterminated tag sequence
    [InlineData("""{"title":"\uD83C\uDFF4\uDB40\uDC7F","body":"b"}""")] // cancel tag alone
    [InlineData("""{"title":"ms-resource:AppName","body":"b"}""")]
    [InlineData("""{"title":"t","body":" MS-RESOURCE:x"}""")]
    [InlineData("""{"title":"t","body":"ms-appx:///a.png"}""")]
    [InlineData("""{"title":"{progressValue}","body":"b"}""")]
    public async Task Invalid_text_is_rejected_without_reaching_the_adapter_or_consuming_quota(string json)
    {
        var s = Create();
        var r = await s.Raw(json);
        Assert.Equal((ToolStatus.Error, ErrorCodes.InvalidArgument), (r.Status, r.Error!.Code));
        Assert.Equal(0, s.Os.Prepared);
        Assert.Empty(s.Os.Sent);
        Assert.Null(s.H.Audit.Final(r.RequestId).Arguments);
        Assert.True((await s.Send()).IsSuccess);
    }

    [Fact]
    public async Task Length_limits_count_utf16_code_units_and_nothing_is_truncated()
    {
        var s = Create();
        var emoji = "😀"; // 2 UTF-16 code units, 1 code point (JSON Schema maxLength counts code points and is looser)
        foreach (var (title, body, ok) in new[]
        {
            (new string('a', 64), new string('b', 256), true),
            (new string('a', 65), "b", false),
            ("a", new string('b', 257), false),
            (string.Concat(Enumerable.Repeat(emoji, 32)), string.Concat(Enumerable.Repeat(emoji, 128)), true),
            (string.Concat(Enumerable.Repeat(emoji, 32)) + "a", "b", false),
            ("a", string.Concat(Enumerable.Repeat(emoji, 128)) + "a", false),
            ("a", new string('b', 254) + "\r\n", true), // CRLF counts as 2 as received
            ("a", new string('b', 255) + "\r\n", false),
        })
        {
            s.Time.Advance(60_000);
            var r = await s.Send(title, body);
            Assert.Equal(ok ? ToolStatus.Success : ToolStatus.Error, r.Status);
        }

        Assert.Equal(3, s.Os.Sent.Count);
        Assert.All(s.Os.Sent, n => Assert.True(n.Title.Length <= 64));
    }

    [Theory]
    [InlineData("ビルド完了 ✅", "テストが成功しました。\n次の行")]
    [InlineData("<toast launch=\"x\">&amp;</toast>", "\"quotes\" 'single' <actions><action activationType=\"protocol\" arguments=\"calc:\"/></actions> ]]> &#x41;")]
    [InlineData("👨‍👩‍👧‍👦 🏳️‍🌈 👍🏽 1️⃣", "🏴\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F می‌خواهم é क्‍ष")]
    [InlineData("https://example.com {x} y", "C:\\Temp ms-resource is just a word")]
    public async Task Unicode_emoji_and_markup_characters_are_passed_through_as_text(string title, string body)
    {
        var s = Create();
        var r = await s.Send(title, body);
        Assert.Equal((ToolStatus.Success, Verification.Accepted), (r.Status, r.Verification));
        Assert.Equal((title, body), Assert.Single(s.Os.Sent));
    }

    [Fact]
    public async Task Crlf_is_normalized_to_lf_and_the_result_never_echoes_the_text()
    {
        var s = Create();
        var r = await s.Send("secret-title", "secret-line1\r\nsecret-line2");
        Assert.Equal("secret-line1\nsecret-line2", Assert.Single(s.Os.Sent).Body);
        Assert.Equal("""{"accepted":true,"retry_after_ms":null}""", r.Data!.ToJsonString());
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(r), StringComparison.Ordinal);
        Assert.All(s.H.Audit.Events, e => Assert.Null(e.Arguments));
    }

    // ---- rate limit ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sends_need_a_two_second_interval()
    {
        var s = Create();
        Assert.True((await s.Send()).IsSuccess);
        Assert.Equal(2000, RetryAfter(await s.Send()));
        s.Time.Advance(1999.5);
        Assert.Equal(1, RetryAfter(await s.Send()));
        s.Time.Advance(0.5);
        Assert.True((await s.Send()).IsSuccess);
        Assert.Equal(2, s.Os.Sent.Count);
        Assert.Equal(2, s.Os.Prepared); // refused before any native preparation
    }

    [Fact]
    public async Task Five_sends_per_rolling_sixty_seconds_including_the_window_edge()
    {
        var s = Create();
        for (var i = 0; i < 5; i++) // starts at 0, 2, 4, 6, 8 s
        {
            Assert.True((await s.Send()).IsSuccess);
            s.Time.Advance(2000);
        }

        Assert.Equal(50_000, RetryAfter(await s.Send())); // t=10
        s.Time.Advance(49_999);
        Assert.Equal(1, RetryAfter(await s.Send()));
        s.Time.Advance(1); // t=60: only the first start left the window
        Assert.True((await s.Send()).IsSuccess);
        Assert.Equal(2000, RetryAfter(await s.Send())); // waits for the second start (t=62), no burst at the boundary
        s.Time.Advance(2000);
        Assert.True((await s.Send()).IsSuccess);
        Assert.Equal(7, s.Os.Sent.Count);
    }

    [Fact]
    public async Task The_limit_is_shared_by_clients_reissued_tokens_and_the_local_console_and_ignores_the_wall_clock()
    {
        var s = Create();
        var (other, _) = s.H.Store.AddClient("c2", Name);
        s.H.Store.EnabledTools[Name] = true;
        Assert.True((await s.Send()).IsSuccess);
        RetryAfter(await s.Send(caller: other));
        RetryAfter(await s.Send(caller: Caller.LocalConsole));
        s.H.Update(() => s.H.Store.Reissue("c2"));
        var reissued = Caller.Client("c2", "c2", "10.0.0.9", s.H.Store.Hashes["c2"]);
        RetryAfter(await s.Send(caller: reissued));
        s.Time.WallOffset = TimeSpan.FromDays(1);
        RetryAfter(await s.Send());
        s.Time.WallOffset = TimeSpan.FromDays(-1);
        RetryAfter(await s.Send());
        s.Time.Advance(2000);
        Assert.True((await s.Send(caller: Caller.LocalConsole)).IsSuccess);
        Assert.Equal(2, s.Os.Sent.Count);
    }

    [Fact]
    public async Task Concurrent_calls_start_only_one_send()
    {
        var s = Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => s.Send(), Ct)));
        Assert.Single(s.Os.Sent);
        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Equal(ErrorCodes.Busy, r.Error!.Code));
    }

    // ---- start point, authorization and failures -------------------------------------------------------------

    [Fact]
    public async Task A_pending_record_is_written_before_the_send_and_without_it_nothing_is_sent()
    {
        var s = Create();
        AuditOutcome[]? atSend = null;
        s.Os.OnSend = () => atSend = [.. s.H.Audit.Events.Select(e => e.Outcome)];
        Assert.True((await s.Send()).IsSuccess);
        Assert.Equal([AuditOutcome.Pending], atSend);

        s.Time.Advance(2000);
        s.H.Audit.Fail = true;
        Assert.Equal(ErrorCodes.AuditUnavailable, (await s.Send()).Error!.Code);
        Assert.Single(s.Os.Sent);
    }

    [Theory]
    [InlineData("revoke", ErrorCodes.PermissionDenied)]
    [InlineData("reissue", ErrorCodes.PermissionDenied)]
    [InlineData("expire", ErrorCodes.PermissionDenied)]
    [InlineData("ungrant", ErrorCodes.PermissionDenied)]
    [InlineData("disable", ErrorCodes.ToolDisabled)]
    public async Task Authorization_lost_during_preparation_sends_nothing_and_consumes_no_quota(string change, string code)
    {
        var s = Create();
        s.Os.OnPrepare = () => s.H.Update(() =>
        {
            switch (change)
            {
                case "revoke": s.H.Store.Revoke("c1"); break;
                case "reissue": s.H.Store.Reissue("c1"); break;
                case "expire": s.H.Store.Clients["c1"] = s.H.Store.Clients["c1"] with { ExpiresAt = DateTimeOffset.UnixEpoch }; break;
                case "ungrant": s.H.Store.Grants[("c1", Name)] = false; break;
                default: s.H.Store.EnabledTools[Name] = false; break;
            }
        });

        var r = await s.Send();
        Assert.Equal(code, r.Error!.Code);
        Assert.Empty(s.Os.Sent);
        Assert.Equal(AuditOutcome.Denied, s.H.Audit.Final(r.RequestId).Outcome);

        s.Os.OnPrepare = null;
        s.H.Store.EnabledTools[Name] = true;
        Assert.True((await s.Send(caller: Caller.LocalConsole)).IsSuccess); // immediately: nothing was consumed
    }

    [Fact]
    public async Task Calls_without_permission_or_while_disabled_do_not_reach_the_adapter()
    {
        var s = Create();
        var (none, _) = s.H.Store.AddClient("none");
        Assert.Equal(ErrorCodes.PermissionDenied, (await s.Send(caller: none)).Error!.Code);
        s.H.Store.EnabledTools[Name] = false;
        Assert.Equal(ErrorCodes.ToolDisabled, (await s.Send()).Error!.Code);
        Assert.Equal(ErrorCodes.ToolDisabled, (await s.Send(caller: Caller.LocalConsole)).Error!.Code);
        Assert.Equal(0, s.Os.Prepared);
    }

    [Theory]
    [InlineData(ErrorCodes.NotificationsDisabled, false)]
    [InlineData(ErrorCodes.NotSupported, false)]
    [InlineData(ErrorCodes.OsApiError, true)]
    public async Task A_preparation_failure_is_an_error_and_consumes_no_quota(string code, bool retryable)
    {
        var s = Create();
        s.Os.OnPrepare = () => throw new ToolFailureException(code, "Nothing was sent.", retryable);
        var r = await s.Send();
        Assert.Equal((ToolStatus.Error, code, retryable), (r.Status, r.Error!.Code, r.Error.Retryable));
        Assert.Empty(s.Os.Sent);
        s.Os.OnPrepare = null;
        Assert.True((await s.Send()).IsSuccess);
    }

    [Fact]
    public async Task A_registration_failure_makes_the_tool_unavailable()
    {
        var s = Create();
        s.Os.Availability = ToolAvailability.Unavailable(ErrorCodes.NotSupported, "Notification registration failed when Sysacta started.");
        Assert.DoesNotContain(s.H.Executor.ListTools(s.Caller), t => t.Name == Name);
        Assert.Equal(ErrorCodes.NotSupported, (await s.Send()).Error!.Code);
        Assert.Equal(0, s.Os.Prepared);
    }

    [Fact]
    public async Task A_failed_send_is_unknown_not_retried_consumes_quota_and_leaks_no_text()
    {
        var s = Create();
        s.Os.OnSend = () => throw new InvalidOperationException("payload synthetic-secret C:\\private");
        var r = await s.Send("synthetic-secret", "synthetic-secret");
        Assert.Equal((ToolStatus.Unknown, Verification.Unknown, ErrorCodes.OsApiError, false), (r.Status, r.Verification, r.Error!.Code, r.Error.Retryable));
        Assert.Null(r.Data!["accepted"]);
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(r) + JsonSerializer.Serialize(s.H.Audit.Events), StringComparison.Ordinal);
        Assert.Equal(AuditOutcome.Unknown, s.H.Audit.Final(r.RequestId).Outcome);
        Assert.Single(s.Os.Sent);
        s.Os.OnSend = null;
        RetryAfter(await s.Send());
    }

    [Fact]
    public async Task A_cancellation_during_preparation_sends_nothing()
    {
        var s = Create();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var release = new ManualResetEventSlim();
        s.Os.OnPrepare = () =>
        {
            cancel.Cancel();
            release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
        };
        var r = await s.H.Executor.ExecuteAsync(s.Caller, Name, TestJson.Args(new { title = "t", body = "b" }), cancel.Token);
        Assert.Equal((ToolStatus.Error, ErrorCodes.Cancelled), (r.Status, r.Error!.Code));
        release.Set();
        await Until(() => s.H.Executor.InFlight == 0);
        Assert.Empty(s.Os.Sent);
        s.Os.OnPrepare = null;
        Assert.True((await s.Send()).IsSuccess);
    }

    [Fact]
    public async Task A_hung_preparation_times_out_and_never_sends_late()
    {
        var s = Create();
        using var release = new ManualResetEventSlim();
        s.Os.OnPrepare = () => release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
        var r = await s.Send();
        Assert.Equal((ToolStatus.Error, ErrorCodes.Timeout), (r.Status, r.Error!.Code));
        release.Set();
        await Until(() => s.H.Executor.InFlight == 0);
        Assert.Empty(s.Os.Sent);
    }

    [Fact]
    public async Task A_hung_send_is_unknown_and_keeps_the_lock_and_the_power_gate_until_it_ends()
    {
        var s = Create();
        using var release = new ManualResetEventSlim();
        s.Os.OnSend = () => release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = s.H.Executor.ExecuteAsync(s.Caller, Name, TestJson.Args(new { title = "t", body = "b" }), cancel.Token);
        await Until(() => !s.Os.Sent.IsEmpty);
        cancel.Cancel();
        var r = await first;
        Assert.Equal((ToolStatus.Unknown, false), (r.Status, r.Error!.Retryable));
        Assert.Null(r.Data!["accepted"]);

        s.Time.Advance(60_000);
        using var short1 = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        short1.CancelAfter(200);
        var blocked = await s.H.Executor.ExecuteAsync(s.Caller, Name, TestJson.Args(new { title = "t", body = "b" }), short1.Token);
        Assert.Equal(ToolStatus.Error, blocked.Status); // still waiting for the lock
        Assert.Equal(ErrorCodes.Busy, (await s.H.Executor.ExecuteAsync(s.Caller, "power_lock", null, Ct)).Error!.Code);
        Assert.Single(s.Os.Sent);

        release.Set();
        await Until(() => s.H.Executor.InFlight == 0);
        s.Os.OnSend = null;
        Assert.True((await s.Send()).IsSuccess);
        Assert.Equal(2, s.Os.Sent.Count);
    }

    [Fact]
    public async Task Nothing_is_sent_while_a_power_transition_is_held()
    {
        var s = Create();
        Assert.True(s.H.Power.TryEnterTransition(PowerTransition.Shutdown));
        Assert.Equal(ErrorCodes.Busy, (await s.Send()).Error!.Code);
        Assert.Equal(0, s.Os.Prepared);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(10, Ct);
        }
    }
}
