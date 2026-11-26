using System.Text.Json.Nodes;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

/// <summary>The Phase 1B audio tools exercised through the real executor with fake Core Audio adapters.</summary>
public abstract class AudioPhase1BTestBase
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private protected readonly FakeAudioEndpoints Audio = new();
    private protected readonly FakeAudioSessions Sessions = new();
    private protected readonly Harness H;
    private protected readonly Caller Caller;

    protected AudioPhase1BTestBase()
    {
        H = new Harness(store =>
        [
            new AudioSetVolumeTool(Audio, store),
            new AudioListDevicesTool(Audio, store),
            new AudioSetMuteTool(Audio, store),
            new AudioListSessionsTool(Audio, store, Sessions),
            new AudioSetAppVolumeTool(Audio, Sessions),
            new AudioSetAppMuteTool(Audio, Sessions),
        ]);
        (Caller, _) = H.Store.AddClient("c1", "audio_set_volume", "audio_list_devices", "audio_set_mute", "audio_list_sessions", "audio_set_app_volume", "audio_set_app_mute");
    }

    protected Task<ToolResult> Call(string tool, object args, CancellationToken? cancellationToken = null) =>
        H.Executor.ExecuteAsync(Caller, tool, TestJson.Args(args), cancellationToken ?? Ct);

    protected static string PlayerId => AudioSessions.SessionId("spk", FakeAudioSessions.Player);

    protected static string SecondId => AudioSessions.SessionId("spk", FakeAudioSessions.PlayerSecond);

    private protected static JsonArray Array(ToolResult r, string name) => r.Data![name]!.AsArray();

    protected int AllWrites => Audio.Writes + Sessions.Writes.Count;
}

public class AudioListDevicesToolTests : AudioPhase1BTestBase
{
    [Theory]
    [InlineData(null, new[] { "spk", "mic", "hp" })]
    [InlineData("all", new[] { "spk", "mic", "hp" })]
    [InlineData("output", new[] { "spk", "hp" })]
    [InlineData("input", new[] { "mic" })]
    public async Task Lists_by_direction_with_active_devices_first(string? direction, string[] expected)
    {
        var r = await Call("audio_list_devices", direction is null ? new { } : new { direction });

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(expected, Array(r, "devices").Select(d => (string)d!["id"]!));
        Assert.Equal(expected.Length, (int)r.Data!["total_count"]!);
        Assert.False((bool)r.Data["truncated"]!);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("Output")]
    public async Task Rejects_other_directions(string direction) =>
        Assert.Equal(ErrorCodes.InvalidArgument, (await Call("audio_list_devices", new { direction })).Error!.Code);

