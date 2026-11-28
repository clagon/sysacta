using System.Runtime.InteropServices;
using Sysacta.Core.Tools.SystemMonitoring;
using Sysacta.Windows.SystemMonitoring;

namespace Sysacta.Windows.Tests;

/// <summary>
/// Read-only checks of the monitoring adapter on the machine running the tests (CI runner or a developer PC). No load is
/// generated and nothing is changed. Missing hardware (GPU, sensors) is accepted as a reason, never as a value.
/// </summary>
public class WindowsSystemMonitorTests
{
    [Fact]
    public void Native_struct_layouts_match_the_SDK()
    {
        Assert.Equal(16, Marshal.SizeOf<NativeMethods.PdhFmtCounterValue>());
        Assert.Equal(24, Marshal.SizeOf<NativeMethods.PdhFmtCounterValueItem>());
        Assert.Equal(64, Marshal.SizeOf<NativeMethods.MemoryStatusEx>());
        Assert.Equal(96, Marshal.SizeOf<NativeMethods.DiskSpaceInformation>());
        Assert.Equal(64, Marshal.SizeOf<NativeMethods.D3dkmtAdapterPerfData>());
        Assert.Equal(56, Marshal.OffsetOf<NativeMethods.D3dkmtAdapterPerfData>(nameof(NativeMethods.D3dkmtAdapterPerfData.Temperature)).ToInt32());
        Assert.Equal(12, Marshal.SizeOf<NativeMethods.D3dkmtOpenAdapterFromLuid>());
        Assert.Equal(24, Marshal.SizeOf<NativeMethods.D3dkmtQueryAdapterInfo>());
        // WCHAR[128] + 4 UINT + 3 SIZE_T + LUID + UINT, padded to 8.
        Assert.Equal(312, Marshal.SizeOf<NativeMethods.DxgiAdapterDesc1>());
    }

    [Fact]
    public void Drives_are_chosen_by_type_before_any_other_access_and_subst_is_left_out()
    {
        // A: removable, C: fixed, D: fixed (device unreadable), N: network, R: optical, S: SUBST of C:, Z: unknown.
        var types = new Dictionary<char, uint> { ['A'] = 2, ['C'] = 3, ['D'] = 3, ['N'] = 4, ['R'] = 5, ['S'] = 3, ['Z'] = 0 };
        var targets = new Dictionary<char, string?> { ['C'] = @"\Device\HarddiskVolume3", ['D'] = null, ['S'] = @"\??\C:\Users\x" };
        var touched = new List<char>();
        var mask = types.Keys.Aggregate(0u, (m, l) => m | (1u << (l - 'A')));
        var (drives, excluded) = WindowsSystemMonitor.SelectDrives(mask, root => types[root[0]],
            l => { touched.Add(l); return targets[l]; },
            l => { touched.Add(l); return new(l, new(10, 5, 5, 1, 1), null); });

        Assert.Equal(['C', 'C', 'D', 'S'], touched); // C: device then space; D: device only; S: device only
        Assert.Equal(['C', 'D'], drives.Select(d => d.Letter));
        Assert.Equal(UnavailableReason.Unavailable, drives.Single(d => d.Letter == 'D').Reason);
        Assert.Equal(5, excluded);
    }

    [Fact]
    public void A_hung_source_is_not_started_again_until_it_ends_but_normal_reads_overlap()
    {
        var guard = new HangGuard(TimeSpan.FromMilliseconds(200));
        var first = guard.TryEnter();
        var second = guard.TryEnter();
        Assert.NotNull(first);
        Assert.NotNull(second);
        guard.Exit(second);
        Thread.Sleep(300);
        Assert.Null(guard.TryEnter());
        guard.Exit(first);
        var again = guard.TryEnter();
        Assert.NotNull(again);
        guard.Exit(again);
        guard.Exit(null);
        Assert.Equal(0, guard.Running);
    }

    [Fact]
    public void Status_samples_a_valid_window_with_cpu_memory_and_uptime()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var reading = new WindowsSystemMonitor().SampleStatus(SystemStatus.SamplingInterval, TestContext.Current.CancellationToken);
        var d = SystemStatus.Interpret(reading, SystemStatus.SamplingInterval);

