using System.Diagnostics;
using System.Runtime.InteropServices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using Sysacta.Windows.Display;

namespace Sysacta.Windows.Tests;

/// <summary>Adapter logic that needs no Windows API, so it also runs on other hosts.</summary>
public class WindowsDisplayLogicTests
{
    [Fact]
    public void Native_struct_sizes_match_the_x64_headers()
    {
        Assert.Equal(20, Marshal.SizeOf<NativeMethods.PathSourceInfo>());
        Assert.Equal(48, Marshal.SizeOf<NativeMethods.PathTargetInfo>());
        Assert.Equal(72, Marshal.SizeOf<NativeMethods.PathInfo>());
        Assert.Equal(64, Marshal.SizeOf<NativeMethods.ModeInfo>());
        Assert.Equal(20, Marshal.SizeOf<NativeMethods.DeviceInfoHeader>());
        Assert.Equal(84, Marshal.SizeOf<NativeMethods.SourceDeviceName>());
        Assert.Equal(420, Marshal.SizeOf<NativeMethods.TargetDeviceName>());
        Assert.Equal(220, Marshal.SizeOf<NativeMethods.DevMode>());
        Assert.Equal(104, Marshal.SizeOf<NativeMethods.MonitorInfoEx>());
        Assert.Equal(IntPtr.Size + 256, Marshal.SizeOf<NativeMethods.PhysicalMonitor>());
    }

    [Fact]
    public void Ids_are_deterministic_opaque_and_distinguish_ports()
    {
        const string First = @"\\?\DISPLAY#ABC1234#5&111&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string Second = @"\\?\DISPLAY#ABC1234#5&111&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        var id = DisplayTopologyReader.CanonicalId(First);

        Assert.Equal(id, DisplayTopologyReader.CanonicalId(First.ToLowerInvariant()));
        Assert.NotEqual(id, DisplayTopologyReader.CanonicalId(Second));
        Assert.Matches("^display-[0-9a-f]{32}$", id);
    }

    [Fact]
    public void Wmi_instances_match_only_their_own_panel()
    {
        var instance = WmiPanelControl.InstanceId(@"\\?\DISPLAY#ABC1234#5&111&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");

        Assert.Equal(@"DISPLAY\ABC1234\5&111&0&UID4352", instance);
        Assert.True(WmiPanelControl.Matches(@"DISPLAY\abc1234\5&111&0&uid4352_0", instance!));
        Assert.False(WmiPanelControl.Matches(@"DISPLAY\ABC1234\5&111&0&UID43520_0", instance!));
        Assert.False(WmiPanelControl.Matches(@"DISPLAY\ABC1234\5&111&0&UID4353_0", instance!));
        Assert.False(WmiPanelControl.Matches(@"DISPLAY\ABC1234\5&111&0&UID4352", instance!));
        Assert.Null(WmiPanelControl.InstanceId(@"\\?\ROOT#BasicDisplay#0000#{guid}#x"));
    }

    [Theory]
    [InlineData(5u, DisplayKind.External, "hdmi")]
    [InlineData(10u, DisplayKind.External, "displayport")]
    [InlineData(11u, DisplayKind.Internal, "embedded_displayport")]
    [InlineData(0x80000000u, DisplayKind.Internal, "internal")]
    [InlineData(17u, DisplayKind.Virtual, "indirect_virtual")]
    [InlineData(0xffffffffu, DisplayKind.Unknown, "other")]
    public void Output_technologies_are_classified(uint technology, DisplayKind kind, string connection) =>
        Assert.Equal((kind, connection), DisplayTopologyReader.Classify(technology));

    [Theory]
    [InlineData(0xC0262580u, "NOT_SUPPORTED")]
    [InlineData(0xC0262584u, "NOT_SUPPORTED")]
    [InlineData(0xC026258Du, "DEVICE_DISCONNECTED")]
    [InlineData(0xC0262582u, "OS_API_ERROR")]
    [InlineData(0xC0262581u, "OS_API_ERROR")] // no answer on the bus: not proof of missing support
    [InlineData(0u, "OS_API_ERROR")]
    public void Ddc_errors_separate_missing_support_from_temporary_failures(uint error, string code) =>
        Assert.Equal(code, DdcCiControl.Failure(unchecked((int)error)).Code);
}

/// <summary>
/// Read-only checks against the real display stack. Nothing is written. Skipped on non-Windows hosts and sessions
/// without a display. The output records what was detected (see docs/phase-1c-1-progress.md).
/// </summary>
[Collection("real display")]
public class WindowsDisplaysTests(ITestOutputHelper output)
{
    private static DisplayTopology Require(WindowsDisplays displays)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        DisplayTopology topology;
        try
        {
            topology = displays.GetTopology();
        }
        catch (ToolFailureException ex)
        {
            Assert.Skip($"The display configuration is not available: {ex.Code}.");
            throw;
        }

