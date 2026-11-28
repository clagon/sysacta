using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Sysacta.Core.Tools.SystemMonitoring;

// ---- system_get_hardware -----------------------------------------------------------------------------------------

public sealed record OsInfo(
    [property: Description("Win32_OperatingSystem Caption, for example \"Microsoft Windows 11 Pro\" (untrusted text).")] string? ProductName,
    [property: Description("major.minor.build, for example \"10.0.26100\".")] string? Version,
    int? BuildNumber,
    [property: Description("Update build revision (the number after the build, for example 2605 in 26100.2605).")] int? UpdateBuildRevision,
    [property: Description("Feature update name, for example \"24H2\".")] string? DisplayVersion,
    [property: Description("OS architecture: x64, arm64, x86 or arm.")] string? Architecture);

public sealed record ProcessorInfo(
    [property: Description("Model name (untrusted text).")] string? Name,
    string? Manufacturer,
    [property: Description("Physical cores of this processor as reported by Win32_Processor.")] int? PhysicalCores,
    int? LogicalProcessors);

public sealed record ProcessorTopology(
    [property: Description("Processor packages (sockets), GetLogicalProcessorInformationEx.")] int? Packages,
    [property: Description("Physical cores in all packages, GetLogicalProcessorInformationEx (counted, never derived from logical processors).")] int? PhysicalCores,
    [property: Description("Active logical processors in all processor groups.")] int? LogicalProcessors);

public sealed record MemoryInfo(
    [property: Description("Physically installed RAM from the SMBIOS tables (GetPhysicallyInstalledSystemMemory), bytes.")] long? InstalledBytes,
    [property: Description("Physical memory the OS can use (GlobalMemoryStatusEx ullTotalPhys), bytes; less than installed because of hardware reservations.")] long? OsUsableBytes);

public sealed record GpuInfo(
    [property: Description("Adapter LUID, same as gpu_id in system_get_status and device_id in system_get_temperatures; valid until restart or driver reset.")] string GpuId,
    [property: Description("Untrusted adapter name from DXGI; null when not displayable.")] string? Name,
    GpuAdapterKind AdapterType,
    [property: Description("PCI vendor id, 4 hex digits (for example 10de, 1002, 8086).")] string VendorId,
    [property: Description("PCI device id, 4 hex digits.")] string DeviceId,
    [property: Description("DXGI DedicatedVideoMemory (64-bit), bytes. On an integrated GPU this is only the reserved carve-out.")] long? DedicatedVideoMemoryBytes,
    [property: Description("DXGI DedicatedSystemMemory, bytes.")] long? DedicatedSystemMemoryBytes,
    [property: Description("DXGI SharedSystemMemory: the most system memory the adapter may use, bytes.")] long? SharedSystemMemoryBytes);

public sealed record MachineInfo(
    [property: Description("Win32_ComputerSystem Manufacturer (untrusted text).")] string? Manufacturer,
    [property: Description("Win32_ComputerSystem Model (untrusted text).")] string? Model);

public sealed record HardwareGap(HardwarePart Item, UnavailableReason Reason);

public sealed record SystemHardwareData(
    [property: Description("UTC time when the configuration was read.")] DateTimeOffset MeasuredAt,
    OsInfo Os,
    IReadOnlyList<ProcessorInfo> Processors,
    bool ProcessorsTruncated,
    ProcessorTopology ProcessorTopology,
    MemoryInfo Memory,
    IReadOnlyList<GpuInfo> Gpus,
    bool GpusTruncated,
    MachineInfo Machine,
    [property: Description("Items that could not be read; their fields are null (an empty list in processors/gpus means the item is listed here, or none exist).")] IReadOnlyList<HardwareGap> Unavailable);

// ---- system_get_temperatures ---------------------------------------------------------------------------------------

public enum TemperatureDeviceType { Cpu, Gpu, Storage, AcpiThermalZone }

public enum TemperatureSensorType
{
    /// <summary>The GPU driver's main temperature reading (D3DKMT_ADAPTER_PERFDATA). Not specified as core, hotspot or memory.</summary>
    GpuTemperature,
    /// <summary>A firmware-defined ACPI thermal zone. It is not a CPU temperature.</summary>
    AcpiThermalZone,
    CpuPackage,
    CpuCore,
    GpuHotspot,
    GpuMemoryJunction,
    StorageDrive,
}

public enum TemperatureSource { D3dkmtAdapterPerfdata, PdhThermalZone }

