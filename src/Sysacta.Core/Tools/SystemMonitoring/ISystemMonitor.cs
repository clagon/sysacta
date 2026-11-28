namespace Sysacta.Core.Tools.SystemMonitoring;

/// <summary>Why a value is missing. Missing values are null in results, never 0 or an empty string.</summary>
public enum UnavailableReason
{
    /// <summary>This environment or this build has no supported way to read it (no API, driver does not report it).</summary>
    NotSupported,
    /// <summary>The OS refused access with the current (normal user) rights.</summary>
    PermissionDenied,
    /// <summary>The read failed or produced no usable value this time (temporary failure, not ready, no data yet).</summary>
    Unavailable,
    /// <summary>The OS returned a value that failed validation (out of range, inconsistent, overflow, unrecognized format).</summary>
    InvalidData,
    /// <summary>The system slept or hibernated during the sampling window, so the sample is discarded.</summary>
    Interrupted,
    /// <summary>More items than the fixed internal limit; an aggregate over a partial set is not reported.</summary>
    Truncated,
    /// <summary>
    /// The source did not answer within its time budget, or an earlier read of it is still hung; a hung source is not
    /// opened again until that read ends.
    /// </summary>
    Timeout,
}

/// <summary>A value read from the OS, or the reason it is missing.</summary>
public sealed record Observed<T>(T? Value, UnavailableReason? Reason)
    where T : class;

public static class Observed
{
    public static Observed<T> Ok<T>(T value) where T : class => new(value, null);

    public static Observed<T> Missing<T>(UnavailableReason reason) where T : class => new(null, reason);
}

/// <summary>One DXGI adapter (IDXGIAdapter1::GetDesc1). <paramref name="Description"/> is raw OS text.</summary>
/// <param name="Luid">AdapterLuid as (HighPart &lt;&lt; 32) | LowPart. Valid until restart or driver reset.</param>
public sealed record GpuAdapterReading(
    ulong Luid,
    string? Description,
    uint VendorId,
    uint DeviceId,
    ulong DedicatedVideoMemory,
    ulong DedicatedSystemMemory,
    ulong SharedSystemMemory,
    bool Software);

/// <summary>One instance of a wildcard performance counter. <paramref name="Value"/> is null when PDH reported no valid data for it.</summary>
public sealed record CounterSample(string Instance, double? Value);

/// <summary>One <c>GPU Adapter Memory</c> instance (bytes; null when PDH reported no valid data).</summary>
public sealed record GpuMemorySample(string Instance, long? DedicatedUsage, long? SharedUsage);

/// <summary>
/// Raw result of one sampling window for <c>system_get_status</c>. Rate counters (CPU, GPU engines) are the average over
/// the window; memory, GPU memory and uptime are instantaneous values read once after it.
/// </summary>
/// <param name="ElapsedMs">GetTickCount64 difference over the window (includes time spent in sleep/hibernation).</param>
/// <param name="WorkingMs">QueryUnbiasedInterruptTime difference over the window (excludes sleep/hibernation).</param>
/// <param name="CpuPercent">Formatted <c>\Processor Information(_Total)\% Processor Time</c> without the 100 cap.</param>
public sealed record StatusReading(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long ElapsedMs,
    long WorkingMs,
    double? CpuPercent,
    UnavailableReason? CpuReason,
    int? LogicalProcessors,
    ulong? MemoryTotalBytes,
    ulong? MemoryAvailableBytes,
    ulong? UptimeMs,
    Observed<IReadOnlyList<GpuAdapterReading>> Adapters,
    Observed<IReadOnlyList<CounterSample>> Engines,
    Observed<IReadOnlyList<GpuMemorySample>> GpuMemory);

/// <summary>One Win32_Processor instance (one per socket). Strings are raw OS text.</summary>
public sealed record ProcessorReading(string? Name, string? Manufacturer, int? Cores, int? LogicalProcessors);

