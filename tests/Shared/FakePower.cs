using System.Collections.Concurrent;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Power;

namespace Sysacta.Tests;

/// <summary>
/// In-memory power adapter, shared by Core.Tests and Mcp.Tests. Never calls Windows: no lock, sleep, shutdown, restart or
/// plan change happens on the test machine. <see cref="Log"/> records the order of preparation, start point and requests.
/// </summary>
public sealed class FakePower : IPower
{
    public static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid Saver = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");

    public PowerStatusReading? Status { get; set; } = new(1, 128, 255, 0, uint.MaxValue, uint.MaxValue);
    public PowerCapabilitiesReading? Capabilities { get; set; } = new(false, false, true, true, true, false);
    public Guid? Active { get; set; } = Balanced;
    public List<PowerPlanEntry> Plans { get; } = [new(Balanced, "Balanced"), new(Saver, "Power saver")];
    public bool ListFails { get; set; }
    public bool HoldsPrivilege { get; set; } = true;

    public PowerRequestResult PlanResult { get; set; } = PowerRequestResult.Accepted;
    /// <summary>Plan the OS ends up with after a write (default: the requested one when accepted).</summary>
    public Func<Guid, Guid?>? AfterWrite { get; set; }
    public bool ReadBackFails { get; set; }

    public PowerRequestResult RequestResult { get; set; } = PowerRequestResult.Accepted;
    /// <summary>Thrown during preparation, before the start point (for example the privilege is not held).</summary>
    public ToolFailureException? PrepareFailure { get; set; }
    /// <summary>When set, the "native" request blocks (ignoring cancellation) until released.</summary>
    public SemaphoreSlim? Block { get; set; }
    /// <summary>Runs after preparation, immediately before the start point (for example to revoke the client).</summary>
    public Action? BeforeStart { get; set; }
    /// <summary>Runs inside the request, after the start point (for example to inspect the audit log).</summary>
    public Action? DuringRequest { get; set; }

    public ConcurrentQueue<string> Log { get; } = new();
    private int _writes;
    private int _requests;
    private int _privilegeScopes;
    public int PlanWrites => Volatile.Read(ref _writes);
    public int Requests => Volatile.Read(ref _requests);
    /// <summary>Privilege scopes opened and not yet closed (must be 0 after every call).</summary>
    public int OpenPrivilegeScopes => Volatile.Read(ref _privilegeScopes);

    public PowerStatusReading ReadStatus() => Status ?? throw new ToolFailureException(ErrorCodes.OsApiError, "status");

    public PowerCapabilitiesReading ReadCapabilities() => Capabilities ?? throw new ToolFailureException(ErrorCodes.OsApiError, "caps");

    public Guid ReadActivePlan()
    {
        if (PlanWrites > 0 && ReadBackFails)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "read");
        }

        return Active ?? throw new ToolFailureException(ErrorCodes.OsApiError, "active");
    }

    public PowerPlanEnumeration ListPlans(int max)
    {
        if (ListFails)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "list");
        }

        lock (Plans)
        {
            return new([.. Plans.Take(max)], Plans.Count > max);
        }
    }

    public PowerRequestResult SetActivePlan(Guid planId)
    {
        Interlocked.Increment(ref _writes);
        Log.Enqueue("set_plan");
        Active = AfterWrite is { } after ? after(planId) : PlanResult == PowerRequestResult.Accepted ? planId : Active;
        return PlanResult;
    }

    public bool HoldsShutdownPrivilege() => HoldsPrivilege;

    public PowerRequestResult Request(PowerTransition transition, Action beginSideEffect)
    {
        var privileged = transition != PowerTransition.Lock;
        if (privileged)
        {
            Interlocked.Increment(ref _privilegeScopes);
        }

        try
        {
            Log.Enqueue("prepare");
            if (PrepareFailure is { } failure)
            {
                throw failure;
            }

            BeforeStart?.Invoke();
            beginSideEffect();
            Log.Enqueue("request:" + transition);
            Interlocked.Increment(ref _requests);
            DuringRequest?.Invoke();
            Block?.Wait(CancellationToken.None);
            return RequestResult;
        }
        finally
        {
            if (privileged)
            {
                Interlocked.Decrement(ref _privilegeScopes);
                Log.Enqueue("revert");
            }
        }
    }
}
