using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using Sysacta.Core.Auditing;
using Sysacta.Core.Tools;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

/// <summary>network_set_adapter_enabled over HTTP/MCP with a fake adapter: nothing is enabled or disabled.</summary>
public class NetworkSetServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Reads = ["network_get_status", "network_list_adapters", "network_get_traffic"];
    private const string Set = "network_set_adapter_enabled";
    private static readonly string Spare = FakeNetwork.Id(FakeNetwork.SpareGuid), Wired = FakeNetwork.Id(FakeNetwork.WiredGuid);

    private static JsonElement Structured(ModelContextProtocol.Protocol.CallToolResult r) => Assert.IsType<JsonElement>(r.StructuredContent);

    private static string? Code(ModelContextProtocol.Protocol.CallToolResult r) =>
        Structured(r).TryGetProperty("error", out var e) ? e.GetProperty("code").GetString() : null;

    private static Dictionary<string, object?> Args(string adapter, bool enabled) => new() { ["adapter"] = adapter, ["enabled"] = enabled };

    [Fact]
    public async Task The_change_is_off_in_a_new_database_hidden_until_enabled_and_refused_without_a_grant()
    {
        await using var f = await ServerFixture.StartAsync(includeNetwork: true);
        Assert.False(f.Store.IsToolEnabled(Set));
        await using (var client = await f.ClientAsync(f.Issue([.. Reads, Set]).Token))
        {
            Assert.DoesNotContain(await client.ListToolsAsync(cancellationToken: Ct), t => t.Name == Set);
            Assert.Equal(ErrorCodes.ToolDisabled, Code(await client.CallToolAsync(Set, Args(Spare, false), cancellationToken: Ct)));
        }

        await Task.Run(() => f.Store.SetToolEnabled(Set, true), Ct);
        await using (var client = await f.ClientAsync(f.Issue(Reads).Token))
        {
            Assert.Equal(ErrorCodes.PermissionDenied, Code(await client.CallToolAsync(Set, Args(Spare, false), cancellationToken: Ct)));
        }

        f.Network.IsElevated = false;
        await using (var client = await f.ClientAsync(f.Issue([.. Reads, Set]).Token))
        {
            Assert.DoesNotContain(await client.ListToolsAsync(cancellationToken: Ct), t => t.Name == Set);
            Assert.Equal(ErrorCodes.AdminRequired, Code(await client.CallToolAsync(Set, Args(Spare, false), cancellationToken: Ct)));
        }

        Assert.Empty(f.Network.Invocations);
    }

    [Fact]
    public async Task Forged_forwarding_headers_do_not_unlock_the_active_adapter_and_the_audit_keeps_only_id_and_boolean()
    {
        const string Secret = "SYNTHETICSECRETnetadapter7";
        await using var f = await ServerFixture.StartAsync(includeNetwork: true);
        await Task.Run(() => f.Store.SetToolEnabled(Set, true), Ct);
        var token = f.Issue([.. Reads, Set]).Token;
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = f.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + token,
                ["X-Forwarded-For"] = "203.0.113.250",
                ["Forwarded"] = "for=203.0.113.250;proto=https",
                ["X-Real-IP"] = "203.0.113.250",
            },
        }), cancellationToken: Ct);

        var refused = await client.CallToolAsync(Set, Args(Wired, false), cancellationToken: Ct);
        Assert.True(refused.IsError);
        Assert.Equal(ErrorCodes.TargetProtected, Code(refused));
        Assert.Equal("active_adapter", Structured(refused).GetProperty("data").GetProperty("blocked_reason").GetString());
        Assert.Equal(ErrorCodes.TargetNotFound, Code(await client.CallToolAsync(Set, Args(Secret, false), cancellationToken: Ct)));

        var done = await client.CallToolAsync(Set, Args(Spare, false), cancellationToken: Ct);
        Assert.False(done.IsError);
        Assert.Equal("verified", Structured(done).GetProperty("verification").GetString());
        Assert.Single(f.Network.Invocations);

        // Addresses are the read tools' explicit contract, returned to the authorized client only.
        var list = await client.CallToolAsync("network_list_adapters", cancellationToken: Ct);
        Assert.Contains("192.0.2.10", Structured(list).GetRawText(), StringComparison.Ordinal);
        await client.CallToolAsync("network_get_traffic", new Dictionary<string, object?> { ["adapter"] = Spare }, cancellationToken: Ct);
        await client.CallToolAsync("network_get_status", cancellationToken: Ct);

        var events = await Task.Run(() => f.Audit.Query(new AuditQuery()), Ct);
        var network = events.Where(e => e.ToolName?.StartsWith("network_", StringComparison.Ordinal) == true).ToList();
        var sets = network.Where(e => e.ToolName == Set && e.Outcome != AuditOutcome.Pending).OrderBy(e => e.Timestamp).ToList();
        Assert.Equal($$"""{"enabled":false,"target":"{{Wired}}"}""", sets[0].Arguments);
        Assert.Equal("""{"enabled":false}""", sets[1].Arguments); // unresolved input is never recorded
        Assert.Equal($$"""{"enabled":false,"target":"{{Spare}}"}""", sets[2].Arguments);
        Assert.All(network, e => Assert.NotEqual("203.0.113.250", e.RemoteAddress));

        using var csv = new StringWriter();
        AuditLog.ExportCsv(events, csv);
        using var json = new MemoryStream();
        AuditLog.ExportJson(events, json);
        var contents = new List<byte[]> { Encoding.UTF8.GetBytes(csv.ToString()), json.ToArray() };
        using var liveDatabase = f.Database.Open(); // Keep SQLite from removing WAL during inspection.
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(f.Database.Path)!))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            contents.Add(copy.ToArray());
        }

        foreach (var leaked in new[] { Secret, "192.0.2.10", "2001:db8::10", "203.0.113.250", "Ethernet 2", "Tailscale", "active_adapter" })
        {
            foreach (var content in contents)
            {
                Assert.True(content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(leaked)) < 0, leaked);
                Assert.True(content.AsSpan().IndexOf(Encoding.Unicode.GetBytes(leaked)) < 0, leaked);
            }
        }
    }
}
