using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

public class MultiSideEffectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Harness H, FakeAudioEndpoints Audio, FakeDefaultEndpointWriter Writer, Caller Caller) Setup(TimeSpan? timeout = null)
    {
        var audio = new FakeAudioEndpoints();
        audio.Devices["spk2"] = new("spk2", "Speakers 2", AudioDirection.Output, AudioDeviceState.Active, 0.7f, false);
        var writer = new FakeDefaultEndpointWriter(audio);
        var h = new Harness(store => [new TimedDefaultTool(audio, store, writer, timeout)]);
        var (caller, _) = h.Store.AddClient("c1", "audio_set_default_device");
        return (h, audio, writer, caller);
    }

    private sealed class TimedDefaultTool(
        IAudioEndpoints endpoints,
        Core.Devices.IDeviceAliases aliases,
        IAudioDefaultEndpointWriter writer,
        TimeSpan? timeout) : SysactaTool<AudioSetDefaultDeviceArgs, AudioSetDefaultDeviceData>
    {
        private readonly AudioSetDefaultDeviceTool _inner = new(endpoints, aliases, writer);

        public override ToolDescriptor Descriptor { get; } = new(
            "audio_set_default_device",
            ToolCategories.Audio,
            "Set default audio device",
            "Test wrapper",
            DefaultPolicy.Experimental,
            ReadOnly: false,
            AuditedArguments: ["direction", "role"],
            Timeout: timeout);

        protected override ValueTask<ToolTarget> ResolveTargetAsync(AudioSetDefaultDeviceArgs args, CancellationToken cancellationToken) =>
            new AudioSetDefaultDeviceTool(endpoints, aliases, writer).Descriptor is not null
                ? ResolveInner(args, cancellationToken)
                : throw new InvalidOperationException();

        private ValueTask<ToolTarget> ResolveInner(AudioSetDefaultDeviceArgs args, CancellationToken cancellationToken)
        {
            var state = AudioTarget.ResolveExplicit(endpoints, aliases, args.Device, args.Direction);
            return ValueTask.FromResult(new ToolTarget(
                [
                    AudioTarget.DeviceKey(state.Id),
                    AudioTarget.DefaultRoleKey(args.Direction, AudioRole.Console),
                    AudioTarget.DefaultRoleKey(args.Direction, AudioRole.Multimedia),
                    AudioTarget.DefaultRoleKey(args.Direction, AudioRole.Communications),
                ],
                state.Id));
        }

        protected override Task<ToolOutcome<AudioSetDefaultDeviceData>> ExecuteAsync(AudioSetDefaultDeviceArgs args, ToolExecution execution, CancellationToken cancellationToken)
        {
            var method = typeof(AudioSetDefaultDeviceTool).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            return (Task<ToolOutcome<AudioSetDefaultDeviceData>>)method.Invoke(_inner, [args, execution, cancellationToken])!;
        }
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("reissue")]
    [InlineData("grant")]
    [InlineData("global")]
    [InlineData("expire")]
    public async Task Permission_change_before_first_write_denies_with_zero_writes(string change)
    {
        var (h, audio, writer, caller) = Setup();
        writer.OpenFailure = null;
        var applied = false;
        h.Store.EnabledTools["audio_set_default_device"] = true;
        // Trigger permission change during the pre-write endpoint read.
        audio.OnRead = () =>
        {
            if (applied) return;
            applied = true;
            h.Update(() => ApplyChange(h, change));
        };

        var r = await h.Executor.ExecuteAsync(caller, "audio_set_default_device", TestJson.Args(new { device = "spk2", direction = "output", role = "all" }), Ct);

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Contains(r.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled });
        Assert.Empty(writer.Writes);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("reissue")]
    [InlineData("grant")]
    [InlineData("global")]
    [InlineData("expire")]
    public async Task Permission_change_mid_all_stops_before_second_write_and_returns_partial(string change)
    {
        var (h, audio, writer, caller) = Setup();
        writer.OnWrite = (_, role) =>
        {
            if (role == AudioRole.Console)
            {
                h.Update(() => ApplyChange(h, change));
            }
        };

        var r = await h.Executor.ExecuteAsync(caller, "audio_set_default_device", TestJson.Args(new { device = "spk2", direction = "output", role = "all" }), Ct);

        Assert.Equal(ToolStatus.Partial, r.Status);
        Assert.Equal(AuditOutcome.Partial, h.Audit.Final(r.RequestId).Outcome);
        Assert.Single(writer.Writes);
        var roles = r.Data!["roles"]!.AsArray();
        Assert.Equal("changed", (string)roles[0]!["status"]!);
        Assert.Equal("not_attempted", (string)roles[1]!["status"]!);
        Assert.Equal("not_attempted", (string)roles[2]!["status"]!);
    }

    [Fact]
    public async Task Cancellation_between_writes_stops_and_returns_partial()
    {
        var (h, audio, writer, caller) = Setup();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        writer.OnWrite = (_, role) =>
        {
            if (role == AudioRole.Console)
            {
                cts.Cancel();
            }
        };

        var r = await h.Executor.ExecuteAsync(caller, "audio_set_default_device", TestJson.Args(new { device = "spk2", direction = "output", role = "all" }), cts.Token);

        // WaitAsync(token) may throw OperationCanceledException (Unknown with progress) or observe the returned Partial result.
        Assert.Contains(r.Status, new[] { ToolStatus.Partial, ToolStatus.Unknown });
        Assert.Equal(ErrorCodes.Cancelled, r.Error!.Code);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Executor.InFlight > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Single(writer.Writes);
    }

    [Fact]
    public async Task Timeout_during_hung_write_returns_unknown_with_progress_and_holds_locks_until_done()
    {
        var (h, _, writer, caller) = Setup(TimeSpan.FromMilliseconds(200));
        using var gate = new ManualResetEventSlim();
        writer.BlockGates[AudioRole.Multimedia] = gate;

        var r = await h.Executor.ExecuteAsync(caller, "audio_set_default_device", TestJson.Args(new { device = "spk2", direction = "output", role = "all" }), Ct);

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(ErrorCodes.Timeout, r.Error!.Code);
        Assert.NotNull(r.Data);
        var roles = r.Data!["roles"]!.AsArray();
        Assert.Equal("changed", (string)roles[0]!["status"]!);
        Assert.Equal("unknown", (string)roles[1]!["status"]!);
        Assert.Equal("not_attempted", (string)roles[2]!["status"]!);
        Assert.True(h.Executor.InFlight > 0);
        Assert.True(h.Executor.TrackedLockCount > 0);

        gate.Set();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Executor.InFlight > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(0, h.Executor.InFlight);
        Assert.Equal(0, h.Executor.TrackedLockCount);
        Assert.Equal(2, writer.Writes.Count); // Console + hung Multimedia; Communications must not have started after timeout detach
    }

    [Fact]
    public async Task Timeout_while_waiting_on_authorization_gate_prevents_second_write()
    {
        var (h, _, writer, caller) = Setup(TimeSpan.FromMilliseconds(200));
        using var gateHeld = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        Task? updater = null;
        writer.OnWrite = (_, role) =>
        {
            if (role == AudioRole.Console)
            {
                updater = Task.Run(() =>
                {
                    using var scope = h.Gate.BeginUpdate();
                    gateHeld.Set();
                    releaseGate.Wait(CancellationToken.None);
                }, CancellationToken.None);
                gateHeld.Wait(CancellationToken.None);
            }
        };

        var r = await h.Executor.ExecuteAsync(caller, "audio_set_default_device", TestJson.Args(new { device = "spk2", direction = "output", role = "all" }), Ct);

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(ErrorCodes.Timeout, r.Error!.Code);

        releaseGate.Set();
        await updater!;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Executor.InFlight > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(0, h.Executor.InFlight);
        Assert.Single(writer.Writes); // Multimedia must not start after timeout while waiting on AuthorizationGate
    }

    [Fact]
    public async Task Partial_without_begin_side_effect_fails_safe_as_internal_error()
    {
        var tool = new UnstartedPartialTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var r = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.InternalError, r.Error!.Code);
        Assert.Equal(AuditOutcome.Error, h.Audit.Final(r.RequestId).Outcome);
    }

    [Fact]
    public async Task BeginNextSideEffect_before_BeginSideEffect_throws_and_is_caught_as_error()
    {
        var tool = new MisorderedNextTool();
        var h = new Harness(null, tool);
        var (caller, _) = h.Store.AddClient("c1", tool.Name);

        var r = await h.Executor.ExecuteAsync(caller, tool.Name, TestJson.Args(new { value = 1 }), Ct);

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.InternalError, r.Error!.Code);
    }

    private sealed class UnstartedPartialTool : SysactaTool<EchoArgs, EchoData>
    {
        public override ToolDescriptor Descriptor { get; } = new("power_set_unstarted", ToolCategories.Power, "x", "x", DefaultPolicy.Off, false, []);

        protected override Task<ToolOutcome<EchoData>> ExecuteAsync(EchoArgs args, ToolExecution execution, CancellationToken cancellationToken) =>
            Task.FromResult(ToolOutcome.Partial(new EchoData(1), Verification.Verified, ErrorCodes.VerificationFailed, "bad"));
    }

    private sealed class MisorderedNextTool : SysactaTool<EchoArgs, EchoData>
    {
        public override ToolDescriptor Descriptor { get; } = new("power_set_misordered", ToolCategories.Power, "x", "x", DefaultPolicy.Off, false, []);

        protected override Task<ToolOutcome<EchoData>> ExecuteAsync(EchoArgs args, ToolExecution execution, CancellationToken cancellationToken)
        {
            _ = execution.BeginNextSideEffect(cancellationToken);
            return Task.FromResult(new ToolOutcome<EchoData>(new EchoData(1), Verification.Verified));
        }
    }

    private static void ApplyChange(Harness h, string change)
    {
        switch (change)
        {
            case "revoke": h.Store.Revoke("c1"); break;
            case "reissue": h.Store.Reissue("c1"); break;
            case "grant": h.Store.Grants[("c1", "audio_set_default_device")] = false; break;
            case "global": h.Store.EnabledTools["audio_set_default_device"] = false; break;
            case "expire": h.Store.Clients["c1"] = h.Store.Clients["c1"] with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }; break;
        }
    }
}
