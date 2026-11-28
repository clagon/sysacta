using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Network;

public sealed class NetworkSetAdapterEnabledArgs
{
    [Description("Network adapter id (net-...) or alias. Required; 'all', 'default' and lists are not accepted.")]
    [StringLength(64, MinimumLength = 1)]
    [NoControlCharacters]
    public required string Adapter { get; init; }

    [Description("true to enable the adapter, false to disable it (absolute value).")]
    public required bool Enabled { get; init; }
}

public sealed record NetworkSetAdapterEnabledData(
    string AdapterId,
    bool RequestedEnabled,
    [property: Description("Administrative state read from the provider (MSFT_NetAdapter) before the call.")] NetworkAdminState PreviousAdminState,
    [property: Description("Administrative state read back after the call. Not the link state: an enabled adapter may still be down, and connectivity is not implied.")] NetworkAdminState CurrentAdminState,
    bool Changed,
    [property: Description("Why the change was refused before anything was done; null otherwise.")] NetworkChangeBlock? BlockedReason);

/// <summary>
/// network_set_adapter_enabled (spec §7.7, dangerous). Administrator only, physical wired / Wi-Fi adapters only, and a
/// disable only for an adapter that cannot be carrying traffic (<see cref="NetworkGuard"/>). Every change shares the
/// <see cref="NetworkAdapters.TopologyKey"/> lock, so the checks of one change are never invalidated by another running
/// change of Sysacta. External changes (cable plugged in, other software) cannot be excluded.
/// </summary>
public sealed class NetworkSetAdapterEnabledTool(INetworkPlatform platform, IDeviceAliases aliases, NetworkChanges changes) : SysactaTool<NetworkSetAdapterEnabledArgs, NetworkSetAdapterEnabledData>
{
    private const int ReadBackAttempts = 10;
    private static readonly TimeSpan ReadBackDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>No new read-back starts after this, so the call ends near its 15 s timeout even with slow WMI reads.</summary>
    private static readonly TimeSpan ReadBackBudget = TimeSpan.FromSeconds(6);

    public override ToolDescriptor Descriptor { get; } = new("network_set_adapter_enabled", ToolCategories.Network, "Enable or disable a network adapter",
        "DANGEROUS: changes the administrative state of one physical wired or Wi-Fi adapter through MSFT_NetAdapter Enable/Disable and verifies it by reading the state back. Requires Sysacta to be started as administrator. Disabling is allowed only for an adapter that is not in use: link down and media disconnected, no IP address, no route and no stacked interface; active adapters, the adapters of the current connection, loopback, VPN/tunnel (e.g. Tailscale), Hyper-V/WSL and other virtual adapters are refused with blocked_reason. Enabling may change routes and VPN behaviour; connectivity is never guaranteed. Recover from Windows Settings > Network & internet on the PC itself.",
        DefaultPolicy.Off, false, ["enabled"], TimeSpan.FromSeconds(15));

    public override ToolAvailability GetAvailability() => platform.IsElevated
        ? ToolAvailability.Available
        : ToolAvailability.Unavailable(ErrorCodes.AdminRequired, "Changing a network adapter requires Sysacta to be started as administrator by the local user.");

    protected override ValueTask<ToolTarget> ResolveTargetAsync(NetworkSetAdapterEnabledArgs args, CancellationToken cancellationToken)
    {
        var (id, byAlias) = NetworkAdapters.ResolveId(aliases, args.Adapter);
        return ValueTask.FromResult<ToolTarget>(new AdapterTarget(id, byAlias));
    }

    protected override Task<ToolOutcome<NetworkSetAdapterEnabledData>> ExecuteAsync(NetworkSetAdapterEnabledArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var target = (AdapterTarget)execution.Target;
        var guid = NetworkAdapters.ParseId(target.Id) ?? throw new ToolFailureException(ErrorCodes.TargetNotFound, "The specified network adapter was not found.");
        if (!platform.IsElevated)
        {
            throw new ToolFailureException(ErrorCodes.AdminRequired, "Changing a network adapter requires Sysacta to be started as administrator by the local user.");
        }

        // Every check below runs under the lock, after waiting, immediately before the start point.
        var snapshot = platform.ReadSnapshot();
        var set = NetworkAdapters.Identify(snapshot.Interfaces);
        var adapter = set.Find(target.Id);
        using var handle = platform.OpenProviderAdapter(guid, cancellationToken) ?? throw (target.ByAlias
            ? new ToolFailureException(ErrorCodes.DeviceDisconnected, "The network adapter registered under this alias is not present.")
            : new ToolFailureException(ErrorCodes.TargetNotFound, "The specified network adapter was not found. Use network_list_adapters for ids."));
        var previous = NetworkGuard.AdminStateOf(handle.Adapter);
        if (previous == NetworkAdminState.Unknown)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The adapter's administrative state could not be determined. Nothing was changed.");
        }

        NetworkSetAdapterEnabledData Data(NetworkAdminState current, bool changed, NetworkChangeBlock? blocked = null) =>
            new(target.Id, args.Enabled, previous, current, changed, blocked);

        if ((previous == NetworkAdminState.Enabled) == args.Enabled)
        {
            return Task.FromResult(new ToolOutcome<NetworkSetAdapterEnabledData>(Data(previous, false), Verification.Verified));
        }

