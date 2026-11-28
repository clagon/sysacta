using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Sysacta.Core.Tools.SystemMonitoring;

public enum GpuAdapterKind
{
    /// <summary>A hardware adapter listed by DXGI.</summary>
    Hardware,
    /// <summary>A software adapter (for example Microsoft Basic Render Driver).</summary>
    Software,
    /// <summary>Seen only in the GPU performance counters; DXGI did not list this LUID, so nothing else is known.</summary>
    Unknown,
}

public sealed record CpuStatus(
    [property: Description("Average % Processor Time of all logical processors in all processor groups over the sampling window, 0-100 (not frequency-scaled % Processor Utility, which Task Manager shows). null when unavailable.")] double? UsagePercent,
    UnavailableReason? Reason,
    [property: Description("Active logical processors in all processor groups.")] int? LogicalProcessors);

public sealed record MemoryStatus(
    [property: Description("Physical memory the OS can use (GlobalMemoryStatusEx ullTotalPhys), bytes. Not the installed RAM, commit or page file.")] long? TotalBytes,
    [property: Description("Physical memory available to processes without paging (ullAvailPhys), bytes.")] long? AvailableBytes,
    [property: Description("total_bytes - available_bytes.")] long? UsedBytes,
    [property: Description("used_bytes / total_bytes * 100, 0-100.")] double? UsagePercent,
    UnavailableReason? Reason);

public sealed record GpuEngineStatus(
    int PhysicalIndex,
    int EngineIndex,
    [property: Description("Engine type reported by the driver (for example 3D, Copy, VideoDecode); untrusted text, null when not a plain name.")] string? EngineType,
    [property: Description("Sum of all process instances of this engine over the window, 0-100.")] double? UtilizationPercent,
    UnavailableReason? Reason);

public sealed record GpuStatus(
    [property: Description("Adapter LUID (luid_0x<high>_0x<low>). Matches gpu_id/device_id of the other system tools; valid until restart or driver reset, not a persistent id.")] string GpuId,
    [property: Description("Untrusted adapter name from DXGI; null when not matched or not displayable.")] string? Name,
    GpuAdapterKind AdapterType,
    [property: Description("Busiest engine's utilization over the window (Task Manager's per-GPU figure), 0-100. null when any engine could not be measured.")] double? UtilizationPercent,
    UnavailableReason? UtilizationReason,
    string? BusiestEngineType,
    IReadOnlyList<GpuEngineStatus> Engines,
    bool EnginesTruncated,
    [property: Description("Process instances left out of the engine sums because they first appeared during the window (no rate yet). Processes that ended during the window are not visible and not counted either, so values can be slightly low.")] int ExcludedNewInstances,
    [property: Description("Dedicated GPU memory in use by all processes (GPU Adapter Memory\\Dedicated Usage), bytes.")] long? DedicatedMemoryUsedBytes,
    [property: Description("Shared system memory in use by the GPU (GPU Adapter Memory\\Shared Usage), bytes.")] long? SharedMemoryUsedBytes,
    UnavailableReason? MemoryUsageReason,
    [property: Description("DXGI DedicatedVideoMemory, bytes (for an integrated GPU, the small reserved carve-out). null when DXGI did not list the adapter.")] long? DedicatedMemoryTotalBytes,
    [property: Description("DXGI SharedSystemMemory: the most system memory the adapter may use, bytes.")] long? SharedMemoryLimitBytes);

public sealed record SystemStatusData(
    [property: Description("UTC time when memory and uptime were read (end of the sampling window).")] DateTimeOffset MeasuredAt,
    [property: Description("UTC start of the CPU/GPU sampling window.")] DateTimeOffset SamplingStartedAt,
    [property: Description("UTC end of the CPU/GPU sampling window.")] DateTimeOffset SamplingEndedAt,
    [property: Description("Measured length of the sampling window in milliseconds (working time, excluding sleep).")] long SamplingMs,
    CpuStatus Cpu,
    MemoryStatus Memory,
    [property: Description("Seconds since Windows started (GetTickCount64; includes sleep and hibernation, and continues across Fast Startup shutdowns).")] long? UptimeSeconds,
    UnavailableReason? UptimeReason,
    IReadOnlyList<GpuStatus> Gpus,
    [property: Description("Adapters seen in DXGI or the GPU counters.")] int GpuTotalCount,
    bool GpusTruncated,
    [property: Description("Set when no GPU information at all could be read (gpus is then empty).")] UnavailableReason? GpusReason);