        Assert.SkipWhen(topology.Displays.Count == 0, "No identifiable display in this session.");
        return topology;
    }

    [Fact]
    public void Topology_modes_and_values_can_be_read()
    {
        var displays = new WindowsDisplays();
        var watch = Stopwatch.StartNew();
        var topology = Require(displays);
        output.WriteLine($"topology: {watch.ElapsedMilliseconds} ms, unidentified {topology.UnidentifiedCount}, {Environment.OSVersion.VersionString}");
        Assert.Equal(topology.Displays.Count, topology.Displays.Select(d => d.Id).Distinct().Count());
        Assert.Equal(topology.Displays.Select(d => d.Id), displays.GetTopology().Displays.Select(d => d.Id));

        foreach (var d in topology.Displays)
        {
            output.WriteLine($"{d.Name} {d.Kind} {d.Connection} primary={d.IsPrimary} cloned={d.IsCloned} mode={d.CurrentMode}");
            watch.Restart();
            try
            {
                var modes = displays.ListModes(d.Id);
                output.WriteLine($"  modes: {modes.Modes.Count}, current {modes.Current}, {watch.ElapsedMilliseconds} ms");
                Assert.NotEmpty(modes.Modes);
                Assert.NotNull(modes.Current);
            }
            catch (ToolFailureException ex)
            {
                output.WriteLine($"  modes: {ex.Code}");
            }

            foreach (var feature in Enum.GetValues<DisplayFeature>())
            {
                watch.Restart();
                var state = DisplayFeatureState.Read(displays, d.Id, feature, TestContext.Current.CancellationToken);
                output.WriteLine($"  {feature}: {state.Support} level={state.Level} range={state.NativeRange} method={state.Method} error={state.ErrorCode} {watch.ElapsedMilliseconds} ms");
                Assert.Equal(state.Support == DisplaySupport.Supported, state.Level is not null);
            }
        }
    }

    [Fact]
    public void A_panel_without_a_matching_wmi_instance_is_never_substituted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        using var control = new WmiPanelControl(@"DISPLAY\NONE000\0&0&0&UID0", new MemoryStream());

        var ex = Assert.Throws<ToolFailureException>(() => control.Read());

        output.WriteLine($"WMI without a matching panel: {ex.Code}");
        Assert.Contains(ex.Code, new[] { ErrorCodes.NotSupported, ErrorCodes.AdminRequired, ErrorCodes.OsApiError });
    }

    [Fact]
    public void Access_is_exclusive_bounded_and_released()
    {
        var displays = new WindowsDisplays();
        var topology = Require(displays);
        var ct = TestContext.Current.CancellationToken;

        // A failed open releases the gate.
        for (var i = 0; i < 2; i++)
        {
            var missing = Assert.Throws<ToolFailureException>(() => displays.Open("display-missing", DisplayFeature.Brightness, ct));
            Assert.Equal(ErrorCodes.DeviceDisconnected, missing.Code);
        }

        var target = topology.Displays.FirstOrDefault(d => DisplayCatalog.Blocked(d, DisplayFeature.Brightness) is null);
        Assert.SkipWhen(target is null, "No display with a possible brightness control.");
        IDisplayControl first;
        try
        {
            first = displays.Open(target!.Id, DisplayFeature.Brightness, ct);
        }
        catch (ToolFailureException ex)
        {
            Assert.Skip($"The display cannot be opened: {ex.Code}.");
            throw;
        }

        using (first)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert.ThrowsAny<OperationCanceledException>(() => displays.Open(target.Id, DisplayFeature.Contrast, timeout.Token));
        }

        first.Dispose(); // a second dispose does not release the gate twice
        using var again = displays.Open(target.Id, DisplayFeature.Brightness, ct);
        using var busy = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.ThrowsAny<OperationCanceledException>(() => displays.Open(target.Id, DisplayFeature.Brightness, busy.Token));
    }

    /// <summary>
    /// Changes a real monitor. Needs SYSACTA_HARDWARE_TESTS=1 and the id of the one display the owner approved in
    /// SYSACTA_HARDWARE_DISPLAY; moves its value by 5 steps of 100 and restores it.
    /// </summary>
    [Theory]
    [InlineData(DisplayFeature.Brightness)]
    [InlineData(DisplayFeature.Contrast)]
    public void Set_level_changes_and_restores_the_approved_display(DisplayFeature feature)
    {
        var id = Environment.GetEnvironmentVariable("SYSACTA_HARDWARE_DISPLAY");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SYSACTA_HARDWARE_TESTS") == "1" && !string.IsNullOrEmpty(id),
            "Set SYSACTA_HARDWARE_TESTS=1 and SYSACTA_HARDWARE_DISPLAY=<approved display id> to change a real monitor.");
        var displays = new WindowsDisplays();
        Require(displays);
        using var control = displays.Open(id!, feature, TestContext.Current.CancellationToken);
        var before = control.Read().Validated();
        var level = before.ToLevel(before.Current);
        var target = before.NativeFor(level >= 50 ? level - 5 : level + 5);
        Assert.SkipWhen(target == before.Current, "The display has no nearby value.");
        output.WriteLine($"{id} {feature}: {before.Current} -> {target} -> {before.Current}");
        try
        {
            control.Write(target);
            Thread.Sleep(500);
            Assert.Equal(target, control.Read().Current);
        }
        finally
        {
            control.Write(before.Current);
        }

        Thread.Sleep(500);
        Assert.Equal(before.Current, control.Read().Current);
    }
}