        // Two interface rows with this GUID: never treat the adapter as "not listed".
        var block = snapshot.Interfaces.Rows.Count(r => !r.FilterInterface && r.InterfaceGuid == guid) > 1 ? NetworkChangeBlock.IdentityMismatch
            : !args.Enabled && adapter is not null ? NetworkGuard.Check(adapter, false, set, snapshot) ?? NetworkGuard.CheckProvider(handle.Adapter, adapter, false, snapshot)
            : NetworkGuard.CheckProvider(handle.Adapter, adapter, args.Enabled, snapshot);
        if (block is { } reason)
        {
            return Task.FromResult(ToolOutcome.Error(Data(previous, false, reason),
                reason == NetworkChangeBlock.Unsupported ? ErrorCodes.NotSupported : ErrorCodes.TargetProtected,
                RefusalMessage(reason)));
        }

        // The provider enumeration can take seconds: read the interface table again and judge the same object once more
        // immediately before the start point (a link that came up meanwhile refuses the change).
        if (!args.Enabled && adapter is not null)
        {
            var fresh = snapshot with { Interfaces = platform.ReadInterfaces() };
            var freshSet = NetworkAdapters.Identify(fresh.Interfaces);
            var again = freshSet.Find(target.Id) is { } now && now.Row.Luid == adapter.Row.Luid && now.Row.Index == adapter.Row.Index
                ? NetworkGuard.Check(now, false, freshSet, fresh)
                : NetworkChangeBlock.IdentityMismatch;
            if (again is { } late)
            {
                return Task.FromResult(ToolOutcome.Error(Data(previous, false, late),
                    late == NetworkChangeBlock.Unsupported ? ErrorCodes.NotSupported : ErrorCodes.TargetProtected,
                    RefusalMessage(late)));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        execution.BeginSideEffect();
        using (changes.Begin())
        {
            uint result;
            try
            {
                result = handle.Invoke(args.Enabled);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                throw new ToolOutcomeUnknownException(ErrorCodes.OsApiError, "The adapter state change failed; whether it was applied is unknown. Check the adapter in Windows Settings.");
            }

            var current = ReadBack(guid, args.Enabled, cancellationToken);
            var wanted = args.Enabled ? NetworkAdminState.Enabled : NetworkAdminState.Disabled;
            if (current == wanted)
            {
                return Task.FromResult(new ToolOutcome<NetworkSetAdapterEnabledData>(Data(current, true), Verification.Verified));
            }

            // 4096 is the CIM "job started" value: still running, not a refusal. Other non-zero values with the state read
            // back unchanged are refusals (the method's return codes are not documented beyond the class page).
            if (current == previous && result is not (0 or 4096))
            {
                return Task.FromResult(ToolOutcome.Error(Data(current, false), ErrorCodes.OsApiError, "Windows refused the change; the administrative state is unchanged."));
            }

            return Task.FromResult(ToolOutcome.Unknown(Data(current, false), ErrorCodes.VerificationFailed,
                "The administrative state did not reach the requested value in time. Do not retry automatically; read the adapters again."));
        }
    }

    /// <summary>Reads the provider state until it matches or the bounded attempts end. Unknown when it cannot be read.</summary>
    private NetworkAdminState ReadBack(Guid guid, bool enabled, CancellationToken cancellationToken)
    {
        var wanted = enabled ? NetworkAdminState.Enabled : NetworkAdminState.Disabled;
        var state = NetworkAdminState.Unknown;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var attempt = 0; attempt < ReadBackAttempts; attempt++)
        {
            if (attempt > 0 && (System.Diagnostics.Stopwatch.GetElapsedTime(started) > ReadBackBudget || cancellationToken.WaitHandle.WaitOne(ReadBackDelay)))
            {
                break;
            }

            try
            {
                state = platform.ReadProviderAdapters(guid, cancellationToken) is [var p] ? NetworkGuard.AdminStateOf(p) : NetworkAdminState.Unknown;
            }
            catch (ToolFailureException)
            {
                state = NetworkAdminState.Unknown;
            }

            if (state == wanted)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return state;
    }

    internal static string RefusalMessage(NetworkChangeBlock reason) => reason switch
    {
        NetworkChangeBlock.NotElevated => "Refused (not_elevated): Sysacta is not running as administrator. Nothing was changed.",
        NetworkChangeBlock.Unsupported => "Refused (unsupported): only physical wired or Wi-Fi adapters are changed. Nothing was changed.",
        NetworkChangeBlock.ActiveAdapter => "Refused (active_adapter): the adapter is up, connected, or its link state is not known to be down. Nothing was changed.",
        NetworkChangeBlock.AddressAssigned => "Refused (address_assigned): the adapter has an IP address. Nothing was changed.",
        NetworkChangeBlock.RouteInUse => "Refused (route_in_use): a route uses the adapter. Nothing was changed.",
        NetworkChangeBlock.DependencyPresent => "Refused (dependency_present): another interface is stacked with the adapter. Nothing was changed.",
        NetworkChangeBlock.InformationIncomplete => "Refused (information_incomplete): the adapter's use could not be ruled out from complete OS information. Nothing was changed.",
        _ => "Refused (identity_mismatch): the adapter could not be identified consistently. Nothing was changed.",
    };

    private sealed record AdapterTarget(string Id, bool ByAlias) : ToolTarget([NetworkAdapters.TopologyKey, NetworkAdapters.DeviceKey(Id)], Id);
}
