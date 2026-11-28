using System.Text;
using System.Text.Json;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Apps;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

/// <summary>Phase 2A over HTTP/MCP with the real SQLite allow-list and a fake OS (nothing real is launched or terminated).</summary>
public class AppServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Tools = ["app_list_available", "app_list_running", "app_launch"];

    private static string Code(ModelContextProtocol.Protocol.CallToolResult r) =>
        Assert.IsType<JsonElement>(r.StructuredContent).GetProperty("error").GetProperty("code").GetString()!;

    private static JsonElement Body(ModelContextProtocol.Protocol.CallToolResult r) => Assert.IsType<JsonElement>(r.StructuredContent);

    private static ExecutableIdentity Id(int n) => FakeAppRegistry.Identity(n);

    /// <summary>Registers and allows one app the way the console does, and puts its file in the fake OS.</summary>
    private static RegisteredApp Register(ServerFixture f, int n, IReadOnlyList<string> args, bool allow = true)
    {
        var path = $@"C:\Apps\app{n}\app{n}.exe";
        var id = f.Apps.Register(path, $"App {n}", args, Id(n));
        if (allow)
        {
            f.Apps.SetAllowed(id, 1, true);
        }

        var app = f.Apps.ListAll().Single(a => a.Id == id).App!;
        f.AppOs.AddFile(app);
        return app;
    }

    [Fact]
    public async Task A_new_install_enables_only_the_everyday_tools_and_an_upgrade_adds_all_disabled_without_grants()
    {
        await using var f = await ServerFixture.StartAsync(includeApps: true);
        Assert.Equal([true, true, true], Tools.Select(f.Store.IsToolEnabled));

        var dir = Path.GetDirectoryName(f.Database.Path)!;
        var registry = new FakeAppRegistry();
        var os = new FakeAppPlatform();
        SysactaTool[] appTools = [new AppListAvailableTool(registry, os), new AppListRunningTool(registry, os), new AppLaunchTool(registry, os)];
        var oldDb = new SysactaDatabase(Path.Combine(dir, "apps-upgrade.db"));
        oldDb.Migrate();
        var old = new AccessStore(oldDb, TimeProvider.System, new AuthorizationGate());
        old.SyncTools([f.Executor.Registry.Find("audio_get_state")!.Descriptor], "before");
        var client = old.IssueClient("old", null, PermissionPresets.InitialGrants(PermissionPreset.AllEnabled, f.Executor.Registry.All, old.IsToolEnabled));
        old.SyncTools(appTools.Select(t => t.Descriptor), "after");
        Assert.All(Tools, t => Assert.False(old.IsToolEnabled(t)));
        Assert.DoesNotContain(old.GetClientGrants(client.Client.Id), Tools.Contains);
    }

    [Fact]
    public async Task Mcp_offers_no_way_to_register_allow_or_change_apps()
    {
        await using var f = await ServerFixture.StartAsync(includeApps: true);
        await Task.Run(() => { foreach (var t in Tools) f.Store.SetToolEnabled(t, true); }, Ct);
        await using var client = await f.ClientAsync(f.Issue(Tools).Token);
        var names = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Where(n => n.StartsWith("app_", StringComparison.Ordinal) || n.StartsWith("process_", StringComparison.Ordinal)).Order().ToList();
        Assert.Equal(Tools.Order(), names);
        foreach (var tool in (await client.ListToolsAsync(cancellationToken: Ct)).Where(t => Tools.Contains(t.Name)))
        {
            Assert.False(tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.NotNull(tool.ProtocolTool.OutputSchema);
        }

        using var http = f.Http(f.Issue(Tools).Token);
        using var response = await http.SendAsync(ServerFixture.JsonRpc(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"app_register","arguments":{"path":"C:\\x.exe"}}}"""), Ct);
        Assert.Contains("Unknown tool", await response.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(f.Apps.ListAll());
    }

    [Fact]
    public async Task Launch_over_mcp_uses_only_the_local_approval()
    {
        await using var f = await ServerFixture.StartAsync(includeApps: true);
        var app = await Task.Run(() => Register(f, 1, ["--profile", "p"]), Ct);
        await using var client = await f.ClientAsync(f.Issue(Tools).Token);

        var listed = Body(await client.CallToolAsync("app_list_available", cancellationToken: Ct));
        var entry = listed.GetProperty("data").GetProperty("apps")[0];
        Assert.Equal((app.Id, "ready", true), (entry.GetProperty("app_id").GetString(), entry.GetProperty("status").GetString(), entry.GetProperty("launchable").GetBoolean()));

        var launched = await client.CallToolAsync("app_launch", new Dictionary<string, object?> { ["app_id"] = app.Id }, cancellationToken: Ct);
        Assert.False(launched.IsError);
        Assert.Equal("accepted", Body(launched).GetProperty("verification").GetString());
        Assert.Equal(1, f.AppOs.Launches);
    }

    [Fact]
    public async Task Direct_calls_without_grant_global_permission_or_app_permission_never_reach_the_os()
    {
        await using var f = await ServerFixture.StartAsync(includeApps: true);
        var app = await Task.Run(() => Register(f, 1, []), Ct);
        var other = await Task.Run(() => Register(f, 2, [], allow: false), Ct);
        var args = new Dictionary<string, object?> { ["app_id"] = app.Id };

        await using (var client = await f.ClientAsync(f.Issue("app_list_available").Token))
        {
            Assert.Equal(ErrorCodes.PermissionDenied, Code(await client.CallToolAsync("app_launch", args, cancellationToken: Ct)));
        }

        await using (var client = await f.ClientAsync(f.Issue(Tools).Token))
        {
            Assert.Equal(ErrorCodes.TargetNotFound, Code(await client.CallToolAsync("app_launch", new Dictionary<string, object?> { ["app_id"] = other.Id }, cancellationToken: Ct)));
            await Task.Run(() => f.Apps.SetAllowed(app.Id, 1, false), Ct);
            Assert.Equal(ErrorCodes.TargetNotFound, Code(await client.CallToolAsync("app_launch", args, cancellationToken: Ct)));
        }

        Assert.Equal(0, f.AppOs.Launches);
    }

    [Fact]
    public async Task Disabling_in_the_console_during_preparation_refuses_the_launch()
    {
        await using var f = await ServerFixture.StartAsync(includeApps: true);
        var app = await Task.Run(() => Register(f, 1, []), Ct);
        f.AppOs.OnPrepare = () => f.Apps.SetAllowed(app.Id, 1, false);
        await using var client = await f.ClientAsync(f.Issue(Tools).Token);
        var r = await client.CallToolAsync("app_launch", new Dictionary<string, object?> { ["app_id"] = app.Id }, cancellationToken: Ct);
        Assert.Equal(ErrorCodes.PermissionDenied, Code(r));
        Assert.Equal(0, f.AppOs.Launches);

        await Task.Run(() => f.Apps.SetAllowed(app.Id, 1, true), Ct);
        f.AppOs.OnPrepare = () => f.Apps.Reapprove(app.Id, 1, app.Path, ["changed"], app.Identity);
        r = await client.CallToolAsync("app_launch", new Dictionary<string, object?> { ["app_id"] = app.Id }, cancellationToken: Ct);
        Assert.Equal(ErrorCodes.PermissionDenied, Code(r));
        Assert.Equal(0, f.AppOs.Launches);
    }

    [Fact]
    public async Task Paths_arguments_hashes_filters_and_exceptions_never_reach_responses_or_the_audit_log()
    {
        await using var f = await ServerFixture.StartAsync(includeApps: true);
        await Task.Run(() => { foreach (var t in Tools) f.Store.SetToolEnabled(t, true); }, Ct);
        var app = await Task.Run(() => Register(f, 1, ["synthetic-arg"]), Ct);
        await using var client = await f.ClientAsync(f.Issue(Tools).Token);
        var responses = new List<string>();
        async Task Call(string tool, Dictionary<string, object?>? args = null) =>
            responses.Add(Body(await client.CallToolAsync(tool, args, cancellationToken: Ct)).GetRawText());

        await Call("app_list_available");
        await Call("app_list_running");
        await Call("app_launch", new() { ["app_id"] = "synthetic-app-id" });
        await Call("app_launch", new() { ["app_id"] = app.Id });

        foreach (var secret in new[] { "synthetic", app.Path, @"C:\\Apps", app.Identity.Sha256, app.Identity.FileId })
        {
            Assert.DoesNotContain(responses, r => r.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        var events = f.Audit.Query(new AuditQuery());
        Assert.Contains(events, e => e.Arguments?.Contains(app.Id, StringComparison.Ordinal) == true);
        using var csv = new StringWriter();
        AuditLog.ExportCsv(events, csv);
        using var json = new MemoryStream();
        AuditLog.ExportJson(events, json);
        foreach (var text in new[] { csv.ToString(), Encoding.UTF8.GetString(json.ToArray()), JsonSerializer.Serialize(events) })
        {
            foreach (var secret in new[] { "synthetic", "Apps", app.Identity.Sha256, app.Identity.FileId, "private" })
            {
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The database legitimately holds the local registration (path, arguments); client input and OS exception text
        // must not be anywhere in the database or WAL files.
        var data = new List<byte[]>();
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
            foreach (var secret in new[] { "synthetic-app-id" })
            {
                Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0, secret);
                Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret)) < 0, secret);
            }
        }
    }
}
