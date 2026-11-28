using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Sysacta.Core.Devices;
using Sysacta.Core.Tools.SystemMonitoring;

namespace Sysacta.Core.Tools.Network;

/// <summary>Adapter class from official flags and types only (never from names or vendors).</summary>
public enum NetworkAdapterKind
{
    /// <summary>Hardware interface, ifType ethernetCsmacd and physical medium 802.3.</summary>
    Wired,
    /// <summary>Hardware interface, ifType ieee80211 and physical medium native 802.11.</summary>
    Wifi,
    Loopback,
    /// <summary>ifType tunnel or ppp, or a tunnel type set (VPN, IPv6 transition tunnels).</summary>
    Tunnel,
    /// <summary>Not a hardware interface: Hyper-V/WSL vEthernet, VPN drivers such as Wintun (Tailscale), WAN miniports, ...</summary>
    Virtual,
    /// <summary>A hardware interface of another type or medium (Bluetooth PAN, mobile broadband, ...).</summary>
    Unknown,
}

public enum NetworkAdminState
{
    Enabled,
    Disabled,
    Unknown,
}

public enum NetworkOperState
{
    Up,
    Down,
    Testing,
    Unknown,
    Dormant,
    NotPresent,
    LowerLayerDown,
}

public enum NetworkMediaState
{
    Connected,
    Disconnected,
    Unknown,
}

/// <summary>Why the administrative state of an adapter may not be changed (fixed reasons; no addresses or routes).</summary>
public enum NetworkChangeBlock
{
    /// <summary>Sysacta is not running as administrator; the provider methods require it.</summary>
    NotElevated,
    /// <summary>Only physical wired / Wi-Fi adapters with a connector are changed (no loopback, tunnel, VPN, virtual, hidden).</summary>
    Unsupported,
    /// <summary>The adapter is operational, connected, or its link/media state is not known to be down.</summary>
    ActiveAdapter,
    /// <summary>The adapter has an IP address (any family, any state).</summary>
    AddressAssigned,
    /// <summary>A route uses the adapter.</summary>
    RouteInUse,
    /// <summary>The adapter is stacked under or over another interface (team, bridge-like or filter stack).</summary>
    DependencyPresent,
    /// <summary>An OS table could not be read completely, so the adapter's use cannot be ruled out.</summary>
    InformationIncomplete,
    /// <summary>The provider instance and the IP Helper interface do not describe the same adapter consistently.</summary>
    IdentityMismatch,
}

/// <summary>An interface with a canonical id: unique non-empty InterfaceGuid, not a filter interface.</summary>
public sealed record NetworkAdapter(
    string Id,
    NetInterfaceRow Row,
    NetworkAdapterKind Kind,
    NetworkAdminState AdminState,
    NetworkOperState OperState,
    NetworkMediaState MediaState,
    string? Name);

/// <summary>The identified adapters of one interface table read, ordered by id.</summary>
public sealed record NetworkAdapterSet(IReadOnlyList<NetworkAdapter> Adapters, int UnidentifiedCount, bool Complete)
{
    public NetworkAdapter? Find(string id) => Adapters.FirstOrDefault(a => a.Id == id);
}

public enum NetworkAddressScope
{
    Loopback,
    LinkLocal,
    Unicast,
}

public sealed record NetworkAddress(
    [property: Description("IP address text without scope id.")] string Address,
    int PrefixLength,
    NetworkAddressScope Scope);

public sealed record NetworkAdapterEntry(
    [property: Description("Canonical id: 'net-' + InterfaceGuid (32 lowercase hex). Stable across restarts; a reinstalled or re-created (virtual) adapter can get a new id.")] string Id,
    [property: Description("Interface alias (e.g. 'Ethernet'); null when unavailable or unsafe to show.")] string? Name,
    IReadOnlyList<string> Aliases,
    NetworkAdapterKind Kind,
    [property: Description("Administrative state. 'unknown' when Windows does not report it unambiguously (an adapter whose hardware is not present may also be disabled).")] NetworkAdminState AdminState,
    [property: Description("Operational (link) state, independent of the administrative state. 'down' is not 'disabled'.")] NetworkOperState OperState,
    NetworkMediaState MediaState,
    [property: Description("Reported transmit link speed in bits per second (not traffic); null when not connected or not reported.")] long? TransmitLinkSpeedBps,
    [property: Description("Reported receive link speed in bits per second (not traffic); null when not connected or not reported.")] long? ReceiveLinkSpeedBps,
    [property: Description("Usable (preferred/deprecated) IPv4 unicast addresses, at most 16. Assigned by the OS; not proof of reachability.")] IReadOnlyList<NetworkAddress> Ipv4Addresses,
    bool Ipv4Truncated,
    [property: Description("Usable (preferred/deprecated) IPv6 unicast addresses, at most 16.")] IReadOnlyList<NetworkAddress> Ipv6Addresses,
    bool Ipv6Truncated,
    [property: Description("Why network_set_adapter_enabled would refuse to change this adapter to the opposite of its current administrative state now; null when the change is not ruled out here (it is re-checked at call time).")] NetworkChangeBlock? AdminChangeBlockedReason);

