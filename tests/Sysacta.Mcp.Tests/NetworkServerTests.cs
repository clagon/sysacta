using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using Sysacta.Core.Auditing;
using Sysacta.Core.Tools;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

/// <summary>Phase 2B over HTTP/MCP with a fake network adapter: no real adapter is read or changed.</summary>
public class NetworkServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Reads = ["network_get_status", "network_list_adapters", "network_get_traffic"];
    private static readonly string Spare = FakeNetwork.Id(FakeNetwork.SpareGuid);

    /// <summary>Every network tool the fixture registers.</summary>
    private static string[] Names => [.. ServerFixture.NetworkTools(new FakeNetwork(), null!).Select(t => t.Name)];

    private static JsonElement Structured(ModelContextProtocol.Protocol.CallToolResult r) => Assert.IsType<JsonElement>(r.StructuredContent);

    private static string? Code(ModelContextProtocol.Protocol.CallToolResult r) =>
        Structured(r).TryGetProperty("error", out var e) ? e.GetProperty("code").GetString() : null;

    [Fact]
    public async Task Reads_are_on_in_a_new_database_and_listed_read_only()
    {
        await using var f = await ServerFixture.StartAsync(includeNetwork: true);
        Assert.All(Reads, n => Assert.True(f.Store.IsToolEnabled(n), n));

        await using var client = await f.ClientAsync(f.Issue(Reads).Token);
        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Where(t => t.Name.StartsWith("network_", StringComparison.Ordinal)).ToList();
        Assert.Equal(Reads.Order(), tools.Select(t => t.Name).Order());
        Assert.All(tools, t =>
        {
            Assert.True(t.ProtocolTool.Annotations!.ReadOnlyHint);
            Assert.NotNull(t.ProtocolTool.OutputSchema);
        });
    }

    [Fact]
    public async Task Tools_added_to_an_existing_database_start_disabled()
    {
        var dir = Directory.CreateTempSubdirectory("sysacta-net-").FullName;
        try
        {
            var db = new SysactaDatabase(Path.Combine(dir, "sysacta.db"));
            db.Migrate();
            var store = new AccessStore(db, TimeProvider.System, new Core.Security.AuthorizationGate());
            store.SyncTools([new ToolDescriptor("audio_get_state", ToolCategories.Audio, "t", "d", DefaultPolicy.On, true, [])], "1.0");
            var issued = store.IssueClient("old", null, ["audio_get_state"]);
            store.SyncTools(ServerFixture.NetworkTools(new FakeNetwork(), store).Select(t => t.Descriptor), "2.0");
            Assert.All(Names, n => Assert.False(store.IsToolEnabled(n), n));
            Assert.All(Names, n => Assert.False(store.IsClientToolAllowed(issued.Client.Id, n), n));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Direct_calls_without_a_grant_are_refused_before_the_adapter()
    {
        await using var f = await ServerFixture.StartAsync(includeNetwork: true);
        await using (var client = await f.ClientAsync(f.Issue("audio_get_state").Token))
        {
            foreach (var name in Reads)
            {
                Assert.Equal(ErrorCodes.PermissionDenied, Code(await client.CallToolAsync(name, cancellationToken: Ct)));
            }
        }

        var token = f.Issue(Reads).Token;
        await Task.Run(() => f.Store.SetToolEnabled("network_get_traffic", false), Ct);
        await using (var client = await f.ClientAsync(token))
        {
            Assert.Equal(ErrorCodes.ToolDisabled, Code(await client.CallToolAsync("network_get_traffic", cancellationToken: Ct)));
            Assert.Equal(ErrorCodes.InvalidArgument, Code(await client.CallToolAsync("network_list_adapters", new Dictionary<string, object?> { ["interface_index"] = 12 }, cancellationToken: Ct)));
        }

        Assert.Equal(0, f.Network.InterfaceReads);
    }

    [Fact]
    public async Task Addresses_reach_the_authorized_client_but_the_audit_keeps_only_the_resolved_id()
    {
        await using var f = await ServerFixture.StartAsync(includeNetwork: true);
        await using var client = await f.ClientAsync(f.Issue(Reads).Token);
        var list = await client.CallToolAsync("network_list_adapters", cancellationToken: Ct);
        Assert.Contains("192.0.2.10", Structured(list).GetRawText(), StringComparison.Ordinal); // the read contract
        Assert.False((await client.CallToolAsync("network_get_status", cancellationToken: Ct)).IsError);
        Assert.False((await client.CallToolAsync("network_get_traffic", new Dictionary<string, object?> { ["adapter"] = Spare }, cancellationToken: Ct)).IsError);
        Assert.Equal(ErrorCodes.TargetNotFound, Code(await client.CallToolAsync("network_get_traffic", new Dictionary<string, object?> { ["adapter"] = "SYNTHETICSECRETtraffic3" }, cancellationToken: Ct)));

        var events = (await Task.Run(() => f.Audit.Query(new AuditQuery()), Ct)).Where(e => e.ToolName?.StartsWith("network_", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(4, events.Count);
        Assert.Contains(events, e => e.Arguments == $$"""{"target":"{{Spare}}"}""");
        var text = JsonSerializer.Serialize(events);
        foreach (var leaked in new[] { "192.0.2.10", "2001:db8", "Ethernet", "SYNTHETICSECRET", "internet_access" })
        {
            Assert.DoesNotContain(leaked, text, StringComparison.Ordinal);
        }
    }
}
