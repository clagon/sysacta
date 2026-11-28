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
/// closed before returning; there is no background sampling, cache or shared state, so calls are independent.
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
    private const int MaxMemoryInstances = 256;
    private const int MaxZoneInstances = 256;
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(3);

    public StatusReading SampleStatus(TimeSpan interval, CancellationToken cancellationToken)
    {
        var adapters = Adapters();
        using var query = PdhQuery.Open(out var openStatus);
        var cpu = query?.Add(CpuCounter) ?? (IntPtr.Zero, openStatus);
        var engines = query?.Add(EngineCounter) ?? (IntPtr.Zero, openStatus);
        var dedicated = query?.Add(DedicatedCounter) ?? (IntPtr.Zero, openStatus);
        var shared = query?.Add(SharedCounter) ?? (IntPtr.Zero, openStatus);

        // Rate counters need two collections; the first value of a rate counter is never used.
        var first = query?.Collect() ?? openStatus;
        var startedAt = DateTimeOffset.UtcNow;
        var tick0 = Environment.TickCount64;
        QueryUnbiasedInterruptTime(out var unbiased0);
        if (cancellationToken.WaitHandle.WaitOne(interval))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

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

        var engineArray = Array(query, engines, second, SystemStatus.MaxEngineInstances, PdhFmtDouble | PdhFmtNoCap100);
        var engineSamples = engineArray.Reason is { } engineReason
            ? Observed.Missing<IReadOnlyList<CounterSample>>(engineReason)
            : Observed.Ok<IReadOnlyList<CounterSample>>(engineArray.Items!.Select(i => new CounterSample(i.Name, i.Value)).ToList());

        var memory = GpuMemory(query, dedicated, shared, second);
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
            memory);
    }

    public HardwareReading ReadHardware()
    {
        var failures = new Dictionary<HardwarePart, UnavailableReason>();

        string? caption = null;
        Wmi("SELECT Caption FROM Win32_OperatingSystem", o => caption ??= o["Caption"] as string, HardwarePart.OsProductName, failures);

        var processors = new List<ProcessorReading>();
        if (!Wmi("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor",
            o => processors.Add(new(o["Name"] as string, o["Manufacturer"] as string, Int(o["NumberOfCores"]), Int(o["NumberOfLogicalProcessors"]))),
            HardwarePart.Processors, failures, SystemInventory.MaxItems + 1))
        {
            processors = null;
        }

        string? manufacturer = null, model = null;
        Wmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem", o =>
        {
            manufacturer ??= o["Manufacturer"] as string;
            model ??= o["Model"] as string;
        }, HardwarePart.MachineManufacturer, failures);
        if (failures.TryGetValue(HardwarePart.MachineManufacturer, out var machineFailure)) failures[HardwarePart.MachineModel] = machineFailure;

        int? ubr = null;
        string? displayVersion = null;
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

    public TemperatureReading ReadTemperatures()
    {
        var adapters = Adapters();
        var gpus = adapters.Reason is { } r
            ? Observed.Missing<IReadOnlyList<GpuTemperatureReading>>(r)
            : Observed.Ok<IReadOnlyList<GpuTemperatureReading>>(adapters.Value!.Where(a => !a.Software).Select(GpuTemperature).ToList());
        var gpusAt = DateTimeOffset.UtcNow;

        using var query = PdhQuery.Open(out var openStatus);
        var hp = query?.Add(ZoneHighPrecisionCounter) ?? (IntPtr.Zero, openStatus);
        var k = query?.Add(ZoneCounter) ?? (IntPtr.Zero, openStatus);
        var collected = query?.Collect() ?? openStatus;
        var zonesAt = DateTimeOffset.UtcNow;
        var hpItems = Array(query, hp, collected, MaxZoneInstances, PdhFmtDouble | PdhFmtNoCap100);
        var kItems = Array(query, k, collected, MaxZoneInstances, PdhFmtDouble | PdhFmtNoCap100);

        Observed<IReadOnlyList<ThermalZoneReading>> zones;
        if (hpItems.Reason is not null && kItems.Reason is not null)
        {
            zones = Observed.Missing<IReadOnlyList<ThermalZoneReading>>(kItems.Reason.Value);
        }
        else
        {
            var hpByName = (hpItems.Items ?? []).GroupBy(i => i.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            var kByName = (kItems.Items ?? []).GroupBy(i => i.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            zones = Observed.Ok<IReadOnlyList<ThermalZoneReading>>(hpByName.Keys.Union(kByName.Keys, StringComparer.Ordinal)
                .Select(n => new ThermalZoneReading(n, hpByName.GetValueOrDefault(n), kByName.GetValueOrDefault(n)))
                .ToList());
        }

        return new TemperatureReading(gpusAt, gpus, zonesAt, zones);
    }

    public StorageReading ReadStorage()
    {
        var drives = new List<DriveReading>();
        var excluded = 0;
        // Never show the "insert a disk" / critical error dialog for this thread; restored before returning.
        var restore = SetThreadErrorMode(SemFailCriticalErrors | SemNoOpenFileErrorBox, out var oldMode);
        try
        {
            var mask = GetLogicalDrives();
            if (mask == 0)
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "The drive list could not be read.", retryable: true);
            }

            for (var i = 0; i < 26; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                var letter = (char)('A' + i);
                var root = $"{letter}:\\";
                // The type comes first: network, removable and optical drives are never queried further.
                if (GetDriveTypeW(root) != DriveFixed)
                {
                    excluded++;
                    continue;
                }

                // A SUBST drive is a folder of another volume (\??\ target): counting it would duplicate that volume.
                var target = new char[1024];
                if (QueryDosDeviceW($"{letter}:", target, (uint)target.Length) == 0)
                {
                    drives.Add(new(letter, null, UnavailableReason.Unavailable));
                    continue;
                }

                if (new string(target, 0, 4) == @"\??\")
                {
                    excluded++;
                    continue;
                }

                var hr = GetDiskSpaceInformationW(root, out var info);
                drives.Add(hr < 0
                    ? new(letter, null, hr == unchecked((int)0x80070005) ? UnavailableReason.PermissionDenied : UnavailableReason.Unavailable)
                    : new(letter, new DiskSpaceUnits(info.ActualTotalAllocationUnits, info.ActualAvailableAllocationUnits, info.CallerAvailableAllocationUnits,
                        info.SectorsPerAllocationUnit, info.BytesPerSector), null));
            }
        }
        finally
        {
            if (restore) SetThreadErrorMode(oldMode, out _);
        }

        return new StorageReading(DateTimeOffset.UtcNow, drives, excluded);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>DXGI adapters (EnumAdapters1 / GetDesc1). No device is created; every COM object is released.</summary>
    internal static Observed<IReadOnlyList<GpuAdapterReading>> Adapters()
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

    /// <summary>D3DKMT_ADAPTER_PERFDATA of physical adapter 0: open from LUID, one query, close.</summary>
    internal static GpuTemperatureReading GpuTemperature(GpuAdapterReading adapter)
    {
        var open = new D3dkmtOpenAdapterFromLuid { AdapterLuidLowPart = (uint)adapter.Luid, AdapterLuidHighPart = (int)(uint)(adapter.Luid >> 32) };
        var status = D3DKMTOpenAdapterFromLuid(ref open);
        if (status != 0) return new(adapter.Luid, adapter.Description, null, NtReason(status));

        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<D3dkmtAdapterPerfData>());
        try
        {
            Marshal.StructureToPtr(new D3dkmtAdapterPerfData { PhysicalAdapterIndex = 0 }, buffer, false);
            var query = new D3dkmtQueryAdapterInfo
            {
                Adapter = open.Adapter,
                Type = KmtqaitypeAdapterPerfData,
                PrivateDriverData = buffer,
                PrivateDriverDataSize = (uint)Marshal.SizeOf<D3dkmtAdapterPerfData>(),
            };
            status = D3DKMTQueryAdapterInfo(ref query);
            if (status != 0) return new(adapter.Luid, adapter.Description, null, NtReason(status));
            return new(adapter.Luid, adapter.Description, Marshal.PtrToStructure<D3dkmtAdapterPerfData>(buffer).Temperature, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            var close = new D3dkmtCloseAdapter { Adapter = open.Adapter };
            _ = D3DKMTCloseAdapter(ref close);
        }
    }

    private static UnavailableReason NtReason(int status) => status switch
    {
        StatusAccessDenied => UnavailableReason.PermissionDenied,
        StatusInvalidParameter or StatusNotSupported or StatusNotImplemented => UnavailableReason.NotSupported,
        _ => UnavailableReason.Unavailable,
    };

    private static Observed<IReadOnlyList<GpuMemorySample>> GpuMemory(PdhQuery? query, (IntPtr Counter, uint Status) dedicated, (IntPtr Counter, uint Status) shared, uint collected)
    {
        var d = Array(query, dedicated, collected, MaxMemoryInstances, PdhFmtLarge);
        var s = Array(query, shared, collected, MaxMemoryInstances, PdhFmtLarge);
        if (d.Reason is { } dr) return Observed.Missing<IReadOnlyList<GpuMemorySample>>(dr);
        if (s.Reason is { } sr) return Observed.Missing<IReadOnlyList<GpuMemorySample>>(sr);
        var dByName = d.Items!.GroupBy(i => i.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count() == 1 ? g.First().Large : null, StringComparer.Ordinal);
        var sByName = s.Items!.GroupBy(i => i.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count() == 1 ? g.First().Large : null, StringComparer.Ordinal);
        return Observed.Ok<IReadOnlyList<GpuMemorySample>>(dByName.Keys.Union(sByName.Keys, StringComparer.Ordinal)
            .Select(n => new GpuMemorySample(n, dByName.GetValueOrDefault(n), sByName.GetValueOrDefault(n)))
            .ToList());
    }

    private sealed record PdhItem(string Name, double? Value, long? Large);

    private sealed record PdhArray(IReadOnlyList<PdhItem>? Items, UnavailableReason? Reason);

    /// <summary>Formatted values of a wildcard counter, or why they are unusable.</summary>
    private static PdhArray Array(PdhQuery? query, (IntPtr Counter, uint Status) counter, uint collected, int maxItems, uint format)
    {
        if (query is null || counter.Status != 0) return new(null, Reason(counter.Status));
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
                var itemSize = IntPtr.Size + Marshal.SizeOf<PdhFmtCounterValue>();
                for (var i = 0; i < count; i++)
                {
                    var item = buffer + (i * itemSize);
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    var value = Marshal.PtrToStructure<PdhFmtCounterValue>(item + IntPtr.Size);
                    var valid = IsValid(value.CStatus);
                    items.Add(new(name, valid && (format & PdhFmtDouble) != 0 ? value.DoubleValue : null, valid && (format & PdhFmtLarge) != 0 ? value.LargeValue : null));
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

    private static bool IsValid(uint cstatus) => cstatus is PdhCstatusValidData or PdhCstatusNewData;

    private static UnavailableReason Reason(uint pdhStatus) => pdhStatus switch
    {
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

    /// <summary>One fixed query against local root\cimv2. Returns false (and records why) when it fails.</summary>
    private static bool Wmi(string query, Action<ManagementBaseObject> read, HardwarePart part, Dictionary<HardwarePart, UnavailableReason> failures, int max = 1)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery(query),
                new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = true, Rewindable = false });
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
        catch (UnauthorizedAccessException)
        {
            failures[part] = UnavailableReason.PermissionDenied;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException)
        {
            failures[part] = UnavailableReason.Unavailable;
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
