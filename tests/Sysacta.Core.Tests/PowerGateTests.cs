using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Core.Tools.Display;
using Sysacta.Core.Tools.Power;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

/// <summary>
/// Phase 1E: the power transition gate inside ToolExecutor. Every state change holds a shared admission; a power
/// transition is admitted exclusively; held transitions are released only by a resume (sleep) or the local console.
/// No real lock, sleep, shutdown or restart is ever requested (FakePower).
/// </summary>
public class PowerGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Transitions = ["power_lock", "power_sleep", "power_shutdown", "power_restart"];

    private sealed class Rig
    {
        public FakePower Power { get; } = new();
        public FakeAudioEndpoints Audio { get; } = new();
        public GateTool Change { get; } = new();
        public Harness H { get; }
        public Caller Caller { get; }

        public ProbeTool Probe { get; } = new();

        public Rig(ExecutorLimits? limits = null)
        {
            var gate = new PowerTransitionGate();
            H = new Harness(store =>
            [
                new PowerGetStateTool(Power), new PowerListPlansTool(Power), new PowerSetPlanTool(Power),
                new PowerLockTool(Power), new PowerSleepTool(Power), new PowerShutdownTool(Power), new PowerRestartTool(Power),
                new PowerReleaseGateTool(gate), Change, Probe,
                new AudioSetVolumeTool(Audio, store), new DisplayRestoreModeTool(new FakeDisplays(), new DisplayModeRecovery()),
            ], limits, gate);
            (Caller, _) = H.Store.AddClient("c1", [.. Transitions, "power_get_state", "power_set_plan", Change.Name, Probe.Name, "audio_set_volume", "display_restore_mode"]);
        }

        public PowerTransitionGate Gate => H.Power;

        public Task<ToolResult> Call(string tool, object? args = null, CancellationToken? ct = null) =>
            H.Executor.ExecuteAsync(Caller, tool, args is null ? null : TestJson.Args(args), ct ?? Ct);

        public Task<ToolResult> Local(string tool) => H.Executor.ExecuteAsync(Caller.LocalConsole, tool, null, Ct);

        /// <summary>Starts the blocking state change and waits until its side effect has started.</summary>
        public async Task<Task<ToolResult>> StartChangeAsync(string resource = "a", CancellationToken? ct = null)
        {
            var before = Volatile.Read(ref Change.Started);
            var task = Call(Change.Name, new { resource }, ct);
            await Until(() => Volatile.Read(ref Change.Started) > before);
            return task;
        }

        public async Task<Task<ToolResult>> StartTransitionAsync(string tool, CancellationToken? ct = null)
        {
            Power.Block = new SemaphoreSlim(0);
            var before = Power.Requests;
            var task = Call(tool, null, ct);
            await Until(() => Power.Requests > before);
            return task;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.True(condition());
    }

    private static void AssertBusy(ToolResult result)
    {
        Assert.Equal(ToolStatus.Error, result.Status);
        Assert.Equal(ErrorCodes.Busy, result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Theory]
    [MemberData(nameof(TransitionNames))]
    public async Task A_state_change_in_progress_refuses_a_transition_before_its_side_effect(string transition)
    {
        var r = new Rig();
        var change = await r.StartChangeAsync();

        var refused = await r.Call(transition);

        AssertBusy(refused);
        Assert.Equal(0, r.Power.Requests);
        Assert.Empty(r.Power.Log);
        Assert.Equal(AuditOutcome.Denied, r.H.Audit.Final(refused.RequestId).Outcome);
        Assert.Equal(PowerGatePhase.Idle, r.Gate.State.Phase);

        r.Change.Gate("a").Release();
        Assert.True((await change).IsSuccess);
        Assert.Equal(0, r.Gate.State.RunningChanges);
    }

    [Theory]
    [MemberData(nameof(TransitionNames))]
    public async Task A_running_transition_refuses_every_new_state_change_but_allows_reads(string transition)
    {
        var r = new Rig();
        var running = await r.StartTransitionAsync(transition);
        Assert.Equal(PowerGatePhase.Running, r.Gate.State.Phase);

        AssertBusy(await r.Call(r.Change.Name, new { resource = "a" }));
        AssertBusy(await r.Call("audio_set_volume", new { level = 10 }));
        AssertBusy(await r.Call("power_set_plan", new { plan_id = FakePower.Saver.ToString() }));
        AssertBusy(await r.Local("display_restore_mode"));
        foreach (var other in Transitions)
        {
            AssertBusy(await r.Call(other));
        }

        Assert.True((await r.Call("power_get_state")).IsSuccess);
        Assert.Equal(0, Volatile.Read(ref r.Change.Started));
        Assert.Equal(0, r.Power.PlanWrites);
        Assert.Equal(1, r.Power.Requests);

        r.Power.Block!.Release();
        var result = await running;
        Assert.Equal(ToolStatus.Success, result.Status);
        Assert.Equal(Verification.Accepted, result.Verification);
    }

    [Fact]
    public async Task Lock_releases_the_gate_when_the_request_completes()
    {
        var r = new Rig();

        Assert.True((await r.Call("power_lock")).IsSuccess);

        Assert.Equal(PowerGatePhase.Idle, r.Gate.State.Phase);
        r.Change.Gate("a").Release();
        Assert.True((await r.Call(r.Change.Name, new { resource = "a" })).IsSuccess);
    }

    [Theory]
    [InlineData("power_shutdown")]
    [InlineData("power_restart")]
    public async Task An_accepted_shutdown_or_restart_is_held_until_the_local_console_releases_it(string transition)
    {
        var r = new Rig();

        var result = await r.Call(transition);

        Assert.Equal(Verification.Accepted, result.Verification);
        Assert.Equal(PowerGatePhase.Held, r.Gate.State.Phase);
        AssertBusy(await r.Call(r.Change.Name, new { resource = "a" }));
        AssertBusy(await r.Call(transition));
        r.Gate.NotifySuspend();
        r.Gate.NotifyResume();
        Assert.Equal(PowerGatePhase.Held, r.Gate.State.Phase); // no OS signal releases a shutdown

        // MCP clients cannot see or call the release.
        var mcp = await r.Call("power_release_gate");
        Assert.Equal(ToolExecutor.UnknownToolCode, mcp.Error!.Code);
        Assert.Equal(PowerGatePhase.Held, r.Gate.State.Phase);

        var released = await r.Local("power_release_gate");
        Assert.True(released.IsSuccess);
        Assert.Equal("released", released.Data!["result"]!.GetValue<string>());
        Assert.Equal(AuditOutcome.Success, r.H.Audit.Final(released.RequestId).Outcome);
        Assert.Equal(PowerGatePhase.Idle, r.Gate.State.Phase);
        Assert.Equal("not_held", (await r.Local("power_release_gate")).Data!["result"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(DefiniteFailures))]
    public async Task A_definite_OS_refusal_releases_the_gate(string transition, PowerRequestResult outcome)
    {
        var r = new Rig();
        r.Power.RequestResult = outcome;

        var result = await r.Call(transition);

        Assert.Equal(ToolStatus.Error, result.Status);
        Assert.False(result.Error!.Retryable);
        Assert.False(result.Data!["accepted"]!.GetValue<bool>());
        Assert.Equal(1, r.Power.Requests);
        Assert.Equal(PowerGatePhase.Idle, r.Gate.State.Phase);
        Assert.Equal(0, r.Power.OpenPrivilegeScopes);
    }

    [Theory]
    [MemberData(nameof(TransitionNames))]
    public async Task A_preparation_failure_requests_nothing_and_releases_the_gate(string transition)
    {
        var r = new Rig();
        r.Power.PrepareFailure = new ToolFailureException(ErrorCodes.OsApiError, "Windows did not grant the shutdown privilege to this account.");

        var result = await r.Call(transition);

        Assert.Equal(ErrorCodes.OsApiError, result.Error!.Code);
        Assert.Equal(0, r.Power.Requests);
        Assert.DoesNotContain(r.Power.Log, e => e.StartsWith("request", StringComparison.Ordinal));
        Assert.Equal(0, r.Power.OpenPrivilegeScopes);
        Assert.Equal(PowerGatePhase.Idle, r.Gate.State.Phase);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("reissue")]
    [InlineData("grant")]
    [InlineData("global")]
    [InlineData("expire")]
    public async Task Losing_authorization_after_privilege_preparation_refuses_at_the_start_point(string change)
    {
        foreach (var transition in Transitions)
        {
            var r = new Rig();
            r.Power.BeforeStart = () => r.H.Update(() =>
            {
                switch (change)
                {
                    case "revoke": r.H.Store.Revoke("c1"); break;
                    case "reissue": r.H.Store.Reissue("c1"); break;
                    case "grant": r.H.Store.Grants[("c1", transition)] = false; break;
                    case "global": r.H.Store.EnabledTools[transition] = false; break;
                    case "expire": r.H.Store.Clients["c1"] = r.H.Store.Clients["c1"] with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }; break;
                }
            });

            var result = await r.Call(transition);

            Assert.Equal(ToolStatus.Error, result.Status);
            Assert.Equal(AuditOutcome.Denied, r.H.Audit.Final(result.RequestId).Outcome);
            Assert.Equal(0, r.Power.Requests);
            Assert.Equal(0, r.Power.OpenPrivilegeScopes); // the privilege scope was closed although BeginSideEffect threw
            Assert.Equal(PowerGatePhase.Idle, r.Gate.State.Phase);
        }
    }

    [Theory]
    [MemberData(nameof(TransitionNames))]
    public async Task The_pending_audit_record_is_written_before_the_request(string transition)
    {
        var r = new Rig();
        var pendingSeen = false;
        r.Power.DuringRequest = () => pendingSeen = r.H.Audit.Events.Any(e => e.ToolName == transition && e.Outcome == AuditOutcome.Pending);

        var result = await r.Call(transition);

        Assert.True(result.IsSuccess);
        Assert.True(pendingSeen);
        var records = r.H.Audit.Events.Where(e => e.RequestId == result.RequestId).ToList();
        Assert.Equal([AuditOutcome.Pending, AuditOutcome.Success], records.Select(e => e.Outcome));
        Assert.All(records, e => Assert.Null(e.Arguments));
    }

    [Theory]
    [MemberData(nameof(TransitionNames))]
    public async Task An_unwritable_audit_log_refuses_the_transition(string transition)
    {
        var r = new Rig();
        r.H.Audit.Fail = true;

        var result = await r.Call(transition);

        Assert.Equal(ErrorCodes.AuditUnavailable, result.Error!.Code);
        Assert.Equal(0, r.Power.Requests);
        Assert.Empty(r.Power.Log);
        Assert.Equal(PowerGatePhase.Idle, r.Gate.State.Phase);
    }

    [Theory]
    [InlineData("power_lock", PowerGatePhase.Idle)]
    [InlineData("power_shutdown", PowerGatePhase.Held)]
    [InlineData("power_restart", PowerGatePhase.Held)]
    [InlineData("power_sleep", PowerGatePhase.Held)]
    public async Task A_cancelled_native_request_keeps_the_gate_until_it_really_ends(string transition, PowerGatePhase after)
    {
        var r = new Rig();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        r.Power.DuringRequest = cts.Cancel;
        var running = await r.StartTransitionAsync(transition, cts.Token);

        var result = await running;

        Assert.Equal(ToolStatus.Unknown, result.Status);
        Assert.False(result.Error!.Retryable);
        Assert.Equal(PowerGatePhase.Running, r.Gate.State.Phase);
        AssertBusy(await r.Call(r.Change.Name, new { resource = "a" }));
        var early = await r.Local("power_release_gate");
        Assert.Equal(ErrorCodes.Busy, early.Error!.Code); // never released while the native call runs
        Assert.Equal(PowerGatePhase.Running, r.Gate.State.Phase);
        // The privilege scope lives exactly as long as the native request (none for lock).
        Assert.Equal(transition == "power_lock" ? 0 : 1, r.Power.OpenPrivilegeScopes);

        r.Power.Block!.Release();
        await Until(() => r.H.Executor.InFlight == 0);
        Assert.Equal(after, r.Gate.State.Phase);
        Assert.Equal(0, r.Power.OpenPrivilegeScopes);
        Assert.Equal(1, r.Power.Requests);
    }

    [Fact]
    public async Task A_state_change_isolated_after_a_timeout_still_blocks_transitions()
    {
        var r = new Rig();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var change = await r.StartChangeAsync("a", cts.Token);
        await cts.CancelAsync();
        Assert.Equal(ToolStatus.Unknown, (await change).Status);
        Assert.Equal(1, r.Gate.State.RunningChanges);

        AssertBusy(await r.Call("power_shutdown"));
        Assert.Equal(0, r.Power.Requests);

        r.Change.Gate("a").Release();
        await Until(() => r.H.Executor.InFlight == 0);
        Assert.Equal(0, r.Gate.State.RunningChanges);
        Assert.True((await r.Call("power_lock")).IsSuccess);
    }

    [Fact]
    public async Task A_state_change_hanging_in_target_resolution_blocks_transitions_until_it_returns()
    {
        var r = new Rig();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        r.Probe.ResolveBlock = new SemaphoreSlim(0);
        var hung = r.Call(r.Probe.Name, new { value = 1 }, cts.Token);
        await Until(() => r.Gate.State.RunningChanges == 1);
        await cts.CancelAsync();
        Assert.Equal(ErrorCodes.Cancelled, (await hung).Error!.Code);

        AssertBusy(await r.Call("power_restart"));
        Assert.Equal(0, r.Power.Requests);

        r.Probe.ResolveBlock.Release();
        await Until(() => r.H.Executor.InFlight == 0);
        Assert.Equal(0, r.Gate.State.RunningChanges);
        Assert.True((await r.Call("power_lock")).IsSuccess);
    }

    [Fact]
    public async Task Sleep_is_released_only_by_a_suspend_and_resume_after_its_start_point()
    {
        var r = new Rig();
        // Notifications before the transition (a sleep by the user) are not evidence for it.
        r.Gate.NotifySuspend();
        r.Gate.NotifyResume();

        Assert.Equal(Verification.Accepted, (await r.Call("power_sleep")).Verification);
        Assert.Equal(PowerGatePhase.Held, r.Gate.State.Phase);

        r.Gate.NotifyResume(); // a resume without a suspend after the start point
        Assert.Equal(PowerGatePhase.Held, r.Gate.State.Phase);
        AssertBusy(await r.Call(r.Change.Name, new { resource = "a" }));

        r.Gate.NotifySuspend();
        Assert.Equal(PowerGatePhase.Held, r.Gate.State.Phase);
        r.Gate.NotifyResume();
        Assert.Equal(PowerGatePhase.Idle, r.Gate.State.Phase);
    }

    [Fact]
    public async Task Sleep_resumed_before_the_request_returns_is_released_when_it_returns()
    {
        var r = new Rig();
        r.Power.DuringRequest = () =>
        {
            r.Gate.NotifySuspend();
            r.Gate.NotifyResume();
            Assert.Equal(PowerGatePhase.Running, r.Gate.State.Phase); // not before the native call ends
        };

        Assert.True((await r.Call("power_sleep")).IsSuccess);

        Assert.Equal(PowerGatePhase.Idle, r.Gate.State.Phase);
    }

    [Fact]
    public async Task A_held_sleep_without_notifications_can_be_released_locally()
    {
        var r = new Rig();
        await r.Call("power_sleep");
        Assert.Equal(PowerGatePhase.Held, r.Gate.State.Phase);

        Assert.True((await r.Local("power_release_gate")).IsSuccess);

        Assert.Equal(PowerGatePhase.Idle, r.Gate.State.Phase);
    }

    [Theory]
    [MemberData(nameof(TransitionNames))]
    public async Task Calls_refused_before_admission_never_hold_the_gate(string transition)
    {
        var r = new Rig();
        var (other, _) = r.H.Store.AddClient("c2", "power_get_state");

        var denied = await r.H.Executor.ExecuteAsync(other, transition, null, Ct);
        var invalid = await r.Call(transition, new { force = true });
        var delayed = await r.Call(transition, new { delay = 5 });

        Assert.Equal(ErrorCodes.PermissionDenied, denied.Error!.Code);
        Assert.Equal(ErrorCodes.InvalidArgument, invalid.Error!.Code);
        Assert.Equal(ErrorCodes.InvalidArgument, delayed.Error!.Code);
        Assert.Equal(new PowerGateState(PowerGatePhase.Idle, null, 0), r.Gate.State);
        Assert.Equal(0, r.Power.Requests);
    }

    [Fact]
    public async Task A_full_queue_refuses_without_leaking_the_gate()
    {
        var r = new Rig(new ExecutorLimits(MaxConcurrent: 1, MaxQueued: 0));
        var change = await r.StartChangeAsync();

        AssertBusy(await r.Call("power_lock"));
        AssertBusy(await r.Call(r.Change.Name, new { resource = "b" }));

        r.Change.Gate("a").Release();
        await change;
        Assert.Equal(new PowerGateState(PowerGatePhase.Idle, null, 0), r.Gate.State);
        Assert.True((await r.Call("power_lock")).IsSuccess);
    }

    /// <summary>Changes and transitions started together never overlap: the admission check is atomic.</summary>
    [Fact]
    public async Task Concurrent_changes_and_transitions_never_run_together()
    {
        var r = new Rig();
        var changesRunning = 0;
        var transitionsRunning = 0;
        var overlaps = 0;
        r.Power.DuringRequest = () =>
        {
            if (Interlocked.Increment(ref transitionsRunning) > 1 || Volatile.Read(ref changesRunning) > 0)
            {
                Interlocked.Increment(ref overlaps);
            }

            Thread.SpinWait(2000);
            Interlocked.Decrement(ref transitionsRunning);
        };
        r.Probe.Body = () =>
        {
            Interlocked.Increment(ref changesRunning);
            if (Volatile.Read(ref transitionsRunning) > 0)
            {
                Interlocked.Increment(ref overlaps);
            }

            Thread.SpinWait(2000);
            Interlocked.Decrement(ref changesRunning);
        };

        var calls = Enumerable.Range(0, 200).Select(i => Task.Run(() => i % 2 == 0
            ? r.Call("power_lock")
            : r.Call(r.Probe.Name, new { value = i % 10 }), Ct)).ToArray();
        var results = await Task.WhenAll(calls);

        Assert.Equal(0, overlaps);
        Assert.All(results, x => Assert.True(x.IsSuccess || x.Error!.Code == ErrorCodes.Busy));
        Assert.Contains(results, x => x.IsSuccess);
        await Until(() => r.H.Executor.InFlight == 0);
        Assert.Equal(new PowerGateState(PowerGatePhase.Idle, null, 0), r.Gate.State);
    }

    [Fact]
    public void Transition_descriptors_are_only_for_state_changing_MCP_tools()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new BadTransition(readOnly: true, localOnly: false)]));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new BadTransition(readOnly: false, localOnly: true)]));
    }

    public static TheoryData<string> TransitionNames() => [.. Transitions];

    public static TheoryData<string, PowerRequestResult> DefiniteFailures()
    {
        var data = new TheoryData<string, PowerRequestResult>();
        foreach (var t in Transitions)
        {
            foreach (var o in new[] { PowerRequestResult.Refused, PowerRequestResult.Busy, PowerRequestResult.Failed })
            {
                data.Add(t, o);
            }
        }

        return data;
    }

    /// <summary>A non-blocking state change on its own resource per call value.</summary>
    private sealed class ProbeTool : SysactaTool<EchoArgs, EchoData>
    {
        public Action? Body { get; set; }

        /// <summary>When set, target resolution blocks (ignoring cancellation) like a hung OS call.</summary>
        public SemaphoreSlim? ResolveBlock { get; set; }

        public override ToolDescriptor Descriptor { get; } = new("audio_set_probe", ToolCategories.Audio, "Probe", "Probe", DefaultPolicy.On, false, ["value"]);

        protected override ValueTask<ToolTarget> ResolveTargetAsync(EchoArgs args, CancellationToken cancellationToken)
        {
            ResolveBlock?.Wait(CancellationToken.None);
            return ValueTask.FromResult(new ToolTarget([$"probe:{args.Value}"], null));
        }

        protected override Task<ToolOutcome<EchoData>> ExecuteAsync(EchoArgs args, ToolExecution execution, CancellationToken cancellationToken)
        {
            execution.BeginSideEffect();
            Body?.Invoke();
            return Task.FromResult(new ToolOutcome<EchoData>(new EchoData(args.Value), Verification.Verified));
        }
    }

    private sealed class BadTransition(bool readOnly, bool localOnly) : SysactaTool<PowerNoArgs, EchoData>
    {
        public override ToolDescriptor Descriptor { get; } = new("power_bad_transition", ToolCategories.Power, "x", "x", DefaultPolicy.Off, readOnly, [], LocalOnly: localOnly, Transition: PowerTransition.Lock);

        protected override Task<ToolOutcome<EchoData>> ExecuteAsync(PowerNoArgs args, ToolExecution execution, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolOutcome<EchoData>(new EchoData(0), Verification.NotApplicable));
    }
}