    [Fact]
    public async Task Reports_roles_aliases_operations_and_no_values_for_inactive_devices()
    {
        Audio.RoleDefaults[(AudioDirection.Output, AudioRole.Communications)] = "hp";
        H.Store.Aliases[(ToolCategories.Audio, "desk")] = "spk";

        var devices = Array(await Call("audio_list_devices", new { direction = "output" }), "devices");

        var spk = devices[0]!;
        Assert.True((bool)spk["is_default"]!);
        Assert.Equal(["console", "multimedia"], spk["default_roles"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(["desk"], spk["aliases"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(50, (int)spk["volume"]!);
        Assert.Contains("audio_set_mute", spk["operations"]!.AsArray().Select(x => (string)x!));

        var hp = devices[1]!;
        Assert.False((bool)hp["is_default"]!);
        Assert.Equal(["communications"], hp["default_roles"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("unplugged", (string)hp["state"]!);
        Assert.Null(hp["volume"]);
        Assert.Null(hp["muted"]);
        Assert.Equal(["audio_get_state"], hp["operations"]!.AsArray().Select(x => (string)x!));
    }

    [Fact]
    public async Task No_devices_is_an_empty_list()
    {
        Audio.Devices.Clear();
        Audio.DefaultOutput = Audio.DefaultInput = null;

        var r = await Call("audio_list_devices", new { });

        Assert.True(r.IsSuccess);
        Assert.Empty(Array(r, "devices"));
    }

    [Fact]
    public async Task A_broken_device_is_reported_without_failing_the_list()
    {
        Audio.BrokenDevices.Add("spk");
        Audio.UnreadableDevices = 1;

        var r = await Call("audio_list_devices", new { direction = "output" });

        Assert.True(r.IsSuccess);
        Assert.Equal(1, (int)r.Data!["failed_count"]!);
        Assert.False((bool)r.Data["truncated"]!);
        Assert.Equal(ErrorCodes.OsApiError, (string)Array(r, "devices")[0]!["error_code"]!);
        Assert.Null(Array(r, "devices")[0]!["volume"]);
    }

    [Fact]
    public async Task An_enumeration_failure_is_an_error_and_the_tool_stays_available()
    {
        Audio.ListFailure = new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API returned an error.");
        Assert.Equal(ErrorCodes.OsApiError, (await Call("audio_list_devices", new { })).Error!.Code);

        Audio.ListFailure = null;
        Assert.True((await Call("audio_list_devices", new { })).IsSuccess);
    }

    [Fact]
    public async Task The_result_is_limited_and_says_so()
    {
        for (var i = 0; i < AudioListDevicesTool.MaxDevices + 5; i++)
        {
            Audio.Devices[$"old{i}"] = new($"old{i}", "Old", AudioDirection.Output, AudioDeviceState.NotPresent, null, null);
        }

        var r = await Call("audio_list_devices", new { });

        Assert.Equal(AudioListDevicesTool.MaxDevices, Array(r, "devices").Count);
        Assert.Equal(AudioListDevicesTool.MaxDevices + 8, (int)r.Data!["total_count"]!);
        Assert.True((bool)r.Data["truncated"]!);
        Assert.Equal("spk", (string)Array(r, "devices")[0]!["id"]!); // active devices are never the ones left out
    }
}

public class AudioSetMuteToolTests : AudioPhase1BTestBase
{
    [Fact]
    public async Task Mutes_verifies_and_leaves_the_volume()
    {
        var r = await Call("audio_set_mute", new { enabled = true });

        Assert.Equal(Verification.Verified, r.Verification);
        Assert.False((bool)r.Data!["previous_muted"]!);
        Assert.True((bool)r.Data["current_muted"]!);
        Assert.True((bool)r.Data["changed"]!);
        Assert.Equal("spk", (string)r.Data["device"]!["id"]!);
        Assert.Equal(("spk", true), Assert.Single(Audio.MuteCalls));
        Assert.Empty(Audio.SetCalls);
        Assert.Equal("""{"enabled":true,"direction":"output","target":"spk"}""", H.Audit.Final(r.RequestId).Arguments);
    }

    [Fact]
    public async Task Unmutes_the_input_device()
    {
        var r = await Call("audio_set_mute", new { enabled = false, direction = "input" });

        Assert.True(r.IsSuccess);
        Assert.Equal(("mic", false), Assert.Single(Audio.MuteCalls));
    }

    [Fact]
    public async Task Requested_state_already_set_is_a_verified_no_op()
    {
        var r = await Call("audio_set_mute", new { enabled = false });

        Assert.Equal(Verification.Verified, r.Verification);
        Assert.False((bool)r.Data!["changed"]!);
        Assert.Equal(0, Audio.Writes);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"enabled":"true"}""")]
    [InlineData("""{"enabled":1}""")]
    [InlineData("""{"enabled":null}""")]
    [InlineData("""{"enabled":true,"direction":"all"}""")]
    [InlineData("""{"enabled":true,"device":""}""")]
    [InlineData("""{"enabled":true,"device":"a\u0000b"}""")]
    [InlineData("""{"enabled":true,"toggle":true}""")]
    [InlineData("""{"enabled":true,"device":"all"}""")]
    public async Task Invalid_arguments_are_rejected(string json)
    {
        var r = await H.Executor.ExecuteAsync(Caller, "audio_set_mute", TestJson.Raw(json), Ct);

        Assert.Equal(ErrorCodes.InvalidArgument, r.Error!.Code);
        Assert.Equal(0, Audio.Writes);
    }

    [Theory]
    [InlineData("nope", ErrorCodes.TargetNotFound)]
    [InlineData("mic", ErrorCodes.TargetNotFound)]
    [InlineData("hp", ErrorCodes.DeviceDisconnected)]
    public async Task Target_errors_never_fall_back(string device, string code)
    {
        var r = await Call("audio_set_mute", new { enabled = true, device });

        Assert.Equal(code, r.Error!.Code);
        Assert.Equal(0, Audio.Writes);
    }

    [Fact]
    public async Task Os_failure_mismatch_and_unreadable_result_are_distinguished()
    {
        Audio.SetFailure = new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API returned an error.");
        Assert.Equal(ErrorCodes.OsApiError, (await Call("audio_set_mute", new { enabled = true })).Error!.Code);

        Audio.SetFailure = null;
        Audio.ApplyMute = _ => false;
        var mismatch = await Call("audio_set_mute", new { enabled = true });
        Assert.Equal(ToolStatus.Error, mismatch.Status);
        Assert.Equal(ErrorCodes.VerificationFailed, mismatch.Error!.Code);

        Audio.FailReadAfterSet = false;
        Audio.OnWrite = _ => Audio.FailReadAfterSet = true;
        var unknown = await Call("audio_set_mute", new { enabled = true });
        Assert.Equal(ToolStatus.Unknown, unknown.Status);
        Assert.False(unknown.Error!.Retryable);
        Assert.Equal(AuditOutcome.Unknown, H.Audit.Final(unknown.RequestId).Outcome);
    }

    [Fact]
    public async Task Default_and_explicit_calls_on_one_device_are_serialized_across_mute_and_volume()
    {
        using var inWrite = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Audio.OnWrite = _ =>
        {
            if (!inWrite.IsSet)
            {
                inWrite.Set();
                release.Wait(CancellationToken.None);
            }
        };
        var volume = Call("audio_set_volume", new { level = 10 });
        inWrite.Wait(Ct);

        var mute = Call("audio_set_mute", new { enabled = true, device = "SPK" });
        var other = await Call("audio_set_mute", new { enabled = false, direction = "input" }); // another device is not blocked

        Assert.True(other.IsSuccess);
        await Task.Delay(100, Ct);
        Assert.False(mute.IsCompleted);
        release.Set();
        Assert.True((await volume).IsSuccess);
        Assert.True((await mute).IsSuccess);
    }
}

public class AudioSessionToolTests : AudioPhase1BTestBase
{
    [Fact]
    public async Task Lists_sessions_of_the_default_device_without_exposing_raw_identifiers()
    {
        var r = await Call("audio_list_sessions", new { });

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal("spk", (string)r.Data!["device_id"]!);
        var sessions = Array(r, "sessions");
        Assert.Equal([PlayerId, SecondId], sessions.Select(s => (string)s!["session_id"]!)); // same app and PID, two ids
        Assert.Equal("active", (string)sessions[0]!["state"]!);
        Assert.Equal(60, (int)sessions[0]!["volume"]!);
        Assert.Equal(100, (int)sessions[0]!["process_id"]!);
        Assert.DoesNotContain("Harddisk", r.Data.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Session_ids_are_deterministic_and_bound_to_the_endpoint()
    {
        Assert.Equal(PlayerId, AudioSessions.SessionId("spk", FakeAudioSessions.Player));
        Assert.NotEqual(PlayerId, AudioSessions.SessionId("other", FakeAudioSessions.Player));
        Assert.Matches("^as_[A-Za-z0-9_-]{43}$", PlayerId);
    }

    [Fact]
    public async Task Empty_expired_and_unreadable_sessions()
    {
        Sessions.Devices["spk"] = [new("gone", null, null, AudioSessionStatus.Expired, null, null, false)];
        Sessions.UnreadableSessions = 2;
        var r = await Call("audio_list_sessions", new { });
        Assert.Empty(Array(r, "sessions")[0]!["operations"]!.AsArray());
        Assert.Null(Array(r, "sessions")[0]!["name"]);
        Assert.Equal(2, (int)r.Data!["failed_count"]!);

        Sessions.Devices.Clear();
        Sessions.UnreadableSessions = 0;
        Assert.Empty(Array(await Call("audio_list_sessions", new { }), "sessions"));

        Sessions.BrokenDevices.Add("spk");
        Assert.Equal(ErrorCodes.OsApiError, (await Call("audio_list_sessions", new { })).Error!.Code);
    }

    [Fact]
    public async Task The_session_list_is_limited_and_drops_expired_sessions_first()
    {
        Sessions.Devices["spk"] =
        [
            new("expired", null, null, AudioSessionStatus.Expired, null, null, false),
            .. Enumerable.Range(0, AudioListSessionsTool.MaxSessions).Select(i => new AudioSessionState($"s{i}", "x", i, AudioSessionStatus.Active, 1f, false, false)),
        ];

        var r = await Call("audio_list_sessions", new { });

        Assert.Equal(AudioListSessionsTool.MaxSessions, Array(r, "sessions").Count);
        Assert.True((bool)r.Data!["truncated"]!);
        Assert.DoesNotContain(Array(r, "sessions"), s => (string)s!["state"]! == "expired");
    }

    [Theory]
    [InlineData("nope", ErrorCodes.TargetNotFound)]
    [InlineData("mic", ErrorCodes.TargetNotFound)]
    [InlineData("hp", ErrorCodes.DeviceDisconnected)]
    public async Task Listing_a_missing_wrong_or_disconnected_device_never_falls_back(string device, string code) =>
        Assert.Equal(code, (await Call("audio_list_sessions", new { device })).Error!.Code);

    [Fact]
    public async Task Sets_only_the_addressed_session_volume()
    {
        var r = await Call("audio_set_app_volume", new { session_id = PlayerId, level = 30 });

        Assert.Equal(Verification.Verified, r.Verification);
        Assert.Equal(60, (int)r.Data!["previous_volume"]!);
        Assert.Equal(30, (int)r.Data["current_volume"]!);
        Assert.True((bool)r.Data["changed"]!);
        Assert.Equal(PlayerId, (string)r.Data["session_id"]!);
        Assert.Equal("spk", (string)r.Data["device_id"]!);
        Assert.Equal(("spk", FakeAudioSessions.Player, "volume:30"), Assert.Single(Sessions.Writes));
        Assert.Equal(0, Audio.Writes);
        Assert.Equal(0, Sessions.OpenHandles);
        Assert.Equal($$"""{"level":30,"target":"{{PlayerId}}"}""", H.Audit.Final(r.RequestId).Arguments);
    }

    [Fact]
    public async Task Sets_only_the_addressed_session_mute()
    {
        var r = await Call("audio_set_app_mute", new { session_id = SecondId, enabled = true });

        Assert.Equal(Verification.Verified, r.Verification);
        Assert.False((bool)r.Data!["previous_muted"]!);
        Assert.True((bool)r.Data["current_muted"]!);
        Assert.Equal(("spk", FakeAudioSessions.PlayerSecond, "mute:True"), Assert.Single(Sessions.Writes));
        Assert.Equal(0, Audio.Writes);
        Assert.Equal($$"""{"enabled":true,"target":"{{SecondId}}"}""", H.Audit.Final(r.RequestId).Arguments);
    }

    [Fact]
    public async Task Requested_value_already_set_is_a_verified_no_op()
    {
        var volume = await Call("audio_set_app_volume", new { session_id = PlayerId, level = 60 });
        var mute = await Call("audio_set_app_mute", new { session_id = PlayerId, enabled = false });

        Assert.All([volume, mute], r =>
        {
            Assert.Equal(Verification.Verified, r.Verification);
            Assert.False((bool)r.Data!["changed"]!);
        });
        Assert.Empty(Sessions.Writes);
        Assert.Equal(0, Sessions.OpenHandles);
    }

    [Theory]
    [InlineData("audio_set_app_volume", """{"level":10}""")]
    [InlineData("audio_set_app_volume", """{"session_id":"ID"}""")]
    [InlineData("audio_set_app_volume", """{"session_id":"ID","level":101}""")]
    [InlineData("audio_set_app_volume", """{"session_id":"ID","level":-1}""")]
    [InlineData("audio_set_app_volume", """{"session_id":"ID","level":1.5}""")]
    [InlineData("audio_set_app_volume", """{"session_id":"ID","level":"10"}""")]
    [InlineData("audio_set_app_volume", """{"session_id":"ID","level":10,"process_id":100}""")]
    [InlineData("audio_set_app_volume", """{"session_id":"","level":10}""")]
    [InlineData("audio_set_app_volume", """{"session_id":"a\n","level":10}""")]
    [InlineData("audio_set_app_mute", """{"enabled":true}""")]
    [InlineData("audio_set_app_mute", """{"session_id":"ID"}""")]
    [InlineData("audio_set_app_mute", """{"session_id":"ID","enabled":"yes"}""")]
    [InlineData("audio_set_app_mute", """{"session_id":null,"enabled":true}""")]
    public async Task Invalid_session_arguments_are_rejected(string tool, string json)
    {
        var r = await H.Executor.ExecuteAsync(Caller, tool, TestJson.Raw(json.Replace("ID", PlayerId, StringComparison.Ordinal)), Ct);

        Assert.Equal(ErrorCodes.InvalidArgument, r.Error!.Code);
        Assert.Empty(Sessions.Writes);
    }

    [Theory]
    [InlineData("player")]
    [InlineData("100")]
    [InlineData("as_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Names_pids_and_unknown_ids_are_not_guessed_and_not_audited(string id)
    {
        var r = await Call("audio_set_app_volume", new { session_id = id, level = 1 });

        Assert.Equal(ErrorCodes.TargetNotFound, r.Error!.Code);
        Assert.Empty(Sessions.Writes);
        Assert.Equal("""{"level":1}""", H.Audit.Final(r.RequestId).Arguments);
    }

    [Fact]
    public async Task A_recreated_session_gets_a_new_id_and_the_old_one_is_refused()
    {
        var old = PlayerId;
        Sessions.Devices["spk"][0] = Sessions.Devices["spk"][0] with { InstanceId = FakeAudioSessions.Player + "-new" };

        Assert.Equal(ErrorCodes.TargetNotFound, (await Call("audio_set_app_volume", new { session_id = old, level = 1 })).Error!.Code);
        Assert.Empty(Sessions.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_session_that_ends_after_resolution_is_not_written(bool replaced)
    {
        Sessions.OnOpen = () => Sessions.Devices["spk"][0] = replaced
            ? Sessions.Devices["spk"][0] with { InstanceId = "replacement" }
            : Sessions.Devices["spk"][0] with { Status = AudioSessionStatus.Expired };

        var r = await Call("audio_set_app_volume", new { session_id = PlayerId, level = 1 });

        Assert.Equal(ErrorCodes.TargetNotFound, r.Error!.Code);
        Assert.Empty(Sessions.Writes);
    }

    [Fact]
    public async Task Duplicate_instances_are_ambiguous()
    {
        Sessions.Devices["spk"].Add(Sessions.Devices["spk"][0]);

        Assert.Equal(ErrorCodes.AmbiguousTarget, (await Call("audio_set_app_mute", new { session_id = PlayerId, enabled = true })).Error!.Code);
        Assert.Empty(Sessions.Writes);
    }

    [Fact]
    public async Task Sessions_on_other_devices_are_found_and_partial_search_failures_are_not_called_missing()
    {
        Audio.Devices["usb"] = new("usb", "USB", AudioDirection.Output, AudioDeviceState.Active, 1f, false);
        Sessions.Devices["usb"] = [new(FakeAudioSessions.Player, "player", 100, AudioSessionStatus.Active, 1f, false, false)];
        var onUsb = AudioSessions.SessionId("usb", FakeAudioSessions.Player);

        Assert.True((await Call("audio_set_app_volume", new { session_id = onUsb, level = 5 })).IsSuccess);
        Assert.Equal("usb", Assert.Single(Sessions.Writes).Device); // the same instance text on "spk" is another session

        Sessions.BrokenDevices.Add("usb");
        Assert.Equal(ErrorCodes.OsApiError, (await Call("audio_set_app_volume", new { session_id = onUsb, level = 6 })).Error!.Code);
        Assert.True((await Call("audio_set_app_volume", new { session_id = PlayerId, level = 6 })).IsSuccess);
    }

    [Fact]
    public async Task Os_failure_mismatch_and_loss_after_the_write_are_distinguished()
    {
        var args = new { session_id = PlayerId, level = 10 };
        Sessions.SetFailure = new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API returned an error.");
        Assert.Equal(ErrorCodes.OsApiError, (await Call("audio_set_app_volume", args)).Error!.Code);

        Sessions.SetFailure = null;
        Sessions.IgnoreWrites = true;
        Assert.Equal(ErrorCodes.VerificationFailed, (await Call("audio_set_app_volume", args)).Error!.Code);

        Sessions.IgnoreWrites = false;
        Sessions.FailReadAfterSet = true;
        var unreadable = await Call("audio_set_app_volume", args);
        Assert.Equal(ToolStatus.Unknown, unreadable.Status);

        Sessions.FailReadAfterSet = false;
        Sessions.ExpireOnSet = true;
        var ended = await Call("audio_set_app_mute", new { session_id = PlayerId, enabled = true });
        Assert.Equal(ToolStatus.Unknown, ended.Status);
        Assert.Equal(AuditOutcome.Unknown, H.Audit.Final(ended.RequestId).Outcome);
        Assert.Equal(0, Sessions.OpenHandles);
    }

    [Fact]
    public async Task Volume_and_mute_of_one_session_are_serialized_while_other_sessions_run()
    {
        using var inWrite = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Sessions.OnWrite = _ =>
        {
            if (!inWrite.IsSet)
            {
                inWrite.Set();
                release.Wait(CancellationToken.None);
            }
        };
        var volume = Call("audio_set_app_volume", new { session_id = PlayerId, level = 10 });
        inWrite.Wait(Ct);

        var mute = Call("audio_set_app_mute", new { session_id = PlayerId, enabled = true });
        var other = await Call("audio_set_app_mute", new { session_id = SecondId, enabled = true });

        Assert.True(other.IsSuccess);
        await Task.Delay(100, Ct);
        Assert.False(mute.IsCompleted);
        Assert.Single(Sessions.Writes);
        release.Set();
        Assert.True((await volume).IsSuccess);
        Assert.True((await mute).IsSuccess);
    }
}

/// <summary>SEC-01 and the shared executor guarantees for each new state-changing tool.</summary>
public class AudioPhase1BBoundaryTests : AudioPhase1BTestBase
{
    private static object Args(string tool) => tool switch
    {
        "audio_set_mute" => new { enabled = true },
        "audio_set_app_volume" => new { session_id = PlayerId, level = 10 },
        _ => new { session_id = PlayerId, enabled = true },
    };

    /// <summary>Hooks the last read of the previous state, immediately before the start point.</summary>
    private void BeforeStart(string tool, Action action)
    {
        if (tool == "audio_set_mute")
        {
            // A default-device call reads the endpoint first in ExecuteAsync (the previous state).
            var reads = 0;
            Audio.OnRead = () =>
            {
                if (++reads == 1)
                {
                    action();
                }
            };
        }
        else
        {
            Sessions.OnRead = action;
        }
    }

    public static TheoryData<string, string> Changes()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in new[] { "audio_set_mute", "audio_set_app_volume", "audio_set_app_mute" })
        {
            foreach (var change in new[] { "revoke", "reissue", "grant", "global", "expire" })
            {
                data.Add(tool, change);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_change_completed_before_the_start_point_means_zero_writes(string tool, string change)
    {
        var applied = false;
        BeforeStart(tool, () =>
        {
            applied = true;
            H.Update(() =>
            {
                switch (change)
                {
                    case "revoke": H.Store.Revoke("c1"); break;
                    case "reissue": H.Store.Reissue("c1"); break;
                    case "grant": H.Store.Grants[("c1", tool)] = false; break;
                    case "global": H.Store.EnabledTools[tool] = false; break;
                    default: H.Store.Clients["c1"] = H.Store.Clients["c1"] with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }; break;
                }
            });
        });

        var r = await Call(tool, Args(tool));

        Assert.True(applied);
        Assert.Contains(r.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled });
        Assert.Equal(AuditOutcome.Denied, H.Audit.Final(r.RequestId).Outcome);
        Assert.Equal(0, AllWrites);
    }

    [Theory]
    [InlineData("audio_set_mute")]
    [InlineData("audio_set_app_volume")]
    [InlineData("audio_set_app_mute")]
    public async Task Without_an_audit_log_nothing_is_written(string tool)
    {
        H.Audit.Fail = true;

        var r = await Call(tool, Args(tool));

        Assert.Equal(ErrorCodes.AuditUnavailable, r.Error!.Code);
        Assert.Equal(0, AllWrites);
    }

    [Theory]
    [InlineData("audio_set_mute")]
    [InlineData("audio_set_app_volume")]
    [InlineData("audio_set_app_mute")]
    public async Task Cancellation_before_the_start_point_changes_nothing(string tool)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var release = new ManualResetEventSlim();
        BeforeStart(tool, () =>
        {
            cancel.Cancel();
            release.Wait(CancellationToken.None);
        });

        var r = await Call(tool, Args(tool), cancel.Token);
        release.Set();
        await Drain();

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.Cancelled, r.Error!.Code);
        Assert.Equal(0, AllWrites);
    }

    [Theory]
    [InlineData("audio_set_mute")]
    [InlineData("audio_set_app_volume")]
    [InlineData("audio_set_app_mute")]
    public async Task Cancellation_during_the_write_is_unknown(string tool)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var release = new ManualResetEventSlim();
        Action<string> block = _ =>
        {
            cancel.Cancel();
            release.Wait(CancellationToken.None);
        };
        Audio.OnWrite = block;
        Sessions.OnWrite = block;

        var r = await Call(tool, Args(tool), cancel.Token);

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(AuditOutcome.Unknown, H.Audit.Final(r.RequestId).Outcome);
        Assert.Equal(1, H.Executor.InFlight); // locks are held until the native call really ends
        release.Set();
        await Drain();
    }

    private async Task Drain()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (H.Executor.InFlight > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The call did not finish.");
            await Task.Delay(10, Ct);
        }
    }
}
