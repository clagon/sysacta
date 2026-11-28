using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using Sysacta.Core.Devices;
using Sysacta.Core.Tools.SystemMonitoring;

namespace Sysacta.Core.Tools.Network;

public sealed class NetworkNoArgs;

public sealed class NetworkGetTrafficArgs
{
    [Description("Network adapter id (net-...) or alias. Omit to measure every adapter separately.")]
    [StringLength(64, MinimumLength = 1)]
    [NoControlCharacters]
    public string? Adapter { get; init; }
}

public sealed record NetworkStatusData(
    DateTimeOffset MeasuredAt,
    [property: Description("Identified adapters.")] int AdapterCount,
    [property: Description("Adapters whose operational state is up (loopback excluded).")] IReadOnlyList<string> UpAdapterIds,
    [property: Description("Adapters that have an IPv4 default route (0.0.0.0/0), all of them; null when the routes could not be read completely.")] IReadOnlyList<string>? Ipv4DefaultRouteAdapterIds,
    [property: Description("Adapters that have an IPv6 default route (::/0), all of them; null when the routes could not be read completely.")] IReadOnlyList<string>? Ipv6DefaultRouteAdapterIds,
    [property: Description("Default routes on interfaces without an id (not listed above).")] int UnidentifiedDefaultRouteCount,
    UnavailableReason? RoutesUnavailableReason,
    [property: Description("Windows' own connectivity observation (NCSI) for the internet connection profile. Sysacta sends no probe; this is not a reachability test.")] NetworkConnectivityLevel? OsConnectivity,
    UnavailableReason? OsConnectivityUnavailableReason,
    int UnidentifiedCount);

public sealed class NetworkGetStatusTool(INetworkPlatform platform, TimeProvider time) : SysactaTool<NetworkNoArgs, NetworkStatusData>
{
    public override ToolDescriptor Descriptor { get; } = new("network_get_status", ToolCategories.Network, "Get network status",
        "Summarizes the network state reported by Windows: adapters that are up, every adapter holding an IPv4/IPv6 default route (several may), and Windows' own connectivity level (NCSI). Sysacta sends no traffic to any host; the actual path of internet traffic and reachability are not asserted. No routing table, gateways or remote endpoints are returned. Missing values are null with a reason.",
        DefaultPolicy.On, true, []);

    protected override Task<ToolOutcome<NetworkStatusData>> ExecuteAsync(NetworkNoArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var snapshot = platform.ReadSnapshot();
        if (snapshot.Interfaces.Problem is { } problem and not UnavailableReason.Truncated)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The network interfaces could not be read.", retryable: problem != UnavailableReason.PermissionDenied);
        }

        var set = NetworkAdapters.Identify(snapshot.Interfaces);
        var byLuid = set.Adapters.GroupBy(a => a.Row.Luid).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
        var defaults = snapshot.Routes.Rows.Where(r => r.DestinationPrefixLength == 0).ToList();
        IReadOnlyList<string>? Ids(AddressFamily family) => snapshot.Routes.IsComplete
            ? [.. defaults.Where(r => r.Family == family && byLuid.ContainsKey(r.Luid)).Select(r => byLuid[r.Luid].Id).Distinct().Order(StringComparer.Ordinal)]
            : null;

        NetworkConnectivityLevel? connectivity = null;
        try
        {
            connectivity = platform.ReadConnectivity();
        }
        catch (ToolFailureException)
        {
        }

        var data = new NetworkStatusData(
            time.GetUtcNow(),
            set.Adapters.Count,
            [.. set.Adapters.Where(a => a.OperState == NetworkOperState.Up && a.Kind != NetworkAdapterKind.Loopback).Select(a => a.Id)],
            Ids(AddressFamily.InterNetwork),
            Ids(AddressFamily.InterNetworkV6),
            defaults.Count(r => !byLuid.ContainsKey(r.Luid)),
            snapshot.Routes.Problem,
            connectivity,
            connectivity is null ? UnavailableReason.Unavailable : null,
            set.UnidentifiedCount);
        return Task.FromResult(new ToolOutcome<NetworkStatusData>(data, Verification.NotApplicable));
    }
}

public sealed class NetworkListAdaptersTool(INetworkPlatform platform, IDeviceAliases aliases, TimeProvider time) : SysactaTool<NetworkNoArgs, NetworkAdapterListData>
{
    public override ToolDescriptor Descriptor { get; } = new("network_list_adapters", ToolCategories.Network, "List network adapters",
        "Lists network adapters (physical and virtual, including loopback, tunnels/VPN and Hyper-V/WSL) in a stable order by id: canonical id, name, aliases, kind (from official interface type/flags), administrative state, operational state, media state, reported link speeds (bit/s, not traffic), usable IPv4/IPv6 unicast addresses with prefix length (at most 16 per family), and why changing the administrative state would be refused. Addresses are OS observations, not proof of reachability. No MAC addresses, SSIDs, DNS suffixes, hardware paths or history. At most 128 adapters.",
        DefaultPolicy.On, true, []);

    protected override Task<ToolOutcome<NetworkAdapterListData>> ExecuteAsync(NetworkNoArgs args, ToolExecution execution, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolOutcome<NetworkAdapterListData>(NetworkAdapters.List(platform, aliases, time.GetUtcNow()), Verification.NotApplicable));
}

public sealed class NetworkGetTrafficTool(INetworkPlatform platform, IDeviceAliases aliases, NetworkChanges changes, TimeProvider time) : SysactaTool<NetworkGetTrafficArgs, NetworkTrafficData>
{
    public override ToolDescriptor Descriptor { get; } = new("network_get_traffic", ToolCategories.Network, "Get network traffic",
        "Measures traffic over a fixed ~1 second window from the OS 64-bit cumulative byte counters, per adapter (one adapter when 'adapter' is given). Directions are from this PC. Returns cumulative totals (bytes), the window's deltas (bytes) and rates in bits per second (floor). A rate is null with a reason when the adapter was added/removed, its runtime identity or state changed, a counter decreased (no wrap correction), or the window was unusable; 0 is a real measurement. Values are never summed across adapters (loopback, VPN and virtual adapters repeat the same traffic). Link speed is not traffic.",
        DefaultPolicy.On, true, []);

    protected override ValueTask<ToolTarget> ResolveTargetAsync(NetworkGetTrafficArgs args, CancellationToken cancellationToken)
    {
        if (args.Adapter is null)
        {
            return ValueTask.FromResult(ToolTarget.None);
        }

        var (id, byAlias) = NetworkAdapters.ResolveId(aliases, args.Adapter);
        return ValueTask.FromResult<ToolTarget>(new TrafficTarget(id, byAlias));
    }

    protected override async Task<ToolOutcome<NetworkTrafficData>> ExecuteAsync(NetworkGetTrafficArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var target = execution.Target as TrafficTarget;
        var data = await NetworkTraffic.MeasureAsync(platform, changes, time, target?.TargetId, target?.ByAlias == true, cancellationToken).ConfigureAwait(false);
        return new ToolOutcome<NetworkTrafficData>(data, Verification.NotApplicable);
    }

    private sealed record TrafficTarget(string Id, bool ByAlias) : ToolTarget([], Id);
}
