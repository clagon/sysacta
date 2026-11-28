using System.Diagnostics;
using System.Text.Json;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;

namespace Sysacta.Core.Tools;

/// <summary>Spec §10 provisional limits.</summary>
public sealed record ExecutorLimits(int MaxConcurrent = 8, int MaxQueued = 32);

/// <summary>
/// The only way to run a tool. Every call, whether from MCP or from the local console, goes through
/// <see cref="ExecuteAsync"/>, which applies in order:
/// <list type="number">
/// <item>tool lookup, authorization (global ∩ client ∩ capability) and strict argument validation;</item>
/// <item>admission control (queue limit), the power transition gate (state changes only) and target resolution;</item>
/// <item>resource locks (state-changing tools only) and a concurrency slot;</item>
/// <item>re-authorization immediately before execution (revocations while queued take effect);</item>
/// <item>a persisted "pending" audit record before any state change (refuses to run if it cannot be written);</item>
/// <item>execution with timeout; if the native call does not finish in time, the locks stay held until it does;</item>
/// <item>the final audit record.</item>
/// </list>
/// </summary>
public sealed partial class ToolExecutor
{
    public const string UnknownToolCode = "UNKNOWN_TOOL";

    private readonly ToolRegistry _registry;
    private readonly AccessPolicy _policy;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ExecutorLimits _limits;
    private readonly AuthorizationGate _gate;
    private readonly PowerTransitionGate _power;
    private readonly ResourceLocks _locks = new();
    private readonly SemaphoreSlim _slots;
    private int _admitted;

    /// <param name="gate">The same gate instance the permission store uses for its updates (see <see cref="AuthorizationGate"/>).</param>
    /// <param name="power">The process-wide power transition gate (one per process; a new one when omitted, for tests).</param>
    public ToolExecutor(ToolRegistry registry, AccessPolicy policy, IAuditLog audit, TimeProvider time, AuthorizationGate gate, ExecutorLimits? limits = null, PowerTransitionGate? power = null)
    {
        _registry = registry;
        _gate = gate;
        _power = power ?? new PowerTransitionGate();
        _policy = policy;
        _audit = audit;
        _time = time;
        _limits = limits ?? new ExecutorLimits();
        _slots = new SemaphoreSlim(_limits.MaxConcurrent, _limits.MaxConcurrent);
    }

    public ToolRegistry Registry => _registry;

    /// <summary>The power transition gate this executor admits state changes through.</summary>
    public PowerTransitionGate PowerGate => _power;

    /// <summary>Calls admitted and not yet finished (queued + running, including isolated timed-out native calls).</summary>
    public int InFlight => Volatile.Read(ref _admitted);

    internal int TrackedLockCount => _locks.TrackedCount;

    /// <summary>Tools this caller may call now (used for tools/list). Not an authorization by itself.</summary>
    public IReadOnlyList<SysactaTool> ListTools(Caller caller) =>
        [.. _registry.All.Where(t => _policy.Evaluate(caller, t) == AccessDecision.Allowed).OrderBy(t => t.Name, StringComparer.Ordinal)];

