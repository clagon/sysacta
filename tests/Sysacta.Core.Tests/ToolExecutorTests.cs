using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;

namespace Sysacta.Core.Tests;

public class ToolExecutorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Unknown_tool_is_rejected_and_audited()
    {
        var h = new Harness(null, new EchoTool());
        var (caller, _) = h.Store.AddClient("c1");

        var result = await h.Executor.ExecuteAsync(caller, "audio_secret_tool", null, Ct);

        Assert.Equal(ToolExecutor.UnknownToolCode, result.Error!.Code);
        Assert.Equal(AuditOutcome.Denied, h.Audit.Final(result.RequestId).Outcome);
    }

    [Fact]
    public async Task Direct_call_to_globally_disabled_tool_is_denied_without_execution()
    {
        var tool = new EchoTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);
        h.Store.EnabledTools[tool.Name] = false;

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ErrorCodes.ToolDisabled, result.Error!.Code);
        Assert.Equal(0, tool.Calls);
        Assert.Empty(h.Executor.ListTools(caller));
    }

    [Fact]
    public async Task Direct_call_to_tool_not_granted_to_client_is_denied()
    {
        var tool = new EchoTool();
        var h = new Harness(null, tool);
        h.Store.AddClient("owner", tool.Name);
        var (other, _) = h.Store.AddClient("other");

        var result = await h.Executor.ExecuteAsync(other, tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ErrorCodes.PermissionDenied, result.Error!.Code);
        Assert.Equal(0, tool.Calls);
        Assert.Empty(h.Executor.ListTools(other));
        Assert.Equal(AuditOutcome.Denied, h.Audit.Final(result.RequestId).Outcome);
        Assert.Equal("other", h.Audit.Final(result.RequestId).ClientId);
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_execution()
    {
        var tool = new EchoTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 99 }), Ct);

        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
        Assert.Equal(0, tool.Calls);
        Assert.Null(h.Audit.Final(result.RequestId).Arguments);
    }

    [Fact]
    public async Task Success_is_audited_with_allow_listed_arguments_only()
    {
        var tool = new EchoTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 3, secret = "hunter2" }), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, (int)result.Data!["value"]!);
        Assert.False(string.IsNullOrEmpty(result.RequestId));
        var e = h.Audit.Final(result.RequestId);
        Assert.Equal(AuditOutcome.Success, e.Outcome);
        Assert.Equal("""{"value":3}""", e.Arguments);
        Assert.DoesNotContain(h.Audit.Events, a => (a.Arguments ?? "").Contains("hunter2", StringComparison.Ordinal));
        Assert.Equal("mcp", e.Source);
        Assert.Equal("127.0.0.1", e.RemoteAddress);
    }

    [Fact]
    public async Task State_change_records_pending_intent_before_execution()
    {
        var tool = new GateTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var call = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        await WaitUntil(() => tool.Started == 1);
        Assert.Equal(AuditOutcome.Pending, Assert.Single(h.Audit.Events).Outcome);

        tool.Gate("a").Release();
        var result = await call;
        Assert.Equal(AuditOutcome.Success, h.Audit.Final(result.RequestId).Outcome);
    }

    [Fact]
    public async Task State_change_is_refused_when_audit_cannot_be_written()
    {
        var gate = new GateTool();
        var echo = new EchoTool();
        var h = new Harness(null, gate, echo);
        var (caller, _) = h.Store.AddClient("c1", gate.Name, echo.Name);
        h.Audit.Fail = true;

        var refused = await h.Executor.ExecuteAsync(caller, gate.Name, TestJson.Args(new { resource = "a" }), Ct);
        var read = await h.Executor.ExecuteAsync(caller, echo.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ErrorCodes.AuditUnavailable, refused.Error!.Code);
        Assert.Equal(0, gate.Started);
        Assert.True(read.IsSuccess);
    }

    [Fact]
    public async Task Same_resource_is_serialized_and_different_resources_run_in_parallel()
    {
        var tool = new GateTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var a1 = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        var a2 = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        var b = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "b" }), Ct);

        await WaitUntil(() => tool.Started == 2); // one "a" and the "b"
        await Task.Delay(100, Ct);
        Assert.Equal(2, tool.Started);

        tool.Gate("a").Release(2);
        tool.Gate("b").Release();
        var results = await Task.WhenAll(a1, a2, b);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, tool.MaxOverlapPerResource);
        Assert.Equal(0, h.Executor.InFlight);
        Assert.Equal(0, h.Executor.TrackedLockCount);
    }

    [Fact]
    public async Task Revocation_while_queued_prevents_execution()
    {
        var tool = new GateTool();
        var h = new Harness(null, tool);
        var (holder, _) = h.Store.AddClient("holder", tool.Name);
        var (victim, _) = h.Store.AddClient("victim", tool.Name);

        var first = h.Executor.ExecuteAsync(holder, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        await WaitUntil(() => tool.Started == 1);
        var queued = h.Executor.ExecuteAsync(victim, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        await WaitUntil(() => h.Executor.InFlight == 2);

        h.Store.Revoke("victim");
        tool.Gate("a").Release();

        Assert.True((await first).IsSuccess);
        var result = await queued;
        Assert.Equal(ErrorCodes.PermissionDenied, result.Error!.Code);
        Assert.Equal(1, tool.Started);
        Assert.Equal(AuditOutcome.Denied, h.Audit.Final(result.RequestId).Outcome);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("client")]
    public async Task Permission_change_while_queued_prevents_execution(string which)
    {
        var tool = new GateTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var first = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        await WaitUntil(() => tool.Started == 1);
        var queued = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        await WaitUntil(() => h.Executor.InFlight == 2);

        if (which == "global")
        {
            h.Store.EnabledTools[tool.Name] = false;
        }
        else
        {
            h.Store.Grants[("c1", tool.Name)] = false;
        }

        tool.Gate("a").Release();
        await first;
        var result = await queued;
        Assert.Equal(which == "global" ? ErrorCodes.ToolDisabled : ErrorCodes.PermissionDenied, result.Error!.Code);
        Assert.Equal(1, tool.Started);
    }

    [Fact]
    public async Task Timeout_of_native_call_reports_unknown_and_keeps_lock_until_it_finishes()
    {
        var tool = new GateTool(TimeSpan.FromMilliseconds(300));
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var timedOut = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct);

        Assert.Equal(ToolStatus.Unknown, timedOut.Status);
        Assert.Equal(ErrorCodes.Timeout, timedOut.Error!.Code);
        Assert.False(timedOut.Error.Retryable);
        Assert.Equal(AuditOutcome.Unknown, h.Audit.Final(timedOut.RequestId).Outcome);

        // The native call is still running: the lock must still be held, so a new call cannot start and times out
        // while waiting (safe to retry because nothing ran).
        Assert.Equal(1, h.Executor.InFlight);
        var blocked = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        Assert.Equal(ToolStatus.Error, blocked.Status);
        Assert.Equal(ErrorCodes.Timeout, blocked.Error!.Code);
        Assert.True(blocked.Error.Retryable);
        Assert.Equal(1, tool.Started);

        tool.Gate("a").Release();
        await WaitUntil(() => h.Executor.InFlight == 0 && h.Executor.TrackedLockCount == 0);

        tool.Gate("a").Release();
        Assert.True((await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct)).IsSuccess);
    }

    [Fact]
    public async Task Cancellation_while_queued_is_retryable_and_never_executes()
    {
        var tool = new GateTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var first = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        await WaitUntil(() => tool.Started == 1);
        using var cts = new CancellationTokenSource();
        var queued = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), cts.Token);
        await cts.CancelAsync();

        var result = await queued;
        Assert.Equal(ErrorCodes.Cancelled, result.Error!.Code);
        Assert.True(result.Error.Retryable);

        tool.Gate("a").Release();
        await first;
        Assert.Equal(1, tool.Started);
        Assert.Equal(0, h.Executor.InFlight);
    }

    [Fact]
    public async Task Cancellation_during_state_change_reports_unknown()
    {
        var tool = new GateTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);
        using var cts = new CancellationTokenSource();

        var call = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), cts.Token);
        await WaitUntil(() => tool.Started == 1);
        await cts.CancelAsync();
        var result = await call;

        Assert.Equal(ToolStatus.Unknown, result.Status);
        Assert.Equal(ErrorCodes.Cancelled, result.Error!.Code);
        tool.Gate("a").Release();
        await WaitUntil(() => h.Executor.InFlight == 0);
    }

    [Fact]
    public async Task Queue_limit_returns_busy()
    {
        var tool = new GateTool();
        var h = new Harness(new ExecutorLimits(MaxConcurrent: 1, MaxQueued: 1), tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var running = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        await WaitUntil(() => tool.Started == 1);
        var queued = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "b" }), Ct);
        await WaitUntil(() => h.Executor.InFlight == 2);

        var rejected = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "c" }), Ct);
        Assert.Equal(ErrorCodes.Busy, rejected.Error!.Code);
        Assert.True(rejected.Error.Retryable);

        tool.Gate("a").Release();
        tool.Gate("b").Release();
        await Task.WhenAll(running, queued);
        Assert.Equal(2, tool.Started);
    }

    [Fact]
    public async Task Unexpected_exceptions_are_not_leaked()
    {
        var tool = new EchoTool(_ => throw new InvalidOperationException("C:\\secret\\path"));
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ErrorCodes.InternalError, result.Error!.Code);
        Assert.DoesNotContain("secret", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_console_calls_are_audited_as_console()
    {
        var tool = new EchoTool();
        var h = new Harness(null, tool);
        h.Store.EnabledTools[tool.Name] = true;

        var result = await h.Executor.ExecuteAsync(Caller.LocalConsole, tool.Name, TestJson.Args(new { value = 2 }), Ct);

        Assert.True(result.IsSuccess);
        var e = h.Audit.Final(result.RequestId);
        Assert.Equal("local_console", e.Source);
        Assert.Null(e.ClientId);
    }

    [Fact]
    public async Task Failures_before_execution_become_internal_error_without_details()
    {
        var tool = new EchoTool();
        var broken = new BrokenStore();
        var audit = new RecordingAuditLog();
        var executor = new ToolExecutor(new ToolRegistry([tool]), new AccessPolicy(broken, TimeProvider.System), audit, TimeProvider.System);

        var result = await executor.ExecuteAsync(Caller.Client("c1", "c1", null, new string('0', 64)), tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ErrorCodes.InternalError, result.Error!.Code);
        Assert.DoesNotContain("sysacta.db", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, tool.Calls);
        Assert.Equal(0, executor.InFlight);
    }

    [Fact]
    public async Task Unexpected_exception_in_target_resolution_releases_everything()
    {
        var tool = new ThrowingResolveTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ErrorCodes.InternalError, result.Error!.Code);
        Assert.Equal(0, h.Executor.InFlight);
        Assert.Equal(AuditOutcome.Error, h.Audit.Final(result.RequestId).Outcome);
    }

    private sealed class BrokenStore : IAccessStore
    {
        public bool IsToolEnabled(string toolName) => throw new IOException("database is locked: C:\\Users\\x\\sysacta.db");
        public ClientRecord? GetClient(string clientId) => throw new IOException();
        public (ClientRecord Client, string TokenHash)? FindClientByTokenHash(string tokenHash) => throw new IOException();
        public bool IsClientToolAllowed(string clientId, string toolName) => throw new IOException();
        public void RecordClientUse(string clientId, DateTimeOffset at) => throw new IOException();
    }

    private sealed class ThrowingResolveTool : SysactaTool<EchoArgs, EchoData>
    {
        public override ToolDescriptor Descriptor { get; } = new("power_set_thing", ToolCategories.Power, "x", "x", DefaultPolicy.Off, false, []);

        protected override ValueTask<ToolTarget> ResolveTargetAsync(EchoArgs args, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("secret detail");

        protected override Task<ToolOutcome<EchoData>> ExecuteAsync(EchoArgs args, ToolTarget target, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Reissue_while_queued_refuses_the_call_made_with_the_old_token()
    {
        var tool = new GateTool();
        var h = new Harness(null, tool);
        var (holder, _) = h.Store.AddClient("holder", tool.Name);
        var (victim, _) = h.Store.AddClient("victim", tool.Name);

        var first = h.Executor.ExecuteAsync(holder, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        await WaitUntil(() => tool.Started == 1);
        var queued = h.Executor.ExecuteAsync(victim, tool.Name, TestJson.Args(new { resource = "a" }), Ct);
        await WaitUntil(() => h.Executor.InFlight == 2);

        h.Store.Reissue("victim"); // client stays active, but the credential that authenticated the call is gone
        tool.Gate("a").Release();

        Assert.True((await first).IsSuccess);
        Assert.Equal(ErrorCodes.PermissionDenied, (await queued).Error!.Code);
        Assert.Equal(1, tool.Started);
    }

    [Fact]
    public async Task Hung_target_resolution_stays_counted_until_it_finishes()
    {
        var tool = new HangingResolveTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ErrorCodes.Timeout, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.Equal(1, h.Executor.InFlight); // the blocked worker still counts against the admission limit

        tool.Release.Set();
        await WaitUntil(() => h.Executor.InFlight == 0);
    }

    private sealed class HangingResolveTool : SysactaTool<EchoArgs, EchoData>
    {
        public ManualResetEventSlim Release { get; } = new();

        public override ToolDescriptor Descriptor { get; } = new("power_set_hang", ToolCategories.Power, "x", "x", DefaultPolicy.Off, false, [], TimeSpan.FromMilliseconds(200));

        protected override ValueTask<ToolTarget> ResolveTargetAsync(EchoArgs args, CancellationToken cancellationToken)
        {
            Release.Wait(CancellationToken.None); // a native call that ignores cancellation
            return ValueTask.FromResult(ToolTarget.None);
        }

        protected override Task<ToolOutcome<EchoData>> ExecuteAsync(EchoArgs args, ToolTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolOutcome<EchoData>(new EchoData(1), Verification.Verified));
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("reissue")]
    [InlineData("grant")]
    [InlineData("global")]
    [InlineData("expire")]
    public async Task Changes_committed_while_the_pending_record_is_written_prevent_the_side_effect(string change)
    {
        var tool = new GateTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);
        h.Audit.OnWrite = e =>
        {
            if (e.Outcome != AuditOutcome.Pending)
            {
                return;
            }

            switch (change)
            {
                case "revoke": h.Store.Revoke("c1"); break;
                case "reissue": h.Store.Reissue("c1"); break;
                case "grant": h.Store.Grants[("c1", tool.Name)] = false; break;
                case "global": h.Store.EnabledTools[tool.Name] = false; break;
                case "expire": h.Store.Clients["c1"] = h.Store.Clients["c1"] with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }; break;
            }
        };

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { resource = "a" }), Ct);

        Assert.Equal(ToolStatus.Error, result.Status);
        Assert.Contains(result.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled });
        Assert.Equal(0, tool.Started);
        Assert.Equal(AuditOutcome.Denied, h.Audit.Final(result.RequestId).Outcome); // the pending record was replaced
        Assert.Equal(0, h.Executor.InFlight);
    }

    [Theory]
    [InlineData("audio_secret_tool", "audio_secret_tool")]
    [InlineData("Bearer sya_MARKERmarker0123456789", "(invalid name)")]
    [InlineData("C:\\Users\\someone\\file.txt", "(invalid name)")]
    public async Task Unknown_tool_names_are_audited_only_when_they_look_like_tool_names(string name, string recorded)
    {
        var h = new Harness(null, new EchoTool());
        var (caller, _) = h.Store.AddClient("c1");

        var result = await h.Executor.ExecuteAsync(caller, name, null, Ct);

        Assert.Equal(recorded, h.Audit.Final(result.RequestId).ToolName);
    }
}
