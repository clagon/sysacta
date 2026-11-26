using System.Text.Json.Nodes;
using Sysacta.Core.Security;

namespace Sysacta.Core.Tools;

/// <summary>
/// One call of a tool, as seen by the tool. State-changing tools must call <see cref="BeginSideEffect"/> exactly at the
/// point where the change starts: after reads and validation, immediately before the first OS call that changes state.
/// </summary>
public sealed class ToolExecution
{
    private const int Pending = 0;
    private const int Started = 1;
    private const int Abandoned = 2;

    private readonly Func<ToolResult?> _authorize;
    private readonly AuthorizationGate _gate;
    private int _state;
    private int _detached;
    private JsonNode? _progress;
    private JsonNode? _acceptedProgress;

    internal ToolExecution(ToolTarget target, Func<ToolResult?> authorize, AuthorizationGate gate)
    {
        Target = target;
        _authorize = authorize;
        _gate = gate;
    }

    /// <summary>The resolved target (from ResolveTargetAsync).</summary>
    public ToolTarget Target { get; }

    /// <summary>Shortcut for <c>Target.TargetId</c>.</summary>
    public string? TargetId => Target.TargetId;

    /// <summary>
    /// Start point of the state change. Re-checks authorization so that a revocation, re-issue, permission removal,
    /// tool disabling or expiry completed before this point refuses the call, and marks the operation as started:
    /// from here on a timeout or cancellation reports <c>status: unknown</c>; before it, the call is known not to have
    /// changed anything. Throws if the call is no longer authorized or was already abandoned (timeout/cancel).
    /// Calling it again is a no-op.
    /// </summary>
    public void BeginSideEffect()
    {
        _gate.Evaluate(() =>
        {
            if (Volatile.Read(ref _state) == Started)
            {
                return 0;
            }

            if (Volatile.Read(ref _state) == Abandoned)
            {
                throw new OperationCanceledException("The call was abandoned before its side effect started.");
            }

            if (_authorize() is { } refusal)
            {
                throw new SideEffectRefusedException(refusal);
            }

            if (Interlocked.CompareExchange(ref _state, Started, Pending) == Abandoned)
            {
                throw new OperationCanceledException("The call was abandoned before its side effect started.");
            }

            return 0;
        });
    }

    /// <summary>
    /// Re-checks cancellation, executor detachment (timeout/cancel), and authorization immediately before a subsequent
    /// OS write in a multi-step tool. Must be called only after <see cref="BeginSideEffect"/>.
    /// When this returns a non-null <see cref="ToolError"/>, the caller must NOT perform the next write and must report
    /// any already-applied changes (typically as <see cref="ToolStatus.Partial"/>) without rolling back.
    /// </summary>
    public ToolError? BeginNextSideEffect(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) != Started)
        {
            throw new InvalidOperationException("BeginSideEffect must be called before BeginNextSideEffect.");
        }

        if (Volatile.Read(ref _acceptedProgress) is not null)
        {
            throw new InvalidOperationException("No further writes are allowed after reporting acceptance.");
        }

        if (Volatile.Read(ref _detached) != 0 || cancellationToken.IsCancellationRequested)
        {
            return new ToolError(ErrorCodes.Cancelled, "The operation was cancelled or timed out before the next change started.", false);
        }

        return _gate.Evaluate(() =>
        {
            if (Volatile.Read(ref _detached) != 0 || cancellationToken.IsCancellationRequested)
            {
                return new ToolError(ErrorCodes.Cancelled, "The operation was cancelled or timed out before the next change started.", false);
            }

            if (_authorize() is { } refusal)
            {
                return (refusal.Error ?? new ToolError(ErrorCodes.PermissionDenied, "Authorization was revoked.", false)) with { Retryable = false };
            }

            if (Volatile.Read(ref _detached) != 0 || cancellationToken.IsCancellationRequested)
            {
                return new ToolError(ErrorCodes.Cancelled, "The operation was cancelled or timed out before the next change started.", false);
            }

            return null;
        });
    }

    internal bool HasStarted => Volatile.Read(ref _state) == Started;

    internal JsonNode? Progress => Volatile.Read(ref _progress);

    internal void SetProgress(JsonNode? data) => Volatile.Write(ref _progress, data);

    internal JsonNode? AcceptedProgress => Volatile.Read(ref _acceptedProgress);

    internal void SetAcceptedProgress(JsonNode data)
    {
        if (!HasStarted)
        {
            throw new InvalidOperationException("A request can be accepted only after its side effect starts.");
        }
        Volatile.Write(ref _progress, data);
        Volatile.Write(ref _acceptedProgress, data);
    }

    internal void Detach() => Interlocked.Exchange(ref _detached, 1);

    /// <summary>
    /// Called by the executor on timeout/cancellation. Returns true if the side effect had not started; it then can no
    /// longer start, because <see cref="BeginSideEffect"/> will throw.
    /// </summary>
    internal bool TryAbandon() => Interlocked.CompareExchange(ref _state, Abandoned, Pending) != Started;
}

/// <summary>The call lost its authorization before the side effect started (handled by the executor as a denial).</summary>
internal sealed class SideEffectRefusedException(ToolResult result) : Exception(result.Error?.Message)
{
    public ToolResult Result { get; } = result;
}
