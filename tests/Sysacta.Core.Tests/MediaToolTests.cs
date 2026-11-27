using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Media;

namespace Sysacta.Core.Tests;

public class MediaToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeMedia _media = new();
    private readonly Harness _h;
    private readonly Caller _caller;

    public MediaToolTests()
    {
        _h = new Harness(_ => [new MediaListSessionsTool(_media), new MediaGetStateTool(_media),
            new MediaPlayTool(_media), new MediaPauseTool(_media), new MediaStopTool(_media),
            new MediaNextTrackTool(_media), new MediaPreviousTrackTool(_media), new MediaSeekTool(_media)]);
        (_caller, _) = _h.Store.AddClient("client", "media_list_sessions", "media_get_state", "media_play", "media_pause", "media_stop", "media_next_track", "media_previous_track", "media_seek");
    }

    private Task<ToolResult> Call(string name, object args) => _h.Executor.ExecuteAsync(_caller, name, TestJson.Args(args), Ct);

    [Theory]
    [InlineData("media_play")]
    [InlineData("media_pause")]
    [InlineData("media_stop")]
    [InlineData("media_next_track")]
    [InlineData("media_previous_track")]
    public async Task Invalid_session_inputs_do_not_start_an_operation(string name)
    {
        foreach (var value in new string?[] { null, "", "all", "default", "m_0000000000000000000000000000000\n" })
        {
            var result = await Call(name, new { session_id = value });
            Assert.Equal(ErrorCodes.InvalidArgument, result.Error?.Code);
        }
        Assert.Equal(0, _media.Starts);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(922337203685478L)]
    public async Task Seek_rejects_unrepresentable_values(long position)
    {
        var result = await Call("media_seek", new { position_ms = position });
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Empty_list_and_missing_current_are_distinct_from_expired_explicit_id()
    {
        _media.HasSession = false;
        var list = await Call("media_list_sessions", new { });
        Assert.True(list.IsSuccess);
        Assert.Equal(Verification.NotApplicable, list.Verification);
        Assert.Empty(list.Data!["sessions"]!.AsArray());
        var current = await Call("media_get_state", new { });
        Assert.Equal(ErrorCodes.NoActiveSession, current.Error?.Code);
        var expired = await Call("media_play", new { session_id = FakeMedia.Id });
        Assert.Equal(ErrorCodes.TargetNotFound, expired.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Ambiguous_current_session_never_falls_back_to_an_explicit_target()
    {
        _media.AmbiguousCurrent = true;
        var result = await Call("media_play", new { });
        Assert.Equal(ErrorCodes.AmbiguousTarget, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Current_session_switch_during_preparation_refuses_to_write()
    {
        _media.OnRead = n => { if (n == 2) _media.CurrentId = FakeMedia.OtherId; };
        var result = await Call("media_play", new { });
        Assert.Equal(ErrorCodes.TargetChanged, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Explicit_session_does_not_follow_current_switch()
    {
        _media.OnRead = n => { if (n == 2) _media.CurrentId = FakeMedia.OtherId; };
        var result = await Call("media_play", new { session_id = FakeMedia.Id });
        Assert.True(result.IsSuccess);
        Assert.Equal(Verification.Verified, result.Verification);
        Assert.Equal(FakeMedia.Id, _media.StartedId);
    }

    [Theory]
    [InlineData("media_play", MediaPlaybackStatus.Paused, MediaPlaybackStatus.Playing)]
    [InlineData("media_pause", MediaPlaybackStatus.Playing, MediaPlaybackStatus.Paused)]
    [InlineData("media_stop", MediaPlaybackStatus.Playing, MediaPlaybackStatus.Stopped)]
    public async Task Explicit_playback_requests_are_verified_only_after_matching_readback(
        string tool, MediaPlaybackStatus before, MediaPlaybackStatus after)
    {
        _media.State = _media.State with { PlaybackStatus = before };
        _media.OnStart = () => _media.State = _media.State with { PlaybackStatus = after };
        var result = await Call(tool, new { });
        Assert.True(result.IsSuccess);
        Assert.Equal(Verification.Verified, result.Verification);
        Assert.True((bool)result.Data!["changed"]!);
        Assert.Equal(1, _media.Starts);
    }

    [Theory]
    [InlineData("media_play")]
    [InlineData("media_pause")]
    [InlineData("media_stop")]
    [InlineData("media_next_track")]
    [InlineData("media_previous_track")]
    [InlineData("media_seek")]
    public async Task Disabled_operation_is_rejected_before_any_native_request(string tool)
    {
        _media.State = _media.State with { PlaybackStatus = MediaPlaybackStatus.Opened, Controls = new(false, false, false, false, false, false) };
        var args = tool == "media_seek" ? (object)new { position_ms = 1000 } : new { };
        var result = await Call(tool, args);
        Assert.Equal(ErrorCodes.NotSupported, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"position_ms\":null}")]
    [InlineData("{\"position_ms\":\"500\"}")]
    [InlineData("{\"position_ms\":1.5}")]
    [InlineData("{\"position_ms\":500,\"session_id\":null}")]
    [InlineData("{\"position_ms\":500,\"extra\":true}")]
    [InlineData("{\"position_ms\":500,\"position_ms\":500}")]
    public async Task Seek_rejects_invalid_json_before_any_native_request(string json)
    {
        var result = await _h.Executor.ExecuteAsync(_caller, "media_seek", TestJson.Raw(json), Ct);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Noop_and_unsupported_are_distinguished()
    {
        var noop = await Call("media_pause", new { });
        Assert.Equal(Verification.Verified, noop.Verification);
        Assert.False((bool)noop.Data!["changed"]!);
        _media.State = _media.State with { Controls = _media.State.Controls with { Play = false } };
        var unsupported = await Call("media_play", new { });
        Assert.Equal(ErrorCodes.NotSupported, unsupported.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Skip_is_accepted_without_claiming_a_track_change()
    {
        var result = await Call("media_next_track", new { });
        Assert.Equal(Verification.Accepted, result.Verification);
        Assert.Null(result.Data!["changed"]);
        Assert.Equal(1, _media.Starts);
    }

    [Fact]
    public async Task Api_false_is_unknown_and_not_retryable()
    {
        _media.Accepted = false;
        var result = await Call("media_next_track", new { });
        Assert.Equal(ToolStatus.Unknown, result.Status);
        Assert.False(result.Error!.Retryable);
        Assert.Equal(1, _media.Starts);
    }

    [Fact]
    public async Task Native_failure_after_start_is_unknown_and_never_leaks_details()
    {
        _media.NativeResult = Task.FromException<bool>(new ToolFailureException(ErrorCodes.OsApiError, "secret native message"));
        var result = await Call("media_next_track", new { });
        Assert.Equal(ToolStatus.Unknown, result.Status);
        Assert.False(result.Error!.Retryable);
        Assert.DoesNotContain("secret", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(1, _media.Starts);
    }

    [Fact]
    public async Task Negative_range_start_does_not_exclude_a_valid_nonnegative_position()
    {
        _media.State = _media.State with { MinSeekMs = -1000, MaxSeekMs = 5000 };
        var result = await Call("media_seek", new { position_ms = 0 });
        Assert.True(result.IsSuccess);
        Assert.Equal(Verification.Accepted, result.Verification);
        Assert.Equal(0, _media.Ticks);
        Assert.Equal(1, _media.Starts);
    }

    [Fact]
    public async Task Seek_validates_range_and_rejects_a_changed_item()
    {
        var outside = await Call("media_seek", new { position_ms = 5001 });
        Assert.Equal(ErrorCodes.InvalidArgument, outside.Error?.Code);
        _media.Reads = 0;
        _media.OnRead = n => { if (n == 2) _media.State = _media.State with { Title = "new" }; };
        var changed = await Call("media_seek", new { position_ms = 1000 });
        Assert.Equal(ErrorCodes.TargetChanged, changed.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Seek_noop_requires_exact_native_ticks_and_stationary_state()
    {
        _media.State = _media.State with { PositionTicks = 500 * TimeSpan.TicksPerMillisecond };
        var paused = await Call("media_seek", new { position_ms = 500 });
        Assert.Equal(Verification.Verified, paused.Verification);
        Assert.False((bool)paused.Data!["changed"]!);
        Assert.Equal(0, _media.Starts);
        _media.State = _media.State with { PositionTicks = 500 * TimeSpan.TicksPerMillisecond + 1 };
        var fractional = await Call("media_seek", new { position_ms = 500 });
        Assert.Equal(Verification.Accepted, fractional.Verification);
        Assert.Equal(1, _media.Starts);
    }

    [Fact]
    public async Task Seek_readback_requires_a_new_stationary_observation_on_the_same_item()
    {
        var originalTime = DateTimeOffset.UtcNow;
        _media.State = _media.State with { MetadataFingerprint = "same", LastUpdatedTime = originalTime, PositionTicks = 100 };
        _media.OnStart = () => _media.State = _media.State with { PositionTicks = 1000 * TimeSpan.TicksPerMillisecond, LastUpdatedTime = originalTime.AddSeconds(1) };
        var result = await Call("media_seek", new { position_ms = 1000 });
        Assert.Equal(Verification.Verified, result.Verification);
        Assert.True((bool)result.Data!["changed"]!);
    }

    [Fact]
    public async Task Seek_converts_milliseconds_to_ticks_and_does_not_claim_verification()
    {
        var result = await Call("media_seek", new { position_ms = 1000 });
        Assert.Equal(Verification.Accepted, result.Verification);
        Assert.Equal(1000 * TimeSpan.TicksPerMillisecond, _media.Ticks);
        Assert.Equal(1, _media.Starts);
    }

    [Fact]
    public async Task Current_switch_during_final_preparation_is_refused_before_side_effect()
    {
        _media.OnPrepare = () => _media.CurrentId = FakeMedia.OtherId;
        var result = await Call("media_play", new { });
        Assert.Equal(ToolStatus.Error, result.Status);
        Assert.Equal(ErrorCodes.TargetChanged, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Capability_lost_during_final_preparation_prevents_the_native_call()
    {
        _media.OnPrepare = () => _media.State = _media.State with { Controls = _media.State.Controls with { Play = false } };
        var result = await Call("media_play", new { });
        Assert.Equal(ErrorCodes.NotSupported, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Track_change_after_last_read_prevents_seek_even_when_timeline_is_identical()
    {
        _media.State = _media.State with { MediaRevision = 1 };
        _media.OnPrepare = () => _media.State = _media.State with { MediaRevision = 2 };
        var result = await Call("media_seek", new { position_ms = 1000 });
        Assert.Equal(ErrorCodes.TargetChanged, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Track_change_between_metadata_samples_prevents_seek()
    {
        _media.State = _media.State with { MediaRevision = 1 };
        _media.OnRead = n => { if (n == 2) _media.State = _media.State with { MediaRevision = 2 }; };
        var result = await Call("media_seek", new { position_ms = 1000 });
        Assert.Equal(ErrorCodes.TargetChanged, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Playback_state_changed_during_final_preparation_prevents_the_native_call()
    {
        _media.OnPrepare = () => _media.State = _media.State with { PlaybackStatus = MediaPlaybackStatus.Stopped };
        var result = await Call("media_play", new { });
        Assert.Equal(ErrorCodes.TargetChanged, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Seek_range_changed_during_final_preparation_prevents_the_native_call()
    {
        _media.OnPrepare = () => _media.State = _media.State with { MaxSeekTicks = 4000 * TimeSpan.TicksPerMillisecond };
        _media.State = _media.State with { MinSeekTicks = 0, MaxSeekTicks = 5000 * TimeSpan.TicksPerMillisecond };
        var result = await Call("media_seek", new { position_ms = 1000 });
        Assert.Equal(ErrorCodes.TargetChanged, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Metadata_fingerprint_is_never_serialized()
    {
        _media.State = _media.State with { MetadataFingerprint = "private-fingerprint" };
        var result = await Call("media_get_state", new { });
        Assert.True(result.IsSuccess);
        Assert.DoesNotContain("private-fingerprint", result.Data!.ToJsonString(), StringComparison.Ordinal);
        var schema = _h.Executor.Registry.Find("media_get_state")!.OutputSchema.ToString();
        Assert.DoesNotContain("metadata_fingerprint", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("min_seek_ticks", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("max_seek_ticks", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("media_revision", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Permission_revoked_during_preparation_prevents_the_native_call()
    {
        _media.OnRead = n =>
        {
            if (n == 2) _h.Update(() => _h.Store.Grants[("client", "media_play")] = false);
        };
        var result = await Call("media_play", new { });
        Assert.Equal(ErrorCodes.PermissionDenied, result.Error?.Code);
        Assert.Equal(0, _media.Starts);
    }

    [Fact]
    public async Task Same_session_remains_locked_until_the_native_task_really_finishes()
    {
        var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _media.NativeResult = native.Task;
        using var cancel = new CancellationTokenSource();
        var first = _h.Executor.ExecuteAsync(_caller, "media_next_track", TestJson.Args(new { }), cancel.Token);
        await _media.Started.Task.WaitAsync(Ct);
        cancel.Cancel();
        var cancelled = await first;
        Assert.Equal(ToolStatus.Unknown, cancelled.Status);
        Assert.False(cancelled.Error!.Retryable);

        var second = Call("media_previous_track", new { session_id = FakeMedia.Id });
        await Task.Delay(100, Ct);
        Assert.Equal(1, _media.Starts);
        native.SetResult(true);
        var result = await second;
        Assert.True(result.IsSuccess);
        Assert.Equal(2, _media.Starts);
    }

    [Fact]
    public async Task Different_session_lock_keys_allow_independent_operations()
    {
        var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _media.NativeResult = native.Task;
        var first = Call("media_next_track", new { session_id = FakeMedia.Id });
        await _media.Started.Task.WaitAsync(Ct);
        var second = Call("media_previous_track", new { session_id = FakeMedia.OtherId });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref _media.Starts) < 2 && DateTime.UtcNow < deadline) await Task.Delay(10, Ct);
        try
        {
            Assert.Equal(2, _media.Starts);
        }
        finally
        {
            native.TrySetResult(true);
        }
        Assert.True((await first).IsSuccess);
        Assert.True((await second).IsSuccess);
    }

    [Fact]
    public async Task Prepared_session_is_kept_alive_until_native_operation_finishes()
    {
        var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _media.NativeResult = native.Task;
        _media.TrackPreparedLifetime = true;
        using var cancel = new CancellationTokenSource();
        var call = _h.Executor.ExecuteAsync(_caller, "media_next_track", TestJson.Args(new { }), cancel.Token);
        await _media.Started.Task.WaitAsync(Ct);
        cancel.Cancel();
        Assert.Equal(ToolStatus.Unknown, (await call).Status);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.True(_media.PreparedLifetime!.IsAlive);
        native.SetResult(true);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_h.Executor.InFlight > 0)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Accepted_request_survives_readback_cancellation_without_releasing_the_lock()
    {
        _media.ReadGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var first = _h.Executor.ExecuteAsync(_caller, "media_next_track", TestJson.Args(new { }), cancel.Token);
        await _media.ReadbackStarted.Task.WaitAsync(Ct);
        cancel.Cancel();
        var result = await first;
        Assert.True(result.IsSuccess);
        Assert.Equal(Verification.Accepted, result.Verification);
        Assert.True((bool)result.Data!["accepted"]!);
        Assert.Null(result.Data["changed"]);
        var next = Call("media_previous_track", new { session_id = FakeMedia.Id });
        await Task.Delay(100, Ct);
        Assert.Equal(1, _media.Starts);
        _media.ReadGate.SetResult(true);
        Assert.True((await next).IsSuccess);
        Assert.Equal(2, _media.Starts);
    }

    [Fact]
    public async Task Rotated_public_ids_for_the_same_instance_share_the_lock()
    {
        var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _media.NativeResult = native.Task;
        _media.LockKeyOverride = "same-instance";
        using var cancel = new CancellationTokenSource();
        var first = _h.Executor.ExecuteAsync(_caller, "media_next_track", TestJson.Args(new { }), cancel.Token);
        await _media.Started.Task.WaitAsync(Ct);
        cancel.Cancel();
        Assert.Equal(ToolStatus.Unknown, (await first).Status);
        _media.CurrentId = FakeMedia.OtherId;
        var second = Call("media_previous_track", new { });
        await Task.Delay(100, Ct);
        Assert.Equal(1, _media.Starts);
        native.SetResult(true);
        Assert.True((await second).IsSuccess);
        Assert.Equal(2, _media.Starts);
    }

    [Fact]
    public async Task Audit_records_resolved_id_and_numeric_seek_only()
    {
        _media.State = _media.State with { Title = "private-title", Artist = "private-artist" };
        var result = await Call("media_seek", new { position_ms = 1000, session_id = FakeMedia.Id });
        var audit = _h.Audit.Final(result.RequestId).Arguments!;
        Assert.Contains("position_ms", audit, StringComparison.Ordinal);
        Assert.Contains(FakeMedia.Id, audit, StringComparison.Ordinal);
        Assert.DoesNotContain("private", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("session_id", audit, StringComparison.Ordinal);
    }

    private sealed class FakeMedia : IMediaSessions
    {
        public const string Id = "m_00000000000000000000000000000001";
        public const string OtherId = "m_00000000000000000000000000000002";
        public string CurrentId = Id;
        public bool HasSession = true;
        public bool AmbiguousCurrent;
        public string? LockKeyOverride;
        public MediaState State = new(Id, true, MediaPlaybackStatus.Paused, new(true, true, true, true, true, true), "old", "artist", 500, 0, 5000, 0, 5000, null);
        public int Reads;
        public int Starts;
        public string? StartedId;
        public long? Ticks;
        public bool Accepted = true;
        public Action<int>? OnRead;
        public Action? OnPrepare;
        public Action? OnStart;
        public bool TrackPreparedLifetime;
        public WeakReference? PreparedLifetime;
        public Task<bool>? NativeResult;
        public TaskCompletionSource<bool>? ReadGate;
        public TaskCompletionSource<bool> ReadbackStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MediaSessionList> ListAsync(CancellationToken cancellationToken) => Task.FromResult(new MediaSessionList([], 0, 0, false));
        public Task<MediaSessionTarget> ResolveAsync(string? sessionId, CancellationToken cancellationToken)
        {
            if (sessionId is null && AmbiguousCurrent)
                throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "Current session is ambiguous.");
            if (!HasSession)
                throw new ToolFailureException(sessionId is null ? ErrorCodes.NoActiveSession : ErrorCodes.TargetNotFound, "Session is unavailable.");
            return Task.FromResult(new MediaSessionTarget(sessionId ?? CurrentId, LockKeyOverride ?? sessionId ?? CurrentId));
        }
        public async Task<MediaState> ReadAsync(string sessionId, bool requireCurrent, CancellationToken cancellationToken)
        {
            var read = ++Reads;
            OnRead?.Invoke(read);
            if (read == 3 && ReadGate is not null)
            {
                ReadbackStarted.TrySetResult(true);
                await ReadGate.Task;
            }
            if (requireCurrent && CurrentId != sessionId)
            {
                throw new ToolFailureException(ErrorCodes.TargetChanged, "Current session changed.");
            }
            return State with { IsCurrent = CurrentId == sessionId };
        }
        public Func<Task<bool>> Prepare(string sessionId, bool requireCurrent, MediaOperation operation, long? positionTicks, MediaState expected)
        {
            OnPrepare?.Invoke();
            if (requireCurrent && CurrentId != sessionId)
            {
                throw new ToolFailureException(ErrorCodes.TargetChanged, "Current session changed.");
            }
            if (!State.Controls.Supports(operation))
                throw new ToolFailureException(ErrorCodes.NotSupported, "Operation no longer supported.");
            if (State.PlaybackStatus != expected.PlaybackStatus ||
                (operation == MediaOperation.Seek && (State.MediaRevision != expected.MediaRevision || State.MinSeekTicks != expected.MinSeekTicks || State.MaxSeekTicks != expected.MaxSeekTicks)))
                throw new ToolFailureException(ErrorCodes.TargetChanged, "State changed.");
            var lifetime = TrackPreparedLifetime ? new object() : null;
            if (lifetime is not null) PreparedLifetime = new WeakReference(lifetime);
            return () =>
            {
                GC.KeepAlive(lifetime);
                Interlocked.Increment(ref Starts);
                Started.TrySetResult(true);
                OnStart?.Invoke();
                StartedId = sessionId;
                Ticks = positionTicks;
                if (operation == MediaOperation.Play && Accepted) State = State with { PlaybackStatus = MediaPlaybackStatus.Playing };
                return NativeResult ?? Task.FromResult(Accepted);
            };
        }
    }
}
