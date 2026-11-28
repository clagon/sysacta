using System.Runtime.InteropServices;

namespace Sysacta.Windows.Network;

/// <summary>
/// Documented IP Helper entry points (netioapi.h), loaded from System32 only. Every table is allocated by the OS and freed
/// with FreeMibTable. Layouts follow the x64 SDK headers and are checked by Windows.Tests.
/// </summary>
internal static class NativeMethods
{
    public const uint ErrorSuccess = 0;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorNotFound = 1168;
    public const ushort AfUnspec = 0;
    public const ushort AfInet = 2;
    public const ushort AfInet6 = 23;

    /// <summary>InterfaceAndOperStatusFlags bits of MIB_IF_ROW2.</summary>
    public const byte FlagHardwareInterface = 0x01;
    public const byte FlagFilterInterface = 0x02;
    public const byte FlagConnectorPresent = 0x04;

    /// <summary>MIB_IF_ROW2 (1352 bytes). MAC address fields exist only for the layout and are never read.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MibIfRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }

    /// <summary>SOCKADDR_INET (28 bytes, 4-byte aligned): sockaddr_in (address at 4) or sockaddr_in6 (address at 8, scope at 24).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct SockAddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint V4Address;
        [FieldOffset(8)] public uint V6Address0;
        [FieldOffset(12)] public uint V6Address1;
        [FieldOffset(16)] public uint V6Address2;
        [FieldOffset(20)] public uint V6Address3;
        [FieldOffset(24)] public uint V6ScopeId;
    }

    /// <summary>MIB_UNICASTIPADDRESS_ROW (80 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MibUnicastIpAddressRow
    {
        public SockAddrInet Address;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint PrefixOrigin;
        public uint SuffixOrigin;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public byte OnLinkPrefixLength;
        public byte SkipAsSource;
        public uint DadState;
        public uint ScopeId;
        public long CreationTimeStamp;
    }

    /// <summary>MIB_IPFORWARD_ROW2 (104 bytes). IP_ADDRESS_PREFIX is flattened (prefix + length, padded to 32).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MibIpForwardRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public SockAddrInet DestinationPrefix;
        public byte DestinationPrefixLength;
        public SockAddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public uint Origin;
    }

    /// <summary>MIB_IFSTACK_ROW (8 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MibIfStackRow
    {
        public uint HigherLayerInterfaceIndex;
        public uint LowerLayerInterfaceIndex;
    }

    /// <summary>Offset of the first row: MIB_IF_TABLE2 / MIB_UNICASTIPADDRESS_TABLE / MIB_IPFORWARD_TABLE2 rows are 8-byte aligned.</summary>
    public const int AlignedTableHeader = 8;

    /// <summary>MIB_IFSTACK_TABLE rows are 4-byte aligned.</summary>
    public const int StackTableHeader = 4;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint GetUnicastIpAddressTable(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint GetIfStackTable(out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern void FreeMibTable(IntPtr memory);
}
