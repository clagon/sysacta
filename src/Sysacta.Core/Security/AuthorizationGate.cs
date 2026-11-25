namespace Sysacta.Core.Security;

/// <summary>
/// Orders permission changes against the start of state-changing operations (SEC-01).
/// <list type="bullet">
/// <item>Every write that can reduce what a client may do (revoke, re-issue, grant removal, expiry, tool disabling) runs
/// inside <see cref="BeginUpdate"/>.</item>
/// <item>A state-changing tool calls <see cref="Tools.ToolExecution.BeginSideEffect"/> right before its first OS write;
/// that re-evaluates authorization inside the shared side of this gate and marks the operation as started.</item>
/// </list>
/// So a change that completed before the start point is always seen by it, and the evaluation reads a consistent state.
/// The gate is held only for that evaluation, never during the native call, so a slow OS operation cannot hold up
/// permission changes. A change completing after the start point applies to the next call (spec §6: side effects
/// already started cannot be undone).
/// </summary>
public sealed class AuthorizationGate
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    /// <summary>Exclusive scope for a permission change. Dispose on the same thread, after the change is committed.</summary>
    public IDisposable BeginUpdate()
    {
        _lock.EnterWriteLock();
        return new Scope(_lock);
    }

    /// <summary>Runs <paramref name="evaluate"/> while no permission change can complete.</summary>
    internal T Evaluate<T>(Func<T> evaluate)
    {
        _lock.EnterReadLock();
        try
        {
            return evaluate();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private sealed class Scope(ReaderWriterLockSlim owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ExitWriteLock();
            }
        }
    }
}
