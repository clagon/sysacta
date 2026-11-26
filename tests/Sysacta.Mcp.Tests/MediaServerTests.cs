using System.Text;
using System.Text.Json;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Media;

namespace Sysacta.Mcp.Tests;

public sealed class FakeMediaSessions : IMediaSessions
{
    public const string Id = "m_00000000000000000000000000000001";
    private int _writes;
    public int Writes => Volatile.Read(ref _writes);
    public Task<MediaSessionList> ListAsync(CancellationToken cancellationToken) => Task.FromResult(new MediaSessionList([], 0, 0, false));
    public Task<MediaSessionTarget> ResolveAsync(string? sessionId, CancellationToken cancellationToken) => Task.FromResult(new MediaSessionTarget(Id, "one"));
    public Task<MediaState> ReadAsync(string sessionId, bool requireCurrent, CancellationToken cancellationToken) => Task.FromResult(
        new MediaState(Id, true, MediaPlaybackStatus.Paused, new(true, true, true, true, true, true), "secret title", "secret artist", 100, 0, 1000, 0, 1000, null));
    public Func<Task<bool>> Prepare(string sessionId, bool requireCurrent, MediaOperation operation, long? positionTicks) => () =>
    {
        Interlocked.Increment(ref _writes);
        return Task.FromResult(true);
    };
}

public class MediaServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Names = ["media_list_sessions", "media_get_state", "media_play", "media_pause", "media_stop", "media_next_track", "media_previous_track", "media_seek"];

    [Fact]
    public async Task Schemas_are_strict_and_seek_position_is_required()
    {
        await using var f = await ServerFixture.StartAsync(includeMedia: true);
        await using var client = await f.ClientAsync(f.Issue(Names).Token);
        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).ToDictionary(x => x.Name, x => x.ProtocolTool);
        Assert.Equal(Names.Order(), tools.Keys.Order());
        foreach (var tool in tools.Values)
        {
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.NotNull(tool.OutputSchema);
            Assert.Equal(tool.Name is "media_list_sessions" or "media_get_state", tool.Annotations!.ReadOnlyHint);
        }
        Assert.Equal("position_ms", tools["media_seek"].InputSchema.GetProperty("required")[0].GetString());
    }

    [Theory]
    [InlineData("media_play")]
    [InlineData("media_pause")]
    [InlineData("media_stop")]
    [InlineData("media_next_track")]
    [InlineData("media_previous_track")]
    [InlineData("media_seek")]
    public async Task Calls_without_grant_do_not_reach_media(string name)
    {
        await using var f = await ServerFixture.StartAsync(includeMedia: true);
        await using var client = await f.ClientAsync(f.Issue("media_get_state").Token);
        var result = await client.CallToolAsync(name, name == "media_seek" ? new Dictionary<string, object?> { ["position_ms"] = 500 } : [], cancellationToken: Ct);
        var body = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.True(result.IsError);
        Assert.Equal(ErrorCodes.PermissionDenied, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, f.Media.Writes);
    }

    [Fact]
    public async Task New_install_enables_media_but_upgrade_does_not_grant_existing_clients()
    {
        await using var f = await ServerFixture.StartAsync();
        var path = Path.Combine(Path.GetDirectoryName(f.Database.Path)!, "media-upgrade.db");
        var db = new Sysacta.Infrastructure.SysactaDatabase(path);
        db.Migrate();
        var gate = new Sysacta.Core.Security.AuthorizationGate();
        var store = new Sysacta.Infrastructure.AccessStore(db, TimeProvider.System, gate);
        SysactaTool[] initial = [new Sysacta.Core.Tools.Audio.AudioGetStateTool(f.AudioDevice, store)];
        store.SyncTools(initial.Select(t => t.Descriptor), "before");
        var old = store.IssueClient("old", null, ["audio_get_state"]);
        var media = new FakeMediaSessions();
        SysactaTool[] all = [.. initial, new MediaListSessionsTool(media), new MediaGetStateTool(media),
            new MediaPlayTool(media), new MediaPauseTool(media), new MediaStopTool(media), new MediaNextTrackTool(media),
            new MediaPreviousTrackTool(media), new MediaSeekTool(media)];
        store.SyncTools(all.Select(t => t.Descriptor), "after");
        Assert.All(Names, name =>
        {
            Assert.False(store.IsToolEnabled(name));
            Assert.False(store.IsClientToolAllowed(old.Client.Id, name));
        });
        var freshDb = new Sysacta.Infrastructure.SysactaDatabase(path + ".fresh");
        freshDb.Migrate();
        var fresh = new Sysacta.Infrastructure.AccessStore(freshDb, TimeProvider.System, gate);
        fresh.SyncTools(all.Select(t => t.Descriptor), "after");
        Assert.All(Names, name => Assert.True(fresh.IsToolEnabled(name)));
    }

    [Fact]
    public async Task Metadata_is_returned_only_to_authorized_reader_and_not_in_audit()
    {
        await using var f = await ServerFixture.StartAsync(includeMedia: true);
        await using var client = await f.ClientAsync(f.Issue("media_get_state", "media_seek").Token);
        var read = await client.CallToolAsync("media_get_state", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.False(read.IsError);
        Assert.Contains("secret title", Assert.IsType<JsonElement>(read.StructuredContent).GetRawText(), StringComparison.Ordinal);
        var write = await client.CallToolAsync("media_seek", new Dictionary<string, object?> { ["position_ms"] = 500 }, cancellationToken: Ct);
        Assert.False(write.IsError);
        Assert.Equal(1, f.Media.Writes);
        var events = f.Audit.Query(new Sysacta.Infrastructure.AuditQuery());
        using var csv = new StringWriter();
        Sysacta.Infrastructure.AuditLog.ExportCsv(events, csv);
        using var json = new MemoryStream();
        Sysacta.Infrastructure.AuditLog.ExportJson(events, json);
        var data = new List<byte[]> { Encoding.UTF8.GetBytes(csv.ToString()), json.ToArray() };
        using var liveDatabase = f.Database.Open();
        foreach (var path in Directory.GetFiles(Path.GetDirectoryName(f.Database.Path)!))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            data.Add(copy.ToArray());
        }
        foreach (var bytes in data)
        {
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("secret title")) < 0);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("secret artist")) < 0);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("secret title")) < 0);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("secret artist")) < 0);
        }
    }
}
