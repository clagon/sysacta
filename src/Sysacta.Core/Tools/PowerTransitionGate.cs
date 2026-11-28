namespace Sysacta.Core.Tools;

/// <summary>A PC-wide power transition. Set on <see cref="ToolDescriptor.Transition"/> by trusted tool code only.</summary>
public enum PowerTransition
{
    Lock,
    Sleep,
    Shutdown,
    Restart,
}

/// <summary>Phase of the <see cref="PowerTransitionGate"/>.</summary>
public enum PowerGatePhase
{
    /// <summary>No transition: state changes are admitted (shared).</summary>
    Idle,
    /// <summary>A transition was admitted and its call (including an isolated native call) has not ended.</summary>
    Running,
    /// <summary>
    /// The transition's call ended, but the PC may still stop (shutdown/restart accepted or unknown) or has not been seen to
    /// resume (sleep). Only the local user can release it (<see cref="PowerTransitionGate.ReleaseHeld"/>), or a resume.
    /// </summary>
    Held,
}

public sealed record PowerGateState(PowerGatePhase Phase, PowerTransition? Transition, int RunningChanges);

public enum PowerGateRelease
{
    Released,
    NotHeld,
    /// <summary>The transition's call has not ended (for example a native call isolated after a timeout).</summary>
    StillRunning,
}

/// <summary>
/// One per process, shared by the executor (admission), the local console (status, release) and the OS power
/// notifications (resume). Every state-changing call holds a shared admission from before its target resolution until
/// the executor releases its resources (after an isolated native call really ends). A power transition
/// (<see cref="ToolDescriptor.Transition"/>) is admitted only when nothing else is admitted, and while it runs or is held
/// every new state change is refused with BUSY. Read-only calls are never gated. Both checks happen under one lock, so a
/// change and a transition can never both be admitted.
/// </summary>
/// <remarks>
/// Release rules when the transition's call ends (<see cref="ExitTransition"/>):
/// not started, or failed with a definite "not executed" error → Idle; lock → Idle (the OS request is complete; the
/// lock itself is not a stop); sleep → Idle only if a suspend followed by a resume was observed after the start point,
/// otherwise Held until such a resume or a local release; shutdown/restart → Held (no OS signal is treated as proof that
/// the stop was cancelled). Elapsed time, HTTP disconnects or a host restart never release the gate.
/// </remarks>
public sealed class PowerTransitionGate
{
    private readonly Lock _lock = new();
    private int _changes;
    private PowerGatePhase _phase;
    private PowerTransition? _transition;
    private bool _started;
    private bool _suspended;
    private bool _resumed;

    /// <summary>Raised (outside the lock, on any thread) after the phase changes.</summary>
    public event EventHandler? Changed;

    public PowerGateState State
    {
        get
        {
            lock (_lock)
            {
                return new(_phase, _transition, _changes);
            }
        }
    }

    internal bool TryEnterChange()
    {
        lock (_lock)
        {
            if (_phase != PowerGatePhase.Idle)
            {
                return false;
            }

            _changes++;
            return true;
        }
    }

    internal void ExitChange()
    {
        lock (_lock)
        {
            _changes--;
        }
    }

    internal bool TryEnterTransition(PowerTransition transition)
    {
        lock (_lock)
        {
            if (_phase != PowerGatePhase.Idle || _changes > 0)
            {
                return false;
            }

            (_phase, _transition, _started, _suspended, _resumed) = (PowerGatePhase.Running, transition, false, false, false);
        }

        Raise();
        return true;
    }

    /// <summary>Called inside <see cref="ToolExecution.BeginSideEffect"/> when the transition's side effect starts.</summary>
    internal void MarkStarted()
    {
        lock (_lock)
        {
            if (_phase == PowerGatePhase.Running)
            {
                // Only power notifications after the start point count as evidence for this transition.
                (_started, _suspended, _resumed) = (true, false, false);
            }
        }
    }

    /// <param name="notExecuted">The call ended with a definite error after which the OS has not executed the request.</param>
    internal void ExitTransition(bool notExecuted)
    {
        lock (_lock)
        {
            var release = !_started || notExecuted || _transition == PowerTransition.Lock || (_transition == PowerTransition.Sleep && _resumed);
            if (release)
            {
                (_phase, _transition) = (PowerGatePhase.Idle, null);
            }
            else
            {
                _phase = PowerGatePhase.Held;
            }
        }

        Raise();
    }

    /// <summary>The OS reported that the system is suspending (PBT_APMSUSPEND).</summary>
    public void NotifySuspend()
    {
        lock (_lock)
        {
            if (_started && _transition == PowerTransition.Sleep)
            {
                _suspended = true;
            }
        }
    }

    /// <summary>The OS reported a resume. Releases a held sleep only after a suspend was seen after its start point.</summary>
    public void NotifyResume()
    {
        lock (_lock)
        {
            if (!(_started && _suspended && _transition == PowerTransition.Sleep))
            {
                return;
            }

            _resumed = true;
            if (_phase != PowerGatePhase.Held)
            {
                return; // Running: released when the call ends (ExitTransition).
            }

            (_phase, _transition) = (PowerGatePhase.Idle, null);
        }

        Raise();
    }

    /// <summary>
    /// Explicit release by the local user (through the local-only tool, after a confirmation). Never releases a transition
    /// whose call has not ended.
    /// </summary>
    internal PowerGateRelease ReleaseHeld()
    {
        lock (_lock)
        {
            switch (_phase)
            {
                case PowerGatePhase.Running:
                    return PowerGateRelease.StillRunning;
                case PowerGatePhase.Idle:
                    return PowerGateRelease.NotHeld;
            }

            (_phase, _transition) = (PowerGatePhase.Idle, null);
        }

        Raise();
        return PowerGateRelease.Released;
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A status observer (UI) must never break the executor's release path.
        }
    }
}