/// <summary>Interpretation of a <see cref="StatusReading"/>. OS independent; every rule here is unit tested.</summary>
public static partial class SystemStatus
{
    public static readonly TimeSpan SamplingInterval = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Floating-point noise only (values that still read 0.0 or 100.0 at 0.1 precision). Anything beyond is invalid, never
    /// clamped: a sum above 100 can mean double counting.
    /// </summary>
    public const double PercentTolerance = 0.05;

    /// <summary>Longest gap between GetTickCount64 and unbiased interrupt time before the window counts as interrupted.</summary>
    public const long MaxSuspendGapMs = 250;

    public const int MaxGpus = 64;
    public const int MaxEnginesPerGpu = 64;

    /// <summary>Upper bound of GPU Engine counter instances read per sample (one per process, adapter and engine).</summary>
    public const int MaxEngineInstances = 8192;

    private const int MaxInstanceLength = 256;

    public static string GpuId(ulong luid) =>
        string.Create(CultureInfo.InvariantCulture, $"luid_0x{(uint)(luid >> 32):x8}_0x{(uint)luid:x8}");

    public static string? AdapterName(string? raw) => UntrustedText.Sanitize(raw, 128);

    /// <summary>
    /// Whether the sampling window is usable: not interrupted by sleep, not inverted and not much shorter than requested.
    /// Returns the reason when it is not.
    /// </summary>
    public static UnavailableReason? CheckWindow(long elapsedMs, long workingMs, TimeSpan interval)
    {
        if (elapsedMs - workingMs > MaxSuspendGapMs) return UnavailableReason.Interrupted;
        if (workingMs <= 0 || elapsedMs <= 0 || workingMs - elapsedMs > MaxSuspendGapMs || workingMs < interval.TotalMilliseconds / 2)
        {
            return UnavailableReason.InvalidData;
        }

        return null;
    }

    /// <summary>A formatted percentage, rounded to 0.1; null when not finite or outside 0-100 beyond the tolerance.</summary>
    public static double? Percent(double value) =>
        double.IsFinite(value) && value >= -PercentTolerance && value <= 100 + PercentTolerance
            ? Math.Round(Math.Clamp(value, 0, 100), 1)
            : null;

    public static SystemStatusData Interpret(StatusReading r, TimeSpan interval)
    {
        var window = CheckWindow(r.ElapsedMs, r.WorkingMs, interval);

        double? cpu = null;
        var cpuReason = r.CpuReason ?? window;
        if (cpuReason is null)
        {
            cpu = r.CpuPercent is { } v ? Percent(v) : null;
            cpuReason = r.CpuPercent is null ? UnavailableReason.Unavailable : cpu is null ? UnavailableReason.InvalidData : null;
        }

        var (gpus, total, truncated, gpusReason) = Gpus(r, window);
        return new(
            r.EndedAt,
            r.StartedAt,
            r.EndedAt,
            Math.Max(0, r.WorkingMs),
            new CpuStatus(cpu, cpuReason, r.LogicalProcessors is > 0 ? r.LogicalProcessors : null),
            Memory(r.MemoryTotalBytes, r.MemoryAvailableBytes),
            r.UptimeMs is { } up ? (long)(up / 1000) : null,
            r.UptimeMs is null ? UnavailableReason.Unavailable : null,
            gpus,
            total,
            truncated,
            gpusReason);
    }

    public static MemoryStatus Memory(ulong? total, ulong? available)
    {
        if (total is null || available is null) return new(null, null, null, null, UnavailableReason.Unavailable);
        if (total == 0 || available > total || total > long.MaxValue) return new(null, null, null, null, UnavailableReason.InvalidData);
        var used = total.Value - available.Value;
        return new((long)total, (long)available, (long)used, Math.Round(used * 100.0 / total.Value, 1), null);
    }

    private sealed record EngineKey(ulong Luid, int Phys, int Engine);

    private sealed record ParsedEngine(ulong Pid, EngineKey Key, string Type, double? Value);

