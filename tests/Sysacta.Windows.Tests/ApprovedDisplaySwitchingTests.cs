using System.Text.Json;
using Sysacta.Core.Auditing;
using Sysacta.Core.Devices;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using Sysacta.Windows.Display;

namespace Sysacta.Windows.Tests;

/// <summary>
/// Manual, approved checks of the Phase 1C-2 writes on one real monitor, through the production tools and executor (as
/// the local console). Runs only with SYSACTA_HARDWARE_TESTS=1, SYSACTA_HARDWARE_DISPLAY=&lt;approved non-primary display
/// id&gt; and SYSACTA_HARDWARE_SWITCH=mode|power|input, one step at a time, with the user watching. See
/// docs/manual-tests.md (DM / DP / DI).
/// </summary>
[Collection("real display")]
public class ApprovedDisplaySwitchingTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Approved_step_on_the_approved_display()
    {
        var id = Environment.GetEnvironmentVariable("SYSACTA_HARDWARE_DISPLAY");
        var step = Environment.GetEnvironmentVariable("SYSACTA_HARDWARE_SWITCH");
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SYSACTA_HARDWARE_TESTS") == "1"
            && !string.IsNullOrEmpty(id) && step is "mode" or "power" or "on" or "input",
            "Set SYSACTA_HARDWARE_TESTS=1, SYSACTA_HARDWARE_DISPLAY=<approved id> and SYSACTA_HARDWARE_SWITCH=mode|power|on|input.");

        var displays = new WindowsDisplays();
        var display = displays.GetTopology().Displays.Single(d => d.Id == id);
        Assert.False(display.IsPrimary, "Only an approved non-primary display is changed.");
        var recovery = new DisplayModeRecovery();
        var aliases = new NoAliases();
        var audit = new Audit();
        var executor = new ToolExecutor(
            new ToolRegistry(
            [
                new DisplayGetStateTool(displays, aliases),
                new DisplayListModesTool(displays, aliases),
                new DisplaySetModeTool(displays, aliases, recovery),
                new DisplayRestoreModeTool(displays, recovery),
                new DisplaySetPowerTool(displays, aliases),
                new DisplaySetInputTool(displays, aliases),
            ]),
            new AccessPolicy(new AllowAll(), TimeProvider.System),
            audit,
            TimeProvider.System,
            new AuthorizationGate());

        async Task<ToolResult> Call(string tool, object? args)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var r = await executor.ExecuteAsync(Caller.LocalConsole, tool, args is null ? null : JsonSerializer.SerializeToElement(args), Ct);
            output.WriteLine($"{tool} ({watch.ElapsedMilliseconds} ms): {JsonSerializer.Serialize(r, ToolJson.Options)}");
            return r;
        }

        switch (step)
        {
            case "mode":
            {
                var modes = (await Call("display_list_modes", new { device = id })).Data!["modes"]!.AsArray();
                var current = modes.Single(m => (bool)m!["is_current"]!)!;
                var target = modes.FirstOrDefault(m => (int)m!["width"]! == 1280 && (int)m["height"]! == 720
                    && (int)m["refresh_hz"]! == (int)current["refresh_hz"]! && (int)m["bits_per_pixel"]! == (int)current["bits_per_pixel"]!
                    && (int)m["orientation"]! == (int)current["orientation"]! && (int)m["fixed_output"]! == (int)current["fixed_output"]!);
                Assert.SkipWhen(target is null, "No 1280x720 mode with the current refresh rate.");
                var set = await Call("display_set_mode", new { device = id, mode_id = (string)target!["mode_id"]! });
                await Task.Delay(TimeSpan.FromSeconds(4), Ct);
                var restored = await Call("display_restore_mode", null);
                var after = (await Call("display_list_modes", new { device = id })).Data!["modes"]!.AsArray().Single(m => (bool)m!["is_current"]!)!;
                Assert.Equal((string)current["mode_id"]!, (string)after["mode_id"]!);
                Assert.True(set.IsSuccess, set.Error?.Code);
                Assert.True(restored.IsSuccess, restored.Error?.Code);
                break;
            }

            case "power":
            {
                await Call("display_get_state", new { device = id, include_switching = true });
                var off = await Call("display_set_power", new { device = id, state = "off" });
                await Task.Delay(TimeSpan.FromSeconds(5), Ct);
                var on = await Call("display_set_power", new { device = id, state = "on" });
                await Task.Delay(TimeSpan.FromSeconds(3), Ct);
                await Call("display_get_state", new { device = id, include_switching = true });
                // The results are recorded, not asserted: a monitor that stops answering while off is a valid outcome.
                Assert.Contains(off.Status, new[] { ToolStatus.Success, ToolStatus.Error, ToolStatus.Unknown });
                Assert.Contains(on.Status, new[] { ToolStatus.Success, ToolStatus.Error, ToolStatus.Unknown });
                break;
            }

            case "on":
                // Recovery after "power" when the monitor did not answer the first "on".
                await Call("display_set_power", new { device = id, state = "on" });
                break;

            default:
            {
                var state = (await Call("display_get_state", new { device = id, include_switching = true })).Data!["input"]!;
                var original = (string?)state["current_input_id"];
                var other = state["inputs"]!.AsArray().FirstOrDefault(i => !(bool)i!["is_current"]!);
                Assert.SkipWhen(other is null, "No other declared input.");
                var set = await Call("display_set_input", new { device = id, input_id = (string)other!["input_id"]! });
                await Task.Delay(TimeSpan.FromSeconds(5), Ct);
                if (original is not null)
                {
                    await Call("display_set_input", new { device = id, input_id = original });
                }
                else
                {
                    output.WriteLine("The original input has no standard id: switch back with the monitor's menu (OSD).");
                }

                // Recorded, not asserted: the monitor may report another value once it has no signal.
                Assert.NotEqual(ToolStatus.Partial, set.Status);
                break;
            }
        }

        foreach (var e in audit.Events.Where(e => e.Outcome != AuditOutcome.Pending))
        {
            output.WriteLine($"audit: {e.ToolName} {e.Outcome} {e.ErrorCode} {e.Arguments}");
        }
    }

    private sealed class AllowAll : IAccessStore
    {
        public bool IsToolEnabled(string toolName) => true;
        public ClientRecord? GetClient(string clientId) => null;
        public (ClientRecord Client, string TokenHash)? FindClientByTokenHash(string tokenHash) => null;
        public bool IsClientToolAllowed(string clientId, string toolName) => false;

        public void RecordClientUse(string clientId, DateTimeOffset at)
        {
        }
    }

    private sealed class NoAliases : IDeviceAliases
    {
        public string? Resolve(string category, string alias) => null;
        public IReadOnlyList<DeviceAlias> List(string category) => [];
    }

    private sealed class Audit : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public void Write(AuditEvent auditEvent)
        {
            lock (Events)
            {
                Events.Add(auditEvent);
            }
        }
    }
}
