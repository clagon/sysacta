using System.Text;
using System.Text.Json;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Notification;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

/// <summary>Phase 1G over HTTP/MCP with a fake adapter: no notification is ever shown.</summary>
public class NotificationServerTests
{
    private const string Name = "notification_send";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, object?> Args(string title = "t", string body = "b") => new() { ["title"] = title, ["body"] = body };

    private static string Code(ModelContextProtocol.Protocol.CallToolResult r) =>
        Assert.IsType<JsonElement>(r.StructuredContent).GetProperty("error").GetProperty("code").GetString()!;

    private static Task Enable(ServerFixture f, bool enabled = true) => Task.Run(() => f.Store.SetToolEnabled(Name, enabled), Ct);

    [Fact]
    public async Task Accepted_over_mcp_and_the_limit_survives_a_host_restart_and_a_new_client()
    {
        await using var f = await ServerFixture.StartAsync(includeNotification: true);
        await Enable(f);
        await using (var client = await f.ClientAsync(f.Issue(Name).Token))
        {
            var tool = (await client.ListToolsAsync(cancellationToken: Ct)).Single(t => t.Name == Name).ProtocolTool;
            Assert.False(tool.Annotations!.ReadOnlyHint);
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.NotNull(tool.OutputSchema);

            var sent = await client.CallToolAsync(Name, Args("通知 <b>", "😀 & \"x\"\n2"), cancellationToken: Ct);
            Assert.False(sent.IsError);
            var body = Assert.IsType<JsonElement>(sent.StructuredContent);
            Assert.Equal("accepted", body.GetProperty("verification").GetString());
            Assert.Equal("""{"accepted":true,"retry_after_ms":null}""", body.GetProperty("data").GetRawText());
            Assert.Equal(("通知 <b>", "😀 & \"x\"\n2"), Assert.Single(f.Notifications.Sent));
        }

        await f.Host.StopAsync();
        await f.Host.StartAsync(new ServerEndpointOptions { Port = 0 }, Ct);
        await using var other = await f.ClientAsync(f.Issue(Name).Token);
        var refused = await other.CallToolAsync(Name, Args(), cancellationToken: Ct);
        Assert.True(refused.IsError);
        Assert.Equal(ErrorCodes.Busy, Code(refused));
        Assert.True(Assert.IsType<JsonElement>(refused.StructuredContent).GetProperty("data").GetProperty("retry_after_ms").GetInt64() > 0);
        Assert.Single(f.Notifications.Sent);

        f.Time.Advance(NotificationSendTool.MinInterval);
        Assert.False((await other.CallToolAsync(Name, Args(), cancellationToken: Ct)).IsError);
    }

