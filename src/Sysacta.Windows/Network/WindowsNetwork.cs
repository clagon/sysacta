using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Network;
using Sysacta.Core.Tools.SystemMonitoring;
using static Sysacta.Windows.Network.NativeMethods;

namespace Sysacta.Windows.Network;

/// <summary>
/// Network adapter for the network tools: IP Helper tables (read-only), the internet connection profile's connectivity
/// level (WinRT, Windows' own NCSI result; nothing is sent) and the fixed CIM class MSFT_NetAdapter for the administrative
/// state. No shell, netsh, PowerShell or WQL built from input.
/// </summary>
public sealed class WindowsNetwork : INetworkPlatform
{
    /// <summary>Internal row limits; a larger table is reported as truncated (never as complete).</summary>
    internal const int MaxInterfaces = 512;
    internal const int MaxAddresses = 4096;
    internal const int MaxRoutes = 8192;
    internal const int MaxStackRows = 4096;

    private const string ProviderQuery = "SELECT * FROM MSFT_NetAdapter";
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(5);

    private readonly Lazy<bool> _elevated = new(DetectElevated);

    public bool IsElevated => _elevated.Value;

    public NetTable<NetInterfaceRow> ReadInterfaces() =>
        Read<MibIfRow2, NetInterfaceRow>(static (out IntPtr t) => GetIfTable2(out t), AlignedTableHeader, MaxInterfaces, static r => new NetInterfaceRow(
            r.InterfaceGuid,
            r.InterfaceLuid,
            r.InterfaceIndex,
            r.Type,
            r.TunnelType,
            r.PhysicalMediumType,
            r.Alias,
            r.Description,
            r.AdminStatus,
            r.OperStatus,
            r.MediaConnectState,
            (r.InterfaceAndOperStatusFlags & FlagHardwareInterface) != 0,
            (r.InterfaceAndOperStatusFlags & FlagFilterInterface) != 0,
            (r.InterfaceAndOperStatusFlags & FlagConnectorPresent) != 0,
            r.TransmitLinkSpeed,
            r.ReceiveLinkSpeed,
            r.InOctets,
            r.OutOctets));

    public NetworkSnapshot ReadSnapshot() => new(
        ReadInterfaces(),
        Read<MibUnicastIpAddressRow, NetUnicastAddress>(static (out IntPtr t) => GetUnicastIpAddressTable(AfUnspec, out t), AlignedTableHeader, MaxAddresses,
            static r => ToAddress(r.Address) is { } ip ? new NetUnicastAddress(r.InterfaceLuid, ip, r.OnLinkPrefixLength, r.DadState) : null),
        Read<MibIpForwardRow2, NetRoute>(static (out IntPtr t) => GetIpForwardTable2(AfUnspec, out t), AlignedTableHeader, MaxRoutes,
            static r => r.DestinationPrefix.Family switch
            {
                AfInet => new NetRoute(r.InterfaceLuid, AddressFamily.InterNetwork, r.DestinationPrefixLength),
                AfInet6 => new NetRoute(r.InterfaceLuid, AddressFamily.InterNetworkV6, r.DestinationPrefixLength),
                _ => null,
            }),
        Read<MibIfStackRow, NetStackLink>(static (out IntPtr t) => GetIfStackTable(out t), StackTableHeader, MaxStackRows,
            static r => new NetStackLink(r.HigherLayerInterfaceIndex, r.LowerLayerInterfaceIndex)));

