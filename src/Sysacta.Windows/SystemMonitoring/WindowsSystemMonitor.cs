using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.SystemMonitoring;
using static Sysacta.Windows.SystemMonitoring.NativeMethods;

namespace Sysacta.Windows.SystemMonitoring;

/// <summary>
/// Read-only monitoring adapter (docs/phase-1f-progress.md): PDH counters added by their English names, DXGI adapter
/// enumeration (no device is created), D3DKMT adapter performance data, GlobalMemoryStatusEx, GetTickCount64, three fixed
/// WMI queries in root\cimv2, two fixed registry values, and Win32 volume capacity. Every handle is opened per call and
/// closed before returning; there is no background sampling or cache. The only shared state is one
/// <see cref="HangGuard"/> per source, so a hung source is not opened again while its earlier read is still running.
/// </summary>
public sealed class WindowsSystemMonitor : ISystemMonitor
{
    private const string CpuCounter = @"\Processor Information(_Total)\% Processor Time";
    private const string EngineCounter = @"\GPU Engine(*)\Utilization Percentage";
    private const string DedicatedCounter = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string SharedCounter = @"\GPU Adapter Memory(*)\Shared Usage";
    private const string ZoneHighPrecisionCounter = @"\Thermal Zone Information(*)\High Precision Temperature";
    private const string ZoneCounter = @"\Thermal Zone Information(*)\Temperature";
    private const int MaxAdapters = 256;
    private const int MaxPhysicalAdapters = 8;
    private const int MaxMemoryInstances = 256;
    private const int MaxZoneInstances = 256;

    /// <summary>Total budget of the three WMI queries of one hardware read, inside the 5 second tool timeout.</summary>
    private static readonly TimeSpan WmiBudget = TimeSpan.FromSeconds(3);

    private readonly HangGuard _pdh = new(ToolDescriptor.DefaultTimeout);
    private readonly HangGuard _dxgi = new(ToolDescriptor.DefaultTimeout);
    private readonly HangGuard _d3dkmt = new(ToolDescriptor.DefaultTimeout);
    private readonly HangGuard _wmi = new(ToolDescriptor.DefaultTimeout);
    private readonly HangGuard _storage = new(ToolDescriptor.DefaultTimeout);

    public StatusReading SampleStatus(TimeSpan interval, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapters = Adapters();
        cancellationToken.ThrowIfCancellationRequested();
        var pdh = _pdh.TryEnter();
        try
        {
            // uint.MaxValue stands for "PDH not opened because an earlier read is hung" (reported as timeout).
            var refused = uint.MaxValue;
            using var query = pdh is null ? null : PdhQuery.Open(out refused);
            var cpu = query?.Add(CpuCounter) ?? (IntPtr.Zero, refused);
            var engines = query?.Add(EngineCounter) ?? (IntPtr.Zero, refused);
            var dedicated = query?.Add(DedicatedCounter) ?? (IntPtr.Zero, refused);
            var shared = query?.Add(SharedCounter) ?? (IntPtr.Zero, refused);

            // Rate counters need two collections; the first value of a rate counter is never used.
            var first = query?.Collect() ?? refused;
            var startedAt = DateTimeOffset.UtcNow;
            // Instance names present at the first collection: an invalid value later is "new" only if its name is absent.
            var firstEngines = first == 0 && engines.Status == 0 ? InstanceNames(engines.Counter, SystemStatus.MaxEngineInstances) : null;
            var tick0 = Environment.TickCount64;
            QueryUnbiasedInterruptTime(out var unbiased0);
            cancellationToken.WaitHandle.WaitOne(interval);
            cancellationToken.ThrowIfCancellationRequested();
            var second = first == 0 ? query!.Collect() : first;
            var tick1 = Environment.TickCount64;
            QueryUnbiasedInterruptTime(out var unbiased1);
            var endedAt = DateTimeOffset.UtcNow;

            double? cpuValue = null;
            UnavailableReason? cpuReason = null;
            if (cpu.Status != 0 || second != 0)
            {
                cpuReason = Reason(cpu.Status != 0 ? cpu.Status : second);
            }
            else
            {
                var status = PdhGetFormattedCounterValue(cpu.Counter, PdhFmtDouble | PdhFmtNoCap100, IntPtr.Zero, out var value);
                if (status == 0 && IsValid(value.CStatus)) cpuValue = value.DoubleValue;
                else cpuReason = UnavailableReason.Unavailable;
            }

            var engineArray = ReadArray(engines, second, SystemStatus.MaxEngineInstances, PdhFmtDouble | PdhFmtNoCap100);
            var engineSamples = engineArray.Reason is { } engineReason
                ? Observed.Missing<IReadOnlyList<CounterSample>>(engineReason)
                : Observed.Ok<IReadOnlyList<CounterSample>>(engineArray.Items!.Select(i => new CounterSample(i.Name, i.Value, firstEngines?.Contains(i.Name))).ToList());

            var (total, available) = Memory();
            var logical = GetActiveProcessorCount(AllProcessorGroups);
            return new StatusReading(
                startedAt,
                endedAt,
                tick1 - tick0,
                (long)((unbiased1 - unbiased0) / 10_000),
                cpuValue,
                cpuReason,
                logical == 0 ? null : (int)logical,
                total,
                available,
                (ulong)Environment.TickCount64,
                adapters,
                engineSamples,
                GpuMemory(dedicated, shared, second));
        }
        finally
        {
            _pdh.Exit(pdh);
        }
    }

