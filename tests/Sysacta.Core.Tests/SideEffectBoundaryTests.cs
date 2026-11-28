using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;

namespace Sysacta.Core.Tests;

/// <summary>
/// SEC-01: permission changes are ordered against the start point of a state change (ToolExecution.BeginSideEffect),
/// not only against the executor's earlier re-check.
/// </summary>
public class SideEffectBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A state-changing tool with a read phase before its start point, like a real tool reading the previous state.
    /// Counts the writes that actually happened.
    /// </summary>
    private sealed class StagedTool(TimeSpan? timeout = null) : SysactaTool<EchoArgs, EchoData>
    {
        private int _writes;

        public Action? BeforeStart { get; set; }
        public Action? DuringWrite { get; set; }
        public int Writes => Volatile.Read(ref _writes);

        public override ToolDescriptor Descriptor { get; } = new("power_set_staged", ToolCategories.Power, "Staged", "Staged", DefaultPolicy.Off, ReadOnly: false, AuditedArguments: ["value"], timeout ?? TimeSpan.FromSeconds(10));

        protected override Task<ToolOutcome<EchoData>> ExecuteAsync(EchoArgs args, ToolExecution execution, CancellationToken cancellationToken)
        {
            BeforeStart?.Invoke();          // e.g. reading the previous state through the OS
            execution.BeginSideEffect();    // start point
            Interlocked.Increment(ref _writes);
            DuringWrite?.Invoke();          // the native call itself
            return Task.FromResult(new ToolOutcome<EchoData>(new EchoData(args.Value), Verification.Verified));
        }
    }

    private static (Harness H, StagedTool Tool, Caller Caller) Setup(TimeSpan? timeout = null)
    {
        var tool = new StagedTool(timeout);
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);
        return (h, tool, caller);
    }

    private static void Change(Harness h, string change, string toolName)
    {
        switch (change)
        {
            case "revoke": h.Store.Revoke("c1"); break;
            case "reissue": h.Store.Reissue("c1"); break;
            case "grant": h.Store.Grants[("c1", toolName)] = false; break;
            case "global": h.Store.EnabledTools[toolName] = false; break;
            case "expire": h.Store.Clients["c1"] = h.Store.Clients["c1"] with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }; break;
        }
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("reissue")]
    [InlineData("grant")]
    [InlineData("global")]
    [InlineData("expire")]
    public async Task A_change_completed_after_the_executor_check_but_before_the_start_point_refuses_the_write(string change)
    {
        var (h, tool, caller) = Setup();
        tool.BeforeStart = () => h.Update(() => Change(h, change, tool.Name));

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ToolStatus.Error, result.Status);
        Assert.Contains(result.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled });
        Assert.Equal(0, tool.Writes);
        Assert.Equal(AuditOutcome.Denied, h.Audit.Final(result.RequestId).Outcome);
        Assert.Equal(0, h.Executor.InFlight);
    }

    [Fact]
    public async Task The_start_point_waits_for_an_update_in_progress_and_sees_its_result()
    {
        var (h, tool, caller) = Setup();
        using var updateHeld = new ManualResetEventSlim();
        using var reachedStart = new ManualResetEventSlim();
        var updater = Task.Run(() =>
        {
            using var scope = h.Gate.BeginUpdate();
            updateHeld.Set();
            reachedStart.Wait(Ct);
            Thread.Sleep(100);              // the tool is now blocked at its start point
            h.Store.Revoke("c1");           // committed before the update scope ends
        }, Ct);
        updateHeld.Wait(Ct);
        tool.BeforeStart = reachedStart.Set;

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);
        await updater;

        Assert.Equal(ErrorCodes.PermissionDenied, result.Error!.Code);
        Assert.Equal(0, tool.Writes);
    }

    [Fact]
    public async Task Cancellation_while_the_start_gate_is_held_abandons_the_write()
    {
        var (h, tool, caller) = Setup();
        using var reachedStart = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        using var held = h.Gate.BeginUpdate();
        tool.BeforeStart = reachedStart.Set;
        var call = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), cancel.Token);
        reachedStart.Wait(Ct);
        cancel.Cancel();
        held.Dispose();
        var result = await call;
        Assert.Equal(ToolStatus.Error, result.Status);
        Assert.Equal(ErrorCodes.Cancelled, result.Error!.Code);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Executor.InFlight > 0 && DateTime.UtcNow < deadline) await Task.Delay(10, Ct);
        Assert.Equal(0, tool.Writes);
        Assert.Equal(0, h.Executor.InFlight);
    }

    [Fact]
    public async Task A_long_native_call_after_the_start_point_does_not_hold_up_permission_changes()
    {
        var (h, tool, caller) = Setup();
        using var inWrite = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        tool.DuringWrite = () =>
        {
            inWrite.Set();
            releaseWrite.Wait(CancellationToken.None); // a native call that ignores cancellation
        };

        var call = h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);
        inWrite.Wait(Ct);

        var update = Task.Run(() => h.Update(() => h.Store.Revoke("c1")), Ct);
        var finished = await Task.WhenAny(update, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        Assert.Same(update, finished); // the revocation completed while the native call was still running

        releaseWrite.Set();
        Assert.True((await call).IsSuccess); // started before the revocation: cannot be undone (spec §6)
        Assert.Equal(1, tool.Writes);
    }

    [Fact]
    public async Task A_timeout_before_the_start_point_reports_nothing_changed_and_the_write_never_happens()
    {
        var (h, tool, caller) = Setup(TimeSpan.FromMilliseconds(200));
        using var releaseRead = new ManualResetEventSlim();
        tool.BeforeStart = () => releaseRead.Wait(CancellationToken.None); // a slow OS read before the change

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ToolStatus.Error, result.Status);
        Assert.Equal(ErrorCodes.Timeout, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.Equal(AuditOutcome.Error, h.Audit.Final(result.RequestId).Outcome);

        releaseRead.Set(); // the read finishes late: the abandoned call must not start its change
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (h.Executor.InFlight > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(0, h.Executor.InFlight);
        Assert.Equal(0, tool.Writes);
    }

    [Fact]
    public async Task A_timeout_after_the_start_point_reports_unknown()
    {
        var (h, tool, caller) = Setup(TimeSpan.FromMilliseconds(200));
        using var releaseWrite = new ManualResetEventSlim();
        tool.DuringWrite = () => releaseWrite.Wait(CancellationToken.None);

        var result = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ToolStatus.Unknown, result.Status);
        Assert.Equal(1, tool.Writes);
        releaseWrite.Set();
    }

    [Fact]
    public void Text_arguments_cannot_be_put_on_the_audit_allow_list()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ToolRegistry([new TextAuditTool()]));
        Assert.Contains("secret", ex.Message, StringComparison.Ordinal);
    }

    private sealed class TextAuditTool : SysactaTool<EchoArgs, EchoData>
    {
        public override ToolDescriptor Descriptor { get; } = new("power_set_text", ToolCategories.Power, "x", "x", DefaultPolicy.Off, false, ["secret"]);

        protected override Task<ToolOutcome<EchoData>> ExecuteAsync(EchoArgs args, ToolExecution execution, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
