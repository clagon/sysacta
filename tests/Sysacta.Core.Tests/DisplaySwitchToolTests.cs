using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

public class MccsTests
{
    [Theory]
    [InlineData("(prot(monitor)vcp(02 10 60(0F 11) D6(01 04))mccs_ver(2.2))")]
    [InlineData("prot(monitor)vcp(02 10 60(0F 11) D6(01 04))mccs_ver(2.2)")]
    [InlineData("( prot(monitor) vcp(02 10 60( 0F 11 ) D6(01 04)) mccs_ver(2.2) )")]
    [InlineData("(vcp(02 10 60(0f 11) d6(01 04))VCPNAME(14(Color))MCCS_VER(2.2))")]
    public void Well_formed_strings_are_parsed(string text)
    {
        var caps = Mccs.Parse(text);

        Assert.NotNull(caps);
        Assert.Equal(2, caps.MajorVersion);
        Assert.Equal<byte>([0x0F, 0x11], caps.Vcp[0x60]!);
        Assert.Equal<byte>([0x01, 0x04], caps.Vcp[0xD6]!);
        Assert.Null(caps.Vcp[0x10]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(vcp(60(0F 11) D6(01 04))")] // unbalanced
    [InlineData("vcp(60(0F 11) D6(01 04)))")] // unbalanced
    [InlineData("(vcp(60(0F 11 1)))")] // value not two hex digits
    [InlineData("(vcp(60(0F 11) 60(12)))")] // repeated code
    [InlineData("(vcp(6G(0F)))")] // not hex
    [InlineData("(vcp(60(0F(01))))")] // value with children
    [InlineData("(vcp(60(0F)) vcp(D6(01)))")] // two vcp lists
    [InlineData("(prot(monitor)mccs_ver(2.1))")] // no vcp
    [InlineData("(vcp(60(0F\u0001)))")] // control character
    [InlineData("(vcp(60(0F é)))")] // non-ASCII
    [InlineData("(a(b(c(d(e(vcp(60)))))))")] // too deep
    [InlineData("(vcp(60 (0F 11)))")] // value list detached from its code
    public void Malformed_strings_are_refused(string text) => Assert.Null(Mccs.Parse(text));

    [Fact]
    public void Length_and_element_count_are_bounded()
    {
        Assert.Null(Mccs.Parse("(vcp(" + string.Join(' ', Enumerable.Repeat("10", 3000)) + "))"));
        Assert.Null(Mccs.Parse("(vcp(10)" + new string(' ', Mccs.MaxCapabilitiesLength) + ")"));
        Assert.Throws<ToolFailureException>(() => Mccs.ParseOrThrow("(vcp(60"));
    }

    [Theory]
    [InlineData("(vcp(60(0F) D6(01 04)))", null)]
    [InlineData("(vcp(60(0F) D6(01 04))mccs_ver(3.0))", 3)]
    [InlineData("(vcp(60(0F) D6(01 04))mccs_ver(x))", null)]
    public void Without_mccs_2_nothing_is_usable(string text, int? major)
    {
        var caps = Mccs.Parse(text)!;

        Assert.Equal(major, caps.MajorVersion);
        Assert.Equal(ErrorCodes.NotSupported, Assert.Throws<ToolFailureException>(() => Mccs.Usable(caps, DisplaySwitch.Power)).Code);
        Assert.Equal(ErrorCodes.NotSupported, Assert.Throws<ToolFailureException>(() => Mccs.Usable(caps, DisplaySwitch.Input)).Code);
    }

    [Theory]
    [InlineData("(vcp(10)mccs_ver(2.1))")] // not declared
    [InlineData("(vcp(D6 60)mccs_ver(2.1))")] // declared without values
    [InlineData("(vcp(D6(01 05) 60(1B 00))mccs_ver(2.1))")] // only off-list values (no 0x04 off; manufacturer inputs)
    public void Missing_or_partial_declarations_are_not_guessed(string text)
    {
        var caps = Mccs.Parse(text)!;

        Assert.Throws<ToolFailureException>(() => Mccs.Usable(caps, DisplaySwitch.Power));
        Assert.Throws<ToolFailureException>(() => Mccs.Usable(caps, DisplaySwitch.Input));
    }

    [Fact]
    public void Only_the_fixed_allow_list_is_usable()
    {
        var caps = Mccs.Parse("(vcp(D6(01 02 03 04 05) 60(00 0F 11 1B 12 11))mccs_ver(2.2))")!;

        Assert.Equal<byte>([0x01, 0x04], Mccs.Usable(caps, DisplaySwitch.Power));
        Assert.Equal<byte>([0x0F, 0x11, 0x12], Mccs.Usable(caps, DisplaySwitch.Input));
        Assert.False(Mccs.IsAllowed(DisplaySwitch.Power, 0x05));
        Assert.False(Mccs.IsAllowed(DisplaySwitch.Input, 0x13));
        Assert.True(Mccs.IsAllowed(DisplaySwitch.Input, 0x01));
    }

    [Fact]
    public void Input_ids_are_deterministic_and_bound_to_the_display()
    {
        Assert.Equal(Mccs.InputId("display-a", 0x11), Mccs.InputId("display-a", 0x11));
        Assert.NotEqual(Mccs.InputId("display-a", 0x11), Mccs.InputId("display-b", 0x11));
        Assert.NotEqual(Mccs.InputId("display-a", 0x11), Mccs.InputId("display-a", 0x12));
        Assert.Matches("^input-[0-9a-f]{24}$", Mccs.InputId("display-a", 0x11));
    }
}

public class DisplaySwitchToolTests
{
    private const string Power = "display_set_power";
    private const string Input = "display_set_input";
    private const string Device = "display-b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Harness H, FakeDisplays Displays, Caller Caller) Setup()
    {
        var displays = new FakeDisplays();
        var h = new Harness(store =>
        [
            new DisplaySetPowerTool(displays, store),
            new DisplaySetInputTool(displays, store),
            new DisplayGetStateTool(displays, store),
        ]);
        var (caller, _) = h.Store.AddClient("c1", Power, Input, "display_get_state");
        return (h, displays, caller);
    }

    private static Task<ToolResult> SetPower(Harness h, Caller caller, string state, string device = Device) =>
        h.Executor.ExecuteAsync(caller, Power, TestJson.Args(new { device, state }), Ct);

    private static Task<ToolResult> SetInput(Harness h, Caller caller, byte value, string device = Device, string? idOf = null) =>
        h.Executor.ExecuteAsync(caller, Input, TestJson.Args(new { device, input_id = Mccs.InputId(idOf ?? device, value) }), Ct);

    [Fact]
    public async Task Get_state_does_not_query_switching_unless_asked()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].CapabilitiesFailure = new InvalidOperationException("must not be queried");

        var r = await h.Executor.ExecuteAsync(caller, "display_get_state", TestJson.Args(new { device = Device }), Ct);

        Assert.True(r.IsSuccess);
        Assert.Equal("unchecked", (string)r.Data!["power"]!["support"]!);
        Assert.Equal("unchecked", (string)r.Data["input"]!["support"]!);
        Assert.Empty(r.Data["input"]!["inputs"]!.AsArray());
        Assert.Null(r.Data["power"]!["state"]);
    }

    [Fact]
    public async Task Get_state_reports_power_and_standard_inputs_when_asked()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].Input = 0x11;