    public NetworkConnectivityLevel? ReadConnectivity()
    {
        try
        {
            var profile = global::Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return NetworkConnectivityLevel.None;
            }

            return profile.GetNetworkConnectivityLevel() switch
            {
                global::Windows.Networking.Connectivity.NetworkConnectivityLevel.None => NetworkConnectivityLevel.None,
                global::Windows.Networking.Connectivity.NetworkConnectivityLevel.LocalAccess => NetworkConnectivityLevel.LocalAccess,
                global::Windows.Networking.Connectivity.NetworkConnectivityLevel.ConstrainedInternetAccess => NetworkConnectivityLevel.ConstrainedInternetAccess,
                global::Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess => NetworkConnectivityLevel.InternetAccess,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<ProviderAdapter> ReadProviderAdapters(Guid interfaceGuid, CancellationToken cancellationToken)
    {
        var matches = Enumerate(interfaceGuid, cancellationToken);
        try
        {
            return [.. matches.Select(m => m.Adapter)];
        }
        finally
        {
            matches.ForEach(m => m.Instance.Dispose());
        }
    }

    public IProviderAdapterHandle? OpenProviderAdapter(Guid interfaceGuid, CancellationToken cancellationToken)
    {
        var matches = Enumerate(interfaceGuid, cancellationToken);
        if (matches.Count == 1)
        {
            return new Handle(matches[0].Instance, matches[0].Adapter);
        }

        matches.ForEach(m => m.Instance.Dispose());
        return matches.Count == 0 ? null : throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "Several network adapter instances match this id. Nothing was changed.");
    }

    /// <summary>
    /// The MSFT_NetAdapter instances with this InterfaceGuid. The query is a constant; the instance is selected here by the
    /// parsed GUID rather than by building a query from the id.
    /// </summary>
    private static List<(ManagementObject Instance, ProviderAdapter Adapter)> Enumerate(Guid interfaceGuid, CancellationToken cancellationToken) => Guard(() =>
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\StandardCimv2"), new ObjectQuery(ProviderQuery),
            new System.Management.EnumerationOptions { Timeout = ProviderTimeout, ReturnImmediately = false, Rewindable = false });
        using var collection = searcher.Get();
        var matches = new List<(ManagementObject, ProviderAdapter)>(1);
        var count = 0;
        foreach (var item in collection.Cast<ManagementObject>())
        {
            if (++count > MaxInterfaces || cancellationToken.IsCancellationRequested || item["InterfaceGuid"] is not string text
                || !Guid.TryParse(text, out var guid) || guid != interfaceGuid)
            {
                item.Dispose();
                continue;
            }

            matches.Add((item, new ProviderAdapter(
                guid,
                item["NetLuid"] is ulong luid ? luid : 0,
                item["InterfaceIndex"] is uint index ? index : 0,
                item["State"] is uint state ? state : 0,
                item["HardwareInterface"] is true,
                item["Virtual"] is not false,
                item["Hidden"] is not false,
                item["ConnectorPresent"] is true,
                item["NdisPhysicalMedium"] is uint medium ? medium : 0,
                item["MediaConnectState"] is uint media ? media : 0)));
        }

        if (count > MaxInterfaces || cancellationToken.IsCancellationRequested)
        {
            matches.ForEach(m => m.Item1.Dispose());
            cancellationToken.ThrowIfCancellationRequested();
            throw new ToolFailureException(ErrorCodes.OsApiError, "Too many network adapter instances to identify the adapter safely.");
        }

        return matches;
    });

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            throw new ToolFailureException(ErrorCodes.AdminRequired, "Access to the network adapter provider was denied.");
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The network adapter provider returned an error.");
        }
    }

    private sealed class Handle(ManagementObject instance, ProviderAdapter adapter) : IProviderAdapterHandle
    {
        public ProviderAdapter Adapter { get; } = adapter;

        public uint Invoke(bool enabled)
        {
            // Fixed method names of MSFT_NetAdapter; no parameters.
            using var result = instance.InvokeMethod(enabled ? "Enable" : "Disable", null, null);
            return result?["ReturnValue"] is uint value ? value : throw new InvalidOperationException("No return value.");
        }

        public void Dispose() => instance.Dispose();
    }

    private delegate uint TableGetter(out IntPtr table);

    /// <summary>
    /// Reads one OS-allocated table: the entry count, then at most <paramref name="limit"/> rows of <typeparamref name="TRow"/>
    /// (stride = its marshaled size, equal to the native size). Always frees the table. A row the mapper rejects makes the
    /// whole table invalid (never silently dropped).
    /// </summary>
    private static NetTable<T> Read<TRow, T>(TableGetter get, int firstRow, int limit, Func<TRow, T?> map)
        where TRow : struct
        where T : class
    {
        var table = IntPtr.Zero;
        try
        {
            var status = get(out table);
            if (status == ErrorNotFound)
            {
                return NetTable.Ok<T>([]);
            }

            if (status != ErrorSuccess || table == IntPtr.Zero)
            {
                return NetTable.Failed<T>(status == ErrorAccessDenied ? UnavailableReason.PermissionDenied : UnavailableReason.Unavailable);
            }

            var count = (uint)Marshal.ReadInt32(table);
            var take = (int)Math.Min(count, (uint)limit);
            var size = Marshal.SizeOf<TRow>();
            var rows = new List<T>(take);
            for (var i = 0; i < take; i++)
            {
                if (map(Marshal.PtrToStructure<TRow>(table + firstRow + (i * size))) is not { } row)
                {
                    return NetTable.Failed<T>(UnavailableReason.InvalidData);
                }

                rows.Add(row);
            }

            return new NetTable<T>(rows, count > limit ? UnavailableReason.Truncated : null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return NetTable.Failed<T>(UnavailableReason.NotSupported);
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                FreeMibTable(table);
            }
        }
    }

    internal static IPAddress? ToAddress(SockAddrInet address) => address.Family switch
    {
        AfInet => new IPAddress(BitConverter.GetBytes(address.V4Address)),
        AfInet6 => new IPAddress([.. BitConverter.GetBytes(address.V6Address0), .. BitConverter.GetBytes(address.V6Address1),
            .. BitConverter.GetBytes(address.V6Address2), .. BitConverter.GetBytes(address.V6Address3)], address.V6ScopeId),
        _ => null,
    };

    private static bool DetectElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