public sealed record TemperatureSensor(
    [property: Description("Sensor id, unique in this response; null when the OS name could not be validated.")] string? SensorId,
    TemperatureDeviceType DeviceType,
    [property: Description("gpu_id of the adapter for GPU sensors; null for thermal zones.")] string? DeviceId,
    TemperatureSensorType SensorType,
    [property: Description("Untrusted display label (adapter name or ACPI zone path).")] string? Label,
    [property: Description("Degrees Celsius (GPU: 0.1 °C steps; thermal zones: converted from 0.1 K or 1 K steps). null when not reported.")] double? TemperatureC,
    TemperatureSource Source,
    [property: Description("UTC time of the read.")] DateTimeOffset MeasuredAt,
    UnavailableReason? Reason);

public sealed record TemperatureSourceFailure(TemperatureSource Source, UnavailableReason Reason);

public sealed record UncollectedSensor(TemperatureSensorType SensorType, UnavailableReason Reason, string Detail);

public sealed record SystemTemperaturesData(
    [property: Description("UTC time when the last source was read.")] DateTimeOffset MeasuredAt,
    IReadOnlyList<TemperatureSensor> Sensors,
    bool SensorsTruncated,
    [property: Description("Sources that could not be read at all.")] IReadOnlyList<TemperatureSourceFailure> SourceFailures,
    [property: Description("Sensor kinds this tool cannot read on any machine without a kernel driver or vendor tools; never estimated.")] IReadOnlyList<UncollectedSensor> NotCollected);

// ---- system_get_storage --------------------------------------------------------------------------------------------

public sealed record StorageVolume(
    [property: Description("Volume id for this response (drive:<letter>). A drive letter is a selection label, not a persistent physical disk id.")] string VolumeId,
    [property: Description("Drive letter with colon, for example \"C:\".")] string DriveLetter,
    [property: Description("Always fixed (local fixed disk with a drive letter).")] string DriveType,
    [property: Description("Logical volume size ignoring quotas, bytes (not the physical disk size).")] long? TotalBytes,
    [property: Description("Free space on the volume ignoring quotas, bytes.")] long? FreeBytes,
    [property: Description("Free space the current user can use (per-user quotas applied), bytes.")] long? AvailableBytes,
    [property: Description("total_bytes - free_bytes.")] long? UsedBytes,
    [property: Description("used_bytes / total_bytes * 100, 0-100.")] double? UsagePercent,
    UnavailableReason? Reason);

public sealed record SystemStorageData(
    [property: Description("UTC time when the volumes were read.")] DateTimeOffset MeasuredAt,
    IReadOnlyList<StorageVolume> Volumes,
    [property: Description("Fixed drives whose capacity could not be read.")] int FailedCount,
    [property: Description("Drive letters left out: removable, network, optical, RAM disk, unknown, or SUBST folders.")] int ExcludedCount);

/// <summary>Interpretation of hardware, temperature and storage readings. OS independent; unit tested.</summary>
public static partial class SystemInventory
{
    public const int MaxItems = 64;
    public const int MaxSensors = 256;

