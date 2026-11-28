using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Network;
using Sysacta.Core.Tools.SystemMonitoring;

namespace Sysacta.Tests;

/// <summary>
/// In-memory network adapter, shared by Core.Tests and Mcp.Tests. Never touches Windows: no adapter is enabled or
/// disabled and no traffic is sent. Synthetic GUIDs, addresses and names only.
/// </summary>
public sealed class FakeNetwork : INetworkPlatform
{
    public static readonly Guid WiredGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SpareGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid WifiGuid = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid VpnGuid = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid LoopbackGuid = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid HyperVGuid = Guid.Parse("66666666-6666-6666-6666-666666666666");

    public static string Id(Guid interfaceId) => NetworkAdapters.IdFor(interfaceId);

    /// <summary>An active wired adapter (the MCP path), an unused spare wired adapter, Wi-Fi, a VPN, loopback and Hyper-V.</summary>
    public List<NetInterfaceRow> Interfaces { get; } =
    [
        Row(WiredGuid, 0x1000, 11, 6, 14, "Ethernet", oper: 1, media: 1, hardware: true) with { TransmitLinkSpeed = 1_000_000_000, ReceiveLinkSpeed = 1_000_000_000, InOctets = 5_000, OutOctets = 7_000 },
        Row(SpareGuid, 0x2000, 12, 6, 14, "Ethernet 2", oper: 2, media: 2, hardware: true),
        Row(WifiGuid, 0x3000, 13, 71, 9, "Wi-Fi", oper: 1, media: 1, hardware: true) with { TransmitLinkSpeed = 866_700_000, ReceiveLinkSpeed = 780_000_000 },
        Row(VpnGuid, 0x4000, 14, 53, 0, "Tailscale", oper: 1, media: 1, hardware: false),
        Row(LoopbackGuid, 0x5000, 1, 24, 0, "Loopback Pseudo-Interface 1", oper: 1, media: 1, hardware: false),
        Row(HyperVGuid, 0x6000, 15, 6, 14, "vEthernet (WSL)", oper: 1, media: 1, hardware: false),
        // An NDIS filter interface stacked on the spare adapter (never listed).
        Row(Guid.Parse("77777777-7777-7777-7777-777777777777"), 0x7000, 16, 6, 14, "Ethernet 2-WFP LightWeight Filter-0000", oper: 2, media: 2, hardware: false) with { FilterInterface = true },
    ];

    public List<NetUnicastAddress> Addresses { get; } =
    [
        new(0x1000, IPAddress.Parse("192.0.2.10"), 24, 4),
        new(0x1000, IPAddress.Parse("fe80::1%11"), 64, 4),
        new(0x1000, IPAddress.Parse("2001:db8::10"), 64, 4),
        new(0x3000, IPAddress.Parse("198.51.100.20"), 24, 4),
        new(0x4000, IPAddress.Parse("100.64.0.5"), 32, 4),
        new(0x5000, IPAddress.Loopback, 8, 4),
        new(0x5000, IPAddress.IPv6Loopback, 128, 4),
    ];

    public List<NetRoute> Routes { get; } =
    [
        new(0x1000, AddressFamily.InterNetwork, 0),
        new(0x1000, AddressFamily.InterNetwork, 24),
        new(0x1000, AddressFamily.InterNetworkV6, 0),
        new(0x3000, AddressFamily.InterNetwork, 0),
        new(0x4000, AddressFamily.InterNetwork, 10),
    ];

    /// <summary>Stand-alone interfaces plus the filter over the spare adapter (16 over 12).</summary>
    public List<NetStackLink> Stack { get; } = [new(0, 11), new(11, 0), new(16, 12), new(0, 16), new(12, 0), new(0, 13), new(13, 0)];

    public UnavailableReason? InterfacesProblem { get; set; }
    public UnavailableReason? AddressesProblem { get; set; }
    public UnavailableReason? RoutesProblem { get; set; }
    public UnavailableReason? StackProblem { get; set; }
    public NetworkConnectivityLevel? Connectivity { get; set; } = NetworkConnectivityLevel.InternetAccess;
    public bool IsElevated { get; set; } = true;

    /// <summary>Runs before every interface table read with the 0-based read number (to change counters between samples).</summary>
    public Action<int>? OnInterfaceRead { get; set; }

    /// <summary>MSFT_NetAdapter instances, derived from the hardware rows by default.</summary>
    public List<ProviderAdapter> Providers { get; } = [];
    public bool ProviderFails { get; set; }
    /// <summary>Return value of the method; the state changes only when it is 0 and <see cref="ApplyOnInvoke"/> is set.</summary>
    public uint InvokeResult { get; set; }
    public bool ApplyOnInvoke { get; set; } = true;
    public Exception? InvokeThrows { get; set; }
    /// <summary>When set, the "native" method blocks (ignoring cancellation) until released.</summary>
    public SemaphoreSlim? Block { get; set; }
    /// <summary>Runs inside the method call, after the start point.</summary>
    public Action? DuringInvoke { get; set; }
    public ConcurrentQueue<(Guid Guid, bool Enabled)> Invocations { get; } = new();

