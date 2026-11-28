using System.Runtime.InteropServices;

namespace Sysacta.Windows.SystemMonitoring;

/// <summary>
/// Documented Win32 / PDH / DXGI / D3DKMT entry points used for read-only monitoring. Every DLL is loaded from System32
/// only. No driver, service, undocumented export or I/O control is used.
/// </summary>
internal static class NativeMethods
{
    // ---- PDH (pdh.h) ----------------------------------------------------------------------------------------------

    public const uint PdhFmtDouble = 0x00000200;
    public const uint PdhFmtLarge = 0x00000400;
    public const uint PdhFmtNoCap100 = 0x00008000;
    public const uint PdhCstatusValidData = 0x00000000;
    public const uint PdhCstatusNewData = 0x00000001;
    public const uint PdhMoreData = 0x800007D2;
    public const uint PdhNoData = 0x800007D5;
    public const uint PdhCstatusNoObject = 0xC0000BB8;
    public const uint PdhCstatusNoCounter = 0xC0000BB9;
    public const uint PdhAccessDenied = 0xC0000BDB;

    [StructLayout(LayoutKind.Explicit)]
    public struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
        [FieldOffset(8)] public long LargeValue;
    }

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W: the instance name pointer followed by the value (layout per architecture).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    /// <summary>PDH_RAW_COUNTER (CStatus, FILETIME, two LONGLONG values, MultiCount).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PdhRawCounter
    {
        public uint CStatus;
        public uint TimeStampLow;
        public uint TimeStampHigh;
        public long FirstValue;
        public long SecondValue;
        public uint MultiCount;
    }

    /// <summary>PDH_RAW_COUNTER_ITEM_W: the instance name pointer followed by the raw value. Only the name is used.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PdhRawCounterItem
    {
        public IntPtr Name;
        public PdhRawCounter RawValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint PdhGetRawCounterArrayW(IntPtr counter, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhFmtCounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint PdhCloseQuery(IntPtr query);

    // ---- kernel32 ---------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DiskSpaceInformation
    {
        public ulong ActualTotalAllocationUnits;
        public ulong ActualAvailableAllocationUnits;
        public ulong ActualPoolUnavailableAllocationUnits;
        public ulong CallerTotalAllocationUnits;
        public ulong CallerAvailableAllocationUnits;
        public ulong CallerPoolUnavailableAllocationUnits;
        public ulong UsedAllocationUnits;
        public ulong TotalReservedAllocationUnits;
        public ulong VolumeStorageReserveAllocationUnits;
        public ulong AvailableCommittedAllocationUnits;
        public ulong PoolAvailableAllocationUnits;
        public uint SectorsPerAllocationUnit;
        public uint BytesPerSector;
    }

    public const ushort AllProcessorGroups = 0xFFFF;
    public const int RelationProcessorCore = 0;
    public const int RelationProcessorPackage = 3;
    public const int RelationAll = 0xFFFF;
    public const int ErrorInsufficientBuffer = 122;
    public const uint DriveFixed = 3;
    public const uint SemFailCriticalErrors = 0x0001;
    public const uint SemNoOpenFileErrorBox = 0x8000;
    public const int EAccessDenied = unchecked((int)0x80070005);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint QueryDosDeviceW(string deviceName, [Out] char[] targetPath, uint max);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetDiskSpaceInformationW(string rootPath, out DiskSpaceInformation diskSpaceInfo);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadErrorMode(uint newMode, out uint oldMode);

    // ---- DXGI (dxgi.h) ------------------------------------------------------------------------------------------------

    public static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    public const uint DxgiAdapterFlagSoftware = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    // ---- D3DKMT (d3dkmthk.h, gdi32) ----------------------------------------------------------------------------------

    /// <summary>KMTQAITYPE_ADAPTERPERFDATA. The enum skips 14 (KMTQAITYPE_ADAPTERTYPE = 15), so this is 62.</summary>
    public const int KmtqaitypeAdapterPerfData = 62;

    /// <summary>KMTQAITYPE_PHYSICALADAPTERCOUNT (D3DKMT_PHYSICAL_ADAPTER_COUNT: one UINT). 30 for the same reason.</summary>
    public const int KmtqaitypePhysicalAdapterCount = 30;
    public const int StatusAccessDenied = unchecked((int)0xC0000022);
    public const int StatusInvalidParameter = unchecked((int)0xC000000D);
    public const int StatusNotSupported = unchecked((int)0xC00000BB);
    public const int StatusNotImplemented = unchecked((int)0xC0000002);

    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtOpenAdapterFromLuid
    {
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtCloseAdapter
    {
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtQueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    /// <summary>D3DKMT_ADAPTER_PERFDATA: UINT32 then five D3DKMT_ALIGN64 ULONGLONGs, three ULONGs and a UCHAR (64 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct D3dkmtAdapterPerfData
    {
        [FieldOffset(0)] public uint PhysicalAdapterIndex;
        [FieldOffset(8)] public ulong MemoryFrequency;
        [FieldOffset(16)] public ulong MaxMemoryFrequency;
        [FieldOffset(24)] public ulong MaxMemoryFrequencyOc;
        [FieldOffset(32)] public ulong MemoryBandwidth;
        [FieldOffset(40)] public ulong PcieBandwidth;
        [FieldOffset(48)] public uint FanRpm;
        [FieldOffset(52)] public uint Power;
        [FieldOffset(56)] public uint Temperature;
        [FieldOffset(60)] public byte PowerStateOverride;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int D3DKMTOpenAdapterFromLuid(ref D3dkmtOpenAdapterFromLuid openAdapter);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int D3DKMTQueryAdapterInfo(ref D3dkmtQueryAdapterInfo queryAdapterInfo);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter closeAdapter);
}

/// <summary>IDXGIFactory1 vtable (IUnknown, IDXGIObject, IDXGIFactory, IDXGIFactory1). Only EnumAdapters1 is called.</summary>
[ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDxgiFactory1
{
    [PreserveSig] int SetPrivateData();
    [PreserveSig] int SetPrivateDataInterface();
    [PreserveSig] int GetPrivateData();
    [PreserveSig] int GetParent();
    [PreserveSig] int EnumAdapters();
    [PreserveSig] int MakeWindowAssociation();
    [PreserveSig] int GetWindowAssociation();
    [PreserveSig] int CreateSwapChain();
    [PreserveSig] int CreateSoftwareAdapter();
    [PreserveSig] int EnumAdapters1(uint adapter, out IntPtr adapterPointer);
}

/// <summary>IDXGIAdapter1 vtable (IUnknown, IDXGIObject, IDXGIAdapter, IDXGIAdapter1). Only GetDesc1 is called.</summary>
[ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDxgiAdapter1
{
    [PreserveSig] int SetPrivateData();
    [PreserveSig] int SetPrivateDataInterface();
    [PreserveSig] int GetPrivateData();
    [PreserveSig] int GetParent();
    [PreserveSig] int EnumOutputs();
    [PreserveSig] int GetDesc();
    [PreserveSig] int CheckInterfaceSupport();
    [PreserveSig] int GetDesc1(out NativeMethods.DxgiAdapterDesc1 desc);
}