    public static SystemHardwareData Hardware(HardwareReading r)
    {
        var gaps = new Dictionary<HardwarePart, UnavailableReason>(r.Failures);

        string? Text(string? raw, HardwarePart part, int max = 128)
        {
            var value = UntrustedText.Sanitize(raw, max);
            if (value is null) gaps.TryAdd(part, raw is null ? UnavailableReason.Unavailable : UnavailableReason.InvalidData);
            return value;
        }

        T? Need<T>(T? value, HardwarePart part) where T : struct
        {
            if (value is null) gaps.TryAdd(part, UnavailableReason.Unavailable);
            return value;
        }

        var version = Need(r.OsVersion, HardwarePart.OsVersion);
        var displayVersion = r.OsDisplayVersion is { } dv && DisplayVersionText().IsMatch(dv) ? dv : null;
        if (displayVersion is null) gaps.TryAdd(HardwarePart.OsDisplayVersion, r.OsDisplayVersion is null ? UnavailableReason.Unavailable : UnavailableReason.InvalidData);
        var architecture = r.OsArchitecture is "x64" or "arm64" or "x86" or "arm" ? r.OsArchitecture : null;
        if (architecture is null) gaps.TryAdd(HardwarePart.OsArchitecture, r.OsArchitecture is null ? UnavailableReason.Unavailable : UnavailableReason.InvalidData);
        var os = new OsInfo(
            Text(r.OsCaption, HardwarePart.OsProductName),
            version is { } v ? string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{v.Build}") : null,
            version?.Build,
            Need(r.OsUpdateBuildRevision is >= 0 ? r.OsUpdateBuildRevision : null, HardwarePart.OsUpdateBuildRevision),
            displayVersion,
            architecture);

        if (r.Processors is null) gaps.TryAdd(HardwarePart.Processors, UnavailableReason.Unavailable);
        var processors = (r.Processors ?? []).Take(MaxItems)
            .Select(p => new ProcessorInfo(UntrustedText.Sanitize(p.Name, 128), UntrustedText.Sanitize(p.Manufacturer, 64), Positive(p.Cores), Positive(p.LogicalProcessors)))
            .ToList();

        var topology = new ProcessorTopology(Positive(r.ProcessorPackages), Positive(r.PhysicalCores), Positive(r.LogicalProcessors));
        if (topology.Packages is null || topology.PhysicalCores is null || topology.LogicalProcessors is null
            || topology.PhysicalCores > topology.LogicalProcessors || topology.Packages > topology.PhysicalCores)
        {
            gaps.TryAdd(HardwarePart.ProcessorTopology, topology.Packages is null || topology.PhysicalCores is null || topology.LogicalProcessors is null
                ? UnavailableReason.Unavailable : UnavailableReason.InvalidData);
            if (topology.PhysicalCores > topology.LogicalProcessors || topology.Packages > topology.PhysicalCores) topology = new(null, null, topology.LogicalProcessors);
        }

        long? installed = null;
        if (r.InstalledMemoryKilobytes is { } kb)
        {
            installed = kb <= long.MaxValue / 1024 ? (long)kb * 1024 : null;
            if (installed is null) gaps.TryAdd(HardwarePart.InstalledMemory, UnavailableReason.InvalidData);
        }
        else
        {
            gaps.TryAdd(HardwarePart.InstalledMemory, UnavailableReason.Unavailable);
        }

        var usable = r.OsUsableMemoryBytes is { } u ? SystemStatus.Bytes(u) : null;
        if (usable is null) gaps.TryAdd(HardwarePart.OsUsableMemory, r.OsUsableMemoryBytes is null ? UnavailableReason.Unavailable : UnavailableReason.InvalidData);

        if (r.Gpus is null) gaps.TryAdd(HardwarePart.Gpus, UnavailableReason.Unavailable);
        var gpus = (r.Gpus ?? []).OrderBy(g => g.Luid).Take(MaxItems).Select(g => new GpuInfo(
            SystemStatus.GpuId(g.Luid),
            SystemStatus.AdapterName(g.Description),
            g.Software ? GpuAdapterKind.Software : GpuAdapterKind.Hardware,
            Hex4(g.VendorId),
            Hex4(g.DeviceId),
            SystemStatus.Bytes(g.DedicatedVideoMemory),
            SystemStatus.Bytes(g.DedicatedSystemMemory),
            SystemStatus.Bytes(g.SharedSystemMemory))).ToList();

        var machine = new MachineInfo(Text(r.MachineManufacturer, HardwarePart.MachineManufacturer, 64), Text(r.MachineModel, HardwarePart.MachineModel, 64));

        return new(r.MeasuredAt, os, processors, (r.Processors?.Count ?? 0) > MaxItems, topology, new MemoryInfo(installed, usable),
            gpus, (r.Gpus?.Count ?? 0) > MaxItems, machine,
            gaps.OrderBy(g => g.Key).Select(g => new HardwareGap(g.Key, g.Value)).ToList());
    }

    /// <summary>Sensor kinds no reading path in this build covers (documented in docs/phase-1f-progress.md).</summary>
    public static readonly IReadOnlyList<UncollectedSensor> NotCollected =
    [
        new(TemperatureSensorType.CpuPackage, UnavailableReason.NotSupported, "No documented Windows API exposes it to a normal user without a kernel driver."),
        new(TemperatureSensorType.CpuCore, UnavailableReason.NotSupported, "No documented Windows API exposes it to a normal user without a kernel driver."),
        new(TemperatureSensorType.GpuHotspot, UnavailableReason.NotSupported, "Not exposed by the WDDM adapter performance data."),
        new(TemperatureSensorType.GpuMemoryJunction, UnavailableReason.NotSupported, "Not exposed by the WDDM adapter performance data."),
        new(TemperatureSensorType.StorageDrive, UnavailableReason.NotSupported, "Not read by this version."),
    ];

