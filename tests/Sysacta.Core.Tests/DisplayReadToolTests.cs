using System.Text.Json;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

public class DisplayReadToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Harness H, FakeDisplays Displays, Caller Caller) Setup()
    {
        var displays = new FakeDisplays();
        var h = new Harness(store =>
        [
            new DisplayListDevicesTool(displays, store),
            new DisplayGetStateTool(displays, store),
            new DisplayListModesTool(displays, store),
            new DisplaySetBrightnessTool(displays, store),
            new DisplaySetContrastTool(displays, store),
        ]);
        var (caller, _) = h.Store.AddClient("c1", "display_list_devices", "display_get_state", "display_list_modes", "display_set_brightness", "display_set_contrast");
        return (h, displays, caller);
    }

    [Fact]
    public async Task Listing_identifies_same_model_monitors_and_never_claims_unchecked_support()
    {
        var (h, displays, caller) = Setup();
        displays.Replace("display-b", d => d with { Name = "DELL S2422HG" });
        displays.Add("display-c", "Panel", kind: DisplayKind.Internal);
        displays.Add("display-d", "Mirror", cloned: true);
        displays.Add("display-e", "Virtual", kind: DisplayKind.Virtual);
        displays.Unidentified = 1;
        displays.OnOpen = _ => throw new InvalidOperationException("listing must not query monitors");
        h.Store.Aliases[("display", "desk")] = "display-b";
        h.Store.Aliases[("audio", "other")] = "display-a";

        var r = await h.Executor.ExecuteAsync(caller, "display_list_devices", null, Ct);

        Assert.True(r.IsSuccess);
        Assert.Equal(Verification.NotApplicable, r.Verification);
        Assert.Equal(6, (int)r.Data!["total_count"]!);
        Assert.Equal(1, (int)r.Data["unidentified_count"]!);
        Assert.False((bool)r.Data["truncated"]!);
        var devices = r.Data["devices"]!.AsArray();
        var byId = devices.ToDictionary(d => (string)d!["id"]!, d => d!);
        Assert.Equal("display-a", (string)devices[0]!["id"]!); // primary first
        Assert.Equal("desk", (string)byId["display-b"]["aliases"]![0]!);
        Assert.Empty(byId["display-a"]["aliases"]!.AsArray());
        Assert.Equal("unchecked", (string)byId["display-a"]["brightness"]!["support"]!);
        Assert.Equal("unchecked", (string)byId["display-c"]["brightness"]!["support"]!);
        Assert.Equal("not_supported", (string)byId["display-c"]["contrast"]!["support"]!);
        Assert.Equal("unavailable", (string)byId["display-d"]["brightness"]!["support"]!);
        Assert.Equal(ErrorCodes.AmbiguousTarget, (string)byId["display-d"]["brightness"]!["error_code"]!);
        Assert.Equal("not_supported", (string)byId["display-e"]["brightness"]!["support"]!);
    }

    [Fact]
    public async Task Listing_truncates_and_reports_empty_and_api_failure_differently()
    {
        var (h, displays, caller) = Setup();
        for (var i = 0; i < 40; i++)
        {
            displays.Add($"display-x{i:00}", "x");
        }

        var many = await h.Executor.ExecuteAsync(caller, "display_list_devices", null, Ct);
        Assert.Equal(DisplayCatalog.MaxDevices, many.Data!["devices"]!.AsArray().Count);
        Assert.True((bool)many.Data["truncated"]!);

        displays.Displays.Clear();
        var empty = await h.Executor.ExecuteAsync(caller, "display_list_devices", null, Ct);
        Assert.True(empty.IsSuccess);
        Assert.Empty(empty.Data!["devices"]!.AsArray());

        displays.TopologyFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed");
        var failed = await h.Executor.ExecuteAsync(caller, "display_list_devices", null, Ct);
        Assert.Equal(ErrorCodes.OsApiError, failed.Error!.Code);
    }

    [Fact]
    public async Task State_reports_each_feature_separately_and_null_instead_of_zero()
    {
        var (h, displays, caller) = Setup();
        displays.Levels[("display-a", DisplayFeature.Brightness)] = new FakeLevel { Min = 10, Max = 210, Current = 110 };
        displays.Levels[("display-a", DisplayFeature.Contrast)].ReadFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed");

        var r = await h.Executor.ExecuteAsync(caller, "display_get_state", null, Ct);

        Assert.True(r.IsSuccess);
        Assert.Equal("display-a", (string)r.Data!["device_id"]!);
        Assert.Equal(1001, (int)r.Data["current_mode"]!["refresh_denominator"]!);
        var brightness = r.Data["brightness"]!;
        Assert.Equal("supported", (string)brightness["support"]!);
        Assert.Equal(50, (int)brightness["level"]!);
        Assert.Equal("ddc_ci", (string)brightness["method"]!);
        Assert.Equal(210, (int)brightness["native_range"]!["max"]!);
        var contrast = r.Data["contrast"]!;
        Assert.Equal("unavailable", (string)contrast["support"]!);
        Assert.Null(contrast["level"]);
        Assert.Equal(ErrorCodes.OsApiError, (string)contrast["error_code"]!);
        Assert.Equal(0, displays.OpenControls);
        Assert.Empty(displays.Writes);
    }

    [Theory]
    [InlineData(0, 0, 0, null, "NOT_SUPPORTED")] // no adjustable range
    [InlineData(100, 0, 50, null, "OS_API_ERROR")]
    [InlineData(0, 100, 101, null, "OS_API_ERROR")]
    [InlineData(0, 100, 50, "", "OS_API_ERROR")] // empty discrete list
    public async Task Invalid_ranges_are_never_used(long min, long max, long current, string? allowed, string code)
    {
        var (h, displays, caller) = Setup();
        displays.Levels[("display-a", DisplayFeature.Brightness)] = new FakeLevel { Min = min, Max = max, Current = current, Allowed = allowed is null ? null : [] };

        var state = await h.Executor.ExecuteAsync(caller, "display_get_state", null, Ct);
        var set = await h.Executor.ExecuteAsync(caller, "display_set_brightness", TestJson.Args(new { level = 30 }), Ct);

        Assert.Equal(code, (string)state.Data!["brightness"]!["error_code"]!);
        Assert.Null(state.Data["brightness"]!["level"]);
        Assert.Equal(code, set.Error!.Code);
        Assert.Empty(displays.Writes);
    }

    [Theory]
    [InlineData(0, 100, 0, 0)]
    [InlineData(0, 100, 100, 100)]
    [InlineData(20, 80, 0, 20)]
    [InlineData(20, 80, 50, 50)]
    [InlineData(0, 255, 50, 128)]
    [InlineData(0, 4294967295, 100, 4294967295)]
    public void Levels_are_scaled_to_the_native_range(long min, long max, int level, long native) =>
        Assert.Equal(native, new LevelRange(min, max, min).NativeFor(level));

    [Fact]
    public void Discrete_panels_round_to_the_nearest_supported_value()
    {
        var range = new LevelRange(0, 100, 0, [0, 10, 25, 50, 100]);

        Assert.Equal(25, range.NativeFor(30));
        Assert.Equal(50, range.NativeFor(40));
        Assert.Equal(0, range.NativeFor(5)); // tie: the lower value
        Assert.Equal(100, range.NativeFor(99));
        Assert.Equal(25, range.ToLevel(25));
    }

    [Theory]
    [InlineData("display_get_state", "{\"device\":null}")]
    [InlineData("display_get_state", "{\"device\":\"\"}")]
    [InlineData("display_get_state", "{\"device\":\"a\\u0001\"}")]
    [InlineData("display_get_state", "{\"device\":\"all\"}")]
    [InlineData("display_get_state", "{\"device\":\"default\"}")]
    [InlineData("display_get_state", "{\"extra\":1}")]
    [InlineData("display_list_devices", "{\"device\":\"display-a\"}")]
    [InlineData("display_list_modes", "{}")]
    [InlineData("display_list_modes", "{\"device\":\"all\"}")]
    [InlineData("display_set_brightness", "{}")]
    [InlineData("display_set_brightness", "{\"level\":-1}")]
    [InlineData("display_set_brightness", "{\"level\":101}")]
    [InlineData("display_set_brightness", "{\"level\":\"50\"}")]
    [InlineData("display_set_brightness", "{\"level\":50.5}")]
    [InlineData("display_set_brightness", "{\"level\":null}")]
    [InlineData("display_set_brightness", "{\"level\":50,\"device\":null}")]
    [InlineData("display_set_brightness", "{\"level\":50,\"device\":\"ALL\"}")]
    [InlineData("display_set_brightness", "{\"level\":50,\"device\":\"default\"}")]
    [InlineData("display_set_brightness", "{\"level\":50,\"relative\":true}")]
    [InlineData("display_set_contrast", "{\"level\":50,\"device\":\"all\"}")]
    public async Task Invalid_arguments_are_rejected_without_touching_a_display(string tool, string json)
    {
        var (h, displays, caller) = Setup();
        displays.OnOpen = _ => throw new InvalidOperationException("must not be reached");

        var r = await h.Executor.ExecuteAsync(caller, tool, TestJson.Raw(json), Ct);

        Assert.Equal(ErrorCodes.InvalidArgument, r.Error!.Code);
        Assert.Empty(displays.Writes);
    }

    [Fact]
    public void Schemas_require_what_the_contract_requires()
    {
        var (h, _, _) = Setup();
        string[] Required(string tool) => h.Executor.Registry.Find(tool)!.InputSchema.TryGetProperty("required", out var r)
            ? [.. r.EnumerateArray().Select(e => e.GetString()!)]
            : [];

        Assert.Empty(Required("display_list_devices"));
        Assert.Empty(Required("display_get_state"));
        Assert.Equal(["device"], Required("display_list_modes"));
        Assert.Equal(["level"], Required("display_set_brightness"));
        Assert.Equal(["level"], Required("display_set_contrast"));
        foreach (var tool in h.Executor.Registry.All)
        {
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(DefaultPolicy.On, tool.Descriptor.DefaultPolicy);
            Assert.DoesNotContain("\"type\":[", JsonSerializer.Serialize(tool.OutputSchema), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null, "display-b", "TARGET_NOT_FOUND")] // no primary
    [InlineData("display-z", null, "TARGET_NOT_FOUND")]
    [InlineData("DELL P2314H", null, "TARGET_NOT_FOUND")] // names are not identifiers
    [InlineData("gone", null, "DEVICE_DISCONNECTED")]
    public async Task Unresolvable_targets_never_fall_back(string? device, string? removePrimaryFrom, string code)
    {
        var (h, displays, caller) = Setup();
        h.Store.Aliases[("display", "gone")] = "display-gone";
        if (removePrimaryFrom is not null)
        {
            displays.Replace("display-a", d => d with { IsPrimary = false });
        }

        var args = device is null ? TestJson.Args(new { level = 10 }) : TestJson.Args(new { level = 10, device });
        var r = await h.Executor.ExecuteAsync(caller, "display_set_brightness", args, Ct);

        Assert.Equal(code, r.Error!.Code);
        Assert.Empty(displays.Writes);
    }

    [Fact]
    public async Task A_mirrored_primary_and_duplicate_ids_are_ambiguous()
    {
        var (h, displays, caller) = Setup();
        displays.Replace("display-a", d => d with { IsCloned = true });
        displays.Replace("display-b", d => d with { IsCloned = true, IsPrimary = true });

        var primary = await h.Executor.ExecuteAsync(caller, "display_set_brightness", TestJson.Args(new { level = 10 }), Ct);
        var cloned = await h.Executor.ExecuteAsync(caller, "display_set_brightness", TestJson.Args(new { level = 10, device = "display-b" }), Ct);
        var modes = await h.Executor.ExecuteAsync(caller, "display_list_modes", TestJson.Args(new { device = "display-b" }), Ct);
        displays.Displays.Add(displays.Displays[0]);
        var duplicate = await h.Executor.ExecuteAsync(caller, "display_get_state", TestJson.Args(new { device = "display-a" }), Ct);

        Assert.All([primary, cloned, modes, duplicate], r => Assert.Equal(ErrorCodes.AmbiguousTarget, r.Error!.Code));
        Assert.Empty(displays.Writes);
    }

    [Fact]
    public async Task Modes_have_distinct_deterministic_ids_and_mark_the_current_one()
    {
        var (h, displays, caller) = Setup();
        var m60 = new DisplayMode(1920, 1080, 60, 32, false, 0, 0);
        var m59 = m60 with { RefreshHz = 59 };
        DisplayMode[] list = [m59, m60, m60, m60 with { Interlaced = true }, m60 with { BitsPerPixel = 16 }, m60 with { FixedOutput = 1 }, m60 with { Orientation = 180 }];
        displays.Modes["display-a"] = new(list, m59);
        displays.Modes["display-b"] = new(list, null);

        var a = await h.Executor.ExecuteAsync(caller, "display_list_modes", TestJson.Args(new { device = "display-a" }), Ct);
        var again = await h.Executor.ExecuteAsync(caller, "display_list_modes", TestJson.Args(new { device = "display-a" }), Ct);
        var b = await h.Executor.ExecuteAsync(caller, "display_list_modes", TestJson.Args(new { device = "display-b" }), Ct);

        var modes = a.Data!["modes"]!.AsArray();
        Assert.Equal(6, modes.Count); // exact duplicates collapse
        var ids = modes.Select(m => (string)m!["mode_id"]!).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(ids, again.Data!["modes"]!.AsArray().Select(m => (string)m!["mode_id"]!));
        Assert.Empty(ids.Intersect(b.Data!["modes"]!.AsArray().Select(m => (string)m!["mode_id"]!))); // per display
        Assert.Equal(59, (int)modes.Single(m => (bool)m!["is_current"]!)!["refresh_hz"]!);
        Assert.DoesNotContain(b.Data["modes"]!.AsArray(), m => (bool)m!["is_current"]!);
        Assert.False((bool)a.Data["truncated"]!);
    }

    [Fact]
    public async Task Mode_list_is_bounded_keeps_the_current_mode_and_reports_api_errors()
    {
        var (h, displays, caller) = Setup();
        var all = Enumerable.Range(1, DisplayListModesTool.MaxModes + 10).Select(i => new DisplayMode(i, 1, 60, 32, false, 0, 0)).ToArray();
        displays.Modes["display-a"] = new(all, all[0]); // the smallest mode is current

        var r = await h.Executor.ExecuteAsync(caller, "display_list_modes", TestJson.Args(new { device = "display-a" }), Ct);

        Assert.True((bool)r.Data!["truncated"]!);
        Assert.Equal(all.Length, (int)r.Data["total_count"]!);
        var modes = r.Data["modes"]!.AsArray();
        Assert.Equal(DisplayListModesTool.MaxModes, modes.Count);
        Assert.Equal(1, (int)modes.Single(m => (bool)m!["is_current"]!)!["width"]!);

        displays.ModesFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed");
        var failed = await h.Executor.ExecuteAsync(caller, "display_list_modes", TestJson.Args(new { device = "display-a" }), Ct);
        Assert.Equal(ErrorCodes.OsApiError, failed.Error!.Code);
    }
}
