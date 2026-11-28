using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Sysacta.Core.Auditing;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Network;
using Sysacta.Core.Tools.SystemMonitoring;
using static Sysacta.Tests.FakeNetwork;

namespace Sysacta.Core.Tests;

/// <summary>network_set_adapter_enabled with a fake adapter: nothing is enabled or disabled on the test machine.</summary>
public partial class NetworkToolTests
{
    // ---- descriptors and validation ------------------------------------------------------------------------------------

    [Fact]
    public void Descriptors_follow_spec_defaults_and_schemas_are_strict()
    {
        var s = Create();
        var tools = s.H.Executor.Registry.All.ToDictionary(t => t.Name);
        Assert.Equal(All.Order(), tools.Keys.Order());
        foreach (var name in All[..3])
        {
            Assert.Equal(DefaultPolicy.On, tools[name].Descriptor.DefaultPolicy);
            Assert.True(tools[name].Descriptor.ReadOnly);
            Assert.Empty(tools[name].Descriptor.AuditedArguments);
            Assert.Equal(TimeSpan.FromSeconds(5), tools[name].Descriptor.EffectiveTimeout);
        }

        var set = tools["network_set_adapter_enabled"].Descriptor;
        Assert.Equal((DefaultPolicy.Off, false, TimeSpan.FromSeconds(15), (PowerTransition?)null), (set.DefaultPolicy, set.ReadOnly, set.EffectiveTimeout, set.Transition));
        Assert.Equal(["enabled"], set.AuditedArguments);
        Assert.Contains("DANGEROUS", set.Description, StringComparison.Ordinal);

        foreach (var tool in tools.Values)
        {
            Assert.Equal(ToolCategories.Network, tool.Descriptor.Category);
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        }

        var setSchema = tools["network_set_adapter_enabled"].InputSchema;
        Assert.Equal(["adapter", "enabled"], setSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Equal("boolean", setSchema.GetProperty("properties").GetProperty("enabled").GetProperty("type").GetString());
        Assert.Equal(64, setSchema.GetProperty("properties").GetProperty("adapter").GetProperty("maxLength").GetInt32());
        Assert.False(tools["network_get_traffic"].InputSchema.TryGetProperty("required", out _));
        var output = tools["network_get_traffic"].OutputSchema.GetRawText();
        Assert.Contains("received_bits_per_second", output, StringComparison.Ordinal);
        Assert.Contains("rate_unavailable_reason", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"adapter":"all","enabled":false}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":"DEFAULT","enabled":false}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":"","enabled":false}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":" net-22222222222222222222222222222222","enabled":false}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":null,"enabled":false}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"enabled":false}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":"net-22222222222222222222222222222222"}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":"net-22222222222222222222222222222222","enabled":"false"}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":"net-22222222222222222222222222222222","enabled":0}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":["net-22222222222222222222222222222222"],"enabled":false}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":"net-22222222222222222222222222222222","enabled":false,"force":true}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":"net-22222222222222222222222222222222\n","enabled":false}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":"net-22222222222222222222222222222222222222222222222222222222222222222","enabled":false}""", ErrorCodes.InvalidArgument)]
    [InlineData("""{"adapter":"NET-22222222222222222222222222222222","enabled":false}""", ErrorCodes.TargetNotFound)]
    [InlineData("""{"adapter":"Ethernet 2","enabled":false}""", ErrorCodes.TargetNotFound)]
    [InlineData("""{"adapter":"12","enabled":false}""", ErrorCodes.TargetNotFound)]
    [InlineData("""{"adapter":"192.0.2.10","enabled":false}""", ErrorCodes.TargetNotFound)]
    [InlineData("""{"adapter":"net-00000000000000000000000000000000","enabled":false}""", ErrorCodes.TargetNotFound)]
    public async Task Invalid_or_unresolvable_targets_are_refused_before_any_OS_access(string json, string code)
    {
        var s = Create();
        var r = await Call(s, "network_set_adapter_enabled", json);
        Assert.Equal(code, r.Error?.Code);
        Assert.Empty(s.N.Invocations);
        Assert.Equal(0, s.N.Snapshots);
    }

    [Fact]
    public async Task Unauthorized_disabled_or_revoked_callers_never_reach_the_adapter()
    {
        var s = Create();
        var (other, _) = s.H.Store.AddClient("c2", "network_list_adapters");
        Assert.Equal(ErrorCodes.PermissionDenied, (await Set(s, Spare, false, other)).Error?.Code);
        s.H.Store.EnabledTools["network_set_adapter_enabled"] = false;
        Assert.Equal(ErrorCodes.ToolDisabled, (await Set(s, Spare, false)).Error?.Code);
        s.H.Store.EnabledTools["network_set_adapter_enabled"] = true;
        s.H.Store.Revoke("c1");
        Assert.Equal(ErrorCodes.PermissionDenied, (await Set(s, Spare, false)).Error?.Code);
        Assert.Equal(ErrorCodes.PermissionDenied, (await Call(s, "network_get_traffic")).Error?.Code);
        Assert.Empty(s.N.Invocations);
        Assert.Equal(0, s.N.InterfaceReads);
    }

    [Fact]
    public async Task Without_elevation_the_change_tool_is_unavailable_and_hidden()
    {
        var s = Create();
        s.N.IsElevated = false;
        Assert.DoesNotContain(s.H.Executor.ListTools(s.C), t => t.Name == "network_set_adapter_enabled");
        Assert.Equal(ErrorCodes.AdminRequired, (await Set(s, Spare, false)).Error?.Code);
        var list = Data(await Call(s, "network_list_adapters"));
        Assert.All(list["adapters"]!.AsArray(), a => Assert.Equal("not_elevated", Str(a!["admin_change_blocked_reason"])));
        Assert.Empty(s.N.Invocations);
    }

    // ---- network_set_adapter_enabled: results ---------------------------------------------------------------------------

    [Fact]
    public async Task Disabling_an_unused_physical_adapter_calls_the_provider_once_and_verifies_by_reading_back()
    {
        var s = Create();
        var r = await Set(s, Spare, false);
        Assert.Equal((ToolStatus.Success, Verification.Verified), (r.Status, r.Verification));
        var data = Data(r);
        Assert.Equal((Spare, "enabled", "disabled", true), (Str(data["adapter_id"]), Str(data["previous_admin_state"]), Str(data["current_admin_state"]), data["changed"]!.GetValue<bool>()));
        Assert.Equal([(SpareGuid, false)], s.N.Invocations);

        var events = s.H.Audit.Events.Where(e => e.RequestId == r.RequestId).ToList();
        Assert.Equal([AuditOutcome.Pending, AuditOutcome.Success], events.Select(e => e.Outcome));
        Assert.All(events, e => Assert.Equal($$"""{"enabled":false,"target":"{{Spare}}"}""", e.Arguments));

        // Re-enabling uses the same id even though Windows now reports the adapter as disabled.
        s.N.Update(SpareGuid, row => row with { AdminStatus = 2 });
        r = await Set(s, Spare, true);
        Assert.Equal((ToolStatus.Success, Verification.Verified), (r.Status, r.Verification));
        Assert.Equal("enabled", Str(Data(r)["current_admin_state"]));
    }

    [Fact]
    public async Task A_request_for_the_current_state_is_a_verified_no_op_without_a_provider_call()
    {
        var s = Create();
        var r = await Set(s, Wired, true); // the active adapter: enabling it changes nothing
        Assert.Equal((ToolStatus.Success, Verification.Verified, false), (r.Status, r.Verification, Data(r)["changed"]!.GetValue<bool>()));
        s.N.SetProviderState(SpareGuid, 3);
        r = await Set(s, Spare, false);
        Assert.Equal((ToolStatus.Success, false), (r.Status, Data(r)["changed"]!.GetValue<bool>()));
        Assert.Empty(s.N.Invocations);
    }

    [Fact]
    public async Task An_adapter_missing_from_IP_Helper_can_be_enabled_only_after_a_complete_read()
    {
        var s = Create();
        s.N.SetProviderState(SpareGuid, 3);
        s.N.Interfaces.RemoveAll(r => r.InterfaceGuid == SpareGuid);
        s.N.Stack.RemoveAll(l => l.HigherIndex == 12 || l.LowerIndex == 12 || l.HigherIndex == 16 || l.LowerIndex == 16);
        var r = await Set(s, Spare, true);
        Assert.Equal((ToolStatus.Success, Verification.Verified), (r.Status, r.Verification));

        s.N.SetProviderState(SpareGuid, 3);
        s.N.StackProblem = UnavailableReason.Unavailable;
        r = await Set(s, Spare, true);
        Assert.Equal((ErrorCodes.TargetProtected, "information_incomplete"), (r.Error?.Code, Str(Data(r)["blocked_reason"])));

        s.N.StackProblem = null;
        s.N.SetProviderState(SpareGuid, 2);
        r = await Set(s, Spare, false); // enabled per provider but not in the interface table: never disabled
        Assert.Equal("information_incomplete", Str(Data(r)["blocked_reason"]));
        Assert.Single(s.N.Invocations);
    }

    [Fact]
    public async Task Provider_errors_after_the_start_point_are_errors_only_when_the_state_is_known_unchanged()
    {
        var s = Create();
        s.N.InvokeResult = 5;
        var r = await Set(s, Spare, false);
        Assert.Equal((ToolStatus.Error, ErrorCodes.OsApiError, false), (r.Status, r.Error?.Code, r.Error?.Retryable));
        Assert.Equal("enabled", Str(Data(r)["current_admin_state"]));

        s.N.InvokeResult = 0;
        s.N.ApplyOnInvoke = false; // accepted, but the state never changes within the bounded read-back
        r = await Set(s, Spare, false);
        Assert.Equal((ToolStatus.Unknown, ErrorCodes.VerificationFailed, false), (r.Status, r.Error?.Code, r.Error?.Retryable));

        s.N.InvokeThrows = new InvalidOperationException(@"WMI failure at C:\secret\path");
        r = await Set(s, Spare, false);
        Assert.Equal((ToolStatus.Unknown, ErrorCodes.OsApiError), (r.Status, r.Error?.Code));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(r), StringComparison.Ordinal);

        s.N.InvokeThrows = null;
        s.N.ApplyOnInvoke = true;
        s.N.DuringInvoke = () => s.N.ProviderFails = true; // read-back impossible
        r = await Set(s, Spare, false);
        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(4, s.N.Invocations.Count); // one call per request, never retried
        Assert.Contains(s.H.Audit.Events, e => e.RequestId == r.RequestId && e.Outcome == AuditOutcome.Unknown);
    }

    // ---- network_set_adapter_enabled: path protection -------------------------------------------------------------------

    public static TheoryData<string, string, string> Refusals => new()
    {
        { "wired-active", ErrorCodes.TargetProtected, "active_adapter" },
        { "wifi-active", ErrorCodes.TargetProtected, "active_adapter" },
        { "vpn", ErrorCodes.NotSupported, "unsupported" },
        { "hyperv", ErrorCodes.NotSupported, "unsupported" },
        { "loopback", ErrorCodes.NotSupported, "unsupported" },
        { "dormant", ErrorCodes.TargetProtected, "active_adapter" },
        { "media-unknown", ErrorCodes.TargetProtected, "active_adapter" },
        { "link-local-address", ErrorCodes.TargetProtected, "address_assigned" },
        { "tentative-address", ErrorCodes.TargetProtected, "address_assigned" },
        { "route", ErrorCodes.TargetProtected, "route_in_use" },
        { "team", ErrorCodes.TargetProtected, "dependency_present" },
        { "tunnel-over", ErrorCodes.TargetProtected, "dependency_present" },
        { "unknown-neighbour", ErrorCodes.TargetProtected, "dependency_present" },
        { "addresses-failed", ErrorCodes.TargetProtected, "information_incomplete" },
        { "routes-truncated", ErrorCodes.TargetProtected, "information_incomplete" },
        { "stack-failed", ErrorCodes.TargetProtected, "information_incomplete" },
        { "interfaces-truncated", ErrorCodes.TargetProtected, "information_incomplete" },
        { "provider-media-connected", ErrorCodes.TargetProtected, "active_adapter" },
        { "provider-virtual", ErrorCodes.NotSupported, "unsupported" },
        { "provider-hidden", ErrorCodes.NotSupported, "unsupported" },
        { "provider-bluetooth", ErrorCodes.NotSupported, "unsupported" },
        { "provider-luid", ErrorCodes.TargetProtected, "identity_mismatch" },
        { "provider-index", ErrorCodes.TargetProtected, "identity_mismatch" },
        { "bluetooth-medium", ErrorCodes.NotSupported, "unsupported" },
        { "no-connector", ErrorCodes.NotSupported, "unsupported" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Disabling_is_refused_whenever_use_cannot_be_ruled_out_and_nothing_is_called(string scenario, string code, string reason)
    {
        var s = Create();
        var target = scenario switch
        {
            "wired-active" => Wired,
            "wifi-active" => Wifi,
            "vpn" => Vpn,
            "hyperv" => HyperV,
            "loopback" => Loopback,
            _ => Spare,
        };
        foreach (var g in new[] { VpnGuid, HyperVGuid, LoopbackGuid })
        {
            var row = s.N.Interfaces.Single(r => r.InterfaceGuid == g);
            s.N.Providers.Add(new ProviderAdapter(g, row.Luid, row.Index, 2, false, true, false, false, 14, 1));
        }

        void Provider(Func<ProviderAdapter, ProviderAdapter> change)
        {
            var i = s.N.Providers.FindIndex(p => p.InterfaceGuid == SpareGuid);
            s.N.Providers[i] = change(s.N.Providers[i]);
        }

        switch (scenario)
        {
            case "dormant": s.N.Update(SpareGuid, r => r with { OperStatus = 5 }); break;
            case "media-unknown": s.N.Update(SpareGuid, r => r with { MediaConnectState = 0 }); break;
            case "link-local-address": s.N.Addresses.Add(new(0x2000, IPAddress.Parse("fe80::2%12"), 64, 4)); break;
            case "tentative-address": s.N.Addresses.Add(new(0x2000, IPAddress.Parse("169.254.0.2"), 16, 1)); break;
            case "route": s.N.Routes.Add(new(0x2000, AddressFamily.InterNetworkV6, 64)); break;
            case "team": s.N.Stack.Add(new(40, 16)); s.N.Interfaces.Add(Row(Guid.NewGuid(), 0xC000, 40, 6, 0, "Team", 2, 2, false)); break; // a team over the filter over the NIC
            case "tunnel-over": s.N.Stack.Add(new(41, 12)); s.N.Interfaces.Add(Row(Guid.NewGuid(), 0xC100, 41, 131, 0, "Tunnel", 2, 2, false)); break;
            case "unknown-neighbour": s.N.Stack.Add(new(12, 77)); break;
            case "addresses-failed": s.N.AddressesProblem = UnavailableReason.Unavailable; break;
            case "routes-truncated": s.N.RoutesProblem = UnavailableReason.Truncated; break;
            case "stack-failed": s.N.StackProblem = UnavailableReason.PermissionDenied; break;
            case "interfaces-truncated": s.N.Interfaces.Insert(0, s.N.Interfaces.Single(r => r.InterfaceGuid == SpareGuid)); s.N.Interfaces.RemoveAt(s.N.Interfaces.FindLastIndex(r => r.InterfaceGuid == SpareGuid)); s.N.InterfacesProblem = UnavailableReason.Truncated; break;
            case "provider-media-connected": Provider(p => p with { MediaConnectState = 1 }); break;
            case "provider-virtual": Provider(p => p with { Virtual = true }); break;
            case "provider-hidden": Provider(p => p with { Hidden = true }); break;
            case "provider-bluetooth": Provider(p => p with { NdisPhysicalMedium = 10 }); break;
            case "provider-luid": Provider(p => p with { NetLuid = 0x2001 }); break;
            case "provider-index": Provider(p => p with { InterfaceIndex = 99 }); break;
            case "bluetooth-medium": s.N.Update(SpareGuid, r => r with { PhysicalMediumType = 10 }); break;
            case "no-connector": s.N.Update(SpareGuid, r => r with { ConnectorPresent = false }); break;
        }

        var r = await Set(s, target, false);
        Assert.Equal((ToolStatus.Error, code, false), (r.Status, r.Error?.Code, r.Error?.Retryable));
        Assert.Equal(reason, Str(Data(r)["blocked_reason"]));
        Assert.Contains(reason, r.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2", r.Error.Message, StringComparison.Ordinal);
        Assert.Empty(s.N.Invocations);
        Assert.Equal(AuditOutcome.Error, s.H.Audit.Final(r.RequestId).Outcome);
    }

    [Fact]
    public async Task Enabling_is_limited_to_physical_adapters_without_dependencies()
    {
        var s = Create();
        var row = s.N.Interfaces.Single(r => r.InterfaceGuid == VpnGuid);
        s.N.Providers.Add(new ProviderAdapter(VpnGuid, row.Luid, row.Index, 3, false, true, false, false, 0, 2));
        var r = await Set(s, Vpn, true);
        Assert.Equal(("unsupported", ErrorCodes.NotSupported), (Str(Data(r)["blocked_reason"]), r.Error?.Code));

        s.N.SetProviderState(SpareGuid, 3);
        s.N.Stack.Add(new(42, 12));
        s.N.Interfaces.Add(Row(Guid.NewGuid(), 0xC200, 42, 6, 0, "Team", 2, 2, false));
        r = await Set(s, Spare, true);
        Assert.Equal("dependency_present", Str(Data(r)["blocked_reason"]));
        Assert.Empty(s.N.Invocations);
    }

    [Fact]
    public async Task Forwarded_headers_or_loopback_callers_are_not_evidence_the_decision_depends_only_on_the_adapter()
    {
        var s = Create();
        // Whatever the caller's address claims (a loopback proxy such as Tailscale Serve, or a forged address), the
        // active adapter stays protected and the unused one stays allowed.
        foreach (var remote in new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1", "192.0.2.10", null })
        {
            var caller = s.C with { RemoteAddress = remote };
            Assert.Equal("active_adapter", Str(Data(await Set(s, Wired, false, caller))["blocked_reason"]));
        }

        Assert.Empty(s.N.Invocations);
        Assert.True((await Set(s, Spare, false, s.C with { RemoteAddress = "203.0.113.9" })).IsSuccess);
    }

    // ---- concurrency, gates, audit --------------------------------------------------------------------------------------

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Bound);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task Changes_of_different_adapters_are_serialized_and_the_second_is_rechecked_after_waiting()
    {
        var s = Create();
        var otherGuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        s.N.Interfaces.Add(Row(otherGuid, 0xD000, 50, 6, 14, "Ethernet 3", 2, 2, true));
        s.N.Providers.Add(new ProviderAdapter(otherGuid, 0xD000, 50, 2, true, false, false, true, 14, 2));
        using var block = new SemaphoreSlim(0);
        s.N.Block = block;
        var first = Set(s, Spare, false);
        await WaitUntil(() => !s.N.Invocations.IsEmpty);
        var snapshots = s.N.Snapshots;
        var second = Set(s, Id(otherGuid), false);
        await Task.Delay(100, Ct);
        Assert.False(second.IsCompleted);
        Assert.Equal(snapshots, s.N.Snapshots); // judged only after the lock

        // While the first change runs, the second adapter gets an address: the re-check after waiting must see it.
        s.N.Addresses.Add(new(0xD000, IPAddress.Parse("192.0.2.77"), 24, 4));
        block.Release(10);
        Assert.True((await first.WaitAsync(Bound, Ct)).IsSuccess);
        var r = await second.WaitAsync(Bound, Ct);
        Assert.Equal("address_assigned", Str(Data(r)["blocked_reason"]));
        Assert.Equal(1, s.N.MaxConcurrentInvokes);
        Assert.Single(s.N.Invocations);
    }

    [Fact]
    public async Task An_alias_is_fixed_to_its_id_at_resolution_even_if_rebound_while_waiting()
    {
        var s = Create();
        s.H.Store.Aliases[(ToolCategories.Network, "spare")] = Spare;
        using var block = new SemaphoreSlim(0);
        s.N.Block = block;
        var first = Set(s, Spare, false);
        await WaitUntil(() => !s.N.Invocations.IsEmpty);
        var second = Set(s, "spare", true);
        await Task.Delay(100, Ct);
        s.H.Store.Aliases[(ToolCategories.Network, "spare")] = Wired; // rebound by the user while waiting
        block.Release(10);
        await first.WaitAsync(Bound, Ct);
        var r = await second.WaitAsync(Bound, Ct);
        Assert.Equal(Spare, Str(Data(r)["adapter_id"]));
        Assert.All(s.N.Invocations, i => Assert.Equal(SpareGuid, i.Guid));
    }

    [Fact]
    public async Task Revocation_while_waiting_for_the_lock_refuses_without_a_provider_call()
    {
        var s = Create();
        var (late, _) = s.H.Store.AddClient("c2", "network_set_adapter_enabled");
        var otherGuid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        s.N.Interfaces.Add(Row(otherGuid, 0xE000, 51, 6, 14, "Ethernet 4", 2, 2, true));
        s.N.Providers.Add(new ProviderAdapter(otherGuid, 0xE000, 51, 2, true, false, false, true, 14, 2));
        using var block = new SemaphoreSlim(0);
        s.N.Block = block;
        var first = Set(s, Spare, false);
        await WaitUntil(() => !s.N.Invocations.IsEmpty);
        var second = Set(s, Id(otherGuid), false, late);
        await Task.Delay(100, Ct);
        s.H.Update(() => s.H.Store.Revoke("c2"));
        block.Release(10);
        await first.WaitAsync(Bound, Ct);
        var r = await second.WaitAsync(Bound, Ct);
        Assert.Equal(ErrorCodes.PermissionDenied, r.Error?.Code);
        Assert.Single(s.N.Invocations);
    }

    [Fact]
    public async Task A_pending_audit_failure_or_a_power_transition_refuses_the_change()
    {
        var s = Create();
        s.H.Audit.Fail = true;
        Assert.Equal(ErrorCodes.AuditUnavailable, (await Set(s, Spare, false)).Error?.Code);
        s.H.Audit.Fail = false;

        Assert.True(s.H.Power.TryEnterTransition(PowerTransition.Sleep));
        Assert.Equal(ErrorCodes.Busy, (await Set(s, Spare, false)).Error?.Code);
        Assert.True((await Call(s, "network_list_adapters")).IsSuccess); // reads are not gated
        s.H.Power.ExitTransition(notExecuted: true);
        Assert.Empty(s.N.Invocations);
        Assert.True((await Set(s, Spare, false)).IsSuccess);
    }

    [Fact]
    public async Task A_hung_provider_call_keeps_the_lock_and_marks_overlapping_traffic_windows_after_cancellation()
    {
        var s = Create();
        using var block = new SemaphoreSlim(0);
        s.N.Block = block;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = Set(s, Spare, false, ct: cts.Token);
        await WaitUntil(() => !s.N.Invocations.IsEmpty);
        cts.Cancel();
        var r = await first.WaitAsync(Bound, Ct);
        Assert.Equal((ToolStatus.Unknown, ErrorCodes.Cancelled, false), (r.Status, r.Error?.Code, r.Error?.Retryable));

        // The OS call still runs: the next change waits, and traffic windows report the configuration change.
        var second = Set(s, Wired, false);
        var traffic = Data(await Call(s, "network_get_traffic"));
        Assert.All(traffic["adapters"]!.AsArray(), a => Assert.Equal("configuration_changed", Str(a!["rate_unavailable_reason"])));
        await Task.Delay(100, Ct);
        Assert.False(second.IsCompleted);
        block.Release(10);
        Assert.Equal("active_adapter", Str(Data(await second.WaitAsync(Bound, Ct))["blocked_reason"]));
        Assert.Single(s.N.Invocations);
        await WaitUntil(() => s.Changes.Generation % 2 == 0);
    }

    [Fact]
    public async Task Cancellation_before_the_start_point_changes_nothing()
    {
        var s = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await Set(s, Spare, false, ct: cts.Token);
        Assert.Equal((ToolStatus.Error, ErrorCodes.Cancelled, true), (r.Status, r.Error?.Code, r.Error?.Retryable));
        Assert.Empty(s.N.Invocations);
        Assert.Equal(0, s.Changes.Generation);
    }
}