    public static SystemTemperaturesData Temperatures(TemperatureReading r)
    {
        var sensors = new List<TemperatureSensor>();
        var failures = new List<TemperatureSourceFailure>();

        if (r.Gpus.Reason is { } gr)
        {
            failures.Add(new(TemperatureSource.D3dkmtAdapterPerfdata, gr));
        }
        else
        {
            foreach (var g in r.Gpus.Value!.OrderBy(g => g.Luid))
            {
                double? celsius = null;
                var reason = g.Reason;
                if (reason is null)
                {
                    // The structure reports 0 when the driver has no reading (Task Manager then shows no temperature).
                    // A real 0.0 °C cannot be told apart from that and is also reported as not supported.
                    if (g.DeciCelsius is null || g.DeciCelsius == 0) reason = UnavailableReason.NotSupported;
                    else if (g.DeciCelsius > int.MaxValue) reason = UnavailableReason.InvalidData;
                    else celsius = Math.Round(g.DeciCelsius.Value / 10.0, 1);
                }

                var id = SystemStatus.GpuId(g.Luid);
                sensors.Add(new($"{id}:temperature", TemperatureDeviceType.Gpu, id, TemperatureSensorType.GpuTemperature,
                    SystemStatus.AdapterName(g.Description), celsius, TemperatureSource.D3dkmtAdapterPerfdata, r.GpusMeasuredAt, reason));
            }
        }

        if (r.Zones.Reason is { } zr)
        {
            failures.Add(new(TemperatureSource.PdhThermalZone, zr));
        }
        else
        {
            foreach (var z in r.Zones.Value!.OrderBy(z => z.Instance, StringComparer.Ordinal))
            {
                var m = z.Instance.Length <= 64 ? ZoneInstance().Match(z.Instance) : Match.Empty;
                var (celsius, reason) = ZoneCelsius(z);
                sensors.Add(new(m.Success ? $"acpi_thermal_zone:{m.Groups[1].Value}{m.Groups[2].Value}" : null, TemperatureDeviceType.AcpiThermalZone, null,
                    TemperatureSensorType.AcpiThermalZone, m.Success ? m.Groups[1].Value : null, celsius, TemperatureSource.PdhThermalZone, r.ZonesMeasuredAt, reason));
            }
        }

        var measuredAt = r.GpusMeasuredAt > r.ZonesMeasuredAt ? r.GpusMeasuredAt : r.ZonesMeasuredAt;
        return new(measuredAt, sensors.Take(MaxSensors).ToList(), sensors.Count > MaxSensors, failures, NotCollected);
    }

    /// <summary>Kelvin to Celsius. High Precision Temperature (0.1 K) is preferred; 0 K or less, or a non-finite value, is invalid.</summary>
    public static (double? Celsius, UnavailableReason? Reason) ZoneCelsius(ThermalZoneReading z)
    {
        // Rounded to 0.01 °C: kelvin values minus 273.15 always end in 5 at the hundredths, so rounding to 0.1 would move them.
        if (z.HighPrecisionTenthsKelvin is { } hp && double.IsFinite(hp) && hp > 0) return (Math.Round(hp / 10.0 - 273.15, 2), null);
        if (z.Kelvin is { } k && double.IsFinite(k) && k > 0) return (Math.Round(k - 273.15, 2), null);
        return z.HighPrecisionTenthsKelvin is null && z.Kelvin is null ? (null, UnavailableReason.Unavailable) : (null, UnavailableReason.InvalidData);
    }

    public static SystemStorageData Storage(StorageReading r)
    {
        var volumes = r.Fixed.Where(d => d.Letter is >= 'A' and <= 'Z').OrderBy(d => d.Letter).Take(26).Select(Volume).ToList();
        return new(r.MeasuredAt, volumes, volumes.Count(v => v.Reason is not null), r.ExcludedCount);
    }

    private static StorageVolume Volume(DriveReading d)
    {
        var id = $"drive:{d.Letter}";
        var letter = $"{d.Letter}:";
        StorageVolume Missing(UnavailableReason reason) => new(id, letter, "fixed", null, null, null, null, null, reason);

        if (d.Space is not { } s) return Missing(d.Reason ?? UnavailableReason.Unavailable);
        try
        {
            var unit = checked((ulong)s.SectorsPerAllocationUnit * s.BytesPerSector);
            var total = checked(s.ActualTotal * unit);
            var free = checked(s.ActualAvailable * unit);
            var available = checked(s.CallerAvailable * unit);
            if (unit == 0 || total == 0 || total > long.MaxValue || free > total || available > total) return Missing(UnavailableReason.InvalidData);
            var used = total - free;
            return new(id, letter, "fixed", (long)total, (long)free, (long)available, (long)used, Math.Round(used * 100.0 / total, 1), null);
        }
        catch (OverflowException)
        {
            return Missing(UnavailableReason.InvalidData);
        }
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;

    private static string Hex4(uint value) => value.ToString("x4", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[0-9]{2}H[12]$", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayVersionText();

    // An ACPI namespace path such as \_TZ.TZ00 (4-character segments), optionally with PDH's "#n" duplicate suffix.
    [GeneratedRegex(@"^\\((?:[A-Za-z0-9_]{1,4})(?:\.[A-Za-z0-9_]{1,4}){0,7})(#\d{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneInstance();
}
