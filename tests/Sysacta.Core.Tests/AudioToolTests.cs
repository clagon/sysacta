using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;

namespace Sysacta.Core.Tests;

/// <summary>Reference tools exercised through the real executor with a fake Core Audio adapter.</summary>
public class AudioToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeAudioEndpoints _audio = new();
    private readonly Harness _h;
    private readonly Security.Caller _caller;

    public AudioToolTests()
    {
        _h = new Harness(store => [new AudioGetStateTool(_audio, store), new AudioSetVolumeTool(_audio, store)]);
        (_caller, _) = _h.Store.AddClient("c1", "audio_get_state", "audio_set_volume");
    }

    private Task<ToolResult> Call(string tool, object args) => _h.Executor.ExecuteAsync(_caller, tool, TestJson.Args(args), Ct);

    [Fact]
    public async Task Get_state_uses_default_output_device()
    {
        var r = await Call("audio_get_state", new { });

        Assert.True(r.IsSuccess);
        Assert.Equal(Verification.NotApplicable, r.Verification);
        Assert.Equal("spk", (string?)r.Data!["device"]!["id"]);
        Assert.True((bool)r.Data["device"]!["is_default"]!);
        Assert.Equal("active", (string?)r.Data["device"]!["state"]);
        Assert.Equal(50, (int)r.Data["volume"]!);
        Assert.False((bool)r.Data["muted"]!);
    }

    [Fact]
    public async Task Get_state_by_id_alias_and_direction()
    {
        _h.Store.Aliases[(ToolCategories.Audio, "desk")] = "spk";

        Assert.Equal("spk", (string?)(await Call("audio_get_state", new { device = "desk" })).Data!["device"]!["id"]);
        var mic = await Call("audio_get_state", new { direction = "input" });
        Assert.Equal("mic", (string?)mic.Data!["device"]!["id"]);
        Assert.Equal(80, (int)mic.Data["volume"]!);
    }

    [Fact]
    public async Task Get_state_of_inactive_device_reports_null_volume()
    {
        var r = await Call("audio_get_state", new { device = "hp" });
        Assert.True(r.IsSuccess);
        Assert.Equal("unplugged", (string?)r.Data!["device"]!["state"]);
        Assert.Null(r.Data["volume"]);
    }

    [Theory]
    [InlineData("nope", "output", ErrorCodes.TargetNotFound)]
    [InlineData("mic", "output", ErrorCodes.TargetNotFound)] // wrong direction never falls back
    [InlineData("all", "output", ErrorCodes.InvalidArgument)]
    public async Task Target_errors(string device, string direction, string code)
    {
        var r = await Call("audio_get_state", new { device, direction });
        Assert.Equal(code, r.Error!.Code);
    }

    [Fact]
    public void Different_spellings_of_one_device_share_the_lock_and_target()
    {
        var a = AudioTarget.Resolve(_audio, _h.Store, "spk", AudioDirection.Output);
        var b = AudioTarget.Resolve(_audio, _h.Store, "SPK", AudioDirection.Output);
        var byDefault = AudioTarget.Resolve(_audio, _h.Store, null, AudioDirection.Output);

        Assert.Equal("spk", b.TargetId);
        Assert.Equal(a.LockKeys, b.LockKeys);
        Assert.Contains(a.LockKeys[0], byDefault.LockKeys); // explicit and default calls on the same device serialize
    }

    [Fact]
    public async Task No_default_device_is_target_not_found()
    {
        _audio.DefaultOutput = null;
        Assert.Equal(ErrorCodes.TargetNotFound, (await Call("audio_set_volume", new { level = 10 })).Error!.Code);
    }

    [Fact]
    public async Task Set_volume_applies_and_verifies()
    {
        var r = await Call("audio_set_volume", new { level = 30 });

        Assert.True(r.IsSuccess);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.Equal(50, (int)r.Data!["previous_volume"]!);
        Assert.Equal(30, (int)r.Data["current_volume"]!);
        Assert.True((bool)r.Data["changed"]!);
        Assert.Equal(("spk", 0.3f), Assert.Single(_audio.SetCalls));
        Assert.Equal("""{"level":30,"direction":"output","target":"spk"}""", _h.Audit.Final(r.RequestId).Arguments);
    }

    [Fact]
    public async Task Set_volume_to_current_value_is_a_verified_no_op()
    {
        var r = await Call("audio_set_volume", new { level = 50 });
        Assert.True(r.IsSuccess);
        Assert.False((bool)r.Data!["changed"]!);
        Assert.Empty(_audio.SetCalls);
    }

    [Fact]
    public async Task Set_volume_on_inactive_device_is_device_disconnected()
    {
        var r = await Call("audio_set_volume", new { level = 10, device = "hp" });
        Assert.Equal(ErrorCodes.DeviceDisconnected, r.Error!.Code);
        Assert.Empty(_audio.SetCalls);
    }

    [Fact]
    public async Task Read_back_mismatch_is_verification_failed()
    {
        _audio.ApplyTransform = _ => 0.99f; // e.g. another app or driver overrides the value
        var r = await Call("audio_set_volume", new { level = 10 });
        Assert.Equal(ErrorCodes.VerificationFailed, r.Error!.Code);
        Assert.False(r.Error.Retryable);
    }

    [Fact]
    public async Task Read_back_failure_after_the_change_is_reported_as_unknown()
    {
        _audio.FailReadAfterSet = true;
        var r = await Call("audio_set_volume", new { level = 10 });

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(Verification.Unknown, r.Verification);
        Assert.Equal(ErrorCodes.DeviceDisconnected, r.Error!.Code);
        Assert.False(r.Error.Retryable);
        Assert.Single(_audio.SetCalls);
        Assert.Equal(Core.Auditing.AuditOutcome.Unknown, _h.Audit.Final(r.RequestId).Outcome);
    }

    [Fact]
    public async Task Os_failure_is_reported_as_structured_error()
    {
        _audio.SetFailure = new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API returned an error.");
        var r = await Call("audio_set_volume", new { level = 10 });
        Assert.Equal(ErrorCodes.OsApiError, r.Error!.Code);
    }

    [Fact]
    public async Task Invalid_level_is_rejected()
    {
        Assert.Equal(ErrorCodes.InvalidArgument, (await Call("audio_set_volume", new { level = 150 })).Error!.Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Call("audio_set_volume", new { level = 10, mute = true })).Error!.Code);
        Assert.Empty(_audio.SetCalls);
    }

    [Fact]
    public async Task Concurrent_sets_on_same_device_are_serialized_and_all_verified()
    {
        _h.Store.Aliases[(ToolCategories.Audio, "desk")] = "spk";
        var calls = Enumerable.Range(1, 20).Select(i => Call("audio_set_volume", new { level = i, device = i % 2 == 0 ? "desk" : "spk" }));
        var results = await Task.WhenAll(calls);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.Message));
    }
}
