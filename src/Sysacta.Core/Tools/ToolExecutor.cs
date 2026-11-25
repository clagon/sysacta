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
/// <item>admission control (queue limit) and target resolution;</item>
/// <item>resource locks (state-changing tools only) and a concurrency slot;</item>
/// <item>re-authorization immediately before execution (revocations while queued take effect);</item>
/// <item>a persisted "pending" audit record before any state change (refuses to run if it cannot be written);</item>
/// <item>execution with timeout; if the native call does not finish in time, the locks stay held until it does;</item>
/// <item>the final audit record.</item>
/// </list>
/// </summary>
public sealed class ToolExecutor
{
    public const string UnknownToolCode = "UNKNOWN_TOOL";

    private readonly ToolRegistry _registry;
    private readonly AccessPolicy _policy;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ExecutorLimits _limits;
    private readonly ResourceLocks _locks = new();
    private readonly SemaphoreSlim _slots;
    private int _admitted;

    public ToolExecutor(ToolRegistry registry, AccessPolicy policy, IAuditLog audit, TimeProvider time, ExecutorLimits? limits = null)
    {
        _registry = registry;
        _policy = policy;
        _audit = audit;
        _time = time;
        _limits = limits ?? new ExecutorLimits();
        _slots = new SemaphoreSlim(_limits.MaxConcurrent, _limits.MaxConcurrent);
    }

    public ToolRegistry Registry => _registry;

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
        if (tool is null)
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

        call.AuditArguments = tool.SanitizeForAudit(args);

        if (Interlocked.Increment(ref _admitted) > _limits.MaxConcurrent + _limits.MaxQueued)
        {
            Interlocked.Decrement(ref _admitted);
            return call.Finish(AuditOutcome.Denied, ToolResult.Fail(ErrorCodes.Busy, "The server is busy. Retry later.", retryable: true));
        }

        // From here on, Release() must run exactly once: either in finally, or after an isolated native call completes.
        var resources = new HeldResources(this, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
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

            // Re-authorize immediately before the side effect: revocation or permission changes made while this call
            // was queued must apply (spec §6).
            decision = _policy.Evaluate(caller, tool);
            if (decision != AccessDecision.Allowed)
            {
                return call.Finish(AuditOutcome.Denied, AccessPolicy.ToResult(decision, tool));
            }

            if (!tool.Descriptor.ReadOnly && !call.TryWritePending())
            {
                return call.Finish(AuditOutcome.Denied, ToolResult.Fail(ErrorCodes.AuditUnavailable, "The audit log is unavailable; state changes are refused.", retryable: true));
            }

            var work = Task.Run(() => tool.RunAsync(args, target, token), CancellationToken.None);
            try
            {
                var (data, verification) = await work.WaitAsync(token).ConfigureAwait(false);
                return call.Finish(AuditOutcome.Success, new ToolResult { Status = ToolStatus.Success, Verification = verification, Data = data });
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
                return tool.Descriptor.ReadOnly
                    ? call.Finish(AuditOutcome.Error, ToolResult.Fail(code, message, retryable: true))
                    : call.Finish(AuditOutcome.Unknown, ToolResult.Unknown(code, message + " Whether the change was applied is unknown."));
            }
            catch (Exception)
            {
                // Never return raw exception text. A failed state change may have partially happened.
                return tool.Descriptor.ReadOnly
                    ? call.Finish(AuditOutcome.Error, ToolResult.Fail(ErrorCodes.InternalError, "An internal error occurred."))
                    : call.Finish(AuditOutcome.Unknown, ToolResult.Unknown(ErrorCodes.InternalError, "An internal error occurred. Whether the change was applied is unknown."));
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

    private sealed class HeldResources(ToolExecutor owner, CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public ResourceLocks.Lease? Lease { get; set; }
        public bool HasSlot { get; set; }

        public void Release()
        {
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
            ToolName = toolName.Length <= 64 ? toolName : toolName[..64],
            Source = caller.Source,
            Arguments = AuditArguments,
            Outcome = outcome,
            ErrorCode = errorCode,
            DurationMs = duration,
            RemoteAddress = caller.RemoteAddress,
        };
    }
}