    private int _interfaceReads;
    private int _snapshots;
    private int _active;
    public int InterfaceReads => Volatile.Read(ref _interfaceReads);
    public int Snapshots => Volatile.Read(ref _snapshots);
    private int _maxConcurrentInvokes;
    public int MaxConcurrentInvokes => Volatile.Read(ref _maxConcurrentInvokes);

    public FakeNetwork()
    {
        foreach (var r in Interfaces.Where(r => r.HardwareInterface))
        {
            Providers.Add(new ProviderAdapter(r.InterfaceGuid, r.Luid, r.Index, 2, true, false, false, true, r.PhysicalMediumType, r.MediaConnectState));
        }
    }

    public static NetInterfaceRow Row(Guid interfaceId, ulong luid, uint index, uint type, uint medium, string alias, uint oper, uint media, bool hardware) =>
        new(interfaceId, luid, index, type, 0, medium, alias, alias + " adapter", 1, oper, media, hardware, false, hardware, 0, 0, 0, 0);

    public void Update(Guid interfaceId, Func<NetInterfaceRow, NetInterfaceRow> change)
    {
        lock (Interfaces)
        {
            var i = Interfaces.FindIndex(r => r.InterfaceGuid == interfaceId);
            Interfaces[i] = change(Interfaces[i]);
        }
    }

    public NetTable<NetInterfaceRow> ReadInterfaces()
    {
        var n = Interlocked.Increment(ref _interfaceReads) - 1;
        OnInterfaceRead?.Invoke(n);
        lock (Interfaces)
        {
            return InterfacesProblem is { } p
                ? (p == UnavailableReason.Truncated ? new NetTable<NetInterfaceRow>([.. Interfaces.Take(2)], p) : NetTable.Failed<NetInterfaceRow>(p))
                : NetTable.Ok<NetInterfaceRow>([.. Interfaces]);
        }
    }

    public NetworkSnapshot ReadSnapshot()
    {
        Interlocked.Increment(ref _snapshots);
        return new(ReadInterfaces(), Table(Addresses, AddressesProblem), Table(Routes, RoutesProblem), Table(Stack, StackProblem));
    }

    private static NetTable<T> Table<T>(List<T> rows, UnavailableReason? problem) =>
        problem is { } p ? (p == UnavailableReason.Truncated ? new NetTable<T>([.. rows.Take(1)], p) : NetTable.Failed<T>(p)) : NetTable.Ok<T>([.. rows]);

    public NetworkConnectivityLevel? ReadConnectivity() => Connectivity;

    public IReadOnlyList<ProviderAdapter> ReadProviderAdapters(Guid interfaceGuid, CancellationToken cancellationToken)
    {
        if (ProviderFails)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "provider");
        }

        lock (Providers)
        {
            return [.. Providers.Where(p => p.InterfaceGuid == interfaceGuid)];
        }
    }

    public IProviderAdapterHandle? OpenProviderAdapter(Guid interfaceGuid, CancellationToken cancellationToken) =>
        ReadProviderAdapters(interfaceGuid, cancellationToken) switch
        {
            [] => null,
            [var one] => new Handle(this, one),
            _ => throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "several"),
        };

    public void SetProviderState(Guid interfaceId, uint state)
    {
        lock (Providers)
        {
            var i = Providers.FindIndex(p => p.InterfaceGuid == interfaceId);
            Providers[i] = Providers[i] with { State = state };
        }
    }

    private sealed class Handle(FakeNetwork owner, ProviderAdapter adapter) : IProviderAdapterHandle
    {
        public ProviderAdapter Adapter { get; } = adapter;

        public uint Invoke(bool enabled)
        {
            var now = Interlocked.Increment(ref owner._active);
            int max;
            while ((max = Volatile.Read(ref owner._maxConcurrentInvokes)) < now && Interlocked.CompareExchange(ref owner._maxConcurrentInvokes, now, max) != max)
            {
            }

            try
            {
                owner.Invocations.Enqueue((Adapter.InterfaceGuid, enabled));
                owner.DuringInvoke?.Invoke();
                owner.Block?.Wait(CancellationToken.None);
                if (owner.InvokeThrows is { } ex)
                {
                    throw ex;
                }

                if (owner.InvokeResult == 0 && owner.ApplyOnInvoke)
                {
                    owner.SetProviderState(Adapter.InterfaceGuid, enabled ? 2u : 3u);
                }

                return owner.InvokeResult;
            }
            finally
            {
                Interlocked.Decrement(ref owner._active);
            }
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Deterministic clock for the traffic window: timers fire at once and each timestamp read advances by
/// <see cref="Step"/> ticks (frequency 1000 = milliseconds), so the measured window is exactly known.
/// </summary>
public sealed class SteppedTime : TimeProvider
{
    private long _now = 1_000_000;

    public long Step { get; set; } = 1000;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Add(ref _now, Step);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
}