public sealed record NetworkAdapterListData(
    DateTimeOffset MeasuredAt,
    IReadOnlyList<NetworkAdapterEntry> Adapters,
    [property: Description("Number of identified adapters, including ones not returned.")] int Count,
    [property: Description("True when adapters were left out (result limit 128, or the OS table exceeded the internal limit).")] bool Truncated,
    [property: Description("Interfaces without a unique InterfaceGuid; they get no id and are not listed.")] int UnidentifiedCount,
    [property: Description("Null when addresses were read completely; otherwise address lists may be incomplete.")] UnavailableReason? AddressesUnavailableReason);

/// <summary>Rules shared by the network tools and the console's device page.</summary>
public static partial class NetworkAdapters
{
    public const int MaxListed = 128;
    public const int MaxAddressesPerFamily = 16;
    public const string IdPrefix = "net-";

    public static string IdFor(Guid interfaceGuid) => IdPrefix + interfaceGuid.ToString("N");

    /// <summary>The InterfaceGuid of a canonical id (exact lowercase form), or null.</summary>
    public static Guid? ParseId(string id) =>
        IdPattern().IsMatch(id) && Guid.TryParseExact(id[IdPrefix.Length..], "N", out var guid) && guid != Guid.Empty ? guid : null;

    [GeneratedRegex("^net-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static string DeviceKey(string id) => $"network:adapter:{id}";

    /// <summary>Shared by every administrative change, so two changes never run (and are judged) concurrently.</summary>
    public const string TopologyKey = "network:topology";

    /// <summary>
    /// The canonical id named by <paramref name="adapter"/> (an id or an alias of the network category), fixed at resolution.
    /// Never falls back to another adapter; existence is checked later against the OS.
    /// </summary>
    public static (string Id, bool ByAlias) ResolveId(IDeviceAliases aliases, string adapter)
    {
        if (adapter.Length == 0 || adapter.Trim().Length != adapter.Length)
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "'adapter' must not be empty or have leading/trailing whitespace.");
        }

        if (DeviceAliasRules.Reserved.Contains(adapter))
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "Reserved names such as 'all' and 'default' are not accepted; specify one adapter id or alias.");
        }

        if (aliases.Resolve(ToolCategories.Network, adapter) is { } aliased)
        {
            return ParseId(aliased) is null
                ? throw new ToolFailureException(ErrorCodes.TargetNotFound, "The alias does not refer to a valid network adapter id.")
                : (aliased, true);
        }

        return ParseId(adapter) is null
            ? throw new ToolFailureException(ErrorCodes.TargetNotFound, "The specified network adapter was not found. Use network_list_adapters for ids.")
            : (adapter, false);
    }

    public static NetworkAdapterSet Identify(NetTable<NetInterfaceRow> table)
    {
        var rows = table.Rows.Where(r => !r.FilterInterface).ToList();
        var counts = rows.GroupBy(r => r.InterfaceGuid).ToDictionary(g => g.Key, g => g.Count());
        var adapters = rows
            .Where(r => r.InterfaceGuid != Guid.Empty && counts[r.InterfaceGuid] == 1)
            .Select(Describe)
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .ToList();
        return new NetworkAdapterSet(adapters, rows.Count - adapters.Count, table.IsComplete);
    }

    private static NetworkAdapter Describe(NetInterfaceRow r) => new(
        IdFor(r.InterfaceGuid),
        r,
        KindOf(r),
        r.AdminStatus switch
        {
            // An adapter disabled in Windows may be reported with an "up" admin status and a not-present oper status.
            1 when r.OperStatus != 6 => NetworkAdminState.Enabled,
            2 => NetworkAdminState.Disabled,
            _ => NetworkAdminState.Unknown,
        },
        r.OperStatus switch
        {
            1 => NetworkOperState.Up,
            2 => NetworkOperState.Down,
            3 => NetworkOperState.Testing,
            5 => NetworkOperState.Dormant,
            6 => NetworkOperState.NotPresent,
            7 => NetworkOperState.LowerLayerDown,
            _ => NetworkOperState.Unknown,
        },
        r.MediaConnectState switch
        {
            1 => NetworkMediaState.Connected,
            2 => NetworkMediaState.Disconnected,
            _ => NetworkMediaState.Unknown,
        },
        UntrustedText.Sanitize(r.Alias, 64) ?? UntrustedText.Sanitize(r.Description, 64));

    internal static NetworkAdapterKind KindOf(NetInterfaceRow r) => r switch
    {
        { IfType: 24 } => NetworkAdapterKind.Loopback,
        { IfType: 131 or 23 } or { TunnelType: not 0 } => NetworkAdapterKind.Tunnel,
        { HardwareInterface: false } => NetworkAdapterKind.Virtual,
        { IfType: 6, PhysicalMediumType: 14 } => NetworkAdapterKind.Wired,
        { IfType: 71, PhysicalMediumType: 9 } => NetworkAdapterKind.Wifi,
        _ => NetworkAdapterKind.Unknown,
    };

    /// <summary>A reported link speed in bit/s, or null when not connected, not reported (0 / all ones) or too large.</summary>
    internal static long? LinkSpeed(NetworkAdapter a, ulong value) =>
        a.MediaState == NetworkMediaState.Connected && value is > 0 and <= long.MaxValue ? (long)value : null;

    public static NetworkAdapterListData List(INetworkPlatform platform, IDeviceAliases aliases, DateTimeOffset now)
    {
        var snapshot = platform.ReadSnapshot();
        if (snapshot.Interfaces.Problem is { } problem and not UnavailableReason.Truncated)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The network interfaces could not be read.", retryable: problem != UnavailableReason.PermissionDenied);
        }

        var set = Identify(snapshot.Interfaces);
        var byDevice = aliases.List(ToolCategories.Network).ToLookup(a => a.DeviceId, a => a.Alias, StringComparer.Ordinal);
        var elevated = platform.IsElevated;
        var entries = set.Adapters.Take(MaxListed).Select(a =>
        {
            var usable = snapshot.Addresses.Rows.Where(x => x.Luid == a.Row.Luid && x.DadState is 3 or 4).ToList();
            var v4 = usable.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
            var v6 = usable.Where(x => x.Address.AddressFamily == AddressFamily.InterNetworkV6).ToList();
            return new NetworkAdapterEntry(
                a.Id,
                a.Name,
                [.. byDevice[a.Id]],
                a.Kind,
                a.AdminState,
                a.OperState,
                a.MediaState,
                LinkSpeed(a, a.Row.TransmitLinkSpeed),
                LinkSpeed(a, a.Row.ReceiveLinkSpeed),
                [.. v4.Take(MaxAddressesPerFamily).Select(Address)],
                v4.Count > MaxAddressesPerFamily,
                [.. v6.Take(MaxAddressesPerFamily).Select(Address)],
                v6.Count > MaxAddressesPerFamily,
                !elevated ? NetworkChangeBlock.NotElevated
                    // Not present (unplugged, or possibly disabled): only the provider can tell, at call time.
                    : a.AdminState == NetworkAdminState.Unknown && a.OperState == NetworkOperState.NotPresent ? NetworkChangeBlock.InformationIncomplete
                    : NetworkGuard.Check(a, a.AdminState != NetworkAdminState.Enabled, set, snapshot));
        }).ToList();
        return new NetworkAdapterListData(now, entries, set.Adapters.Count, !set.Complete || set.Adapters.Count > entries.Count, set.UnidentifiedCount, snapshot.Addresses.Problem);
    }

    private static NetworkAddress Address(NetUnicastAddress x)
    {
        var ip = x.Address;
        var scope = IPAddress.IsLoopback(ip) ? NetworkAddressScope.Loopback
            : ip.IsIPv6LinkLocal || (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes() is [169, 254, ..]) ? NetworkAddressScope.LinkLocal
            : NetworkAddressScope.Unicast;
        var text = ip.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(ip.GetAddressBytes()).ToString() : ip.ToString();
        return new NetworkAddress(text, x.PrefixLength, scope);
    }
}
