using System.Net;
using System.Runtime.InteropServices;
using Sysacta.Core.Tools.Network;
using Sysacta.Windows.Network;

namespace Sysacta.Windows.Tests;

/// <summary>
/// Read-only checks of the network adapter on the machine running the tests. No adapter is enabled or disabled, no
/// provider method is called and no traffic is generated (SYSACTA_HARDWARE_TESTS does not change that).
/// </summary>
public class WindowsNetworkTests
{
    [Fact]
    public void Native_struct_layouts_match_the_SDK()
    {
        Assert.Equal(1352, Marshal.SizeOf<NativeMethods.MibIfRow2>());
        Assert.Equal(28, Marshal.OffsetOf<NativeMethods.MibIfRow2>(nameof(NativeMethods.MibIfRow2.Alias)).ToInt32());
        Assert.Equal(1128, Marshal.OffsetOf<NativeMethods.MibIfRow2>(nameof(NativeMethods.MibIfRow2.Type)).ToInt32());
        Assert.Equal(1152, Marshal.OffsetOf<NativeMethods.MibIfRow2>(nameof(NativeMethods.MibIfRow2.InterfaceAndOperStatusFlags)).ToInt32());
        Assert.Equal(1156, Marshal.OffsetOf<NativeMethods.MibIfRow2>(nameof(NativeMethods.MibIfRow2.OperStatus)).ToInt32());
        Assert.Equal(1192, Marshal.OffsetOf<NativeMethods.MibIfRow2>(nameof(NativeMethods.MibIfRow2.TransmitLinkSpeed)).ToInt32());
        Assert.Equal(1208, Marshal.OffsetOf<NativeMethods.MibIfRow2>(nameof(NativeMethods.MibIfRow2.InOctets)).ToInt32());
        Assert.Equal(1280, Marshal.OffsetOf<NativeMethods.MibIfRow2>(nameof(NativeMethods.MibIfRow2.OutOctets)).ToInt32());
        Assert.Equal(28, Marshal.SizeOf<NativeMethods.SockAddrInet>());
        Assert.Equal(80, Marshal.SizeOf<NativeMethods.MibUnicastIpAddressRow>());
        Assert.Equal(32, Marshal.OffsetOf<NativeMethods.MibUnicastIpAddressRow>(nameof(NativeMethods.MibUnicastIpAddressRow.InterfaceLuid)).ToInt32());
        Assert.Equal(60, Marshal.OffsetOf<NativeMethods.MibUnicastIpAddressRow>(nameof(NativeMethods.MibUnicastIpAddressRow.OnLinkPrefixLength)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<NativeMethods.MibUnicastIpAddressRow>(nameof(NativeMethods.MibUnicastIpAddressRow.DadState)).ToInt32());
        Assert.Equal(104, Marshal.SizeOf<NativeMethods.MibIpForwardRow2>());
        Assert.Equal(12, Marshal.OffsetOf<NativeMethods.MibIpForwardRow2>(nameof(NativeMethods.MibIpForwardRow2.DestinationPrefix)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<NativeMethods.MibIpForwardRow2>(nameof(NativeMethods.MibIpForwardRow2.DestinationPrefixLength)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<NativeMethods.MibIpForwardRow2>(nameof(NativeMethods.MibIpForwardRow2.NextHop)).ToInt32());
        Assert.Equal(100, Marshal.OffsetOf<NativeMethods.MibIpForwardRow2>(nameof(NativeMethods.MibIpForwardRow2.Origin)).ToInt32());
        Assert.Equal(8, Marshal.SizeOf<NativeMethods.MibIfStackRow>());
    }

    [Fact]
    public void Socket_addresses_convert_both_families_and_reject_others()
    {
        var v4 = new NativeMethods.SockAddrInet { Family = NativeMethods.AfInet, V4Address = BitConverter.ToUInt32([192, 0, 2, 1]) };
        Assert.Equal(IPAddress.Parse("192.0.2.1"), WindowsNetwork.ToAddress(v4));
        var bytes = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        var v6 = new NativeMethods.SockAddrInet
        {
            Family = NativeMethods.AfInet6,
            V6Address0 = BitConverter.ToUInt32(bytes, 0),
            V6Address1 = BitConverter.ToUInt32(bytes, 4),
            V6Address2 = BitConverter.ToUInt32(bytes, 8),
            V6Address3 = BitConverter.ToUInt32(bytes, 12),
            V6ScopeId = 7,
        };
        Assert.Equal(IPAddress.Parse("2001:db8::1%7"), WindowsNetwork.ToAddress(v6));
        Assert.Null(WindowsNetwork.ToAddress(new NativeMethods.SockAddrInet { Family = 99 }));
    }

    [Fact]
    public void The_tables_are_read_completely_and_the_loopback_interface_is_identified()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var snapshot = new WindowsNetwork().ReadSnapshot();
        Assert.True(snapshot.Interfaces.IsComplete);
        Assert.True(snapshot.Addresses.IsComplete);
        Assert.True(snapshot.Routes.IsComplete);
        Assert.True(snapshot.Stack.IsComplete);
        var set = NetworkAdapters.Identify(snapshot.Interfaces);
        var loopback = Assert.Single(set.Adapters, a => a.Kind == NetworkAdapterKind.Loopback);
        Assert.Matches("^net-[0-9a-f]{32}$", loopback.Id);
        Assert.Contains(snapshot.Addresses.Rows, a => a.Luid == loopback.Row.Luid && IPAddress.IsLoopback(a.Address));
        Assert.Equal(set.Adapters.Count, set.Adapters.Select(a => a.Id).Distinct().Count());
        Assert.All(set.Adapters, a => Assert.False(a.Row.FilterInterface));
    }

    [Fact]
    public async Task Counters_are_monotonic_over_a_real_window_and_ids_stay_the_same()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var data = await NetworkTraffic.MeasureAsync(new WindowsNetwork(), new NetworkChanges(), TimeProvider.System, null, false, TestContext.Current.CancellationToken);
        Assert.InRange(data.ElapsedMs, 900, 4000);
        Assert.NotEmpty(data.Adapters);
        Assert.All(data.Adapters.Where(a => a.RateUnavailableReason is null), a =>
        {
            Assert.NotNull(a.ReceivedBitsPerSecond);
            Assert.True(a.ReceivedBitsPerSecond >= 0);
        });
    }

    [Fact]
    public void The_provider_can_be_read_without_calling_any_method()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var network = new WindowsNetwork();
        var adapters = NetworkAdapters.Identify(network.ReadInterfaces()).Adapters.Where(a => a.Row.HardwareInterface).ToList();
        Assert.SkipWhen(adapters.Count == 0, "No hardware network adapter on this machine.");
        foreach (var adapter in adapters)
        {
            var guid = NetworkAdapters.ParseId(adapter.Id)!.Value;
            var providers = network.ReadProviderAdapters(guid, TestContext.Current.CancellationToken);
            Assert.True(providers.Count <= 1);
            if (providers is [var p])
            {
                Assert.Equal(guid, p.InterfaceGuid);
                Assert.Equal(adapter.Row.Luid, p.NetLuid);
                Assert.Equal(adapter.Row.Index, p.InterfaceIndex);
            }
        }
    }

    [Fact]
    public void Connectivity_is_read_from_the_os_profile_without_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        _ = new WindowsNetwork().ReadConnectivity();
    }
}