/// <summary>A part of <c>system_get_hardware</c> that can be missing on its own.</summary>
public enum HardwarePart
{
    OsProductName,
    OsVersion,
    OsUpdateBuildRevision,
    OsDisplayVersion,
    OsArchitecture,
    Processors,
    ProcessorTopology,
    InstalledMemory,
    OsUsableMemory,
    Gpus,
    MachineManufacturer,
    MachineModel,
}

/// <summary>Raw configuration data. Null fields are missing; <paramref name="Failures"/> says why (otherwise treated as unavailable).</summary>
public sealed record HardwareReading(
    DateTimeOffset MeasuredAt,
    string? OsCaption,
    (int Major, int Minor, int Build)? OsVersion,
    int? OsUpdateBuildRevision,
    string? OsDisplayVersion,
    string? OsArchitecture,
    IReadOnlyList<ProcessorReading>? Processors,
    int? ProcessorPackages,
    int? PhysicalCores,
    int? LogicalProcessors,
    ulong? InstalledMemoryKilobytes,
    ulong? OsUsableMemoryBytes,
    IReadOnlyList<GpuAdapterReading>? Gpus,
    string? MachineManufacturer,
    string? MachineModel,
    IReadOnlyDictionary<HardwarePart, UnavailableReason> Failures);

/// <summary>
/// D3DKMT_ADAPTER_PERFDATA.Temperature of one physical adapter (deci-Celsius, 1 = 0.1 °C), or why it could not be read.
/// </summary>
/// <param name="PhysicalIndex">Physical adapter of a linked adapter (0 when not linked).</param>
public sealed record GpuTemperatureReading(ulong Luid, int PhysicalIndex, string? Description, uint? DeciCelsius, UnavailableReason? Reason);

/// <summary>One <c>Thermal Zone Information</c> instance. Null values: PDH reported no valid data for that counter.</summary>
/// <param name="HighPrecisionTenthsKelvin"><c>High Precision Temperature</c> (tenths of a kelvin).</param>
/// <param name="Kelvin"><c>Temperature</c> (kelvin).</param>
public sealed record ThermalZoneReading(string Instance, double? HighPrecisionTenthsKelvin, double? Kelvin);

public sealed record TemperatureReading(
    DateTimeOffset GpusMeasuredAt,
    Observed<IReadOnlyList<GpuTemperatureReading>> Gpus,
    DateTimeOffset ZonesMeasuredAt,
    Observed<IReadOnlyList<ThermalZoneReading>> Zones);

/// <summary>GetDiskSpaceInformationW in allocation units (DISK_SPACE_INFORMATION).</summary>
public sealed record DiskSpaceUnits(ulong ActualTotal, ulong ActualAvailable, ulong CallerAvailable, uint SectorsPerAllocationUnit, uint BytesPerSector);

/// <summary>A local fixed drive with a drive letter, or why its capacity could not be read.</summary>
public sealed record DriveReading(char Letter, DiskSpaceUnits? Space, UnavailableReason? Reason);

/// <param name="Fixed">Drive letters whose type is fixed and that map to a volume device (not SUBST), in letter order.</param>
/// <param name="ExcludedCount">Drive letters skipped by type (removable, network, optical, RAM disk, unknown) or because they are SUBST folders.</param>
public sealed record StorageReading(DateTimeOffset MeasuredAt, IReadOnlyList<DriveReading> Fixed, int ExcludedCount);

/// <summary>
/// Read-only OS adapter for system monitoring. Methods are synchronous and run on a thread-pool thread. Every query,
/// counter path and field is fixed in the implementation; nothing here takes a path, query or counter from a caller, and
/// nothing loads drivers, starts services, changes settings or generates load.
/// </summary>
public interface ISystemMonitor
{
    /// <summary>
    /// Collects the rate counters twice, <paramref name="interval"/> apart. Returns early by throwing
    /// <see cref="OperationCanceledException"/> when cancelled during the wait, without a second collection.
    /// </summary>
    StatusReading SampleStatus(TimeSpan interval, CancellationToken cancellationToken);

    HardwareReading ReadHardware();

    TemperatureReading ReadTemperatures();

    /// <summary>Throws <see cref="ToolFailureException"/> only when the drive list itself cannot be read.</summary>
    StorageReading ReadStorage();
}