    [Fact]
    public async Task Direct_calls_without_grant_global_permission_or_a_live_token_do_not_reach_the_adapter()
    {
        await using var f = await ServerFixture.StartAsync(includeNotification: true);
        await Enable(f);
        await using (var client = await f.ClientAsync(f.Issue("audio_get_state").Token))
        {
            Assert.Equal(ErrorCodes.PermissionDenied, Code(await client.CallToolAsync(Name, Args(), cancellationToken: Ct)));
        }

        var issued = f.Issue(Name);
        await Enable(f, false);
        await using (var client = await f.ClientAsync(issued.Token))
        {
            Assert.Equal(ErrorCodes.ToolDisabled, Code(await client.CallToolAsync(Name, Args(), cancellationToken: Ct)));
        }

        await Enable(f);
        await Task.Run(() => f.Store.RevokeClient(issued.Client.Id), Ct);
        using var http = f.Http(issued.Token);
        using var response = await http.SendAsync(ServerFixture.JsonRpc(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"notification_send","arguments":{"title":"t","body":"b"}}}"""), Ct);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, f.Notifications.Prepared);
        Assert.Empty(f.Notifications.Sent);
    }

    [Theory]
    [InlineData("""{"title":"synthetic-secret\u0007","body":"b"}""")]
    [InlineData("""{"title":"t","body":"synthetic-secret\u202E"}""")]
    [InlineData("""{"title":"synthetic-secret\ud800","body":"b"}""")]
    [InlineData("""{"title":"t","body":"b","launch":"synthetic-secret"}""")]
    public async Task Invalid_input_over_raw_http_sends_nothing_and_is_not_echoed(string arguments)
    {
        await using var f = await ServerFixture.StartAsync(includeNotification: true);
        await Enable(f);
        using var http = f.Http(f.Issue(Name).Token);
        using var response = await http.SendAsync(ServerFixture.JsonRpc(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"notification_send","arguments":{{{arguments}}}}}"""), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("synthetic-secret", text, StringComparison.Ordinal);
        Assert.Empty(f.Notifications.Sent);
    }

    [Fact]
    public async Task Notification_text_and_exception_text_never_reach_the_audit_store_exports_or_responses()
    {
        await using var f = await ServerFixture.StartAsync(includeNotification: true);
        await Enable(f);
        await using var client = await f.ClientAsync(f.Issue(Name).Token);
        var responses = new List<string>();
        async Task Call(string title, string body) =>
            responses.Add(Assert.IsType<JsonElement>((await client.CallToolAsync(Name, Args(title, body), cancellationToken: Ct)).StructuredContent).GetRawText());

        await Call("synthetic-title-1", "synthetic-body-1");
        await Call("synthetic-title-2", "synthetic-body-2"); // rate limited
        f.Time.Advance(NotificationSendTool.MinInterval);
        f.Notifications.OnSend = () => throw new InvalidOperationException("synthetic-exception C:\\private");
        await Call("synthetic-title-3", "synthetic-body-3");
        f.Time.Advance(NotificationSendTool.MinInterval);
        f.Notifications.OnPrepare = () => throw new InvalidOperationException("synthetic-prepare");
        await Call("synthetic-title-4", "synthetic-body-4");

        Assert.DoesNotContain(responses, r => r.Contains("synthetic", StringComparison.Ordinal));
        var events = f.Audit.Query(new AuditQuery(ToolName: Name));
        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.Null(e.Arguments));

        using var csv = new StringWriter();
        AuditLog.ExportCsv(events, csv);
        using var json = new MemoryStream();
        AuditLog.ExportJson(events, json);
        var data = new List<byte[]> { Encoding.UTF8.GetBytes(csv.ToString()), json.ToArray() };
        using var live = f.Database.Open();
        foreach (var path in Directory.GetFiles(Path.GetDirectoryName(f.Database.Path)!))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            data.Add(copy.ToArray());
        }

        foreach (var bytes in data)
        {
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("synthetic")) < 0);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("synthetic")) < 0);
        }
    }

    [Fact]
    public async Task A_new_install_enables_it_and_an_upgrade_adds_it_disabled_without_grants()
    {
        await using var f = await ServerFixture.StartAsync();
        var dir = Path.GetDirectoryName(f.Database.Path)!;
        var tool = new NotificationSendTool(new FakeNotifications(), TimeProvider.System);

        var freshDb = new SysactaDatabase(Path.Combine(dir, "notification-fresh.db"));
        freshDb.Migrate();
        var fresh = new AccessStore(freshDb, TimeProvider.System, new AuthorizationGate());
        fresh.SyncTools([tool.Descriptor], "after");
        Assert.True(fresh.IsToolEnabled(Name));

        var oldDb = new SysactaDatabase(Path.Combine(dir, "notification-upgrade.db"));
        oldDb.Migrate();
        var store = new AccessStore(oldDb, TimeProvider.System, new AuthorizationGate());
        var initial = new Core.Tools.Audio.AudioGetStateTool(f.AudioDevice, store);
        store.SyncTools([initial.Descriptor], "before");
        var old = store.IssueClient("old", null, ["audio_get_state"]);
        store.SyncTools([initial.Descriptor, tool.Descriptor], "after");
        Assert.False(store.IsToolEnabled(Name));
        Assert.False(store.IsClientToolAllowed(old.Client.Id, Name));
    }
}
