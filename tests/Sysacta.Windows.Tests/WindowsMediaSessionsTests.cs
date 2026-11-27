using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Media;
using Sysacta.Windows.Media;

namespace Sysacta.Windows.Tests;

public class WindowsMediaSessionsTests
{
    [Theory]
    [InlineData("song\ntitle", "songtitle")]
    [InlineData("https://example.invalid/secret", null)]
    [InlineData("h\nttps://example.invalid/secret", null)]
    [InlineData("C:\\private\\secret", null)]
    [InlineData("/home/user/secret", null)]
    [InlineData("ftp://example.invalid/secret", null)]
    [InlineData("/etc/private", null)]
    [InlineData("folder/private.mp3", null)]
    [InlineData("folder\\private.mp3", null)]
    [InlineData("listen mailto:private@example.invalid", null)]
    public void External_text_is_bounded_and_paths_and_urls_are_not_returned(string input, string? expected)
    {
        Assert.Equal(expected, WindowsMediaSessions.SafeText(input));
        Assert.Equal(256, WindowsMediaSessions.SafeText(new string('a', 300))!.Length);
        Assert.Null(WindowsMediaSessions.SafeText(new string('a', 4097)));
        Assert.Null(WindowsMediaSessions.SafeText(new string(' ', 4097)));
    }

    [Theory]
    [InlineData(10000L, 1L, 1L)]
    [InlineData(10001L, 1L, 2L)]
    [InlineData(-10001L, -2L, -1L)]
    public void Seek_bounds_round_inwards(long ticks, long maximum, long minimum)
    {
        Assert.Equal(maximum, WindowsMediaSessions.Milliseconds(TimeSpan.FromTicks(ticks)));
        Assert.Equal(minimum, WindowsMediaSessions.MinMilliseconds(TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public async Task Gsmtc_enumeration_is_read_only_and_disposes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        using var sessions = new WindowsMediaSessions();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var list = await sessions.ListAsync(timeout.Token);
        Assert.True(list.TotalCount >= list.Sessions.Count);
        Assert.True(list.Sessions.Count <= 64);
        Assert.Equal(list.Sessions.Count, list.Sessions.Select(s => s.SessionId).Distinct(StringComparer.Ordinal).Count());
        foreach (var entry in list.Sessions) Assert.Matches("^m_[0-9a-f]{32}$", entry.SessionId);
    }
    [Fact]
    public async Task Concurrent_read_only_initialization_is_bounded_and_disposal_refuses_new_work()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        using var sessions = new WindowsMediaSessions();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        MediaSessionList[] lists;
        try
        {
            lists = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sessions.ListAsync(timeout.Token)));
        }
        catch (ToolFailureException ex) when (ex.Code == ErrorCodes.TargetChanged)
        {
            Assert.Skip("The actual session list changed during this read-only test.");
            return;
        }
        Assert.Equal(8, lists.Length);
        Assert.All(lists, list => Assert.True(list.Sessions.Count <= 64));
        sessions.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sessions.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Preparation_rejects_stale_state_and_seek_range_without_starting_media()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        using var sessions = new WindowsMediaSessions();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var list = await sessions.ListAsync(timeout.Token);
        foreach (var entry in list.Sessions)
        {
            MediaState state;
            try { state = await sessions.ReadAsync(entry.SessionId, false, timeout.Token); }
            catch (ToolFailureException ex) when (ex.Code is ErrorCodes.TargetChanged or ErrorCodes.TargetNotFound)
            {
                Assert.Skip("The actual session list changed during this read-only test.");
                return;
            }
            var operations = new[] { MediaOperation.Play, MediaOperation.Pause,
                MediaOperation.Stop, MediaOperation.NextTrack,
                MediaOperation.PreviousTrack };
            var supported = operations.Where(state.Controls.Supports).Cast<MediaOperation?>().FirstOrDefault();
            if (supported is null) continue;
            try
            {
                _ = sessions.Prepare(entry.SessionId, false, supported.Value, null, state); // Never invoke the delegate.
                var changed = state.PlaybackStatus == MediaPlaybackStatus.Playing
                    ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
                var failure = Assert.Throws<ToolFailureException>(() =>
                    sessions.Prepare(entry.SessionId, false, supported.Value, null, state with { PlaybackStatus = changed }));
                Assert.Equal(ErrorCodes.TargetChanged, failure.Code);
                if (state.Controls.Seek && state.MinSeekTicks is { } min && state.MaxSeekTicks > min)
                {
                    var staleRange = Assert.Throws<ToolFailureException>(() =>
                        sessions.Prepare(entry.SessionId, false, MediaOperation.Seek, min,
                            state with { MinSeekTicks = min == long.MinValue ? min + 1 : min - 1 }));
                    Assert.Equal(ErrorCodes.TargetChanged, staleRange.Code);
                }
                return;
            }
            catch (ToolFailureException ex) when (ex.Code is ErrorCodes.TargetChanged or ErrorCodes.TargetNotFound or ErrorCodes.NotSupported)
            {
                Assert.Skip("The actual session changed during this read-only test.");
                return;
            }
        }
        Assert.Skip("No session exposes a suitable operation for this read-only test.");
    }

    [Fact]
    public async Task A_media_properties_event_invalidates_prepared_seek_without_changing_media()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        using var sessions = new WindowsMediaSessions();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var list = await sessions.ListAsync(timeout.Token);
        foreach (var item in list.Sessions)
        {
            var state = await sessions.ReadAsync(item.SessionId, false, timeout.Token);
            if (!state.Controls.Seek || state.MinSeekTicks is not { } min || state.MaxSeekTicks <= min) continue;
            var field = typeof(WindowsMediaSessions).GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var entries = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(sessions));
            var entry = Assert.Single(entries.Values.Cast<object>(), value =>
                (string)value.GetType().GetProperty("Id")!.GetValue(value)! == item.SessionId);
            entry.GetType().GetMethod("OnMediaPropertiesChanged")!.Invoke(entry, [null, null]);
            var failure = Assert.Throws<ToolFailureException>(() => sessions.Prepare(item.SessionId, false, MediaOperation.Seek, min, state));
            Assert.Equal(ErrorCodes.TargetChanged, failure.Code);
            return;
        }
        Assert.Skip("No session exposes a seek range for this read-only test.");
    }

}
