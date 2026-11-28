using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Network;
using Sysacta.Core.Tools.SystemMonitoring;
using Sysacta.Tests;
using static Sysacta.Tests.FakeNetwork;

namespace Sysacta.Core.Tests;

/// <summary>Phase 2B network tools with a fake adapter: no real adapter is read, enabled or disabled.</summary>
public partial class NetworkToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] All = ["network_get_status", "network_list_adapters", "network_get_traffic", "network_set_adapter_enabled"];
    private static readonly string Wired = Id(WiredGuid), Spare = Id(SpareGuid), Wifi = Id(WifiGuid), Vpn = Id(VpnGuid), Loopback = Id(LoopbackGuid), HyperV = Id(HyperVGuid);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed record Setup(Harness H, FakeNetwork N, NetworkChanges Changes, SteppedTime Time, Caller C);

    private static Setup Create(PowerTransitionGate? gate = null)
    {
        var n = new FakeNetwork();
        var changes = new NetworkChanges();
        var time = new SteppedTime();
        var h = new Harness(store => [new NetworkGetStatusTool(n, time), new NetworkListAdaptersTool(n, store, time),
            new NetworkGetTrafficTool(n, store, changes, time)], power: gate);
        var (caller, _) = h.Store.AddClient("c1", All);
        return new Setup(h, n, changes, time, caller);
    }

    private static Task<ToolResult> Call(Setup s, string tool, string? json = null, Caller? caller = null, CancellationToken? ct = null) =>
        s.H.Executor.ExecuteAsync(caller ?? s.C, tool, json is null ? null : TestJson.Raw(json), ct ?? Ct);

    private static Task<ToolResult> Set(Setup s, string adapter, bool enabled, Caller? caller = null, CancellationToken? ct = null) =>
        Call(s, "network_set_adapter_enabled", JsonSerializer.Serialize(new { adapter, enabled }), caller, ct);

    private static JsonNode Data(ToolResult r) => r.Data ?? throw new InvalidOperationException(r.Error?.Code);

    private static string? Str(JsonNode? n) => n?.GetValue<string>();

    [Fact]
    public async Task Traffic_rejects_reserved_names_and_unknown_arguments()
    {
        var s = Create();
        Assert.Equal(ErrorCodes.InvalidArgument, (await Call(s, "network_get_traffic", """{"adapter":"all"}""")).Error?.Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Call(s, "network_get_traffic", """{"interval_ms":5000}""")).Error?.Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Call(s, "network_list_adapters", """{"adapter":"x"}""")).Error?.Code);
        Assert.Equal(0, s.N.InterfaceReads);
    }

    // ---- network_list_adapters ------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_classifies_adapters_by_official_flags_and_separates_admin_oper_and_media_states()
    {
        var s = Create();
        var r = await Call(s, "network_list_adapters");
        Assert.Equal((ToolStatus.Success, Verification.NotApplicable), (r.Status, r.Verification));
        var data = Data(r);
        var adapters = data["adapters"]!.AsArray().ToDictionary(a => Str(a!["id"])!, a => a!);
        Assert.Equal(new[] { Wired, Spare, Wifi, Vpn, Loopback, HyperV }.Order(StringComparer.Ordinal), adapters.Keys); // stable id order, filter left out
        Assert.Equal(6, data["count"]!.GetValue<int>());
        Assert.False(data["truncated"]!.GetValue<bool>());

        Assert.Equal("wired", Str(adapters[Wired]["kind"]));
        Assert.Equal("wifi", Str(adapters[Wifi]["kind"]));
        Assert.Equal("virtual", Str(adapters[Vpn]["kind"]));     // Wintun-like, not a hardware interface
        Assert.Equal("virtual", Str(adapters[HyperV]["kind"]));  // ethernet type but not hardware: never "wired"
        Assert.Equal("loopback", Str(adapters[Loopback]["kind"]));

        Assert.Equal(("enabled", "up", "connected"), (Str(adapters[Wired]["admin_state"]), Str(adapters[Wired]["oper_state"]), Str(adapters[Wired]["media_state"])));
        Assert.Equal(("enabled", "down", "disconnected"), (Str(adapters[Spare]["admin_state"]), Str(adapters[Spare]["oper_state"]), Str(adapters[Spare]["media_state"])));
        Assert.Equal(1_000_000_000, adapters[Wired]["transmit_link_speed_bps"]!.GetValue<long>());
        Assert.Null(adapters[Spare]["transmit_link_speed_bps"]);

        var v4 = adapters[Wired]["ipv4_addresses"]!.AsArray();
        Assert.Equal(("192.0.2.10", 24, "unicast"), (Str(v4[0]!["address"]), v4[0]!["prefix_length"]!.GetValue<int>(), Str(v4[0]!["scope"])));
        var v6 = adapters[Wired]["ipv6_addresses"]!.AsArray().Select(a => (Str(a!["address"]), Str(a!["scope"]))).ToList();
        Assert.Contains(("fe80::1", "link_local"), v6); // no scope id in the text
        Assert.Contains(("2001:db8::10", "unicast"), v6);
        Assert.Equal("loopback", Str(adapters[Loopback]["ipv4_addresses"]![0]!["scope"]));

        Assert.Equal("active_adapter", Str(adapters[Wired]["admin_change_blocked_reason"]));
        Assert.Equal("active_adapter", Str(adapters[Wifi]["admin_change_blocked_reason"]));
        Assert.Equal("unsupported", Str(adapters[Vpn]["admin_change_blocked_reason"]));
        Assert.Equal("unsupported", Str(adapters[HyperV]["admin_change_blocked_reason"]));
        Assert.Equal("unsupported", Str(adapters[Loopback]["admin_change_blocked_reason"]));
        Assert.Null(adapters[Spare]["admin_change_blocked_reason"]); // the filter stacked on it is not a dependency

        var text = data.ToJsonString();
        Assert.DoesNotContain("Ethernet adapter", text, StringComparison.Ordinal); // descriptions (hardware model) are not returned
        Assert.DoesNotContain("physical_address", text, StringComparison.Ordinal);
        Assert.DoesNotContain("luid", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_reports_unidentified_truncated_and_incomplete_reads_without_claiming_completeness()
    {
        var s = Create();
        s.N.Interfaces.Add(Row(Guid.Empty, 0x8000, 20, 6, 14, "No guid", 2, 2, true));
        s.N.Interfaces.Add(Row(WifiGuid, 0x9000, 21, 71, 9, "Duplicate", 2, 2, true));
        for (var i = 0; i < 20; i++)
        {
            s.N.Addresses.Add(new(0x1000, IPAddress.Parse($"203.0.113.{i + 1}"), 24, 4));
        }

        s.N.Addresses.Add(new(0x1000, IPAddress.Parse("203.0.113.200"), 24, 1)); // tentative: not usable
        s.N.AddressesProblem = null;
        var data = Data(await Call(s, "network_list_adapters"));
        Assert.Equal(3, data["unidentified_count"]!.GetValue<int>()); // empty guid and both rows of the duplicate
        var ids = data["adapters"]!.AsArray().Select(a => Str(a!["id"])).ToList();
        Assert.DoesNotContain(Wifi, ids);
        var wired = data["adapters"]!.AsArray().Single(a => Str(a!["id"]) == Wired)!;
        Assert.Equal(16, wired["ipv4_addresses"]!.AsArray().Count);
        Assert.True(wired["ipv4_truncated"]!.GetValue<bool>());
        Assert.DoesNotContain("203.0.113.200", data.ToJsonString(), StringComparison.Ordinal);

        s.N.AddressesProblem = UnavailableReason.Unavailable;
        s.N.InterfacesProblem = UnavailableReason.Truncated;
        data = Data(await Call(s, "network_list_adapters"));
        Assert.True(data["truncated"]!.GetValue<bool>());
        Assert.Equal("unavailable", Str(data["addresses_unavailable_reason"]));
        Assert.All(data["adapters"]!.AsArray(), a => Assert.NotNull(Str(a!["admin_change_blocked_reason"])));

        s.N.InterfacesProblem = UnavailableReason.Unavailable;
        var failed = await Call(s, "network_list_adapters");
        Assert.Equal((ToolStatus.Error, ErrorCodes.OsApiError), (failed.Status, failed.Error?.Code));
    }

    [Fact]
    public async Task List_includes_disabled_adapters_with_the_same_id_and_aliases_of_the_category_only()
    {
        var s = Create();
        s.N.Update(SpareGuid, r => r with { AdminStatus = 2, OperStatus = 2 });
        s.H.Store.Aliases[(ToolCategories.Network, "spare")] = Spare;
        s.H.Store.Aliases[(ToolCategories.Audio, "audio-only")] = Spare;
        var spare = Data(await Call(s, "network_list_adapters"))["adapters"]!.AsArray().Single(a => Str(a!["id"]) == Spare)!;
        Assert.Equal("disabled", Str(spare["admin_state"]));
        Assert.Equal(["spare"], spare["aliases"]!.AsArray().Select(Str));
        Assert.Null(spare["admin_change_blocked_reason"]); // enabling is not ruled out

        s.N.Update(SpareGuid, r => r with { AdminStatus = 1, OperStatus = 6 });
        spare = Data(await Call(s, "network_list_adapters"))["adapters"]!.AsArray().Single(a => Str(a!["id"]) == Spare)!;
        Assert.Equal(("unknown", "not_present"), (Str(spare["admin_state"]), Str(spare["oper_state"]))); // not invented
    }

    // ---- network_get_status ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_reports_every_default_route_adapter_and_the_os_connectivity_without_probing()
    {
        var s = Create();
        var data = Data(await Call(s, "network_get_status"));
        Assert.Equal(new[] { Wired, Wifi }.Order(StringComparer.Ordinal), data["ipv4_default_route_adapter_ids"]!.AsArray().Select(Str));
        Assert.Equal([Wired], data["ipv6_default_route_adapter_ids"]!.AsArray().Select(Str));
        Assert.DoesNotContain(Loopback, data["up_adapter_ids"]!.AsArray().Select(Str));
        Assert.Contains(Vpn, data["up_adapter_ids"]!.AsArray().Select(Str));
        Assert.Equal("internet_access", Str(data["os_connectivity"]));
        Assert.Null(data["routes_unavailable_reason"]);
        Assert.DoesNotContain("192.0.2", data.ToJsonString(), StringComparison.Ordinal); // no addresses or gateways in the summary

        s.N.RoutesProblem = UnavailableReason.Truncated;
        s.N.Connectivity = null;
        data = Data(await Call(s, "network_get_status"));
        Assert.Null(data["ipv4_default_route_adapter_ids"]);
        Assert.Null(data["ipv6_default_route_adapter_ids"]);
        Assert.Equal("truncated", Str(data["routes_unavailable_reason"]));
        Assert.Null(data["os_connectivity"]);
        Assert.Equal("unavailable", Str(data["os_connectivity_unavailable_reason"]));

        s.N.RoutesProblem = null;
        s.N.Routes.Clear();
        data = Data(await Call(s, "network_get_status"));
        Assert.Empty(data["ipv4_default_route_adapter_ids"]!.AsArray()); // a real "none", distinct from null
    }

    // ---- network_get_traffic --------------------------------------------------------------------------------------------

    private static void Counters(FakeNetwork n, Guid guid, int read, ulong rx0, ulong tx0, ulong rx1, ulong tx1) =>
        n.OnInterfaceRead += i =>
        {
            if (i == read)
            {
                n.Update(guid, r => r with { InOctets = rx0, OutOctets = tx0 });
            }
            else if (i == read + 1)
            {
                n.Update(guid, r => r with { InOctets = rx1, OutOctets = tx1 });
            }
        };

    private static JsonNode Entry(JsonNode data, string id) => data["adapters"]!.AsArray().Single(a => Str(a!["adapter_id"]) == id)!;

    [Fact]
    public async Task Traffic_uses_two_samples_and_the_monotonic_window_per_adapter_without_summing()
    {
        var s = Create();
        Counters(s.N, WiredGuid, 0, 10_000, 20_000, 11_250, 20_000);
        s.Time.Step = 500; // the monotonic window is 0.5 s, whatever the requested delay was
        var data = Data(await Call(s, "network_get_traffic"));
        Assert.Equal(500, data["elapsed_ms"]!.GetValue<long>());
        var wired = Entry(data, Wired);
        Assert.Equal((11_250UL, 20_000UL), (wired["received_bytes_total"]!.GetValue<ulong>(), wired["sent_bytes_total"]!.GetValue<ulong>()));
        Assert.Equal((1_250UL, 0UL), (wired["received_bytes_delta"]!.GetValue<ulong>(), wired["sent_bytes_delta"]!.GetValue<ulong>()));
        Assert.Equal(20_000, wired["received_bits_per_second"]!.GetValue<long>()); // 1250 B * 8 / 0.5 s
        Assert.Equal(0, wired["sent_bits_per_second"]!.GetValue<long>());           // a real zero
        Assert.Null(wired["rate_unavailable_reason"]);
        Assert.Equal(6, data["adapters"]!.AsArray().Count);
        Assert.DoesNotContain("total_bits", data.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(2, s.N.InterfaceReads);
    }

    [Fact]
    public void Rates_use_exact_128_bit_arithmetic_and_never_overflow()
    {
        Assert.Equal(8, NetworkTraffic.Rate(1, 1000, 1000));
        Assert.Equal(2, NetworkTraffic.Rate(1, 3000, 1000)); // floor(8/3)
        Assert.Equal(long.MaxValue / 8 * 8, NetworkTraffic.Rate((ulong)(long.MaxValue / 8), 1, 1));
        Assert.Null(NetworkTraffic.Rate(ulong.MaxValue, 1, 1));
        Assert.Null(NetworkTraffic.Rate(ulong.MaxValue, 1_000, 1_000));
    }

    [Fact]
    public async Task Counters_near_the_64_bit_limit_give_exact_deltas_and_a_decrease_is_never_wrap_corrected()
    {
        var s = Create();
        Counters(s.N, WiredGuid, 0, ulong.MaxValue - 1_000, 5, ulong.MaxValue, 5);
        Counters(s.N, WifiGuid, 0, 900, 900, 100, 1_000); // reset or wrap
        var data = Data(await Call(s, "network_get_traffic"));
        Assert.Equal(1_000UL, Entry(data, Wired)["received_bytes_delta"]!.GetValue<ulong>());
        Assert.Equal(8_000, Entry(data, Wired)["received_bits_per_second"]!.GetValue<long>());
        var wifi = Entry(data, Wifi);
        Assert.Equal("counter_decreased", Str(wifi["rate_unavailable_reason"]));
        Assert.Null(wifi["received_bits_per_second"]);
        Assert.Null(wifi["sent_bits_per_second"]);
        Assert.Null(wifi["received_bytes_delta"]);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("added")]
    [InlineData("luid")]
    [InlineData("index")]
    [InlineData("oper")]
    [InlineData("media")]
    [InlineData("admin")]
    public async Task Changes_of_the_adapter_during_the_window_give_no_rate(string change)
    {
        var s = Create();
        var extra = Row(Guid.Parse("88888888-8888-8888-8888-888888888888"), 0xA000, 30, 6, 14, "New", 1, 1, true);
        s.N.OnInterfaceRead += i =>
        {
            if (i != 1)
            {
                return;
            }

            switch (change)
            {
                case "removed": s.N.Interfaces.RemoveAll(r => r.InterfaceGuid == SpareGuid); break;
                case "added": s.N.Interfaces.Add(extra); break;
                case "luid": s.N.Update(SpareGuid, r => r with { Luid = 0xB000 }); break;
                case "index": s.N.Update(SpareGuid, r => r with { Index = 99 }); break;
                case "oper": s.N.Update(SpareGuid, r => r with { OperStatus = 1 }); break;
                case "media": s.N.Update(SpareGuid, r => r with { MediaConnectState = 1 }); break;
                case "admin": s.N.Update(SpareGuid, r => r with { AdminStatus = 2 }); break;
            }
        };

        var data = Data(await Call(s, "network_get_traffic"));
        var (id, reason) = change switch
        {
            "removed" => (Spare, "adapter_removed"),
            "added" => (NetworkAdapters.IdFor(extra.InterfaceGuid), "adapter_added"),
            "luid" or "index" => (Spare, "identity_changed"),
            _ => (Spare, "state_changed"),
        };
        var entry = Entry(data, id);
        Assert.Equal(reason, Str(entry["rate_unavailable_reason"]));
        Assert.Null(entry["received_bits_per_second"]);
        Assert.Null(Entry(data, Wired)["rate_unavailable_reason"]); // other adapters keep their rate
    }

    [Fact]
    public async Task A_failed_second_sample_an_unusable_window_or_a_Sysacta_change_give_no_rate()
    {
        var s = Create();
        s.N.OnInterfaceRead = i => s.N.InterfacesProblem = i == 1 ? UnavailableReason.Unavailable : null;
        var data = Data(await Call(s, "network_get_traffic"));
        Assert.All(data["adapters"]!.AsArray(), a => Assert.Equal("sample_failed", Str(a!["rate_unavailable_reason"])));

        s.N.OnInterfaceRead = null;
        s.N.InterfacesProblem = null;
        s.Time.Step = 0;
        data = Data(await Call(s, "network_get_traffic"));
        Assert.All(data["adapters"]!.AsArray(), a => Assert.Equal("invalid_elapsed", Str(a!["rate_unavailable_reason"])));

        s.Time.Step = 4_001;
        data = Data(await Call(s, "network_get_traffic"));
        Assert.All(data["adapters"]!.AsArray(), a => Assert.Equal("invalid_elapsed", Str(a!["rate_unavailable_reason"])));

        s.Time.Step = 1000;
        IDisposable? scope = null;
        s.N.OnInterfaceRead = _ => scope ??= s.Changes.Begin(); // a Sysacta change starts during the first sample
        data = Data(await Call(s, "network_get_traffic"));
        Assert.All(data["adapters"]!.AsArray(), a => Assert.Equal("configuration_changed", Str(a!["rate_unavailable_reason"])));
        scope!.Dispose();
        s.N.OnInterfaceRead = null;
        data = Data(await Call(s, "network_get_traffic"));
        Assert.All(data["adapters"]!.AsArray(), a => Assert.Null(a!["rate_unavailable_reason"]));

        s.N.InterfacesProblem = UnavailableReason.PermissionDenied;
        var failed = await Call(s, "network_get_traffic");
        Assert.Equal((ErrorCodes.OsApiError, false), (failed.Error?.Code, failed.Error?.Retryable));
    }

    [Fact]
    public async Task Traffic_for_one_adapter_resolves_ids_and_aliases_and_never_falls_back()
    {
        var s = Create();
        s.H.Store.Aliases[(ToolCategories.Network, "uplink")] = Wired;
        s.H.Store.Aliases[(ToolCategories.Network, "gone")] = Id(Guid.Parse("99999999-9999-9999-9999-999999999999"));
        s.H.Store.Aliases[(ToolCategories.Network, "broken")] = "Ethernet";
        var data = Data(await Call(s, "network_get_traffic", """{"adapter":"uplink"}"""));
        Assert.Equal([Wired], data["adapters"]!.AsArray().Select(a => Str(a!["adapter_id"])));
        Assert.Equal(1, data["count"]!.GetValue<int>());
        Assert.Equal(ErrorCodes.DeviceDisconnected, (await Call(s, "network_get_traffic", """{"adapter":"gone"}""")).Error?.Code);
        Assert.Equal(ErrorCodes.TargetNotFound, (await Call(s, "network_get_traffic", """{"adapter":"broken"}""")).Error?.Code);
        Assert.Equal(ErrorCodes.TargetNotFound, (await Call(s, "network_get_traffic", $$"""{"adapter":"{{Id(Guid.Parse("99999999-9999-9999-9999-999999999999"))}}"}""")).Error?.Code);
        Assert.Equal(ErrorCodes.TargetNotFound, (await Call(s, "network_get_traffic", """{"adapter":"Wi-Fi"}""")).Error?.Code);
    }

    [Fact]
    public async Task Measuring_stops_on_cancellation_and_is_limited_to_128_adapters()
    {
        var n = new FakeNetwork();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkTraffic.MeasureAsync(n, new NetworkChanges(), new SteppedTime(), null, false, cts.Token));
        Assert.Equal(1, n.InterfaceReads); // the second sample is never taken

        for (var i = 0; i < 200; i++)
        {
            n.Interfaces.Add(Row(Guid.NewGuid(), 0x10000UL + (ulong)i, 100 + (uint)i, 6, 14, "x", 2, 2, true));
        }

        var data = await NetworkTraffic.MeasureAsync(n, new NetworkChanges(), new SteppedTime(), null, false, Ct);
        Assert.Equal(NetworkAdapters.MaxListed, data.Adapters.Count);
        Assert.True(data.Truncated);
        Assert.Equal(206, data.Count);
    }
}