    public async Task<ToolResult> ExecuteAsync(Caller caller, string toolName, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var call = new Call(this, caller, toolName, NewRequestId(), _time.GetTimestamp());
        try
        {
            return await ExecuteCoreAsync(call, caller, toolName, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unexpected failure before execution started (e.g. the permission database is unavailable, or a bug in
            // target resolution). Every path after execution starts is handled inside. Never leak exception text.
            return call.Finish(AuditOutcome.Error, ToolResult.Fail(ErrorCodes.InternalError, "An internal error occurred before the operation started."));
        }
    }

    private async Task<ToolResult> ExecuteCoreAsync(Call call, Caller caller, string toolName, JsonElement? arguments, CancellationToken cancellationToken)
    {

        var tool = _registry.Find(toolName);
        // A local-only tool does not exist for MCP clients.
        if (tool is null || (tool.Descriptor.LocalOnly && caller.Kind != CallerKind.LocalConsole))
        {
            return call.Finish(AuditOutcome.Denied, ToolResult.Fail(UnknownToolCode, "Unknown tool."));
        }

        var decision = _policy.Evaluate(caller, tool);
        if (decision != AccessDecision.Allowed)
        {
            return call.Finish(AuditOutcome.Denied, AccessPolicy.ToResult(decision, tool));
        }

        object args;
        try
        {
            args = tool.Parse(arguments);
        }
        catch (ToolFailureException ex)
        {
            return call.Finish(AuditOutcome.Denied, ToolResult.Fail(ex.Code, ex.Message));
        }

        call.AuditArguments = tool.SanitizeForAudit(args, resolvedTarget: null);

        if (Interlocked.Increment(ref _admitted) > _limits.MaxConcurrent + _limits.MaxQueued)
        {
            Interlocked.Decrement(ref _admitted);
            return call.Finish(AuditOutcome.Denied, ToolResult.Fail(ErrorCodes.Busy, "The server is busy. Retry later.", retryable: true));
        }

        // Before target resolution, locks and the slot: a state change is admitted only while no power transition runs or
        // is held, and a power transition only while no other state change is admitted (one atomic check each).
        var transition = tool.Descriptor.Transition;
        if (!tool.Descriptor.ReadOnly && !(transition is { } kind ? _power.TryEnterTransition(kind) : _power.TryEnterChange()))
        {
            Interlocked.Decrement(ref _admitted);
            return call.Finish(AuditOutcome.Denied, ToolResult.Fail(ErrorCodes.Busy, transition is null
                ? "A power transition is in progress or has not been released; state changes are refused."
                : "Another state change or power transition is in progress. Retry later.", retryable: true));
        }

        // From here on, Release() must run exactly once: either in finally, or after an isolated native call completes.
        var resources = new HeldResources(this, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            Gate = tool.Descriptor.ReadOnly ? GateRole.None : transition is null ? GateRole.Change : GateRole.Transition,
        };
        resources.Cancellation.CancelAfter(tool.Descriptor.EffectiveTimeout);
        var token = resources.Cancellation.Token;
        var handedOff = false;
        try
        {
            ToolTarget target;
            // Resolution may call synchronous OS APIs; run it off the caller's thread and bound it by the timeout.
            var resolution = Task.Run(() => tool.ResolveAsync(args, token).AsTask(), CancellationToken.None);
            try
            {
                target = await resolution.WaitAsync(token).ConfigureAwait(false);

                // From now on the record names the resolved target (an OS / registry id), never the raw input.
                call.AuditArguments = tool.SanitizeForAudit(args, target.TargetId);
                if (!tool.Descriptor.ReadOnly)
                {
                    var keys = target.LockKeys.Count > 0 ? target.LockKeys : [$"tool:{tool.Name}"];
                    resources.Lease = await _locks.AcquireAsync(keys, token).ConfigureAwait(false);
                }

                await _slots.WaitAsync(token).ConfigureAwait(false);
                resources.HasSlot = true;
            }
            catch (ToolFailureException ex)
            {
                return call.Finish(AuditOutcome.Error, ToolResult.Fail(ex.Code, ex.Message, ex.Retryable));
            }
            catch (OperationCanceledException)
            {
                if (!resolution.IsCompleted)
                {
                    // A hung OS call during resolution keeps a worker busy: keep it counted against the admission
                    // limit until it really ends, like an isolated execution (no locks are held yet).
                    handedOff = true;
                    _ = resolution.ContinueWith(static (_, state) => ((HeldResources)state!).Release(), resources, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                // Nothing has been executed yet, so retrying is safe.
                return call.Finish(AuditOutcome.Error, cancellationToken.IsCancellationRequested
                    ? ToolResult.Fail(ErrorCodes.Cancelled, "The request was cancelled before execution.", retryable: true)
                    : ToolResult.Fail(ErrorCodes.Timeout, "Timed out waiting for the target resource.", retryable: true));
            }

            if (!tool.Descriptor.ReadOnly && !call.TryWritePending())
            {
                return call.Finish(AuditOutcome.Denied, ToolResult.Fail(ErrorCodes.AuditUnavailable, "The audit log is unavailable; state changes are refused.", retryable: true));
            }

            // Early re-check before starting the work (cheap exit for changes made while queued). The authoritative check
            // for state changes is ToolExecution.BeginSideEffect, at the point where the change actually starts.
            decision = _policy.Evaluate(caller, tool);
            if (decision != AccessDecision.Allowed)
            {
                return call.Finish(AuditOutcome.Denied, AccessPolicy.ToResult(decision, tool));
            }

            var execution = new ToolExecution(target, () =>
            {
                var d = _policy.Evaluate(caller, tool);
                return d == AccessDecision.Allowed ? null : AccessPolicy.ToResult(d, tool);
            }, _gate, transition is null ? null : _power.MarkStarted);
            // Cancellation competes atomically with the start point, even before the waiting caller resumes.
            // The registration lives with the linked token source until the actual work has finished.
            _ = token.UnsafeRegister(static state => { _ = ((ToolExecution)state!).TryAbandon(); }, execution);
            var work = Task.Run(() => tool.RunAsync(args, execution, token), CancellationToken.None);
            resources.Work = work;
            try
            {
                var run = await work.WaitAsync(token).ConfigureAwait(false);
                if (run.Status == ToolStatus.Success)
                {
                    return call.Finish(AuditOutcome.Success, new ToolResult { Status = ToolStatus.Success, Verification = run.Verification, Data = run.Data });
                }

                if (run.Status == ToolStatus.Error && run.Error is not null)
                {
                    return call.Finish(AuditOutcome.Error, new ToolResult { Status = ToolStatus.Error, Data = run.Data, Error = run.Error });
                }

                if (!execution.HasStarted)
                {
                    // Fail safe: a partial or unknown outcome cannot be reported without a started side effect.
                    return call.Finish(AuditOutcome.Error, ToolResult.Fail(ErrorCodes.InternalError, "An internal error occurred."));
                }

                if (run.Status == ToolStatus.Partial)
                {
                    var err = (run.Error ?? new ToolError(ErrorCodes.VerificationFailed, "The operation partially succeeded.", false)) with { Retryable = false };
                    return call.Finish(AuditOutcome.Partial, new ToolResult
                    {
                        Status = ToolStatus.Partial,
                        Verification = run.Verification,
                        Data = run.Data,
                        Error = err,
                    });
                }

                if (run.Status == ToolStatus.Unknown)
                {
                    var err = (run.Error ?? new ToolError(ErrorCodes.InternalError, "Whether the change was applied is unknown.", false)) with { Retryable = false };
                    return call.Finish(AuditOutcome.Unknown, new ToolResult
                    {
                        Status = ToolStatus.Unknown,
                        Verification = Verification.Unknown,
                        Data = run.Data,
                        Error = err,
                    });
                }

                return call.Finish(AuditOutcome.Error, ToolResult.Fail(ErrorCodes.InternalError, "An internal error occurred."));
            }
            catch (SideEffectRefusedException ex)
            {
                // Authorization was lost before the side effect started; nothing was changed.
                return call.Finish(AuditOutcome.Denied, ex.Result);
            }
            catch (ToolOutcomeUnknownException ex)
            {
                return call.Finish(AuditOutcome.Unknown, ToolResult.Unknown(ex.Code, ex.Message));
            }
            catch (ToolFailureException ex)
            {
                return call.Finish(AuditOutcome.Error, ToolResult.Fail(ex.Code, ex.Message, ex.Retryable));
            }
            catch (Exception ex) when (ex is OperationCanceledException || !work.IsCompleted)
            {
                if (!work.IsCompleted)
                {
                    // The native call cannot be cancelled. Keep locks and the slot until it really ends (spec §10).
                    handedOff = true;
                    _ = work.ContinueWith(static (_, state) => ((HeldResources)state!).Release(), resources, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                var (code, message) = cancellationToken.IsCancellationRequested
                    ? (ErrorCodes.Cancelled, "The request was cancelled during execution.")
                    : (ErrorCodes.Timeout, "The operation did not complete within the time limit.");

                // Before the start point the change can be ruled out: abandoning makes BeginSideEffect refuse to start.
                if (tool.Descriptor.ReadOnly || execution.TryAbandon())
                {
                    return call.Finish(AuditOutcome.Error, ToolResult.Fail(code, message + " Nothing was changed.", retryable: true));
                }

                execution.Detach();
                if (execution.AcceptedProgress is { } accepted)
                {
                    return call.Finish(AuditOutcome.Success, new ToolResult { Status = ToolStatus.Success, Verification = Verification.Accepted, Data = accepted });
                }
                return call.Finish(AuditOutcome.Unknown, ToolResult.Unknown(code, message + " Whether the change was applied is unknown.") with { Data = execution.Progress });
            }
            catch (Exception)
            {
                // Never return raw exception text. After the start point a failed change may have partially happened.
                if (tool.Descriptor.ReadOnly || execution.TryAbandon())
                {
                    return call.Finish(AuditOutcome.Error, ToolResult.Fail(ErrorCodes.InternalError, "An internal error occurred."));
                }

                execution.Detach();
                return call.Finish(AuditOutcome.Unknown, ToolResult.Unknown(ErrorCodes.InternalError, "An internal error occurred. Whether the change was applied is unknown.") with { Data = execution.Progress });
            }
        }
        finally
        {
            if (!handedOff)
            {
                resources.Release();
            }
        }
    }

    private static string NewRequestId() => Guid.CreateVersion7().ToString("N");

    /// <summary>
    /// Only registered names are recorded (SEC-03). A name the client made up is replaced by a fixed value, because no
    /// shape check can prove that client text carries no secret.
    /// </summary>
    private static string AuditToolName(ToolRegistry registry, string toolName) =>
        registry.Find(toolName) is { } tool ? tool.Name : "(unknown tool)";

    private enum GateRole
    {
        None,
        Change,
        Transition,
    }

    private sealed class HeldResources(ToolExecutor owner, CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public ResourceLocks.Lease? Lease { get; set; }
        public bool HasSlot { get; set; }
        public GateRole Gate { get; init; }
        public Task<ToolRun>? Work { get; set; }

        public void Release()
        {
            // Runs only when the call, including an isolated native call, has really ended.
            switch (Gate)
            {
                case GateRole.Change:
                    owner._power.ExitChange();
                    break;
                case GateRole.Transition:
                    // Only an error the tool returned (not threw) proves that the OS did not execute the request.
                    owner._power.ExitTransition(notExecuted: Work is { IsCompletedSuccessfully: true } w && w.Result is { Status: ToolStatus.Error, Error: not null });
                    break;
            }

            Lease?.Dispose();
            if (HasSlot)
            {
                owner._slots.Release();
            }

            Cancellation.Dispose();
            Interlocked.Decrement(ref owner._admitted);
        }
    }

    private sealed class Call(ToolExecutor owner, Caller caller, string toolName, string requestId, long startedAt)
    {
        public string? AuditArguments { get; set; }

        public bool TryWritePending()
        {
            try
            {
                owner._audit.Write(Event(AuditOutcome.Pending, null, null));
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }

        public ToolResult Finish(AuditOutcome outcome, ToolResult result)
        {
            var duration = (long)owner._time.GetElapsedTime(startedAt).TotalMilliseconds;
            try
            {
                owner._audit.Write(Event(outcome, result.Error?.Code, duration));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // ponytail: final-record failures are dropped (the pending record, if any, remains). Surface in UI if this matters.
                Debug.WriteLine($"Audit write failed: {ex.GetType().Name}");
            }

            return result with { RequestId = requestId };
        }

        private AuditEvent Event(AuditOutcome outcome, string? errorCode, long? duration) => new()
        {
            RequestId = requestId,
            Timestamp = owner._time.GetUtcNow(),
            ClientId = caller.ClientId,
            ClientName = caller.DisplayName,
            ToolName = AuditToolName(owner._registry, toolName),
            Source = caller.Source,
            Arguments = AuditArguments,
            Outcome = outcome,
            ErrorCode = errorCode,
            DurationMs = duration,
            RemoteAddress = caller.RemoteAddress,
        };
    }
}
