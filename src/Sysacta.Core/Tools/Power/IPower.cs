namespace Sysacta.Core.Tools.Power;

/// <summary>Raw SYSTEM_POWER_STATUS fields (GetSystemPowerStatus). Interpreted by <see cref="PowerStatus"/>.</summary>
public sealed record PowerStatusReading(byte AcLineStatus, byte BatteryFlag, byte BatteryLifePercent, byte SystemStatusFlag, uint BatteryLifeTime, uint BatteryFullLifeTime);

/// <summary>The SYSTEM_POWER_CAPABILITIES members used here (GetPwrCapabilities).</summary>
public sealed record PowerCapabilitiesReading(bool S1, bool S2, bool S3, bool S4, bool HiberFilePresent, bool AoAc);

/// <summary>A power plan as enumerated by the OS. <paramref name="Name"/> is raw OS text (null when it could not be read).</summary>
public sealed record PowerPlanEntry(Guid PlanId, string? Name);

/// <param name="Truncated">More plans exist than the requested maximum.</param>
public sealed record PowerPlanEnumeration(IReadOnlyList<PowerPlanEntry> Plans, bool Truncated);

/// <summary>Outcome of one OS request. Everything except <see cref="Accepted"/> means the OS did not start the request.</summary>
public enum PowerRequestResult
{
    /// <summary>The OS accepted the request. It may still not complete (asynchronous, cancellable by apps or the user).</summary>
    Accepted,
    /// <summary>Access denied, privilege not held, or refused by policy.</summary>
    Refused,
    /// <summary>A shutdown is already in progress or scheduled.</summary>
    Busy,
    /// <summary>Any other failure reported by the OS.</summary>
    Failed,
}

/// <summary>
/// OS adapter for power state, plans and transitions. Methods are synchronous and run on a thread-pool thread.
/// Read failures throw <see cref="ToolFailureException"/>; writes return a result, because once the start point has
/// passed the outcome must be reported, not thrown.
/// </summary>
public interface IPower
{
    PowerStatusReading ReadStatus();

    PowerCapabilitiesReading ReadCapabilities();

    Guid ReadActivePlan();

    /// <param name="names">Read display names (null otherwise); not needed to confirm a plan id.</param>
    PowerPlanEnumeration ListPlans(int max, bool names);

    /// <summary>PowerSetActiveScheme, once. Call only after BeginSideEffect. Never throws.</summary>
    PowerRequestResult SetActivePlan(Guid planId);

    /// <summary>Whether the process token holds SeShutdownPrivilege at all (enabled or not). Cheap, no side effect.</summary>
    bool HoldsShutdownPrivilege();

    /// <summary>
    /// Requests one transition, exactly once, with the OS's normal (non-forced) request. Any preparation (for sleep,
    /// shutdown and restart: SeShutdownPrivilege enabled on a duplicated token for the calling thread only) is completed
    /// first; then <paramref name="beginSideEffect"/> is called, and only if it returns is the OS request made. The
    /// privilege scope is reverted and released before returning on every path.
    /// Throws <see cref="ToolFailureException"/> only before <paramref name="beginSideEffect"/> (nothing requested).
    /// After it, never throws and returns the OS's answer: <see cref="PowerRequestResult.Accepted"/> is not completion.
    /// </summary>
    PowerRequestResult Request(PowerTransition transition, Action beginSideEffect);
}