    public HardwareReading ReadHardware(CancellationToken cancellationToken)
    {
        var failures = new Dictionary<HardwarePart, UnavailableReason>();
        var deadline = Environment.TickCount64 + (long)WmiBudget.TotalMilliseconds;

        string? caption = null;
        cancellationToken.ThrowIfCancellationRequested();
        Wmi("SELECT Caption FROM Win32_OperatingSystem", o => caption ??= o["Caption"] as string, HardwarePart.OsProductName, failures, deadline);
        cancellationToken.ThrowIfCancellationRequested();

        var processors = new List<ProcessorReading>();
        if (!Wmi("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor",
            o => processors.Add(new(o["Name"] as string, o["Manufacturer"] as string, Int(o["NumberOfCores"]), Int(o["NumberOfLogicalProcessors"]))),
            HardwarePart.Processors, failures, deadline, SystemInventory.MaxItems + 1))
        {
            processors = null;
        }

        string? manufacturer = null, model = null;
        cancellationToken.ThrowIfCancellationRequested();
        Wmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem", o =>
        {
            manufacturer ??= o["Manufacturer"] as string;
            model ??= o["Model"] as string;
        }, HardwarePart.MachineManufacturer, failures, deadline);
        if (failures.TryGetValue(HardwarePart.MachineManufacturer, out var machineFailure)) failures[HardwarePart.MachineModel] = machineFailure;

