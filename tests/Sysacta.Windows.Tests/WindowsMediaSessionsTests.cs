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
        Sysacta.Core.Tools.Media.MediaSessionList[] lists;
        try
        {
            lists = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sessions.ListAsync(timeout.Token)));
        }
        catch (Sysacta.Core.Tools.ToolFailureException ex) when (ex.Code == Sysacta.Core.Tools.ErrorCodes.TargetChanged)
        {
            Assert.Skip("The actual session list changed during this read-only test.");
            return;
        }
        Assert.Equal(8, lists.Length);
        Assert.All(lists, list => Assert.True(list.Sessions.Count <= 64));
        sessions.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sessions.ListAsync(CancellationToken.None));
    }

}
