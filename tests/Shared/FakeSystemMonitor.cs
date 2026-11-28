using Sysacta.Core.Tools;
using Sysacta.Core.Tools.SystemMonitoring;

namespace Sysacta.Tests;

/// <summary>
/// In-memory monitoring adapter shared by Core.Tests and Mcp.Tests. Never reads the test machine. Readings can be replaced,
/// a call can be made to block (ignoring cancellation, like a hung native call), and the sampling wait observes cancellation.
/// </summary>
public sealed class FakeSystemMonitor : ISystemMonitor
{
    public const ulong DiscreteLuid = 0x0000_0000_0000_C5A3;
    public const ulong IntegratedLuid = 0x0000_0001_0000_0042;
    public const ulong BasicRenderLuid = 0x0000_0000_0000_9999;
    public static readonly DateTimeOffset T0 = new(2026, 11, 28, 1, 2, 3, TimeSpan.Zero);

    public static string Engine(ulong pid, ulong luid, int phys, int eng, string type) =>
        $"pid_{pid}_luid_0x{(uint)(luid >> 32):X8}_0x{(uint)luid:X8}_phys_{phys}_eng_{eng}_engtype_{type}";

    public static string MemoryInstance(ulong luid, int phys) => $"luid_0x{(uint)(luid >> 32):X8}_0x{(uint)luid:X8}_phys_{phys}";

    public static readonly IReadOnlyList<GpuAdapterReading> DefaultAdapters =
    [
        // 24 GiB: larger than any 32-bit value.
        new(DiscreteLuid, "NVIDIA GeForce RTX 4090", 0x10DE, 0x2684, 24UL << 30, 0, 32UL << 30, false),
        new(IntegratedLuid, "Intel(R) UHD Graphics 770", 0x8086, 0x4680, 128UL << 20, 0, 32UL << 30, false),
        new(BasicRenderLuid, "Microsoft Basic Render Driver", 0x1414, 0x008C, 0, 0, 32UL << 30, true),
    ];

    public Func<StatusReading> Status { get; set; } = () => DefaultStatus();

    public Func<HardwareReading> Hardware { get; set; } = () => DefaultHardware();

    public Func<TemperatureReading> Temperatures { get; set; } = () => DefaultTemperatures();

    public Func<StorageReading> Storage { get; set; } = () => DefaultStorage();

    /// <summary>When set, every call blocks (ignoring cancellation) until released.</summary>
    public SemaphoreSlim? Block { get; set; }

    /// <summary>Signalled when a call has entered the adapter.</summary>
    public SemaphoreSlim Entered { get; } = new(0);

    private int _calls;
    private int _running;
    private int _cancelledWaits;
    private int _stoppedAfterCancel;
    private int _readsCompleted;
    public int Calls => Volatile.Read(ref _calls);
    /// <summary>Calls that found the token cancelled after the (possibly hung) first read and started nothing more.</summary>
    public int StoppedAfterCancel => Volatile.Read(ref _stoppedAfterCancel);
    /// <summary>Calls that went on to their second read and returned data.</summary>
    public int ReadsCompleted => Volatile.Read(ref _readsCompleted);
    public int Running => Volatile.Read(ref _running);
    public int CancelledWaits => Volatile.Read(ref _cancelledWaits);
    public TimeSpan? LastInterval { get; private set; }

    /// <summary>Real wait inside SampleStatus (default none, so tests stay fast).</summary>
    public TimeSpan SampleWait { get; set; } = TimeSpan.Zero;

    public StatusReading SampleStatus(TimeSpan interval, CancellationToken cancellationToken) => Run(() =>
    {
        LastInterval = interval;
        if (SampleWait > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(SampleWait))
        {
            Interlocked.Increment(ref _cancelledWaits);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Status();
    }, cancellationToken);

    public HardwareReading ReadHardware(CancellationToken cancellationToken) => Run(Hardware, cancellationToken);

    public TemperatureReading ReadTemperatures(CancellationToken cancellationToken) => Run(Temperatures, cancellationToken);

    public StorageReading ReadStorage(CancellationToken cancellationToken) => Run(Storage, cancellationToken);

    /// <summary>
    /// Models the real adapter's contract: a first read that may hang (ignoring cancellation, like a native call), then
    /// a cancellation check before the next read.
    /// </summary>
    private T Run<T>(Func<T> read, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Increment(ref _running);
        try
        {
            Entered.Release();
            Block?.Wait(CancellationToken.None); // a hung native call ignores cancellation
            if (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _stoppedAfterCancel);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var result = read();
            Interlocked.Increment(ref _readsCompleted);
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    public static StatusReading DefaultStatus(
        IReadOnlyList<CounterSample>? engines = null,
        IReadOnlyList<GpuMemorySample>? memory = null,
        long elapsedMs = 1003,
        long workingMs = 1001) =>
        new(T0, T0.AddMilliseconds(elapsedMs), elapsedMs, workingMs, 12.345, null, 24, 32UL << 30, 20UL << 30, 3_723_000,
            Observed.Ok<IReadOnlyList<GpuAdapterReading>>(DefaultAdapters),
            Observed.Ok<IReadOnlyList<CounterSample>>(engines ??
            [
                new(Engine(100, DiscreteLuid, 0, 0, "3D"), 30.0),
                new(Engine(200, DiscreteLuid, 0, 0, "3D"), 25.5),
                new(Engine(100, DiscreteLuid, 0, 1, "Copy"), 4.0),
                new(Engine(300, DiscreteLuid, 0, 2, "VideoDecode"), 0.0),
                new(Engine(100, IntegratedLuid, 0, 0, "3D"), 0.0),
            ]),
            Observed.Ok<IReadOnlyList<GpuMemorySample>>(memory ??
            [
                new(MemoryInstance(DiscreteLuid, 0), 20L << 30, 1L << 30),
                new(MemoryInstance(IntegratedLuid, 0), 64L << 20, 2L << 30),
            ]));

    public static HardwareReading DefaultHardware() =>
        new(T0, "Microsoft Windows 11 Pro", (10, 0, 26100), 2605, "24H2", "x64",
            [new("13th Gen Intel(R) Core(TM) i7-13700K", "GenuineIntel", 16, 24)],
            1, 16, 24, 64UL * 1024 * 1024, (64UL << 30) - (300UL << 20), DefaultAdapters,
            "Contoso", "Model X", new Dictionary<HardwarePart, UnavailableReason>());

    public static TemperatureReading DefaultTemperatures() =>
        new(T0, Observed.Ok<IReadOnlyList<GpuTemperatureReading>>(
            [
                new(DiscreteLuid, 0, "NVIDIA GeForce RTX 4090", 455, null),
                new(IntegratedLuid, 0, "Intel(R) UHD Graphics 770", 0, null),
            ]),
            T0, Observed.Ok<IReadOnlyList<ThermalZoneReading>>([new(@"\_TZ.TZ00", 3201, 320)]));

    public static StorageReading DefaultStorage() =>
        new(T0,
        [
            // 2 TB with 4 KiB clusters, 25 % free; per-user quota leaves 100 GiB.
            new('C', new(488_281_250, 122_070_312, 26_214_400, 8, 512), null),
            new('D', null, UnavailableReason.Unavailable),
        ], 2);

    public static ToolFailureException NoDrives() => new(ErrorCodes.OsApiError, "The drive list could not be read.", retryable: true);
}
