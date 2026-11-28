using System.Net;
using System.Net.Sockets;
using Sysacta.Core.Tools.SystemMonitoring;

namespace Sysacta.Core.Tools.Network;

/// <summary>
/// One IP Helper table read (GetIfTable2, GetUnicastIpAddressTable, GetIpForwardTable2, GetIfStackTable). When
/// <paramref name="Problem"/> is <see cref="UnavailableReason.Truncated"/>, <paramref name="Rows"/> holds the rows up to the
/// internal limit; for any other problem <paramref name="Rows"/> is empty. A table is complete only when Problem is null.
/// </summary>
public sealed record NetTable<T>(IReadOnlyList<T> Rows, UnavailableReason? Problem)
{
    public bool IsComplete => Problem is null;
}

public static class NetTable
{
    public static NetTable<T> Ok<T>(IReadOnlyList<T> rows) => new(rows, null);

    public static NetTable<T> Failed<T>(UnavailableReason reason) => new([], reason);
}

/// <summary>
/// One MIB_IF_ROW2 as returned by the OS (raw values; Core interprets them). Alias and description are raw OS text.
/// </summary>
/// <param name="InterfaceGuid">InterfaceGuid: the stable identity the canonical id is derived from.</param>
/// <param name="Luid">NET_LUID (runtime identity; may change after reinstalling the adapter).</param>
/// <param name="Index">InterfaceIndex (runtime; may be reused by another interface after removal).</param>
/// <param name="IfType">IANA ifType (6 ethernetCsmacd, 24 softwareLoopback, 71 ieee80211, 131 tunnel, ...).</param>
/// <param name="TunnelType">TUNNEL_TYPE (0 none).</param>
/// <param name="PhysicalMediumType">NDIS_PHYSICAL_MEDIUM (14 802.3, 9 native 802.11, 10 Bluetooth, ...).</param>
/// <param name="AdminStatus">NET_IF_ADMIN_STATUS (1 up, 2 down, 3 testing).</param>
/// <param name="OperStatus">IF_OPER_STATUS (1 up ... 7 lower layer down).</param>
/// <param name="MediaConnectState">NET_IF_MEDIA_CONNECT_STATE (0 unknown, 1 connected, 2 disconnected).</param>
/// <param name="TransmitLinkSpeed">Bits per second as reported (not traffic).</param>
/// <param name="InOctets">64-bit cumulative received bytes.</param>
/// <param name="OutOctets">64-bit cumulative sent bytes.</param>
public sealed record NetInterfaceRow(
    Guid InterfaceGuid,
    ulong Luid,
    uint Index,
    uint IfType,
    uint TunnelType,
    uint PhysicalMediumType,
    string? Alias,
    string? Description,
    uint AdminStatus,
    uint OperStatus,
    uint MediaConnectState,
    bool HardwareInterface,
    bool FilterInterface,
    bool ConnectorPresent,
    ulong TransmitLinkSpeed,
    ulong ReceiveLinkSpeed,
    ulong InOctets,
    ulong OutOctets);

/// <summary>One MIB_UNICASTIPADDRESS_ROW (any DAD state). IPv6 addresses carry their scope id.</summary>
/// <param name="DadState">NL_DAD_STATE (3 deprecated, 4 preferred are usable; others are not).</param>
public sealed record NetUnicastAddress(ulong Luid, IPAddress Address, byte PrefixLength, uint DadState);

/// <summary>One MIB_IPFORWARD_ROW2, reduced to what the guard and the default-route summary need (no next hop).</summary>
public sealed record NetRoute(ulong Luid, AddressFamily Family, byte DestinationPrefixLength);

/// <summary>One MIB_IFSTACK_ROW (interface indexes; 0 means "nothing above/below").</summary>
public sealed record NetStackLink(uint HigherIndex, uint LowerIndex);

/// <summary>The four IP Helper tables, read one after another (not an atomic snapshot of the OS).</summary>
public sealed record NetworkSnapshot(
    NetTable<NetInterfaceRow> Interfaces,
    NetTable<NetUnicastAddress> Addresses,
    NetTable<NetRoute> Routes,
    NetTable<NetStackLink> Stack);

/// <summary>NetworkConnectivityLevel of the internet connection profile (Windows' own NCSI observation).</summary>
public enum NetworkConnectivityLevel
{
    None,
    LocalAccess,
    ConstrainedInternetAccess,
    InternetAccess,
}

/// <summary>
/// One MSFT_NetAdapter instance (root/StandardCimv2), the provider whose Enable/Disable methods change the administrative
/// state. Raw values; Core interprets them.
/// </summary>
/// <param name="State">0 unknown, 1 present, 2 started, 3 disabled.</param>
/// <param name="NdisPhysicalMedium">Same values as <see cref="NetInterfaceRow.PhysicalMediumType"/>.</param>
/// <param name="MediaConnectState">0 unknown, 1 connected, 2 disconnected.</param>
public sealed record ProviderAdapter(
    Guid InterfaceGuid,
    ulong NetLuid,
    uint InterfaceIndex,
    uint State,
    bool HardwareInterface,
    bool Virtual,
    bool Hidden,
    bool ConnectorPresent,
    uint NdisPhysicalMedium,
    uint MediaConnectState);

/// <summary>
/// One enumerated MSFT_NetAdapter instance held for a single method call on exactly that instance (never a query built
/// from input). Dispose releases the WMI object.
/// </summary>
public interface IProviderAdapterHandle : IDisposable
{
    /// <summary>The instance's values at enumeration time.</summary>
    ProviderAdapter Adapter { get; }

    /// <summary>
    /// Calls MSFT_NetAdapter.Enable or .Disable once on this instance and returns the method's ReturnValue (0 success).
    /// Synchronous; may block while the driver changes state; not cancellable. Throws on a WMI failure.
    /// </summary>
    uint Invoke(bool enabled);
}

/// <summary>
/// OS adapter for the network tools. Read-only methods use IP Helper and WinRT only; the provider methods use the fixed
/// CIM class MSFT_NetAdapter. Never sends traffic to any host. Implementations throw <see cref="ToolFailureException"/>
/// with fixed messages only.
/// </summary>
public interface INetworkPlatform
{
    /// <summary>GetIfTable2 only (cheap; used twice per traffic sample).</summary>
    NetTable<NetInterfaceRow> ReadInterfaces();

    /// <summary>All four IP Helper tables.</summary>
    NetworkSnapshot ReadSnapshot();

    /// <summary>The OS connectivity level of the internet connection profile, or null when it cannot be read.</summary>
    NetworkConnectivityLevel? ReadConnectivity();

    /// <summary>True when the process token is elevated (administrator). Required by the provider methods.</summary>
    bool IsElevated { get; }

    /// <summary>
    /// The MSFT_NetAdapter instances whose InterfaceGuid equals <paramref name="interfaceGuid"/>, read now. Empty when none.
    /// Throws <see cref="ToolFailureException"/> when the provider cannot be read.
    /// </summary>
    IReadOnlyList<ProviderAdapter> ReadProviderAdapters(Guid interfaceGuid, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates MSFT_NetAdapter and returns a handle on the single instance with this InterfaceGuid, or null when there is
    /// none. Throws <see cref="ToolFailureException"/> (AMBIGUOUS_TARGET) when several match, or when the provider fails.
    /// </summary>
    IProviderAdapterHandle? OpenProviderAdapter(Guid interfaceGuid, CancellationToken cancellationToken);
}
