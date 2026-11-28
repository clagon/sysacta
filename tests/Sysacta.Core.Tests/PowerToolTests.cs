using System.Text.Json;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Power;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

/// <summary>Phase 1E power tools with a fake adapter (no real power state is read or changed).</summary>
public class PowerToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] All = ["power_get_state", "power_list_plans", "power_set_plan", "power_lock", "power_sleep", "power_shutdown", "power_restart"];

    private static (Harness H, FakePower P, Caller C) Setup()
    {
        var p = new FakePower();
        var h = new Harness(_ =>
        [
            new PowerGetStateTool(p), new PowerListPlansTool(p), new PowerSetPlanTool(p),
            new PowerLockTool(p), new PowerSleepTool(p), new PowerShutdownTool(p), new PowerRestartTool(p),
        ]);
        var (caller, _) = h.Store.AddClient("c1", All);
        return (h, p, caller);
    }

    private static Task<ToolResult> Call(Harness h, Caller c, string tool, string? json = null) =>
        h.Executor.ExecuteAsync(c, tool, json is null ? null : TestJson.Raw(json), Ct);

    private static string SetPlan(Guid id) => $$"""{"plan_id":"{{id}}"}""";

    // ---- descriptors and schemas -------------------------------------------------------------------------------

    [Fact]
    public void Defaults_follow_spec_and_only_the_four_transitions_are_exclusive()
    {
        var (h, _, _) = Setup();
        var tools = h.Executor.Registry.All.ToDictionary(t => t.Name);
        Assert.Equal(All.Order(), tools.Keys.Order());
        Assert.Equal(DefaultPolicy.On, tools["power_get_state"].Descriptor.DefaultPolicy);
        Assert.Equal(DefaultPolicy.On, tools["power_list_plans"].Descriptor.DefaultPolicy);
        Assert.Equal(DefaultPolicy.On, tools["power_lock"].Descriptor.DefaultPolicy);
        foreach (var off in new[] { "power_set_plan", "power_sleep", "power_shutdown", "power_restart" })
        {
            Assert.Equal(DefaultPolicy.Off, tools[off].Descriptor.DefaultPolicy);
        }

        Assert.Equal(PowerTransition.Lock, tools["power_lock"].Descriptor.Transition);
        Assert.Equal(PowerTransition.Sleep, tools["power_sleep"].Descriptor.Transition);
        Assert.Equal(PowerTransition.Shutdown, tools["power_shutdown"].Descriptor.Transition);
        Assert.Equal(PowerTransition.Restart, tools["power_restart"].Descriptor.Transition);
        Assert.Null(tools["power_set_plan"].Descriptor.Transition);
        Assert.True(tools["power_get_state"].Descriptor.ReadOnly);
        Assert.True(tools["power_list_plans"].Descriptor.ReadOnly);
        Assert.All(tools.Values, t => Assert.Empty(t.Descriptor.AuditedArguments));
    }

    [Fact]
    public void Schemas_are_strict_and_only_set_plan_takes_an_argument()
    {
        var (h, _, _) = Setup();
        foreach (var tool in h.Executor.Registry.All)
        {
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            var properties = tool.InputSchema.TryGetProperty("properties", out var p) ? p.EnumerateObject().Select(x => x.Name).ToArray() : [];
            Assert.Equal(tool.Name == "power_set_plan" ? ["plan_id"] : [], properties);
        }

        var planId = h.Executor.Registry.Find("power_set_plan")!.InputSchema.GetProperty("properties").GetProperty("plan_id");
        Assert.Equal(36, planId.GetProperty("maxLength").GetInt32());
        Assert.Contains("pattern", planId.GetRawText(), StringComparison.Ordinal);
    }

    // ---- power_get_state ----------------------------------------------------------------------------------------

    [Fact]
    public void Status_interpretation_handles_unknown_reserved_and_absent_values()
    {
        // Desktop on AC without a battery.
        var desktop = PowerStatus.Interpret(new(1, 128, 255, 0, uint.MaxValue, uint.MaxValue), FakePower.Balanced, new(false, false, true, true, true, false));
        Assert.Equal(PowerSource.Online, desktop.AcPower);
        Assert.Equal(BatteryPresence.Absent, desktop.Battery);
        Assert.Null(desktop.BatteryCharging);
        Assert.Null(desktop.BatteryPercent);
        Assert.Null(desktop.BatterySecondsRemaining);
        Assert.False(desktop.BatterySaver);
        Assert.True(desktop.SleepSupported);
        Assert.True(desktop.HibernateSupported);
        Assert.False(desktop.ModernStandby);
        Assert.Empty(desktop.Unavailable);

        // Laptop on battery, battery saver on.
        var laptop = PowerStatus.Interpret(new(0, 1, 80, 1, 3600, 7200), null, null);
        Assert.Equal((PowerSource.Offline, BatteryPresence.Present, false, 80, 3600L, 7200L, true), (laptop.AcPower, laptop.Battery, laptop.BatteryCharging, laptop.BatteryPercent, laptop.BatterySecondsRemaining, laptop.BatteryFullSeconds, laptop.BatterySaver));
        Assert.Equal([PowerStatePart.ActivePlan, PowerStatePart.Capabilities], laptop.Unavailable);
        Assert.Null(laptop.SleepSupported);

        // Charging, between low and high (flag 0 + charging bit), time unknown on AC.
        var charging = PowerStatus.Interpret(new(1, 8, 50, 0, uint.MaxValue, uint.MaxValue), null, null);
        Assert.Equal((BatteryPresence.Present, true, 50), (charging.Battery, charging.BatteryCharging, charging.BatteryPercent));
        Assert.Null(charging.BatterySecondsRemaining);
        Assert.False(PowerStatus.Interpret(new(1, 0, 50, 0, 0, 0), null, null).BatteryCharging);

        // Everything unknown; reserved values are never reported as numbers.
        var unknown = PowerStatus.Interpret(new(255, 255, 255, 255, uint.MaxValue, uint.MaxValue), null, null);
        Assert.Equal((PowerSource.Unknown, BatteryPresence.Unknown), (unknown.AcPower, unknown.Battery));
        Assert.Null(unknown.BatteryCharging);
        Assert.Null(unknown.BatteryPercent);
        Assert.Null(unknown.BatterySaver);
        Assert.Null(PowerStatus.Interpret(new(7, 2, 150, 9, 5, 5), null, null).BatteryPercent);
        Assert.Equal(PowerSource.Unknown, PowerStatus.Interpret(new(7, 2, 150, 9, 5, 5), null, null).AcPower);

        // Modern Standby only: no S1-S3, hibernation without a file is not supported.
        var standby = PowerStatus.Interpret(null, null, new(false, false, false, true, false, true));
        Assert.Equal((false, false, true), (standby.SleepSupported, standby.HibernateSupported, standby.ModernStandby));
        Assert.Contains(PowerStatePart.PowerStatus, standby.Unavailable);
    }

    [Fact]
    public async Task Get_state_keeps_readable_parts_and_fails_only_when_nothing_is_readable()
    {
        var (h, p, c) = Setup();
        p.Status = null;

        var partial = await Call(h, c, "power_get_state");
        Assert.True(partial.IsSuccess);
        Assert.Equal(FakePower.Balanced.ToString(), partial.Data!["active_plan_id"]!.GetValue<string>());
        Assert.Equal("power_status", partial.Data["unavailable"]![0]!.GetValue<string>());
        Assert.Equal("unknown", partial.Data["ac_power"]!.GetValue<string>());

        p.Active = null;
        p.Capabilities = null;
        var none = await Call(h, c, "power_get_state");
        Assert.Equal(ErrorCodes.OsApiError, none.Error!.Code);
    }

    // ---- power_list_plans ---------------------------------------------------------------------------------------

    [Fact]
    public async Task List_plans_limits_sanitizes_and_reports_failures()
    {
        var (h, p, c) = Setup();
        p.Plans.Clear();
        for (var i = 0; i < 300; i++)
        {
            p.Plans.Add(new(Guid.CreateVersion7(), i switch
            {
                0 => "@%SystemRoot%\\system32\\powrprof.dll,-14",
                1 => "Bal\u0007anced\u202e",
                2 => null,
                3 => new string('x', 100),
                _ => "plan " + i,
            }));
        }

        p.Plans.Add(new(FakePower.Balanced, "Balanced")); // beyond the enumeration limit

        var result = await Call(h, c, "power_list_plans");

        var data = result.Data!;
        Assert.Equal(64, data["plans"]!.AsArray().Count);
        Assert.Equal(256, data["total_count"]!.GetValue<int>());
        Assert.True(data["truncated"]!.GetValue<bool>());
        Assert.Equal(1, data["failed_count"]!.GetValue<int>());
        Assert.Null(data["plans"]![0]!["name"]);
        Assert.Equal("Balanced", data["plans"]![1]!["name"]!.GetValue<string>());
        Assert.Null(data["plans"]![2]!["name"]);
        Assert.Equal(64, data["plans"]![3]!["name"]!.GetValue<string>().Length);
        Assert.False(data["plans"]![4]!["is_active"]!.GetValue<bool>());
        Assert.Equal(FakePower.Balanced.ToString(), data["active_plan_id"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", data["plans"]![0]!["plan_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task List_plans_distinguishes_an_unreadable_active_plan_from_an_empty_list()
    {
        var (h, p, c) = Setup();
        p.Active = null;

        var data = (await Call(h, c, "power_list_plans")).Data!;
        Assert.True(data["active_plan_read_failed"]!.GetValue<bool>());
        Assert.Null(data["active_plan_id"]);
        Assert.Null(data["plans"]![0]!["is_active"]);

        p.Active = FakePower.Balanced;
        p.Plans.Clear();
        var empty = (await Call(h, c, "power_list_plans")).Data!;
        Assert.Empty(empty["plans"]!.AsArray());
        Assert.False(empty["active_plan_read_failed"]!.GetValue<bool>());

        p.ListFails = true;
        Assert.Equal(ErrorCodes.OsApiError, (await Call(h, c, "power_list_plans")).Error!.Code);
    }

    [Theory]
    [InlineData("Balanced", "Balanced")]
    [InlineData("  バランス  ", "バランス")]
    [InlineData("", null)]
    [InlineData("\u0001\u0002", null)]
    [InlineData("C:\\plans\\x", null)]
    [InlineData("%TEMP%", null)]
    [InlineData("@powrprof.dll,-1", null)]
    [InlineData("C:/Users/example/private-plan", null)]
    [InlineData("/home/example/plan", null)]
    [InlineData("plans/private", null)]
    [InlineData("https://example.invalid/plan", null)]
    [InlineData("mailto:someone@example.invalid", null)]
    [InlineData("C:plan", null)]
    [InlineData("\u0007@powrprof.dll,-1", null)]
    [InlineData("Lenovo Default", "Lenovo Default")]
    [InlineData("高パフォーマンス", "高パフォーマンス")]
    public void Plan_names_are_sanitized(string raw, string? expected) => Assert.Equal(expected, PowerStatus.Name(raw));

    // ---- power_set_plan -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"plan_id":"A1841308-3541-4FAB-BC81-F71556F20B4A"}""")]
    [InlineData("""{"plan_id":"{a1841308-3541-4fab-bc81-f71556f20b4a}"}""")]
    [InlineData("""{"plan_id":"a18413083541-4fab-bc81-f71556f20b4a0"}""")]
    [InlineData("""{"plan_id":"a1841308-3541-4fab-bc81-f71556f20b4"}""")]
    [InlineData("""{"plan_id":""}""")]
    [InlineData("""{"plan_id":null}""")]
    [InlineData("""{"plan_id":123}""")]
    [InlineData("""{}""")]
    [InlineData("""{"plan_id":"a1841308-3541-4fab-bc81-f71556f20b4a","force":true}""")]
    [InlineData("""{"plan_id":"a1841308-3541-4fab-bc81-f71556f20b4a","name":"Power saver"}""")]
    public async Task Set_plan_rejects_malformed_or_extra_arguments(string json)
    {
        var (h, p, c) = Setup();

        var result = await Call(h, c, "power_set_plan", json);

        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
        Assert.Equal(0, p.PlanWrites);
    }

    [Fact]
    public async Task Set_plan_changes_once_and_verifies_by_reading_back()
    {
        var (h, p, c) = Setup();

        var result = await Call(h, c, "power_set_plan", SetPlan(FakePower.Saver));

        Assert.Equal(ToolStatus.Success, result.Status);
        Assert.Equal(Verification.Verified, result.Verification);
        Assert.Equal(FakePower.Balanced.ToString(), result.Data!["previous_plan_id"]!.GetValue<string>());
        Assert.Equal(FakePower.Saver.ToString(), result.Data["current_plan_id"]!.GetValue<string>());
        Assert.True(result.Data["changed"]!.GetValue<bool>());
        Assert.Equal(1, p.PlanWrites);
        var records = h.Audit.Events.Where(e => e.RequestId == result.RequestId).ToList();
        Assert.Equal([AuditOutcome.Pending, AuditOutcome.Success], records.Select(e => e.Outcome));
        Assert.All(records, e => Assert.Equal($$"""{"target":"{{FakePower.Saver}}"}""", e.Arguments));
        Assert.DoesNotContain(records, e => (e.Arguments ?? "").Contains("saver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Set_plan_to_the_active_plan_is_a_verified_no_op()
    {
        var (h, p, c) = Setup();

        var result = await Call(h, c, "power_set_plan", SetPlan(FakePower.Balanced));

        Assert.Equal(Verification.Verified, result.Verification);
        Assert.False(result.Data!["changed"]!.GetValue<bool>());
        Assert.Equal(0, p.PlanWrites);
    }

    [Fact]
    public async Task Set_plan_refuses_plans_that_are_not_listed_exactly_once()
    {
        var (h, p, c) = Setup();
        Assert.Equal(ErrorCodes.TargetNotFound, (await Call(h, c, "power_set_plan", SetPlan(Guid.CreateVersion7()))).Error!.Code);

        p.Plans.Add(new(FakePower.Saver, "Power saver"));
        Assert.Equal(ErrorCodes.AmbiguousTarget, (await Call(h, c, "power_set_plan", SetPlan(FakePower.Saver))).Error!.Code);

        p.Plans.Clear();
        for (var i = 0; i < 300; i++)
        {
            p.Plans.Add(new(Guid.CreateVersion7(), "x"));
        }

        p.Plans.Add(new(FakePower.Saver, "Power saver")); // only beyond the enumeration limit: cannot be confirmed
        Assert.Equal(ErrorCodes.TargetNotFound, (await Call(h, c, "power_set_plan", SetPlan(FakePower.Saver))).Error!.Code);
        Assert.Equal(0, p.PlanWrites);
    }

    [Fact]
    public async Task Set_plan_rechecks_the_plan_right_before_the_start_point()
    {
        var (h, p, c) = Setup();
        // Removed while the pending record is written (after resolution, before the final check).
        h.Audit.OnWrite = e =>
        {
            if (e.Outcome == AuditOutcome.Pending)
            {
                p.Plans.RemoveAll(x => x.PlanId == FakePower.Saver);
            }
        };

        var result = await Call(h, c, "power_set_plan", SetPlan(FakePower.Saver));

        Assert.Equal(ErrorCodes.TargetNotFound, result.Error!.Code);
        Assert.Equal(0, p.PlanWrites);
    }

    [Fact]
    public async Task Set_plan_reports_os_failures_without_retrying()
    {
        var (h, p, c) = Setup();
        p.PlanResult = PowerRequestResult.Failed;
        var unchanged = await Call(h, c, "power_set_plan", SetPlan(FakePower.Saver));
        Assert.Equal(ToolStatus.Error, unchanged.Status);
        Assert.Equal(ErrorCodes.OsApiError, unchanged.Error!.Code);
        Assert.False(unchanged.Error.Retryable);
        Assert.False(unchanged.Data!["changed"]!.GetValue<bool>());
        Assert.Equal(1, p.PlanWrites);

        // An error, but the plan did change: unknown, never "not applied".
        p.AfterWrite = id => id;
        var odd = await Call(h, c, "power_set_plan", SetPlan(FakePower.Saver));
        Assert.Equal(ToolStatus.Unknown, odd.Status);
        Assert.Equal(2, p.PlanWrites);
    }

    [Fact]
    public async Task Set_plan_accepted_without_readback_is_accepted_and_a_mismatch_fails_verification()
    {
        var (h, p, c) = Setup();
        p.ReadBackFails = true;
        var accepted = await Call(h, c, "power_set_plan", SetPlan(FakePower.Saver));
        Assert.Equal(Verification.Accepted, accepted.Verification);
        Assert.Null(accepted.Data!["changed"]);
        Assert.Null(accepted.Data["current_plan_id"]);

        var (h2, p2, c2) = Setup();
        var other = Guid.CreateVersion7();
        p2.Plans.Add(new(other, "Other"));
        p2.AfterWrite = _ => other;
        var mismatch = await Call(h2, c2, "power_set_plan", SetPlan(FakePower.Saver));
        Assert.Equal(ToolStatus.Unknown, mismatch.Status); // something changed, but not to the requested plan
        Assert.Equal(ErrorCodes.VerificationFailed, mismatch.Error!.Code);
        Assert.Equal(1, p2.PlanWrites);
    }

    // ---- transitions -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("power_lock", "Lock", false)]
    [InlineData("power_sleep", "Sleep", true)]
    [InlineData("power_shutdown", "Shutdown", true)]
    [InlineData("power_restart", "Restart", true)]
    public async Task Each_transition_sends_one_request_inside_its_privilege_scope(string tool, string operation, bool privileged)
    {
        var (h, p, c) = Setup();

        var result = await Call(h, c, tool);

        Assert.Equal(ToolStatus.Success, result.Status);
        Assert.Equal(Verification.Accepted, result.Verification);
        Assert.True(result.Data!["accepted"]!.GetValue<bool>());
        Assert.Equal(JsonNamingPolicy.SnakeCaseLower.ConvertName(operation), result.Data["operation"]!.GetValue<string>());
        Assert.Equal(1, p.Requests);
        Assert.Equal(privileged ? ["prepare", "request:" + operation, "revert"] : ["prepare", "request:" + operation], p.Log);
        Assert.Equal(0, p.OpenPrivilegeScopes);
        Assert.Equal(0, p.PlanWrites);
    }

    [Fact]
    public async Task Sleep_is_refused_before_the_start_point_when_S1_to_S3_are_not_supported()
    {
        var (h, p, c) = Setup();
        p.Capabilities = new(false, false, false, true, true, true);
        var tool = h.Executor.Registry.Find("power_sleep")!;
        Assert.Equal(ErrorCodes.NotSupported, tool.GetAvailability().ErrorCode);

        var result = await Call(h, c, "power_sleep");
        Assert.Equal(ErrorCodes.NotSupported, result.Error!.Code);
        Assert.Equal(0, p.Requests);

        // An unreadable capability is not proof of permanent unavailability (the call checks again).
        p.Capabilities = null;
        Assert.True(tool.GetAvailability().IsAvailable);
        Assert.Equal(ErrorCodes.OsApiError, (await Call(h, c, "power_sleep")).Error!.Code);
        Assert.Equal(0, p.Requests);
    }

    [Fact]
    public void Tools_needing_the_shutdown_privilege_are_hidden_when_the_account_lacks_it()
    {
        var (h, p, _) = Setup();
        p.HoldsPrivilege = false;
        foreach (var name in new[] { "power_sleep", "power_shutdown", "power_restart" })
        {
            Assert.False(h.Executor.Registry.Find(name)!.GetAvailability().IsAvailable);
        }

        Assert.True(h.Executor.Registry.Find("power_lock")!.GetAvailability().IsAvailable);
        Assert.True(h.Executor.Registry.Find("power_set_plan")!.GetAvailability().IsAvailable);
    }

    [Theory]
    [InlineData(PowerRequestResult.Refused, "OS_API_ERROR")]
    [InlineData(PowerRequestResult.Busy, "BUSY")]
    [InlineData(PowerRequestResult.Failed, "OS_API_ERROR")]
    public async Task Os_refusals_are_errors_without_retry_or_fallback(PowerRequestResult outcome, string code)
    {
        foreach (var tool in new[] { "power_lock", "power_sleep", "power_shutdown", "power_restart" })
        {
            var (h, p, c) = Setup();
            p.RequestResult = outcome;

            var result = await Call(h, c, tool);

            Assert.Equal(code, result.Error!.Code);
            Assert.False(result.Error.Retryable);
            Assert.Equal(1, p.Requests); // never retried, never another transition
            Assert.Single(p.Log, e => e.StartsWith("request", StringComparison.Ordinal));
            Assert.Equal(AuditOutcome.Error, h.Audit.Final(result.RequestId).Outcome);
        }
    }
}