        Assert.InRange(d.SamplingMs, 500, 4000);
        Assert.True(d.SamplingEndedAt > d.SamplingStartedAt);
        Assert.Equal(TimeSpan.Zero, d.MeasuredAt.Offset);
        Assert.Null(d.Cpu.Reason);
        Assert.InRange(d.Cpu.UsagePercent!.Value, 0, 100);
        // All processor groups, not limited by this process's affinity (Environment.ProcessorCount is).
        Assert.True(d.Cpu.LogicalProcessors >= Environment.ProcessorCount);
        Assert.Null(d.Memory.Reason);
        Assert.Equal(d.Memory.TotalBytes - d.Memory.AvailableBytes, d.Memory.UsedBytes);
        Assert.True(d.UptimeSeconds > 0);
        // Every listed GPU has either a value or a reason, never a silent null.
        Assert.All(d.Gpus, g => Assert.True(g.UtilizationPercent is not null ^ g.UtilizationReason is not null));
    }

    [Fact]
    public void Status_sampling_stops_when_cancelled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.ThrowsAny<OperationCanceledException>(() => new WindowsSystemMonitor().SampleStatus(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public void Hardware_reads_os_topology_memory_and_gpus_without_identifiers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var d = SystemInventory.Hardware(new WindowsSystemMonitor().ReadHardware());

        Assert.Equal(Environment.OSVersion.Version.Build, d.Os.BuildNumber);
        Assert.NotNull(d.Os.Architecture);
        Assert.True(d.ProcessorTopology.LogicalProcessors >= d.ProcessorTopology.PhysicalCores);
        Assert.True(d.ProcessorTopology.PhysicalCores >= d.ProcessorTopology.Packages);
        Assert.NotNull(d.Memory.OsUsableBytes);
        if (d.Memory.InstalledBytes is { } installed) Assert.True(installed >= d.Memory.OsUsableBytes);
        Assert.All(d.Gpus, g => Assert.Matches("^luid_0x[0-9a-f]{8}_0x[0-9a-f]{8}$", g.GpuId));
        var json = System.Text.Json.JsonSerializer.Serialize(d, Sysacta.Core.Tools.ToolJson.Options);
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Temperatures_return_values_or_reasons_for_each_source()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var d = SystemInventory.Temperatures(new WindowsSystemMonitor().ReadTemperatures());
        Assert.All(d.Sensors, s => Assert.True(s.TemperatureC is not null ^ s.Reason is not null));
        Assert.All(d.Sensors, s => Assert.NotEqual(TemperatureDeviceType.Cpu, s.DeviceType));
        Assert.Equal(5, d.NotCollected.Count);
    }

    [Fact]
    public void Storage_lists_only_local_fixed_volumes_with_consistent_capacity()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var d = SystemInventory.Storage(new WindowsSystemMonitor().ReadStorage());
        var system = Path.GetPathRoot(Environment.SystemDirectory)![..2].ToUpperInvariant();
        var c = d.Volumes.Single(v => v.DriveLetter == system);
        Assert.Null(c.Reason);
        Assert.True(c.TotalBytes > 0);
        Assert.True(c.FreeBytes <= c.TotalBytes);
        Assert.Equal(c.TotalBytes - c.FreeBytes, c.UsedBytes);
        Assert.InRange(c.UsagePercent!.Value, 0, 100);
        // Same basis as DriveInfo (GetDiskFreeSpaceEx) for the total free bytes.
        Assert.InRange(c.FreeBytes!.Value, new DriveInfo(system).TotalFreeSpace - (1L << 30), new DriveInfo(system).TotalFreeSpace + (1L << 30));
        Assert.All(d.Volumes, v => Assert.Equal("fixed", v.DriveType));
    }

    [Fact]
    public void Dxgi_and_d3dkmt_agree_on_adapter_identity()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var adapters = new WindowsSystemMonitor().Adapters();
        Assert.Null(adapters.Reason);
        Assert.Equal(adapters.Value!.Count, adapters.Value.Select(a => a.Luid).Distinct().Count());
        foreach (var hardware in adapters.Value.Where(a => !a.Software))
        {
            var readings = WindowsSystemMonitor.GpuTemperatures(hardware);
            Assert.Equal(Enumerable.Range(0, readings.Count), readings.Select(t => t.PhysicalIndex));
            Assert.All(readings, t => Assert.Equal(hardware.Luid, t.Luid));
            Assert.All(readings, t => Assert.True(t.DeciCelsius is not null ^ t.Reason is not null));
        }
    }
}
