using System.Text.Json;
using Sysacta.Core.Auditing;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.SystemMonitoring;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

/// <summary>Phase 1F over HTTP/MCP with a fake monitoring adapter: the test machine is never read.</summary>
public class SystemServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Names = ["system_get_status", "system_get_hardware", "system_get_temperatures", "system_get_storage"];

    private static string Code(ModelContextProtocol.Protocol.CallToolResult r) =>
        Assert.IsType<JsonElement>(r.StructuredContent).GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task Tools_are_listed_read_only_with_strict_empty_schemas_and_on_in_a_new_database()
    {
        await using var f = await ServerFixture.StartAsync(includeSystem: true);
        foreach (var name in Names)
        {
            Assert.True(f.Store.IsToolEnabled(name), name);
        }

        await using var client = await f.ClientAsync(f.Issue(Names).Token);
        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Where(t => t.Name.StartsWith("system_", StringComparison.Ordinal)).ToList();
        Assert.Equal(Names.Order(), tools.Select(t => t.Name).Order());
        foreach (var tool in tools.Select(t => t.ProtocolTool))
        {
            Assert.True(tool.Annotations!.ReadOnlyHint);
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.False(tool.InputSchema.TryGetProperty("properties", out var p) && p.EnumerateObject().Any());
            Assert.NotNull(tool.OutputSchema);
        }
    }

    [Fact]
    public async Task Calls_without_grant_or_global_permission_or_with_arguments_never_reach_the_adapter()
    {
        await using var f = await ServerFixture.StartAsync(includeSystem: true);
        await using (var client = await f.ClientAsync(f.Issue("audio_get_state").Token))
        {
            foreach (var name in Names)
            {
                Assert.Equal(ErrorCodes.PermissionDenied, Code(await client.CallToolAsync(name, cancellationToken: Ct)));
            }

            Assert.DoesNotContain((await client.ListToolsAsync(cancellationToken: Ct)), t => t.Name.StartsWith("system_", StringComparison.Ordinal));
        }

        var token = f.Issue(Names).Token;
        foreach (var name in Names)
        {
            await Task.Run(() => f.Store.SetToolEnabled(name, false), Ct);
        }

        await using (var client = await f.ClientAsync(token))
        {
            foreach (var name in Names)
            {
                Assert.Equal(ErrorCodes.ToolDisabled, Code(await client.CallToolAsync(name, cancellationToken: Ct)));
            }
        }

        foreach (var name in Names)
        {
            await Task.Run(() => f.Store.SetToolEnabled(name, true), Ct);
        }

        await using (var client = await f.ClientAsync(token))
        {
            var withArgs = await client.CallToolAsync("system_get_storage", new Dictionary<string, object?> { ["path"] = @"\\server\share" }, cancellationToken: Ct);
            Assert.Equal(ErrorCodes.InvalidArgument, Code(withArgs));
        }

        Assert.Equal(0, f.Monitor.Calls);
    }

    [Fact]
    public async Task Results_reach_the_client_but_the_audit_log_keeps_no_result_data()
    {
        await using var f = await ServerFixture.StartAsync(includeSystem: true);
        f.Monitor.Hardware = () => FakeSystemMonitor.DefaultHardware() with { MachineModel = "SECRET-MODEL", OsCaption = @"C:\Users\alice\caption" };
        await using var client = await f.ClientAsync(f.Issue(Names).Token);
        var texts = new List<string>();
        foreach (var name in Names)
        {
            var result = await client.CallToolAsync(name, cancellationToken: Ct);
            Assert.False(result.IsError, name);
            var json = Assert.IsType<JsonElement>(result.StructuredContent);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("not_applicable", json.GetProperty("verification").GetString());
            Assert.True(json.GetProperty("data").TryGetProperty("measured_at", out _));
            texts.Add(json.GetRawText());
        }

        var all = string.Join("\n", texts);
        Assert.Contains("SECRET-MODEL", all, StringComparison.Ordinal);   // returned to the authorized client
        Assert.DoesNotContain("alice", all, StringComparison.Ordinal);      // a path never is
        Assert.Contains("luid_0x00000000_0x0000c5a3", all, StringComparison.Ordinal);

        var events = await Task.Run(() => f.Audit.Query(new AuditQuery()), Ct);
        var system = events.Where(e => e.ToolName?.StartsWith("system_", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(4, system.Count);
        Assert.All(system, e =>
        {
            Assert.Equal(AuditOutcome.Success, e.Outcome);
            Assert.Null(e.Arguments);
        });
        var auditText = JsonSerializer.Serialize(events);
        foreach (var leaked in new[] { "SECRET", "RTX", "luid", "drive:", "alice", "Contoso" })
        {
            Assert.DoesNotContain(leaked, auditText, StringComparison.Ordinal);
        }
    }
}