    /// <summary>
    /// GPU utilization. Each process instance of an engine is summed into that engine (instances of the same process are
    /// never counted twice); engines are never added together; the adapter's value is its busiest engine.
    /// </summary>
    private static (IReadOnlyList<GpuStatus> Gpus, int Total, bool Truncated, UnavailableReason? Reason) Gpus(StatusReading r, UnavailableReason? window)
    {
        var adapters = r.Adapters.Value ?? [];
        var engineReason = r.Engines.Reason ?? window;
        List<ParsedEngine>? parsed = null;
        if (engineReason is null)
        {
            parsed = [];
            foreach (var sample in r.Engines.Value!)
            {
                if (ParseEngine(sample) is not { } p)
                {
                    // An instance that cannot be attributed to an adapter could belong to any of them.
                    engineReason = UnavailableReason.InvalidData;
                    parsed = null;
                    break;
                }

                parsed.Add(p);
            }
        }

        Dictionary<ulong, (long? Dedicated, long? Shared, UnavailableReason? Reason)> memory = [];
        var memoryReason = r.GpuMemory.Reason;
        if (memoryReason is null)
        {
            memory = GpuMemory(r.GpuMemory.Value!, out var invalid);
            if (invalid) memoryReason = UnavailableReason.InvalidData;
        }

        var luids = adapters.Select(a => a.Luid)
            .Concat(parsed?.Select(p => p.Key.Luid) ?? [])
            .Concat(memoryReason is null ? memory.Keys : [])
            .Distinct()
            .Order()
            .ToList();
        if (luids.Count == 0 && r.Adapters.Reason is { } none)
        {
            return ([], 0, false, none);
        }

        var result = new List<GpuStatus>();
        foreach (var luid in luids.Take(MaxGpus))
        {
            var dxgi = adapters.FirstOrDefault(a => a.Luid == luid);
            var mine = engineReason is null ? parsed!.Where(p => p.Key.Luid == luid).ToList() : [];
            var (engines, enginesTruncated, utilization, utilizationReason, busiest) = engineReason is { } er
                ? ([], false, null, er, null)
                : Engines(mine);
            var (dedicated, shared, memReason) = memoryReason is { } mr ? (null, null, mr)
                : memory.TryGetValue(luid, out var m) ? m
                : (null, null, UnavailableReason.Unavailable);
            result.Add(new GpuStatus(
                GpuId(luid),
                AdapterName(dxgi?.Description),
                dxgi is null ? GpuAdapterKind.Unknown : dxgi.Software ? GpuAdapterKind.Software : GpuAdapterKind.Hardware,
                utilization,
                utilizationReason,
                busiest,
                engines,
                enginesTruncated,
                mine.Count(p => p.Value is null),
                dedicated,
                shared,
                memReason,
                dxgi is null ? null : Bytes(dxgi.DedicatedVideoMemory),
                dxgi is null ? null : Bytes(dxgi.SharedSystemMemory)));
        }

        return (result, luids.Count, luids.Count > MaxGpus, null);
    }

    private static (IReadOnlyList<GpuEngineStatus>, bool, double?, UnavailableReason?, string?) Engines(List<ParsedEngine> instances)
    {
        if (instances.Count == 0)
        {
            // No engine instance at all: nothing was measured, which is not the same as 0 %.
            return ([], false, null, UnavailableReason.Unavailable, null);
        }

        var engines = new List<GpuEngineStatus>();
        foreach (var group in instances.GroupBy(i => i.Key).OrderBy(g => g.Key.Phys).ThenBy(g => g.Key.Engine))
        {
            var types = group.Select(i => i.Type).Distinct(StringComparer.Ordinal).ToList();
            var type = types.Count == 1 ? EngineType(types[0]) : null;
            double? value = null;
            UnavailableReason? reason = null;
            if (types.Count != 1 || group.GroupBy(i => i.Pid).Any(AmbiguousDuplicate))
            {
                // One engine with two types, or the same process listed twice with the same non-zero value (one counter
                // reported twice, or two counters that happen to match): summing could double count.
                reason = UnavailableReason.InvalidData;
            }
            else
            {
                var valid = group.Where(i => i.Value is not null).ToList();
                if (valid.Count == 0)
                {
                    reason = UnavailableReason.Unavailable;
                }
                else if (valid.Any(i => !double.IsFinite(i.Value!.Value) || i.Value < 0))
                {
                    reason = UnavailableReason.InvalidData;
                }
                else
                {
                    // Instances that appeared after the first collection have no valid rate yet and are left out.
                    value = Percent(valid.Sum(i => i.Value!.Value));
                    reason = value is null ? UnavailableReason.InvalidData : null;
                }
            }

            engines.Add(new GpuEngineStatus(group.Key.Phys, group.Key.Engine, type, value, reason));
        }

        double? utilization = null;
        UnavailableReason? utilizationReason;
        string? busiest = null;
        if (engines.Any(e => e.Reason == UnavailableReason.InvalidData))
        {
            utilizationReason = UnavailableReason.InvalidData;
        }
        else if (engines.Any(e => e.UtilizationPercent is null))
        {
            utilizationReason = UnavailableReason.Unavailable;
        }
        else
        {
            var top = engines.MaxBy(e => e.UtilizationPercent)!;
            utilization = top.UtilizationPercent;
            busiest = top.EngineType;
            utilizationReason = null;
        }

        return (engines.Take(MaxEnginesPerGpu).ToList(), engines.Count > MaxEnginesPerGpu, utilization, utilizationReason, busiest);
    }

