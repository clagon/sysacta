using System.Text.Json;
using System.Text.Json.Nodes;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.SystemMonitoring;
using Sysacta.Tests;
using static Sysacta.Tests.FakeSystemMonitor;

namespace Sysacta.Core.Tests;

/// <summary>Phase 1F monitoring tools with a fake adapter (the test machine is never read).</summary>
public class SystemMonitorToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] All = ["system_get_status", "system_get_hardware", "system_get_temperatures", "system_get_storage"];
    private static readonly TimeSpan Interval = SystemStatus.SamplingInterval;

    private static (Harness H, FakeSystemMonitor M, Caller C) Setup(ExecutorLimits? limits = null, PowerTransitionGate? gate = null)
    {
        var m = new FakeSystemMonitor();
        var h = new Harness(_ => [new SystemGetStatusTool(m), new SystemGetHardwareTool(m), new SystemGetTemperaturesTool(m), new SystemGetStorageTool(m)], limits, gate);
        var (caller, _) = h.Store.AddClient("c1", All);
        return (h, m, caller);
    }

    private static Task<ToolResult> Call(Harness h, Caller c, string tool, string? json = null, CancellationToken? ct = null) =>
        h.Executor.ExecuteAsync(c, tool, json is null ? null : TestJson.Raw(json), ct ?? Ct);

    private static SystemStatusData Status(StatusReading r) => SystemStatus.Interpret(r, Interval);

    private static GpuStatus Gpu(SystemStatusData d, ulong luid) => d.Gpus.Single(g => g.GpuId == SystemStatus.GpuId(luid));

    // ---- descriptors, schemas, pipeline -------------------------------------------------------------------------------

    [Fact]
    public void Tools_are_read_only_on_by_default_strict_and_never_audit_arguments()
    {
        var (h, _, _) = Setup();
        var tools = h.Executor.Registry.All.ToDictionary(t => t.Name);
        Assert.Equal(All.Order(), tools.Keys.Order());
        foreach (var tool in tools.Values)
        {
            Assert.Equal(ToolCategories.System, tool.Descriptor.Category);
            Assert.Equal(DefaultPolicy.On, tool.Descriptor.DefaultPolicy);
            Assert.True(tool.Descriptor.ReadOnly);
            Assert.Null(tool.Descriptor.Transition);
            Assert.False(tool.Descriptor.LocalOnly);
            Assert.Empty(tool.Descriptor.AuditedArguments);
            Assert.Equal(TimeSpan.FromSeconds(5), tool.Descriptor.EffectiveTimeout);
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.False(tool.InputSchema.TryGetProperty("properties", out var p) && p.EnumerateObject().Any());
            var data = tool.OutputSchema.GetProperty("properties").GetProperty("data");
            Assert.Contains("measured_at", data.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(ToolAvailability.Available, tool.GetAvailability());
        }
    }

    [Theory]
    [InlineData("""{"device_id":"x"}""")]
    [InlineData("""{"sampling_ms":10}""")]
    [InlineData("""{"path":"C:\\"}""")]
    public async Task Any_argument_is_rejected_before_the_adapter(string json)
    {
        var (h, m, c) = Setup();
        foreach (var name in All)
        {
            var r = await Call(h, c, name, json);
            Assert.Equal(ErrorCodes.InvalidArgument, r.Error!.Code);
        }

        Assert.Equal(0, m.Calls);
    }

    [Fact]
    public async Task Unauthorized_disabled_and_revoked_calls_never_reach_the_adapter()
    {
        var (h, m, _) = Setup();
        var (other, _) = h.Store.AddClient("c2", "system_get_hardware");
        foreach (var name in All.Except(["system_get_hardware"]))
        {
            Assert.Equal(ErrorCodes.PermissionDenied, (await Call(h, other, name)).Error!.Code);
        }

        h.Store.EnabledTools["system_get_hardware"] = false;
        Assert.Equal(ErrorCodes.ToolDisabled, (await Call(h, other, "system_get_hardware")).Error!.Code);
        h.Store.EnabledTools["system_get_hardware"] = true;
        h.Store.Revoke("c2");
        Assert.Equal(ErrorCodes.PermissionDenied, (await Call(h, other, "system_get_hardware")).Error!.Code);
        Assert.Equal(0, m.Calls);
        Assert.DoesNotContain(h.Executor.ListTools(other), t => t.Name.StartsWith("system_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Success_audits_only_tool_and_outcome_never_result_data()
    {
        var (h, m, c) = Setup();
        m.Hardware = () => DefaultHardware() with { MachineModel = "SECRET-MODEL-1234", OsCaption = "Windows SECRET" };
        foreach (var name in All)
        {
            var r = await Call(h, c, name);
            Assert.True(r.IsSuccess, name);
            Assert.Equal(Verification.NotApplicable, r.Verification);
            var final = h.Audit.Final(r.RequestId);
            Assert.Equal(AuditOutcome.Success, final.Outcome);
            Assert.Equal(name, final.ToolName);
            Assert.Null(final.Arguments);
        }

        // Read-only: no pending record, and no audit text mentions device names, ids, drives or values.
        Assert.DoesNotContain(h.Audit.Events, e => e.Outcome == AuditOutcome.Pending);
        var auditText = JsonSerializer.Serialize(h.Audit.Events);
        foreach (var leaked in new[] { "SECRET", "RTX", "luid", "drive:", "C:", "12.3" })
        {
            Assert.DoesNotContain(leaked, auditText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Status_samples_once_over_the_fixed_window_and_reports_times()
    {
        var (h, m, c) = Setup();
        var r = await Call(h, c, "system_get_status");
        Assert.True(r.IsSuccess);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), m.LastInterval);
        var d = r.Data!;
        Assert.Equal("2026-11-28T01:02:03+00:00", d["sampling_started_at"]!.GetValue<string>());
        Assert.Equal(d["sampling_ended_at"]!.GetValue<string>(), d["measured_at"]!.GetValue<string>());
        Assert.Equal(1001, d["sampling_ms"]!.GetValue<long>());
        Assert.Equal(12.3, d["cpu"]!["usage_percent"]!.GetValue<double>());
        Assert.Equal(3723, d["uptime_seconds"]!.GetValue<long>());
        Assert.Null(d["cpu"]!["reason"]);
    }

    [Fact]
    public async Task Status_fails_only_when_nothing_is_readable()
    {
        var (h, m, c) = Setup();
        m.Status = () => new StatusReading(T0, T0, 0, 0, null, UnavailableReason.Unavailable, null, null, null, null,
            Observed.Missing<IReadOnlyList<GpuAdapterReading>>(UnavailableReason.Unavailable),
            Observed.Missing<IReadOnlyList<CounterSample>>(UnavailableReason.NotSupported),
            Observed.Missing<IReadOnlyList<GpuMemorySample>>(UnavailableReason.NotSupported));
        var r = await Call(h, c, "system_get_status");
        Assert.Equal(ErrorCodes.OsApiError, r.Error!.Code);
        Assert.True(r.Error.Retryable);
    }

    [Fact]
    public async Task Cancellation_during_sampling_stops_without_a_second_collection_and_holds_the_slot_until_return()
    {
        var (h, m, c) = Setup();
        m.SampleWait = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource();
        var call = Call(h, c, "system_get_status", ct: cts.Token);
        await m.Entered.WaitAsync(Ct);
        await cts.CancelAsync();
        var r = await call;
        Assert.Equal(ErrorCodes.Cancelled, r.Error!.Code);
        // The adapter observes cancellation and returns; its slot is released only then.
        while (h.Executor.InFlight != 0) await Task.Delay(10, Ct);
        Assert.Equal(1, m.CancelledWaits);
        Assert.Equal(0, m.Running);
    }

    [Fact]
    public async Task A_hung_read_keeps_its_slot_until_it_really_ends_and_no_new_work_is_started()
    {
        var (h, m, c) = Setup(new ExecutorLimits(MaxConcurrent: 1, MaxQueued: 0));
        using var block = new SemaphoreSlim(0);
        m.Block = block;
        using var cts = new CancellationTokenSource();
        var hung = Call(h, c, "system_get_hardware", ct: cts.Token);
        await m.Entered.WaitAsync(Ct);
        await cts.CancelAsync();
        Assert.Equal(ErrorCodes.Cancelled, (await hung).Error!.Code);

        // The native call is still running: it stays counted and the next call is refused instead of starting another one.
        Assert.Equal(1, h.Executor.InFlight);
        Assert.Equal(ErrorCodes.Busy, (await Call(h, c, "system_get_storage")).Error!.Code);
        Assert.Equal(1, m.Calls);

        block.Release();
        while (h.Executor.InFlight != 0) await Task.Delay(10, Ct);
        m.Block = null;
        Assert.True((await Call(h, c, "system_get_storage")).IsSuccess);
    }

    [Fact]
    public async Task Independent_reads_run_concurrently_up_to_the_executor_limit()
    {
        var (h, m, c) = Setup(new ExecutorLimits(MaxConcurrent: 4, MaxQueued: 4));
        using var block = new SemaphoreSlim(0);
        m.Block = block;
        var calls = All.Select(n => Call(h, c, n)).ToList();
        for (var i = 0; i < 4; i++) await m.Entered.WaitAsync(Ct);
        Assert.Equal(4, m.Running);
        block.Release(4);
        Assert.All(await Task.WhenAll(calls), r => Assert.True(r.IsSuccess));
    }

    [Fact]
    public async Task Reads_are_allowed_during_a_power_transition_and_never_enter_the_gate()
    {
        var gate = new PowerTransitionGate();
        var (h, _, c) = Setup(gate: gate);
        Assert.True(gate.TryEnterTransition(PowerTransition.Sleep));
        gate.MarkStarted();
        var before = gate.State;
        foreach (var name in All)
        {
            Assert.True((await Call(h, c, name)).IsSuccess, name);
        }

        Assert.Equal(before, gate.State);
        Assert.Equal(0, gate.State.RunningChanges);
    }

    // ---- CPU and sampling window ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1003, 1001, null)]
    [InlineData(6000, 1000, UnavailableReason.Interrupted)]     // slept during the window
    [InlineData(1000, 1300, UnavailableReason.InvalidData)]     // inverted clocks
    [InlineData(0, 0, UnavailableReason.InvalidData)]           // zero denominator
    [InlineData(-5, -5, UnavailableReason.InvalidData)]
    [InlineData(300, 300, UnavailableReason.InvalidData)]       // much shorter than requested
    public void Sampling_window_is_validated(long elapsed, long working, UnavailableReason? expected)
    {
        Assert.Equal(expected, SystemStatus.CheckWindow(elapsed, working, Interval));
        var d = Status(DefaultStatus(elapsedMs: elapsed, workingMs: working));
        Assert.Equal(expected, d.Cpu.Reason);
        Assert.Equal(expected is null, d.Cpu.UsagePercent is not null);
        // An invalid window invalidates GPU rates too, but instantaneous values (memory, GPU memory, uptime) remain.
        Assert.Equal(expected ?? null, Gpu(d, DiscreteLuid).UtilizationReason);
        Assert.NotNull(Gpu(d, DiscreteLuid).DedicatedMemoryUsedBytes);
        Assert.NotNull(d.Memory.UsedBytes);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(100.0, 100.0)]
    [InlineData(100.9, 100.0)]
    [InlineData(-0.4, 0.0)]
    [InlineData(101.5, null)]
    [InlineData(250.0, null)]
    [InlineData(-3.0, null)]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    public void Percentages_are_rounded_never_clamped_beyond_the_tolerance(double raw, double? expected)
    {
        Assert.Equal(expected, SystemStatus.Percent(raw));
        var d = Status(DefaultStatus() with { CpuPercent = raw });
        Assert.Equal(expected, d.Cpu.UsagePercent);
        Assert.Equal(expected is null ? UnavailableReason.InvalidData : null, d.Cpu.Reason);
    }

    [Fact]
    public void Cpu_counter_failures_keep_the_os_reason_and_logical_processors_cover_all_groups()
    {
        var d = Status(DefaultStatus() with { CpuPercent = null, CpuReason = UnavailableReason.NotSupported, ElapsedMs = 0, WorkingMs = 0 });
        Assert.Equal(UnavailableReason.NotSupported, d.Cpu.Reason);
        Assert.Equal(UnavailableReason.Unavailable, Status(DefaultStatus() with { CpuPercent = null }).Cpu.Reason);

        // More than 64 logical processors (several processor groups) is reported as one total, not one group.
        Assert.Equal(192, Status(DefaultStatus() with { LogicalProcessors = 192 }).Cpu.LogicalProcessors);
        Assert.Null(Status(DefaultStatus() with { LogicalProcessors = 0 }).Cpu.LogicalProcessors);
    }

    // ---- memory and uptime --------------------------------------------------------------------------------------------

    [Fact]
    public void Memory_values_are_consistent_or_missing()
    {
        var ok = SystemStatus.Memory(32UL << 30, 8UL << 30);
        Assert.Equal((32L << 30, 8L << 30, 24L << 30, 75.0), (ok.TotalBytes!.Value, ok.AvailableBytes!.Value, ok.UsedBytes!.Value, ok.UsagePercent!.Value));
        Assert.Null(ok.Reason);
        Assert.Equal(UnavailableReason.InvalidData, SystemStatus.Memory(8, 9).Reason);
        Assert.Equal(UnavailableReason.InvalidData, SystemStatus.Memory(0, 0).Reason);
        Assert.Equal(UnavailableReason.InvalidData, SystemStatus.Memory(ulong.MaxValue, 0).Reason);
        Assert.Equal(UnavailableReason.Unavailable, SystemStatus.Memory(null, 5).Reason);
        Assert.Null(SystemStatus.Memory(8, 9).UsedBytes);

        var noUptime = Status(DefaultStatus() with { UptimeMs = null });
        Assert.Null(noUptime.UptimeSeconds);
        Assert.Equal(UnavailableReason.Unavailable, noUptime.UptimeReason);
        Assert.Equal(18_446_744_073_709_551L, Status(DefaultStatus() with { UptimeMs = ulong.MaxValue }).UptimeSeconds);
    }

    // ---- GPU ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Gpu_engines_sum_process_instances_and_the_adapter_reports_its_busiest_engine()
    {
        var d = Status(DefaultStatus());
        Assert.Equal(3, d.GpuTotalCount);
        var dgpu = Gpu(d, DiscreteLuid);
        Assert.Equal("luid_0x00000000_0x0000c5a3", dgpu.GpuId);
        Assert.Equal(GpuAdapterKind.Hardware, dgpu.AdapterType);
        Assert.Equal("NVIDIA GeForce RTX 4090", dgpu.Name);
        Assert.Equal([55.5, 4.0, 0.0], dgpu.Engines.Select(e => e.UtilizationPercent!.Value));
        Assert.Equal(["3D", "Copy", "VideoDecode"], dgpu.Engines.Select(e => e.EngineType));
        // Busiest engine, not the sum of engines (59.5).
        Assert.Equal(55.5, dgpu.UtilizationPercent);
        Assert.Equal("3D", dgpu.BusiestEngineType);
        Assert.Equal(24L << 30, dgpu.DedicatedMemoryTotalBytes);
        Assert.Equal(20L << 30, dgpu.DedicatedMemoryUsedBytes);

        // A measured idle GPU is 0, a GPU without any engine instance is null.
        Assert.Equal(0.0, Gpu(d, IntegratedLuid).UtilizationPercent);
        var basic = Gpu(d, BasicRenderLuid);
        Assert.Equal(GpuAdapterKind.Software, basic.AdapterType);
        Assert.Null(basic.UtilizationPercent);
        Assert.Equal(UnavailableReason.Unavailable, basic.UtilizationReason);
        Assert.Equal(UnavailableReason.Unavailable, basic.MemoryUsageReason);
    }

    [Fact]
    public void Gpu_totals_beyond_100_are_invalid_not_clamped()
    {
        var d = Status(DefaultStatus(engines:
        [
            new(Engine(1, DiscreteLuid, 0, 0, "3D"), 70.0),
            new(Engine(2, DiscreteLuid, 0, 0, "3D"), 60.0),
            new(Engine(1, DiscreteLuid, 0, 1, "Copy"), 10.0),
        ]));
        var g = Gpu(d, DiscreteLuid);
        Assert.Null(g.Engines[0].UtilizationPercent);
        Assert.Equal(UnavailableReason.InvalidData, g.Engines[0].Reason);
        Assert.Equal(10.0, g.Engines[1].UtilizationPercent);
        Assert.Null(g.UtilizationPercent);
        Assert.Equal(UnavailableReason.InvalidData, g.UtilizationReason);

        // Rounding noise within the tolerance is reported as 100.
        var edge = Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), 60.0), new(Engine(2, DiscreteLuid, 0, 0, "3D"), 40.6)]));
        Assert.Equal(100.0, Gpu(edge, DiscreteLuid).UtilizationPercent);
    }

    [Fact]
    public void Gpu_duplicates_conflicts_negative_and_unrecognized_instances_are_never_aggregated()
    {
        // The same process twice for one engine with the same non-zero value (also with PDH's "#1" suffix): ambiguous.
        var dup = Gpu(Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), 10.0), new(Engine(1, DiscreteLuid, 0, 0, "3D") + "#1", 10.0)])), DiscreteLuid);
        Assert.Equal(UnavailableReason.InvalidData, dup.Engines.Single().Reason);
        Assert.Null(dup.UtilizationPercent);
        var sameName = Gpu(Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), 10.0), new(Engine(1, DiscreteLuid, 0, 0, "3D"), 10.0)])), DiscreteLuid);
        Assert.Equal(UnavailableReason.InvalidData, sameName.UtilizationReason);

        // Duplicates that are all zero (the Hyper-V VM worker on a real machine) cannot change the result.
        var idle = Gpu(Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), 0.0), new(Engine(1, DiscreteLuid, 0, 0, "3D"), 0.0), new(Engine(2, DiscreteLuid, 0, 0, "3D"), 7.0)])), DiscreteLuid);
        Assert.Equal(7.0, idle.UtilizationPercent);

        // Different values prove different counters: they add up.
        var distinct = Gpu(Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), 10.0), new(Engine(1, DiscreteLuid, 0, 0, "3D"), 2.5)])), DiscreteLuid);
        Assert.Equal(12.5, distinct.UtilizationPercent);

        // One engine reported with two types.
        var conflict = Gpu(Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), 10.0), new(Engine(2, DiscreteLuid, 0, 0, "Copy"), 10.0)])), DiscreteLuid);
        Assert.Equal(UnavailableReason.InvalidData, conflict.UtilizationReason);

        var negative = Gpu(Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), -1.0)])), DiscreteLuid);
        Assert.Equal(UnavailableReason.InvalidData, negative.UtilizationReason);

        // An instance that cannot be attributed to an adapter makes every adapter's figure unreliable.
        var d = Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), 10.0), new("pid_1_luid_garbage", 5.0)]));
        Assert.All(d.Gpus, g => Assert.Equal(UnavailableReason.InvalidData, g.UtilizationReason));
        Assert.All(d.Gpus, g => Assert.Null(g.UtilizationPercent));
        Assert.Equal(20L << 30, Gpu(d, DiscreteLuid).DedicatedMemoryUsedBytes);
        Assert.Equal(UnavailableReason.InvalidData, Status(DefaultStatus(engines: [new(new string('x', 300), 1.0)])).Gpus[0].UtilizationReason);
    }

    [Fact]
    public void Gpu_instances_without_a_valid_rate_are_left_out_and_an_engine_without_any_is_unknown()
    {
        // A process that started during the window has no valid rate yet.
        var partial = Gpu(Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), 20.0), new(Engine(2, DiscreteLuid, 0, 0, "3D"), null)])), DiscreteLuid);
        Assert.Equal(20.0, partial.UtilizationPercent);

        // An engine whose only instance has no valid rate: the busiest engine is unknown.
        var unknown = Gpu(Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), 20.0), new(Engine(2, DiscreteLuid, 0, 1, "Copy"), null)])), DiscreteLuid);
        Assert.Null(unknown.UtilizationPercent);
        Assert.Equal(UnavailableReason.Unavailable, unknown.UtilizationReason);
        Assert.Equal(UnavailableReason.Unavailable, unknown.Engines[1].Reason);
        Assert.Equal(20.0, unknown.Engines[0].UtilizationPercent);
    }

    [Fact]
    public void Gpu_counter_failures_truncation_and_unknown_adapters_are_explicit()
    {
        var truncated = Status(DefaultStatus() with { Engines = Observed.Missing<IReadOnlyList<CounterSample>>(UnavailableReason.Truncated) });
        Assert.All(truncated.Gpus, g => Assert.Equal(UnavailableReason.Truncated, g.UtilizationReason));
        Assert.All(truncated.Gpus, g => Assert.Empty(g.Engines));
        Assert.Equal(20L << 30, Gpu(truncated, DiscreteLuid).DedicatedMemoryUsedBytes);

        var unsupported = Status(DefaultStatus() with { Engines = Observed.Missing<IReadOnlyList<CounterSample>>(UnavailableReason.NotSupported) });
        Assert.All(unsupported.Gpus, g => Assert.Equal(UnavailableReason.NotSupported, g.UtilizationReason));

        // A LUID seen only in the counters is listed, but no name or type is guessed for it.
        const ulong Other = 0x7777;
        var only = Gpu(Status(DefaultStatus(engines: [new(Engine(9, Other, 0, 0, "Compute_0"), 33.3)])), Other);
        Assert.Equal(GpuAdapterKind.Unknown, only.AdapterType);
        Assert.Null(only.Name);
        Assert.Null(only.DedicatedMemoryTotalBytes);
        Assert.Equal(33.3, only.UtilizationPercent);

        // DXGI unavailable and no counters: no GPU list, with the reason.
        var none = Status(DefaultStatus(engines: [], memory: []) with { Adapters = Observed.Missing<IReadOnlyList<GpuAdapterReading>>(UnavailableReason.Unavailable) });
        Assert.Empty(none.Gpus);
        Assert.Equal(UnavailableReason.Unavailable, none.GpusReason);

        // Successfully read and genuinely empty: an empty list without a reason.
        var empty = Status(DefaultStatus(engines: [], memory: []) with { Adapters = Observed.Ok<IReadOnlyList<GpuAdapterReading>>([]) });
        Assert.Empty(empty.Gpus);
        Assert.Null(empty.GpusReason);
    }

    [Fact]
    public void Gpu_lists_and_engine_lists_are_bounded()
    {
        var engines = Enumerable.Range(0, 70).Select(i => new CounterSample(Engine(1, DiscreteLuid, 0, i, "3D"), 1.0)).ToList();
        var g = Gpu(Status(DefaultStatus(engines: engines)), DiscreteLuid);
        Assert.Equal(64, g.Engines.Count);
        Assert.True(g.EnginesTruncated);
        Assert.Equal(1.0, g.UtilizationPercent);

        var many = Enumerable.Range(1, 70).Select(i => new CounterSample(Engine(1, (ulong)i << 8, 0, 0, "3D"), 1.0)).ToList();
        var d = Status(DefaultStatus(engines: many, memory: []) with { Adapters = Observed.Ok<IReadOnlyList<GpuAdapterReading>>([]) });
        Assert.Equal(64, d.Gpus.Count);
        Assert.Equal(70, d.GpuTotalCount);
        Assert.True(d.GpusTruncated);
    }

    [Fact]
    public void Gpu_memory_sums_physical_adapters_and_rejects_duplicates_negatives_and_overflow()
    {
        GpuStatus Mem(IReadOnlyList<GpuMemorySample> samples) => Gpu(Status(DefaultStatus(memory: samples)), DiscreteLuid);

        var linked = Mem([new(MemoryInstance(DiscreteLuid, 0), 1L << 30, 10), new(MemoryInstance(DiscreteLuid, 1), 2L << 30, 20)]);
        Assert.Equal((3L << 30, 30L), (linked.DedicatedMemoryUsedBytes!.Value, linked.SharedMemoryUsedBytes!.Value));

        Assert.Equal(UnavailableReason.InvalidData, Mem([new(MemoryInstance(DiscreteLuid, 0), 1, 1), new(MemoryInstance(DiscreteLuid, 0) + "#1", 1, 1)]).MemoryUsageReason);
        Assert.Equal(UnavailableReason.InvalidData, Mem([new(MemoryInstance(DiscreteLuid, 0), -1, 1)]).MemoryUsageReason);
        Assert.Equal(UnavailableReason.InvalidData, Mem([new(MemoryInstance(DiscreteLuid, 0), long.MaxValue, 1), new(MemoryInstance(DiscreteLuid, 1), 1, 1)]).MemoryUsageReason);
        Assert.Equal(UnavailableReason.Unavailable, Mem([new(MemoryInstance(DiscreteLuid, 0), null, 1)]).MemoryUsageReason);
        var zero = Mem([new(MemoryInstance(DiscreteLuid, 0), 0, 0)]);
        Assert.Equal((0L, 0L), (zero.DedicatedMemoryUsedBytes!.Value, zero.SharedMemoryUsedBytes!.Value));

        var bad = Status(DefaultStatus(memory: [new("luid_bogus", 1, 1)]));
        Assert.All(bad.Gpus, g => Assert.Equal(UnavailableReason.InvalidData, g.MemoryUsageReason));
        Assert.Equal(55.5, Gpu(bad, DiscreteLuid).UtilizationPercent);
    }

    [Fact]
    public void Gpu_names_and_engine_types_are_untrusted_text()
    {
        var adapters = new List<GpuAdapterReading> { DefaultAdapters[0] with { Description = @"C:\Users\alice\gpu" } };
        var d = Status(DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D\u0007<script>"), 1.0)]) with { Adapters = Observed.Ok<IReadOnlyList<GpuAdapterReading>>(adapters) });
        var g = Gpu(d, DiscreteLuid);
        Assert.Null(g.Name);
        Assert.Null(g.Engines.Single().EngineType);
        Assert.Equal(1.0, g.UtilizationPercent);
        var json = JsonSerializer.Serialize(d, ToolJson.Options);
        Assert.DoesNotContain("alice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("script", json, StringComparison.Ordinal);
    }

    // ---- hardware -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hardware_keeps_installed_and_usable_memory_apart_and_64_bit_vram_intact()
    {
        var d = SystemInventory.Hardware(DefaultHardware());
        Assert.Equal(("Microsoft Windows 11 Pro", "10.0.26100", 26100, 2605, "24H2", "x64"), (d.Os.ProductName, d.Os.Version, d.Os.BuildNumber, d.Os.UpdateBuildRevision, d.Os.DisplayVersion, d.Os.Architecture));
        Assert.Equal(64L << 30, d.Memory.InstalledBytes);
        Assert.Equal((64L << 30) - (300L << 20), d.Memory.OsUsableBytes);
        Assert.Equal((1, 16, 24), (d.ProcessorTopology.Packages!.Value, d.ProcessorTopology.PhysicalCores!.Value, d.ProcessorTopology.LogicalProcessors!.Value));
        var gpu = d.Gpus.Single(g => g.GpuId == SystemStatus.GpuId(DiscreteLuid));
        Assert.Equal(24L << 30, gpu.DedicatedVideoMemoryBytes);
        Assert.Equal(("10de", "2684"), (gpu.VendorId, gpu.DeviceId));
        Assert.Equal(GpuAdapterKind.Software, d.Gpus.Single(g => g.GpuId == SystemStatus.GpuId(BasicRenderLuid)).AdapterType);
        Assert.Empty(d.Unavailable);
    }

    [Fact]
    public void Hardware_reports_each_missing_or_invalid_item()
    {
        var r = DefaultHardware() with
        {
            OsCaption = null,
            OsDisplayVersion = "../../x",
            OsArchitecture = "s390x",
            PhysicalCores = 48,                 // more cores than logical processors
            InstalledMemoryKilobytes = ulong.MaxValue,
            Gpus = null,
            MachineModel = @"\\server\share",
            Processors = null,
            Failures = new Dictionary<HardwarePart, UnavailableReason> { [HardwarePart.OsProductName] = UnavailableReason.PermissionDenied },
        };
        var d = SystemInventory.Hardware(r);
        var gaps = d.Unavailable.ToDictionary(g => g.Item, g => g.Reason);
        Assert.Equal(UnavailableReason.PermissionDenied, gaps[HardwarePart.OsProductName]);
        Assert.Equal(UnavailableReason.InvalidData, gaps[HardwarePart.OsDisplayVersion]);
        Assert.Equal(UnavailableReason.InvalidData, gaps[HardwarePart.OsArchitecture]);
        Assert.Equal(UnavailableReason.InvalidData, gaps[HardwarePart.ProcessorTopology]);
        Assert.Equal(UnavailableReason.InvalidData, gaps[HardwarePart.InstalledMemory]);
        Assert.Equal(UnavailableReason.Unavailable, gaps[HardwarePart.Gpus]);
        Assert.Equal(UnavailableReason.InvalidData, gaps[HardwarePart.MachineModel]);
        Assert.Equal(UnavailableReason.Unavailable, gaps[HardwarePart.Processors]);
        Assert.Null(d.ProcessorTopology.PhysicalCores);
        Assert.Equal(24, d.ProcessorTopology.LogicalProcessors);
        Assert.Null(d.Memory.InstalledBytes);
        Assert.Empty(d.Gpus);
        Assert.Null(d.Machine.Model);
        Assert.Equal("Contoso", d.Machine.Manufacturer);
        Assert.DoesNotContain("server", JsonSerializer.Serialize(d, ToolJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public void Hardware_lists_every_processor_up_to_the_limit()
    {
        var two = SystemInventory.Hardware(DefaultHardware() with { Processors = [new("Xeon A", "GenuineIntel", 32, 64), new("Xeon A", "GenuineIntel", 32, 64)], ProcessorPackages = 2, PhysicalCores = 64, LogicalProcessors = 128 });
        Assert.Equal(2, two.Processors.Count);
        Assert.Equal(128, two.ProcessorTopology.LogicalProcessors);
        var many = SystemInventory.Hardware(DefaultHardware() with { Processors = Enumerable.Repeat(new ProcessorReading("x", "y", 1, 1), 70).ToList() });
        Assert.Equal(64, many.Processors.Count);
        Assert.True(many.ProcessorsTruncated);
    }

    // ---- temperatures ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Temperatures_convert_units_and_never_report_unsupported_as_a_value()
    {
        var d = SystemInventory.Temperatures(DefaultTemperatures());
        var dgpu = d.Sensors.Single(s => s.DeviceId == SystemStatus.GpuId(DiscreteLuid));
        Assert.Equal((TemperatureDeviceType.Gpu, TemperatureSensorType.GpuTemperature, TemperatureSource.D3dkmtAdapterPerfdata), (dgpu.DeviceType, dgpu.SensorType, dgpu.Source));
        Assert.Equal(45.5, dgpu.TemperatureC);
        Assert.Equal("luid_0x00000000_0x0000c5a3:temperature", dgpu.SensorId);

        // The integrated GPU's driver reports no temperature (0): null with a reason, not 0 °C.
        var igpu = d.Sensors.Single(s => s.DeviceId == SystemStatus.GpuId(IntegratedLuid));
        Assert.Null(igpu.TemperatureC);
        Assert.Equal(UnavailableReason.NotSupported, igpu.Reason);

        // An ACPI thermal zone is its own sensor type, never a CPU temperature.
        var zone = d.Sensors.Single(s => s.Source == TemperatureSource.PdhThermalZone);
        Assert.Equal((TemperatureDeviceType.AcpiThermalZone, TemperatureSensorType.AcpiThermalZone, "_TZ.TZ00", "acpi_thermal_zone:_TZ.TZ00"), (zone.DeviceType, zone.SensorType, zone.Label, zone.SensorId));
        Assert.Equal(46.95, zone.TemperatureC);
        Assert.Null(zone.DeviceId);
        Assert.DoesNotContain(d.Sensors, s => s.DeviceType == TemperatureDeviceType.Cpu);
        Assert.Contains(d.NotCollected, n => n.SensorType == TemperatureSensorType.CpuPackage && n.Reason == UnavailableReason.NotSupported);
        Assert.Empty(d.SourceFailures);
    }

    [Theory]
    [InlineData(3201.0, 320.0, 46.95, null)]                       // high precision preferred
    [InlineData(null, 300.0, 26.85, null)]                         // whole kelvin only
    [InlineData(0.0, 0.0, null, UnavailableReason.InvalidData)]   // 0 K
    [InlineData(double.NaN, null, null, UnavailableReason.InvalidData)]
    [InlineData(-5.0, null, null, UnavailableReason.InvalidData)]
    [InlineData(null, null, null, UnavailableReason.Unavailable)]
    public void Thermal_zone_values_are_validated(double? tenths, double? kelvin, double? celsius, UnavailableReason? reason)
    {
        Assert.Equal((celsius, reason), SystemInventory.ZoneCelsius(new(@"\_TZ.TZ00", tenths, kelvin)));
    }

    [Fact]
    public void Temperature_identities_are_validated_and_invalid_driver_values_dropped()
    {
        var r = DefaultTemperatures() with
        {
            Gpus = Observed.Ok<IReadOnlyList<GpuTemperatureReading>>(
            [
                new(DiscreteLuid, "GPU", uint.MaxValue, null),
                new(IntegratedLuid, "GPU", null, UnavailableReason.PermissionDenied),
            ]),
            Zones = Observed.Ok<IReadOnlyList<ThermalZoneReading>>(
            [
                new(@"\_TZ.TZ00", 3000, null),
                new(@"\_TZ.TZ00#1", 3100, null),
                new(@"C:\Windows\x", 3000, null),
            ]),
        };
        var d = SystemInventory.Temperatures(r);
        Assert.Equal(UnavailableReason.InvalidData, d.Sensors.Single(s => s.DeviceId == SystemStatus.GpuId(DiscreteLuid)).Reason);
        Assert.Equal(UnavailableReason.PermissionDenied, d.Sensors.Single(s => s.DeviceId == SystemStatus.GpuId(IntegratedLuid)).Reason);
        var zones = d.Sensors.Where(s => s.Source == TemperatureSource.PdhThermalZone).ToList();
        Assert.Equal(3, zones.Count);
        Assert.Equal(2, zones.Count(z => z.SensorId is not null));
        Assert.Equal(zones.Count(z => z.SensorId is not null), zones.Where(z => z.SensorId is not null).Select(z => z.SensorId).Distinct().Count());
        var unnamed = zones.Single(z => z.SensorId is null);
        Assert.Null(unnamed.Label);
        Assert.Equal(26.85, unnamed.TemperatureC);
        Assert.DoesNotContain(@"Windows\\x", JsonSerializer.Serialize(d, ToolJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_readable_temperature_is_still_a_diagnostic_success()
    {
        var (h, m, c) = Setup();
        m.Temperatures = () => new(T0, Observed.Missing<IReadOnlyList<GpuTemperatureReading>>(UnavailableReason.Unavailable),
            T0, Observed.Missing<IReadOnlyList<ThermalZoneReading>>(UnavailableReason.NotSupported));
        var r = await Call(h, c, "system_get_temperatures");
        Assert.True(r.IsSuccess);
        Assert.Empty(r.Data!["sensors"]!.AsArray());
        var failures = r.Data["source_failures"]!.AsArray().Select(f => (f!["source"]!.GetValue<string>(), f["reason"]!.GetValue<string>())).ToList();
        Assert.Equal([("d3dkmt_adapter_perfdata", "unavailable"), ("pdh_thermal_zone", "not_supported")], failures);
        Assert.Equal(5, r.Data["not_collected"]!.AsArray().Count);
    }

    // ---- storage --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Storage_uses_one_basis_for_usage_and_keeps_quota_space_separate()
    {
        var d = SystemInventory.Storage(DefaultStorage());
        var c = d.Volumes.Single(v => v.DriveLetter == "C:");
        Assert.Equal(("drive:C", "fixed"), (c.VolumeId, c.DriveType));
        Assert.Equal(2_000_000_000_000L, c.TotalBytes);
        Assert.Equal(122_070_312L * 4096, c.FreeBytes);
        Assert.Equal(100L << 30, c.AvailableBytes);
        Assert.Equal(c.TotalBytes - c.FreeBytes, c.UsedBytes);
        Assert.Equal(75.0, c.UsagePercent);
        var dDrive = d.Volumes.Single(v => v.DriveLetter == "D:");
        Assert.Equal(UnavailableReason.Unavailable, dDrive.Reason);
        Assert.Null(dDrive.TotalBytes);
        Assert.Equal((1, 2), (d.FailedCount, d.ExcludedCount));
    }

    [Fact]
    public void Storage_rejects_zero_overflow_and_inconsistent_capacities()
    {
        StorageVolume One(DiskSpaceUnits units) => SystemInventory.Storage(new(T0, [new('E', units, null)], 0)).Volumes.Single();
        Assert.Equal(UnavailableReason.InvalidData, One(new(0, 0, 0, 8, 512)).Reason);
        Assert.Equal(UnavailableReason.InvalidData, One(new(10, 11, 0, 8, 512)).Reason);
        Assert.Equal(UnavailableReason.InvalidData, One(new(10, 5, 11, 8, 512)).Reason);
        Assert.Equal(UnavailableReason.InvalidData, One(new(ulong.MaxValue, 0, 0, 8, 512)).Reason);
        Assert.Equal(UnavailableReason.InvalidData, One(new(10, 5, 5, 0, 512)).Reason);
        Assert.Equal(UnavailableReason.InvalidData, One(new(10, 5, 5, uint.MaxValue, uint.MaxValue)).Reason);
        var full = One(new(10, 0, 0, 1, 1));
        Assert.Equal((10L, 0L, 10L, 100.0), (full.TotalBytes!.Value, full.FreeBytes!.Value, full.UsedBytes!.Value, full.UsagePercent!.Value));
        Assert.Equal(UnavailableReason.PermissionDenied, SystemInventory.Storage(new(T0, [new('F', null, UnavailableReason.PermissionDenied)], 0)).Volumes.Single().Reason);
    }

    [Fact]
    public async Task Storage_lists_drives_in_order_and_fails_only_without_a_drive_list()
    {
        var (h, m, c) = Setup();
        m.Storage = () => new(T0, [new('Z', new(10, 5, 5, 1, 1), null), new('C', new(10, 5, 5, 1, 1), null), new('?', new(10, 5, 5, 1, 1), null)], 0);
        var r = await Call(h, c, "system_get_storage");
        Assert.Equal(["C:", "Z:"], r.Data!["volumes"]!.AsArray().Select(v => v!["drive_letter"]!.GetValue<string>()));

        m.Storage = () => new(T0, [], 3);
        var empty = await Call(h, c, "system_get_storage");
        Assert.True(empty.IsSuccess);
        Assert.Empty(empty.Data!["volumes"]!.AsArray());
        Assert.Equal(3, empty.Data["excluded_count"]!.GetValue<int>());

        m.Storage = () => throw NoDrives();
        Assert.Equal(ErrorCodes.OsApiError, (await Call(h, c, "system_get_storage")).Error!.Code);
    }

    // ---- JSON contract --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Results_contain_no_non_finite_numbers_and_missing_values_are_explicit_nulls()
    {
        var (h, m, c) = Setup();
        m.Status = () => DefaultStatus(engines: [new(Engine(1, DiscreteLuid, 0, 0, "3D"), double.NaN)]) with { CpuPercent = double.PositiveInfinity };
        var r = await Call(h, c, "system_get_status");
        Assert.True(r.IsSuccess);
        var text = r.Data!.ToJsonString();
        Assert.DoesNotContain("NaN", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", text, StringComparison.Ordinal);
        var cpu = r.Data["cpu"]!.AsObject();
        Assert.True(cpu.ContainsKey("usage_percent"));
        Assert.Null(cpu["usage_percent"]);
        Assert.Equal("invalid_data", cpu["reason"]!.GetValue<string>());
        Assert.Equal("invalid_data", r.Data["gpus"]!.AsArray().Select(g => g!).Single(g => g["gpu_id"]!.GetValue<string>().EndsWith("c5a3", StringComparison.Ordinal))["utilization_reason"]!.GetValue<string>());

        // Every tool's data validates against the published output schema's top-level properties.
        foreach (var name in All)
        {
            var result = await Call(h, c, name);
            var schema = h.Executor.Registry.Find(name)!.OutputSchema.GetProperty("properties").GetProperty("data");
            var declared = (schema.TryGetProperty("anyOf", out var any) ? any.EnumerateArray().First(x => x.TryGetProperty("properties", out _)) : schema)
                .GetProperty("properties").EnumerateObject().Select(p => p.Name).Order();
            Assert.Equal(declared, result.Data!.AsObject().Select(p => p.Key).Order());
        }
    }
}