        var r = await h.Executor.ExecuteAsync(caller, "display_get_state", TestJson.Args(new { device = Device, include_switching = true }), Ct);

        Assert.True(r.IsSuccess);
        Assert.Equal("supported", (string)r.Data!["power"]!["support"]!);
        Assert.Equal("on", (string)r.Data["power"]!["state"]!);
        var inputs = r.Data["input"]!["inputs"]!.AsArray();
        Assert.Equal(["DisplayPort 1", "HDMI 1", "HDMI 2"], inputs.Select(i => (string)i!["name"]!));
        Assert.Equal(Mccs.InputId(Device, 0x11), (string)r.Data["input"]!["current_input_id"]!);
        Assert.Equal([false, true, false], inputs.Select(i => (bool)i!["is_current"]!));
        Assert.Equal(0, displays.OpenControls);
    }

    [Theory]
    [InlineData("internal", "not_supported", ErrorCodes.NotSupported)]
    [InlineData("cloned", "unavailable", ErrorCodes.AmbiguousTarget)]
    [InlineData("query", "unavailable", ErrorCodes.OsApiError)]
    [InlineData("malformed", "unavailable", ErrorCodes.OsApiError)]
    [InlineData("mccs3", "not_supported", ErrorCodes.NotSupported)]
    public async Task Get_state_reports_why_switching_is_not_available(string kind, string support, string code)
    {
        var (h, displays, caller) = Setup();
        var state = displays.Switches[Device];
        switch (kind)
        {
            case "internal": displays.Replace(Device, d => d with { Kind = DisplayKind.Internal }); break;
            case "cloned": displays.Replace(Device, d => d with { IsCloned = true }); break;
            case "query": state.CapabilitiesFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed"); break;
            case "malformed": state.Capabilities = "(vcp(60(0F"; break;
            case "mccs3": state.Capabilities = "(vcp(60(0F) D6(01 04))mccs_ver(3.0))"; break;
        }

        var r = await h.Executor.ExecuteAsync(caller, "display_get_state", TestJson.Args(new { device = Device, include_switching = true }), Ct);

        Assert.True(r.IsSuccess);
        foreach (var name in new[] { "power", "input" })
        {
            Assert.Equal(support, (string)r.Data![name]!["support"]!);
            Assert.Equal(code, (string)r.Data[name]!["error_code"]!);
        }

        Assert.Empty(r.Data!["input"]!["inputs"]!.AsArray());
    }

    [Fact]
    public async Task A_manufacturer_input_value_is_reported_without_an_id()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].Input = 0x1B;

        var r = await h.Executor.ExecuteAsync(caller, "display_get_state", TestJson.Args(new { device = Device, include_switching = true }), Ct);

        Assert.Equal("supported", (string)r.Data!["input"]!["support"]!);
        Assert.Null(r.Data["input"]!["current_input_id"]);
    }

    [Theory]
    [InlineData("off", 0x01, 0x04)]
    [InlineData("on", 0x04, 0x01)]
    [InlineData("on", 0x02, 0x01)]
    public async Task Power_is_written_once_with_the_allow_listed_value_and_verified(string state, byte before, byte written)
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].Power = before;

        var r = await SetPower(h, caller, state);

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.Equal(state, (string)r.Data!["current_state"]!);
        Assert.True((bool)r.Data["changed"]!);
        Assert.Equal([(Device, DisplaySwitch.Power, written)], displays.SwitchWrites);
        Assert.Equal($$"""{"state":"{{state}}","target":"display-b"}""", h.Audit.Final(r.RequestId).Arguments);
    }

    [Fact]
    public async Task Power_already_in_the_requested_state_is_a_no_op()
    {
        var (h, displays, caller) = Setup();

        var r = await SetPower(h, caller, "on");

        Assert.True(r.IsSuccess);
        Assert.False((bool)r.Data!["changed"]!);
        Assert.Empty(displays.SwitchWrites);
    }

    [Fact]
    public async Task Losing_the_monitor_after_an_accepted_power_off_is_accepted_not_verified()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].ReadFailureAfterWrite = new ToolFailureException(ErrorCodes.OsApiError, "fixed");

        var r = await SetPower(h, caller, "off");

        Assert.Equal(ToolStatus.Success, r.Status);
        Assert.Equal(Verification.Accepted, r.Verification);
        Assert.Null(r.Data!["current_state"]);
        Assert.Null(r.Data["changed"]);
        Assert.Single(displays.SwitchWrites);
    }

    [Fact]
    public async Task A_failed_power_write_is_unknown_and_never_retried()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].WriteFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed");

        var r = await SetPower(h, caller, "off");

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(Verification.Unknown, r.Verification);
        Assert.Null(r.Data!["changed"]);
        Assert.Equal("on", (string)r.Data["previous_state"]!);
        Assert.Single(displays.SwitchWrites);
        Assert.Equal(AuditOutcome.Unknown, h.Audit.Final(r.RequestId).Outcome);
    }

    [Fact]
    public async Task A_power_state_read_back_differently_fails_verification()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].Apply = (_, _) => null;

        var r = await SetPower(h, caller, "off");

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.VerificationFailed, r.Error?.Code);
        Assert.False((bool)r.Data!["changed"]!);
        Assert.Single(displays.SwitchWrites);
    }

    [Fact]
    public async Task A_different_value_read_back_once_is_never_reported_as_accepted()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].Apply = (_, _) => null;
        var reads = 0;
        // Read 1 is before the write, read 2 is the first read-back; afterwards the monitor stops answering.
        displays.OnRead = _ =>
        {
            if (++reads > 2)
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "fixed");
            }
        };

        var r = await SetPower(h, caller, "off");

        Assert.Equal(ErrorCodes.VerificationFailed, r.Error?.Code);
        Assert.Equal("on", (string)r.Data!["current_state"]!);
        Assert.Single(displays.SwitchWrites);
    }

    [Theory]
    [InlineData("undefined")]
    [InlineData("unreadable")]
    [InlineData("no-d6")]
    [InlineData("no-off")]
    [InlineData("internal")]
    [InlineData("virtual")]
    [InlineData("cloned")]
    public async Task Power_is_not_changed_without_evidence(string kind)
    {
        var (h, displays, caller) = Setup();
        var state = displays.Switches[Device];
        switch (kind)
        {
            case "undefined": state.Power = 0x07; break;
            case "unreadable": state.ReadFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed"); break;
            case "no-d6": state.Capabilities = "(vcp(10 60(0F))mccs_ver(2.1))"; break;
            case "no-off": state.Capabilities = "(vcp(D6(01 05))mccs_ver(2.1))"; break;
            case "internal": displays.Replace(Device, d => d with { Kind = DisplayKind.Internal }); break;
            case "virtual": displays.Replace(Device, d => d with { Kind = DisplayKind.Virtual }); break;
            case "cloned": displays.Replace(Device, d => d with { IsCloned = true }); break;
        }

        var r = await SetPower(h, caller, "off");

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Null(r.Data);
        Assert.Empty(displays.SwitchWrites);
        Assert.Equal(0, displays.OpenControls);
    }

    [Theory]
    [InlineData("""{"device":"display-b","state":"standby"}""")]
    [InlineData("""{"device":"display-b","state":"Off"}""")]
    [InlineData("""{"device":"display-b","state":4}""")]
    [InlineData("""{"device":"display-b"}""")]
    [InlineData("""{"state":"off"}""")]
    [InlineData("""{"device":"all","state":"off"}""")]
    [InlineData("""{"device":"default","state":"off"}""")]
    [InlineData("""{"device":"display-b","state":"off","vcp":214}""")]
    public async Task Power_arguments_are_strict(string json)
    {
        var (h, displays, caller) = Setup();

        var r = await h.Executor.ExecuteAsync(caller, Power, TestJson.Raw(json), Ct);

        Assert.Equal(ErrorCodes.InvalidArgument, r.Error?.Code);
        Assert.Empty(displays.SwitchWrites);
    }

    [Fact]
    public async Task An_input_is_switched_once_and_verified()
    {
        var (h, displays, caller) = Setup();

        var r = await SetInput(h, caller, 0x11);

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.Equal(Mccs.InputId(Device, 0x0F), (string)r.Data!["previous_input_id"]!);
        Assert.Equal(Mccs.InputId(Device, 0x11), (string)r.Data["current_input_id"]!);
        Assert.Equal([(Device, DisplaySwitch.Input, (byte)0x11)], displays.SwitchWrites);
        Assert.Equal("""{"target":"display-b"}""", h.Audit.Final(r.RequestId).Arguments);
    }

    [Fact]
    public async Task The_current_input_is_a_no_op()
    {
        var (h, displays, caller) = Setup();

        var r = await SetInput(h, caller, 0x0F);

        Assert.True(r.IsSuccess);
        Assert.False((bool)r.Data!["changed"]!);
        Assert.Empty(displays.SwitchWrites);
    }

    [Theory]
    [InlineData("other-display")]
    [InlineData("undeclared")]
    [InlineData("vanished")]
    public async Task Input_ids_not_declared_by_this_monitor_now_are_refused(string kind)
    {
        var (h, displays, caller) = Setup();
        ToolResult r;
        switch (kind)
        {
            case "other-display":
                r = await SetInput(h, caller, 0x11, idOf: "display-a");
                break;
            case "undeclared":
                r = await SetInput(h, caller, 0x03);
                break;
            default:
                displays.Switches[Device].Capabilities = "(vcp(60(0F 12))mccs_ver(2.1))";
                r = await SetInput(h, caller, 0x11);
                break;
        }

        Assert.Equal(ErrorCodes.InvalidArgument, r.Error?.Code);
        Assert.Empty(displays.SwitchWrites);
    }

    [Fact]
    public async Task Switching_away_from_a_manufacturer_input_reports_it_without_an_id()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].Input = 0x1B;

        var r = await SetInput(h, caller, 0x11);

        Assert.True(r.IsSuccess);
        Assert.Null(r.Data!["previous_input_id"]);
        Assert.Single(displays.SwitchWrites);
    }

    [Fact]
    public async Task Losing_the_monitor_after_an_accepted_input_switch_is_accepted()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].ReadFailureAfterWrite = new ToolFailureException(ErrorCodes.DeviceDisconnected, "fixed");

        var r = await SetInput(h, caller, 0x12);

        Assert.Equal(ToolStatus.Success, r.Status);
        Assert.Equal(Verification.Accepted, r.Verification);
        Assert.Null(r.Data!["current_input_id"]);
        Assert.Null(r.Data["changed"]);
    }

    [Fact]
    public async Task A_failed_input_write_is_unknown_and_never_retried()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].WriteFailure = new ToolFailureException(ErrorCodes.DeviceDisconnected, "fixed");

        var r = await SetInput(h, caller, 0x12);

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Single(displays.SwitchWrites);
    }

    [Fact]
    public async Task An_unreadable_current_input_is_not_guessed()
    {
        var (h, displays, caller) = Setup();
        displays.Switches[Device].ReadFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed");

        var r = await SetInput(h, caller, 0x12);

        Assert.Equal(ErrorCodes.OsApiError, r.Error?.Code);
        Assert.Empty(displays.SwitchWrites);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("disable")]
    [InlineData("expire")]
    public async Task Losing_authorization_while_preparing_writes_nothing(string change)
    {
        var (h, displays, caller) = Setup();
        displays.OnPrepare = _ => h.Update(() =>
        {
            switch (change)
            {
                case "revoke": h.Store.Revoke("c1"); break;
                case "disable": h.Store.EnabledTools[Power] = false; h.Store.EnabledTools[Input] = false; break;
                default: h.Store.Clients["c1"] = h.Store.Clients["c1"] with { ExpiresAt = DateTimeOffset.UnixEpoch }; break;
            }
        });

        var power = await SetPower(h, caller, "off");
        var input = await SetInput(h, caller, 0x11);

        Assert.All([power, input], r => Assert.Contains(r.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled }));
        Assert.Empty(displays.SwitchWrites);
    }

    [Fact]
    public async Task A_hanging_power_write_is_unknown_and_holds_the_display()
    {
        var (h, displays, caller) = Setup();
        using var release = new SemaphoreSlim(0);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        displays.OnWrite = _ =>
        {
            cts.Cancel();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var r = await h.Executor.ExecuteAsync(caller, Power, TestJson.Args(new { device = Device, state = "off" }), cts.Token);

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal("on", (string)r.Data!["previous_state"]!);
        displays.OnWrite = null;
        var input = SetInput(h, caller, 0x11);
        await Task.Delay(100, Ct);
        Assert.False(input.IsCompleted);
        release.Release();
        Assert.True((await input).IsSuccess);
        Assert.Equal(2, displays.SwitchWrites.Count);
    }
}