    /// <summary>
    /// A process can have several instances with the same name for one engine (seen for the Hyper-V VM worker, vmwp, with
    /// GPU paravirtualization). All zero: no effect either way. Different values: they are different counters and add up.
    /// The same non-zero value: indistinguishable from one counter reported twice.
    /// </summary>
    private static bool AmbiguousDuplicate(IGrouping<ulong, ParsedEngine> sameProcess)
    {
        var values = sameProcess.Where(i => i.Value is not null).Select(i => i.Value!.Value).ToList();
        return values.Count > 1 && values.Any(v => v != 0) && values.Distinct().Count() < values.Count;
    }

    private static Dictionary<ulong, (long?, long?, UnavailableReason?)> GpuMemory(IReadOnlyList<GpuMemorySample> samples, out bool invalid)
    {
        invalid = false;
        var parsed = new List<(ulong Luid, int Phys, long? Dedicated, long? Shared)>();
        foreach (var s in samples)
        {
            var m = s.Instance.Length <= MaxInstanceLength ? MemoryInstance().Match(s.Instance) : Match.Empty;
            if (!m.Success)
            {
                invalid = true;
                return [];
            }

            parsed.Add((Luid(m.Groups[1].Value, m.Groups[2].Value), Int(m.Groups[3].Value), s.DedicatedUsage, s.SharedUsage));
        }

        var result = new Dictionary<ulong, (long?, long?, UnavailableReason?)>();
        foreach (var adapter in parsed.GroupBy(p => p.Luid))
        {
            if (adapter.GroupBy(p => p.Phys).Any(g => g.Count() > 1) || adapter.Any(p => p.Phys < 0))
            {
                result[adapter.Key] = (null, null, UnavailableReason.InvalidData);
                continue;
            }

            if (adapter.Any(p => p.Dedicated is null || p.Shared is null))
            {
                result[adapter.Key] = (null, null, UnavailableReason.Unavailable);
                continue;
            }

            if (adapter.Any(p => p.Dedicated < 0 || p.Shared < 0))
            {
                result[adapter.Key] = (null, null, UnavailableReason.InvalidData);
                continue;
            }

            try
            {
                // Physical adapters of one linked adapter have separate memory, so their usage adds up.
                long dedicated = 0, shared = 0;
                foreach (var p in adapter)
                {
                    dedicated = checked(dedicated + p.Dedicated!.Value);
                    shared = checked(shared + p.Shared!.Value);
                }

                result[adapter.Key] = (dedicated, shared, null);
            }
            catch (OverflowException)
            {
                result[adapter.Key] = (null, null, UnavailableReason.InvalidData);
            }
        }

        return result;
    }

    private static ParsedEngine? ParseEngine(CounterSample s)
    {
        var m = s.Instance.Length <= MaxInstanceLength ? EngineInstance().Match(s.Instance) : Match.Empty;
        if (!m.Success || !ulong.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            return null;
        }

        var phys = Int(m.Groups[4].Value);
        var engine = Int(m.Groups[5].Value);
        return phys < 0 || engine < 0 ? null : new ParsedEngine(pid, new EngineKey(Luid(m.Groups[2].Value, m.Groups[3].Value), phys, engine), m.Groups[6].Value, s.Value);
    }

    private static string? EngineType(string raw) => EngineTypeText().IsMatch(raw) ? raw : null;

    private static ulong Luid(string high, string low) =>
        ((ulong)uint.Parse(high, NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 32) | uint.Parse(low, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static int Int(string digits) => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1;

    internal static long? Bytes(ulong value) => value > long.MaxValue ? null : (long)value;

    // pid_<pid>_luid_0x<high>_0x<low>_phys_<n>_eng_<n>_engtype_<type>, optionally with PDH's "#n" duplicate suffix.
    [GeneratedRegex(@"^pid_(\d{1,10})_luid_0x([0-9A-Fa-f]{8})_0x([0-9A-Fa-f]{8})_phys_(\d{1,4})_eng_(\d{1,4})_engtype_([^#]{0,64})(?:#\d{1,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex EngineInstance();

    [GeneratedRegex(@"^luid_0x([0-9A-Fa-f]{8})_0x([0-9A-Fa-f]{8})_phys_(\d{1,4})(?:#\d{1,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex MemoryInstance();

    [GeneratedRegex(@"^[A-Za-z0-9_ ]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex EngineTypeText();
}
