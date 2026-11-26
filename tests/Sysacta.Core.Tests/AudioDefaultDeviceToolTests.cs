using System.Text.Json.Nodes;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

public class AudioDefaultDeviceToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeAudioEndpoints _audio = new();
    private readonly FakeDefaultEndpointWriter _writer;
    private readonly Harness _h;
    private readonly Caller _caller;

    public AudioDefaultDeviceToolTests()
    {
        _audio.Devices["spk2"] = new("spk2", "Speakers 2", AudioDirection.Output, AudioDeviceState.Active, 0.7f, false);
        _audio.Devices["mic2"] = new("mic2", "Microphone 2", AudioDirection.Input, AudioDeviceState.Active, 0.6f, false);
        _writer = new FakeDefaultEndpointWriter(_audio);
        _h = new Harness(store =>
        [
            new AudioSetDefaultDeviceTool(_audio, store, _writer),
            new AudioSetVolumeTool(_audio, store),
            new AudioSetMuteTool(_audio, store),
        ]);
        (_caller, _) = _h.Store.AddClient("c1", "audio_set_default_device", "audio_set_volume", "audio_set_mute");
    }

    private Task<ToolResult> Call(object args, CancellationToken? ct = null) =>
        _h.Executor.ExecuteAsync(_caller, "audio_set_default_device", TestJson.Args(args), ct ?? Ct);

    [Fact]
    public async Task Rejects_missing_null_unknown_and_invalid_arguments()
    {
        var cases = new object[]
        {
            new { },
            new { device = "spk2", direction = "output" },
            new { device = "spk2", role = "console" },
            new { direction = "output", role = "console" },
            new { device = (string?)null, direction = "output", role = "console" },
            new { device = "spk2", direction = (string?)null, role = "console" },
            new { device = "spk2", direction = "output", role = (string?)null },
            new { device = "spk2", direction = "output", role = "console", extra = 1 },
            new { device = "spk2", direction = "Output", role = "console" },
            new { device = "spk2", direction = "output", role = "All" },
            new { device = "spk2", direction = "output", role = "Console" },
            new { device = "all", direction = "output", role = "console" },
            new { device = "default", direction = "output", role = "console" },
            new { device = "spk2\n", direction = "output", role = "console" },
        };

        foreach (var args in cases)
        {
            var r = await Call(args);
            Assert.Equal(ToolStatus.Error, r.Status);
            Assert.Equal(ErrorCodes.InvalidArgument, r.Error!.Code);
        }

        Assert.Empty(_writer.Writes);
    }

    [Fact]
    public async Task Rejects_unknown_other_direction_and_inactive_devices()
    {
        var unknown = await Call(new { device = "missing", direction = "output", role = "console" });
        var wrongDir = await Call(new { device = "mic2", direction = "output", role = "console" });
        var unplugged = await Call(new { device = "hp", direction = "output", role = "console" });

        Assert.Equal(ErrorCodes.TargetNotFound, unknown.Error!.Code);
        Assert.Equal(ErrorCodes.TargetNotFound, wrongDir.Error!.Code);
        Assert.Equal(ErrorCodes.DeviceDisconnected, unplugged.Error!.Code);
        Assert.Empty(_writer.Writes);
    }

    [Theory]
    [InlineData("output", "spk2", "console", AudioRole.Console)]
    [InlineData("output", "spk2", "multimedia", AudioRole.Multimedia)]
    [InlineData("output", "spk2", "communications", AudioRole.Communications)]
    [InlineData("input", "mic2", "console", AudioRole.Console)]
    [InlineData("input", "mic2", "multimedia", AudioRole.Multimedia)]
    [InlineData("input", "mic2", "communications", AudioRole.Communications)]
    public async Task Sets_each_direction_and_role_and_leaves_others_unchanged(
        string direction, string target, string roleName, AudioRole expectedRole)
    {
        var r = await Call(new { device = target, direction, role = roleName });

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(Verification.Verified, r.Verification);
        var entry = Assert.Single(r.Data!["roles"]!.AsArray());
        Assert.Equal("changed", (string)entry!["status"]!);
        Assert.True((bool)entry["changed"]!);
        Assert.Equal(target, (string)entry["current_device_id"]!);
        Assert.Empty(r.Data["unrequested_changes"]!.AsArray());
        Assert.Equal([(target, expectedRole)], _writer.Writes);
    }

    [Fact]
    public async Task Resolves_alias_and_records_canonical_id()
    {
        _h.Store.Aliases[(ToolCategories.Audio, "studio")] = "spk2";

        var r = await Call(new { device = "studio", direction = "output", role = "multimedia" });

        Assert.True(r.IsSuccess);
        Assert.Equal("spk2", (string)r.Data!["device_id"]!);
        Assert.Contains("spk2", _h.Audit.Final(r.RequestId).Arguments!);
    }

    [Fact]
    public async Task All_sets_all_three_roles_and_noop_performs_zero_writes()
    {
        var r = await Call(new { device = "spk2", direction = "output", role = "all" });

        Assert.True(r.IsSuccess);
        Assert.Equal(3, _writer.Writes.Count);
        Assert.All(r.Data!["roles"]!.AsArray(), x => Assert.Equal("changed", (string)x!["status"]!));

        _writer.Writes.Clear();
        var noop = await Call(new { device = "spk2", direction = "output", role = "all" });

        Assert.True(noop.IsSuccess);
        Assert.Equal(Verification.Verified, noop.Verification);
        Assert.Empty(_writer.Writes);
        Assert.All(noop.Data!["roles"]!.AsArray(), x =>
        {
            Assert.Equal("unchanged", (string)x!["status"]!);
            Assert.False((bool)x["changed"]!);
        });
    }

    [Fact]
    public async Task Readback_mismatch_returns_error_when_nothing_changed()
    {
        _writer.Ignore.Add(AudioRole.Console);

        var r = await Call(new { device = "spk2", direction = "output", role = "console" });

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.VerificationFailed, r.Error!.Code);
    }

    [Fact]
    public async Task Readback_to_unexpected_different_device_returns_partial_preserving_data()
    {
        _audio.Devices["spk3"] = new("spk3", "Speakers 3", AudioDirection.Output, AudioDeviceState.Active, 0.5f, false);
        _writer.Ignore.Add(AudioRole.Console);
        _writer.OnWrite = (_, _) => _audio.RoleDefaults[(AudioDirection.Output, AudioRole.Console)] = "spk3";

        var r = await Call(new { device = "spk2", direction = "output", role = "console" });

        Assert.Equal(ToolStatus.Partial, r.Status);
        Assert.Equal(ErrorCodes.VerificationFailed, r.Error!.Code);
        var role = Assert.Single(r.Data!["roles"]!.AsArray());
        Assert.Equal("failed", (string)role!["status"]!);
        Assert.True((bool)role["changed"]!);
        Assert.Equal("spk3", (string)role["current_device_id"]!);
    }

    [Fact]
    public async Task Readback_failure_returns_unknown_with_role_details()
    {
        _writer.FailReadAfterWrite = true;

        var r = await Call(new { device = "spk2", direction = "output", role = "all" });

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(Verification.Unknown, r.Verification);
        var roles = r.Data!["roles"]!.AsArray();
        Assert.Equal("unknown", (string)roles[0]!["status"]!);
        Assert.Null(roles[0]!["changed"]);
        Assert.Equal("not_attempted", (string)roles[1]!["status"]!);
        Assert.Equal("not_attempted", (string)roles[2]!["status"]!);
        Assert.Single(_writer.Writes);
    }

    [Fact]
    public async Task Api_error_with_state_unchanged_returns_error_and_with_state_changed_returns_verified_with_error_code()
    {
        _writer.FailWith[AudioRole.Console] = new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio policy interface returned an error.");

        var failed = await Call(new { device = "spk2", direction = "output", role = "console" });
        Assert.Equal(ToolStatus.Error, failed.Status);
        Assert.Equal(ErrorCodes.OsApiError, failed.Error!.Code);

        _writer.ApplyDespiteFailure.Add(AudioRole.Console);
        var applied = await Call(new { device = "spk2", direction = "output", role = "console" });
        Assert.True(applied.IsSuccess);
        Assert.Equal(Verification.Verified, applied.Verification);
        Assert.Equal(ErrorCodes.OsApiError, (string)applied.Data!["roles"]![0]!["error_code"]!);
    }

    [Fact]
    public async Task Partial_failure_stops_remaining_roles_and_reports_not_attempted()
    {
        _writer.FailWith[AudioRole.Multimedia] = new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio policy interface returned an error.");

        var r = await Call(new { device = "spk2", direction = "output", role = "all" });

        Assert.Equal(ToolStatus.Partial, r.Status);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.Equal(AuditOutcome.Partial, _h.Audit.Final(r.RequestId).Outcome);
        var roles = r.Data!["roles"]!.AsArray();
        Assert.Equal("changed", (string)roles[0]!["status"]!);
        Assert.Equal("failed", (string)roles[1]!["status"]!);
        Assert.Equal("not_attempted", (string)roles[2]!["status"]!);
        Assert.Equal(2, _writer.Writes.Count);
    }

    [Fact]
    public async Task Coupled_roles_are_detected_and_not_written_twice_when_all_requested()
    {
        _writer.Couple[AudioRole.Console] = AudioRole.Multimedia;

        var r = await Call(new { device = "spk2", direction = "output", role = "all" });

        Assert.True(r.IsSuccess);
        Assert.Equal(2, _writer.Writes.Count); // Console + Communications only
        Assert.All(r.Data!["roles"]!.AsArray(), x => Assert.Equal("changed", (string)x!["status"]!));
        Assert.Empty(r.Data["unrequested_changes"]!.AsArray());
    }

    [Fact]
    public async Task Unrequested_role_or_cross_direction_change_returns_partial_and_stops()
    {
        _writer.Couple[AudioRole.Console] = AudioRole.Multimedia;

        var r1 = await Call(new { device = "spk2", direction = "output", role = "console" });
        Assert.Equal(ToolStatus.Partial, r1.Status);
        var unreq1 = Assert.Single(r1.Data!["unrequested_changes"]!.AsArray());
        Assert.Equal("multimedia", (string)unreq1!["role"]!);

        _audio.RoleDefaults.Clear();
        _writer.Couple.Clear();
        _writer.Writes.Clear();
        _writer.CrossDirectionSideEffect = true;

        var r2 = await Call(new { device = "spk2", direction = "output", role = "all" });
        Assert.Equal(ToolStatus.Partial, r2.Status);
        var unreq2 = Assert.Single(r2.Data!["unrequested_changes"]!.AsArray());
        Assert.Equal("input", (string)unreq2!["direction"]!);
        Assert.Single(_writer.Writes);
    }

    [Fact]
    public async Task Default_targeted_volume_queued_behind_default_change_returns_target_changed_without_writing()
    {
        using var inWrite = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        _writer.OnWrite = (_, _) =>
        {
            inWrite.Set();
            releaseWrite.Wait(CancellationToken.None);
        };

        var changeDefault = Call(new { device = "spk2", direction = "output", role = "multimedia" });
        inWrite.Wait(Ct);

        // Volume resolves while default is still "spk", then waits on audio:default:output:multimedia lock.
        var setVolume = _h.Executor.ExecuteAsync(_caller, "audio_set_volume", TestJson.Args(new { level = 20 }), Ct);
        await Task.Delay(50, Ct);

        releaseWrite.Set();
        var defaultResult = await changeDefault;
        var volumeResult = await setVolume;

        Assert.True(defaultResult.IsSuccess);
        Assert.Equal(ToolStatus.Error, volumeResult.Status);
        Assert.Equal(ErrorCodes.TargetChanged, volumeResult.Error!.Code);
        Assert.True(volumeResult.Error.Retryable);
        Assert.Empty(_audio.SetCalls);
    }

    [Fact]
    public async Task Concurrent_all_and_single_role_calls_do_not_deadlock()
    {
        var t1 = Call(new { device = "spk2", direction = "output", role = "all" });
        var t2 = Call(new { device = "spk", direction = "output", role = "console" });

        var results = await Task.WhenAll(t1, t2);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.Message));
    }
}