        int? ubr = null;
        string? displayVersion = null;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            ubr = key?.GetValue("UBR") is int u ? u : null;
            displayVersion = key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            failures[HardwarePart.OsUpdateBuildRevision] = failures[HardwarePart.OsDisplayVersion] = UnavailableReason.PermissionDenied;
        }

        var (packages, cores) = Topology();
        if (packages is null) failures[HardwarePart.ProcessorTopology] = UnavailableReason.Unavailable;
        var logical = GetActiveProcessorCount(AllProcessorGroups);
        ulong? installed = GetPhysicallyInstalledSystemMemory(out var kb) ? kb : null;
        cancellationToken.ThrowIfCancellationRequested();
        var adapters = Adapters();
        if (adapters.Reason is { } gpuFailure) failures[HardwarePart.Gpus] = gpuFailure;
        var version = Environment.OSVersion.Version;

        return new HardwareReading(
            DateTimeOffset.UtcNow,
            caption,
            (version.Major, version.Minor, version.Build),
            ubr,
            displayVersion,
            RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                _ => null,
            },
            processors,
            packages,
            cores,
            logical == 0 ? null : (int)logical,
            installed,
            Memory().Total,
            adapters.Value,
            manufacturer,
            model,
            failures);
    }

    public TemperatureReading ReadTemperatures(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapters = Adapters();
        Observed<IReadOnlyList<GpuTemperatureReading>> gpus;
        if (adapters.Reason is { } r)
        {
            gpus = Observed.Missing<IReadOnlyList<GpuTemperatureReading>>(r);
        }
        else
        {
            var d3dkmt = _d3dkmt.TryEnter();
            try
            {
                gpus = d3dkmt is null
                    ? Observed.Missing<IReadOnlyList<GpuTemperatureReading>>(UnavailableReason.Timeout)
                    : Observed.Ok<IReadOnlyList<GpuTemperatureReading>>(adapters.Value!.Where(a => !a.Software).SelectMany(a => GpuTemperatures(a, cancellationToken)).ToList());
            }
            finally
            {
                _d3dkmt.Exit(d3dkmt);
            }
        }

        var gpusAt = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        var pdh = _pdh.TryEnter();
        try
        {
            if (pdh is null)
            {
                return new TemperatureReading(gpusAt, gpus, DateTimeOffset.UtcNow, Observed.Missing<IReadOnlyList<ThermalZoneReading>>(UnavailableReason.Timeout));
            }

            using var query = PdhQuery.Open(out var openStatus);
            var hp = query?.Add(ZoneHighPrecisionCounter) ?? (IntPtr.Zero, openStatus);
            var k = query?.Add(ZoneCounter) ?? (IntPtr.Zero, openStatus);
            var collected = query?.Collect() ?? openStatus;
            var zonesAt = DateTimeOffset.UtcNow;
            var hpItems = ReadArray(hp, collected, MaxZoneInstances, PdhFmtDouble | PdhFmtNoCap100);
            var kItems = ReadArray(k, collected, MaxZoneInstances, PdhFmtDouble | PdhFmtNoCap100);
            var zones = hpItems.Reason is not null && kItems.Reason is not null
                ? Observed.Missing<IReadOnlyList<ThermalZoneReading>>(kItems.Reason.Value)
                : Observed.Ok<IReadOnlyList<ThermalZoneReading>>(JoinByName(hpItems.Items ?? [], kItems.Items ?? [])
                    .Select(j => new ThermalZoneReading(j.Name, j.A?.Value, j.B?.Value)).ToList());
            return new TemperatureReading(gpusAt, gpus, zonesAt, zones);
        }
        finally
        {
            _pdh.Exit(pdh);
        }
    }

    public StorageReading ReadStorage(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var guard = _storage.TryEnter()
            ?? throw new ToolFailureException(ErrorCodes.Timeout, "An earlier storage read has not finished.", retryable: true);
        // Never show the "insert a disk" / critical error dialog for this thread; restored before returning.
        var restore = SetThreadErrorMode(SemFailCriticalErrors | SemNoOpenFileErrorBox, out var oldMode);
        try
        {
            var mask = GetLogicalDrives();
            if (mask == 0)
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "The drive list could not be read.", retryable: true);
            }

            var (drives, excluded) = SelectDrives(mask, GetDriveTypeW, DosTarget, DiskSpace, cancellationToken);
            return new StorageReading(DateTimeOffset.UtcNow, drives, excluded);
        }
        finally
        {
            if (restore) SetThreadErrorMode(oldMode, out _);
            _storage.Exit(guard);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Chooses the drives to measure. The drive type is checked first, so network, removable, optical, RAM-disk and
    /// unknown drives never reach <paramref name="dosTarget"/> or <paramref name="space"/>. A drive whose device is a
    /// <c>\??\</c> path is a SUBST folder of another volume and is left out (it would duplicate that volume).
    /// </summary>
    internal static (List<DriveReading> Fixed, int Excluded) SelectDrives(uint mask, Func<string, uint> driveType, Func<char, string?> dosTarget, Func<char, DriveReading> space, CancellationToken cancellationToken)
    {
        var drives = new List<DriveReading>();
        var excluded = 0;
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var letter = (char)('A' + i);
            if (driveType($"{letter}:\\") != DriveFixed)
            {
                excluded++;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var target = dosTarget(letter);
            if (target is null)
            {
                drives.Add(new(letter, null, UnavailableReason.Unavailable));
            }
            else if (target.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                excluded++;
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                drives.Add(space(letter));
            }
        }

        return (drives, excluded);
    }

    private static string? DosTarget(char letter)
    {
        var target = new char[1024];
        var length = QueryDosDeviceW($"{letter}:", target, (uint)target.Length);
        return length == 0 ? null : new string(target, 0, (int)Math.Min(length, 8));
    }

    private static DriveReading DiskSpace(char letter)
    {
        var hr = GetDiskSpaceInformationW($"{letter}:\\", out var info);
        return hr < 0
            ? new(letter, null, hr == EAccessDenied ? UnavailableReason.PermissionDenied : UnavailableReason.Unavailable)
            : new(letter, new DiskSpaceUnits(info.ActualTotalAllocationUnits, info.ActualAvailableAllocationUnits, info.CallerAvailableAllocationUnits,
                info.SectorsPerAllocationUnit, info.BytesPerSector), null);
    }

    /// <summary>DXGI adapters (EnumAdapters1 / GetDesc1). No device is created; every COM object is released.</summary>
    internal Observed<IReadOnlyList<GpuAdapterReading>> Adapters()
    {
        var guard = _dxgi.TryEnter();
        if (guard is null) return Observed.Missing<IReadOnlyList<GpuAdapterReading>>(UnavailableReason.Timeout);
        try
        {
            return EnumerateAdapters();
        }
        finally
        {
            _dxgi.Exit(guard);
        }
    }

    private static Observed<IReadOnlyList<GpuAdapterReading>> EnumerateAdapters()
    {
        var hr = CreateDXGIFactory1(IidDxgiFactory1, out var factoryPointer);
        if (hr < 0 || factoryPointer == IntPtr.Zero)
        {
            return Observed.Missing<IReadOnlyList<GpuAdapterReading>>(UnavailableReason.Unavailable);
        }

        IDxgiFactory1? factory = null;
        try
        {
            factory = (IDxgiFactory1)Marshal.GetObjectForIUnknown(factoryPointer);
            var list = new List<GpuAdapterReading>();
            for (uint i = 0; i < MaxAdapters; i++)
            {
                hr = factory.EnumAdapters1(i, out var adapterPointer);
                if (hr == DxgiErrorNotFound) break;
                if (hr < 0 || adapterPointer == IntPtr.Zero) return Observed.Missing<IReadOnlyList<GpuAdapterReading>>(UnavailableReason.Unavailable);
                IDxgiAdapter1? adapter = null;
                try
                {
                    adapter = (IDxgiAdapter1)Marshal.GetObjectForIUnknown(adapterPointer);
                    if (adapter.GetDesc1(out var d) < 0) return Observed.Missing<IReadOnlyList<GpuAdapterReading>>(UnavailableReason.Unavailable);
                    list.Add(new(((ulong)(uint)d.AdapterLuidHighPart << 32) | d.AdapterLuidLowPart, d.Description, d.VendorId, d.DeviceId,
                        d.DedicatedVideoMemory, d.DedicatedSystemMemory, d.SharedSystemMemory, (d.Flags & DxgiAdapterFlagSoftware) != 0));
                }
                finally
                {
                    if (adapter is not null) Marshal.ReleaseComObject(adapter);
                    Marshal.Release(adapterPointer);
                }
            }

            return Observed.Ok<IReadOnlyList<GpuAdapterReading>>(list);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return Observed.Missing<IReadOnlyList<GpuAdapterReading>>(UnavailableReason.Unavailable);
        }
        finally
        {
            if (factory is not null) Marshal.ReleaseComObject(factory);
            Marshal.Release(factoryPointer);
        }
    }

    /// <summary>
    /// D3DKMT_ADAPTER_PERFDATA of each physical adapter: open from LUID, count physical adapters (1 when the count cannot be
    /// read), one query per physical adapter, close.
    /// </summary>
    internal static IReadOnlyList<GpuTemperatureReading> GpuTemperatures(GpuAdapterReading adapter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var open = new D3dkmtOpenAdapterFromLuid { AdapterLuidLowPart = (uint)adapter.Luid, AdapterLuidHighPart = (int)(uint)(adapter.Luid >> 32) };
        var status = D3DKMTOpenAdapterFromLuid(ref open);
        if (status != 0) return [new(adapter.Luid, 0, adapter.Description, null, NtReason(status))];

        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<D3dkmtAdapterPerfData>());
        try
        {
            var count = Query(open.Adapter, KmtqaitypePhysicalAdapterCount, buffer, sizeof(uint)) == 0 ? Marshal.ReadInt32(buffer) : 1;
            var result = new List<GpuTemperatureReading>();
            for (var phys = 0; phys < Math.Clamp(count, 1, MaxPhysicalAdapters); phys++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.StructureToPtr(new D3dkmtAdapterPerfData { PhysicalAdapterIndex = (uint)phys }, buffer, false);
                status = Query(open.Adapter, KmtqaitypeAdapterPerfData, buffer, Marshal.SizeOf<D3dkmtAdapterPerfData>());
                result.Add(status != 0
                    ? new(adapter.Luid, phys, adapter.Description, null, NtReason(status))
                    : new(adapter.Luid, phys, adapter.Description, Marshal.PtrToStructure<D3dkmtAdapterPerfData>(buffer).Temperature, null));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            var close = new D3dkmtCloseAdapter { Adapter = open.Adapter };
            _ = D3DKMTCloseAdapter(ref close);
        }

        static int Query(uint handle, int type, IntPtr data, int size)
        {
            var query = new D3dkmtQueryAdapterInfo { Adapter = handle, Type = type, PrivateDriverData = data, PrivateDriverDataSize = (uint)size };
            return D3DKMTQueryAdapterInfo(ref query);
        }
    }

    private static UnavailableReason NtReason(int status) => status switch
    {
        StatusAccessDenied => UnavailableReason.PermissionDenied,
        StatusInvalidParameter or StatusNotSupported or StatusNotImplemented => UnavailableReason.NotSupported,
        _ => UnavailableReason.Unavailable,
    };

    private static Observed<IReadOnlyList<GpuMemorySample>> GpuMemory((IntPtr Counter, uint Status) dedicated, (IntPtr Counter, uint Status) shared, uint collected)
    {
        var d = ReadArray(dedicated, collected, MaxMemoryInstances, PdhFmtLarge);
        var s = ReadArray(shared, collected, MaxMemoryInstances, PdhFmtLarge);
        if (d.Reason is { } dr) return Observed.Missing<IReadOnlyList<GpuMemorySample>>(dr);
        if (s.Reason is { } sr) return Observed.Missing<IReadOnlyList<GpuMemorySample>>(sr);
        return Observed.Ok<IReadOnlyList<GpuMemorySample>>(JoinByName(d.Items!, s.Items!).Select(j => new GpuMemorySample(j.Name, j.A?.Large, j.B?.Large)).ToList());
    }

    /// <summary>
    /// Pairs two counters of the same object by instance name. A name that appears more than once in either array cannot
    /// be paired: it is returned twice without values, so Core sees the duplicate and rejects it.
    /// </summary>
    private static List<(string Name, PdhItem? A, PdhItem? B)> JoinByName(IReadOnlyList<PdhItem> a, IReadOnlyList<PdhItem> b)
    {
        var result = new List<(string, PdhItem?, PdhItem?)>();
        foreach (var name in a.Select(i => i.Name).Concat(b.Select(i => i.Name)).Distinct(StringComparer.Ordinal))
        {
            var inA = a.Where(i => i.Name == name).ToList();
            var inB = b.Where(i => i.Name == name).ToList();
            if (inA.Count > 1 || inB.Count > 1)
            {
                result.Add((name, null, null));
                result.Add((name, null, null));
            }
            else
            {
                result.Add((name, inA.FirstOrDefault(), inB.FirstOrDefault()));
            }
        }

        return result;
    }

    private sealed record PdhItem(string Name, double? Value, long? Large);

    private sealed record PdhArray(IReadOnlyList<PdhItem>? Items, UnavailableReason? Reason);

    /// <summary>Formatted values of a wildcard counter, or why they are unusable.</summary>
    private static PdhArray ReadArray((IntPtr Counter, uint Status) counter, uint collected, int maxItems, uint format)
    {
        if (counter.Status != 0) return new(null, Reason(counter.Status));
        if (collected != 0) return new(null, Reason(collected));

        // The instance set can change between the size query and the read; retry a few times, never indefinitely.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            var status = PdhGetFormattedCounterArrayW(counter.Counter, format, ref size, out var count, IntPtr.Zero);
            if (status == PdhNoData || (status == 0 && count == 0)) return new([], null);
            if (status != PdhMoreData) return new(null, Reason(status));
            if (count > maxItems || size > 16 * 1024 * 1024) return new(null, UnavailableReason.Truncated);

            var buffer = Marshal.AllocHGlobal((nint)size);
            try
            {
                status = PdhGetFormattedCounterArrayW(counter.Counter, format, ref size, out count, buffer);
                if (status == PdhMoreData) continue;
                if (status != 0) return new(null, Reason(status));
                if (count > maxItems) return new(null, UnavailableReason.Truncated);

                var items = new List<PdhItem>((int)count);
                var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + (i * itemSize));
                    var valid = IsValid(item.Value.CStatus);
                    items.Add(new(Marshal.PtrToStringUni(item.Name) ?? "",
                        valid && (format & PdhFmtDouble) != 0 ? item.Value.DoubleValue : null,
                        valid && (format & PdhFmtLarge) != 0 ? item.Value.LargeValue : null));
                }

                return new(items, null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return new(null, UnavailableReason.Unavailable);
    }

    /// <summary>
    /// Instance names of a wildcard counter from its raw values (available after one collection, unlike formatted rates).
    /// null when they cannot be read or exceed the limit: then no invalid value can be shown to be a new instance.
    /// </summary>
    private static HashSet<string>? InstanceNames(IntPtr counter, int maxItems)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            var status = PdhGetRawCounterArrayW(counter, ref size, out var count, IntPtr.Zero);
            if (status == PdhNoData || (status == 0 && count == 0)) return new(StringComparer.Ordinal);
            if (status != PdhMoreData || count > maxItems || size > 16 * 1024 * 1024) return null;

            var buffer = Marshal.AllocHGlobal((nint)size);
            try
            {
                status = PdhGetRawCounterArrayW(counter, ref size, out count, buffer);
                if (status == PdhMoreData) continue;
                if (status != 0 || count > maxItems) return null;
                var names = new HashSet<string>(StringComparer.Ordinal);
                var itemSize = Marshal.SizeOf<PdhRawCounterItem>();
                for (var i = 0; i < count; i++)
                {
                    if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer + (i * itemSize))) is { } name) names.Add(name);
                }

                return names;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static bool IsValid(uint cstatus) => cstatus is PdhCstatusValidData or PdhCstatusNewData;

    private static UnavailableReason Reason(uint pdhStatus) => pdhStatus switch
    {
        uint.MaxValue => UnavailableReason.Timeout,
        PdhCstatusNoObject or PdhCstatusNoCounter => UnavailableReason.NotSupported,
        PdhAccessDenied => UnavailableReason.PermissionDenied,
        _ => UnavailableReason.Unavailable,
    };

    private static (ulong? Total, ulong? Available) Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (status.TotalPhys, status.AvailPhys) : (null, null);
    }

    /// <summary>Packages and physical cores counted from GetLogicalProcessorInformationEx (all processor groups).</summary>
    private static (int? Packages, int? Cores) Topology()
    {
        uint length = 0;
        if (GetLogicalProcessorInformationEx(RelationAll, IntPtr.Zero, ref length) || Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || length == 0 || length > 4 * 1024 * 1024)
        {
            return (null, null);
        }

        var buffer = Marshal.AllocHGlobal((nint)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, buffer, ref length)) return (null, null);
            int packages = 0, cores = 0;
            for (uint offset = 0; offset + 8 <= length;)
            {
                var relationship = Marshal.ReadInt32(buffer + (nint)offset);
                var size = (uint)Marshal.ReadInt32(buffer + (nint)offset + 4);
                if (size < 8 || offset + size > length) return (null, null);
                if (relationship == RelationProcessorCore) cores++;
                else if (relationship == RelationProcessorPackage) packages++;
                offset += size;
            }

            return packages == 0 || cores == 0 ? (null, null) : (packages, cores);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// One fixed query against local root\cimv2, within what is left of <paramref name="deadline"/>. Returns false (and
    /// records why) when it fails, runs out of time, or an earlier WMI read is still hung.
    /// </summary>
    private bool Wmi(string query, Action<ManagementBaseObject> read, HardwarePart part, Dictionary<HardwarePart, UnavailableReason> failures, long deadline, int max = 1)
    {
        var remaining = deadline - Environment.TickCount64;
        var guard = remaining > 0 ? _wmi.TryEnter() : null;
        if (guard is null)
        {
            failures[part] = UnavailableReason.Timeout;
            return false;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery(query),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromMilliseconds(remaining), ReturnImmediately = true, Rewindable = false });
            using var results = searcher.Get();
            var n = 0;
            foreach (var item in results)
            {
                using (item)
                {
                    if (n++ < max) read(item);
                }
            }

            return true;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            failures[part] = UnavailableReason.PermissionDenied;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.Timedout)
        {
            failures[part] = UnavailableReason.Timeout;
        }
        catch (UnauthorizedAccessException)
        {
            failures[part] = UnavailableReason.PermissionDenied;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException)
        {
            failures[part] = UnavailableReason.Unavailable;
        }
        finally
        {
            _wmi.Exit(guard);
        }

        return false;
    }

    private static int? Int(object? value) => value is uint u && u <= int.MaxValue ? (int)u : null;

    /// <summary>One PDH query handle, closed on dispose. Counters are added by English path, so the UI language does not matter.</summary>
    private sealed class PdhQuery : IDisposable
    {
        private IntPtr _handle;

        private PdhQuery(IntPtr handle) => _handle = handle;

        public static PdhQuery? Open(out uint status)
        {
            status = PdhOpenQueryW(null, IntPtr.Zero, out var handle);
            return status == 0 ? new PdhQuery(handle) : null;
        }

        public (IntPtr Counter, uint Status) Add(string path)
        {
            var status = PdhAddEnglishCounterW(_handle, path, IntPtr.Zero, out var counter);
            return (counter, status);
        }

        public uint Collect() => PdhCollectQueryData(_handle);

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                _ = PdhCloseQuery(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}

/// <summary>
/// Refuses to start a new read of a source while an earlier read of it has been running longer than the limit (it is hung
/// in native code and the executor has given up on it). Normal concurrent reads are not serialized. Each entered read must
/// call <see cref="Exit"/> when it really ends.
/// </summary>
internal sealed class HangGuard(TimeSpan limit)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<long, long> _running = [];
    private long _next;

    /// <summary>A ticket, or null while an earlier read is hung.</summary>
    public long? TryEnter()
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (_running.Values.Any(started => now - started > limit.TotalMilliseconds)) return null;
            _running[++_next] = now;
            return _next;
        }
    }

    public void Exit(long? ticket)
    {
        if (ticket is not { } t) return;
        lock (_lock)
        {
            _running.Remove(t);
        }
    }

    internal int Running
    {
        get
        {
            lock (_lock)
            {
                return _running.Count;
            }
        }
    }
}
