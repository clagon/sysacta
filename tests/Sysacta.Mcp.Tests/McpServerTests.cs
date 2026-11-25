using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Sysacta.Core.Auditing;
using Sysacta.Core.Tools;
using Sysacta.Infrastructure;

namespace Sysacta.Mcp.Tests;

public class AuthenticationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ListBody = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""";

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer not-a-token")]
    [InlineData("Bearer sya_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task Requests_without_valid_credentials_get_401_and_are_audited(string? authorization)
    {
        await using var f = await ServerFixture.StartAsync();
        using var http = f.Http();
        using var request = ServerFixture.JsonRpc(ListBody);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
        var e = Assert.Single(f.Audit.Query(new AuditQuery()));
        Assert.Equal(AuditOutcome.AuthFailed, e.Outcome);
        Assert.Null(e.ClientId);
        Assert.Equal("127.0.0.1", e.RemoteAddress);
        Assert.DoesNotContain("AAAA", JsonSerializer.Serialize(e), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoked_expired_and_replaced_tokens_are_rejected_immediately()
    {
        await using var f = await ServerFixture.StartAsync();
        var a = f.Issue("audio_get_state");
        var b = f.Issue("audio_get_state");
        var c = f.Issue("audio_get_state");

        await using (var client = await f.ClientAsync(a.Token))
        {
            Assert.Single(await client.ListToolsAsync(cancellationToken: Ct));
        }

        f.Store.RevokeClient(a.Client.Id);
        f.Store.SetClientExpiry(b.Client.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
        var c2 = f.Store.ReissueClient(c.Client.Id);

        foreach (var token in new[] { a.Token, b.Token, c.Token })
        {
            using var http = f.Http(token);
            using var response = await http.SendAsync(ServerFixture.JsonRpc(ListBody), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await using var ok = await f.ClientAsync(c2.Token);
        Assert.Single(await ok.ListToolsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Tokens_and_headers_never_reach_the_database()
    {
        await using var f = await ServerFixture.StartAsync();
        var issued = f.Issue("audio_get_state", "audio_set_volume");
        await using (var client = await f.ClientAsync(issued.Token))
        {
            await client.CallToolAsync("audio_set_volume", new Dictionary<string, object?> { ["level"] = 20 }, cancellationToken: Ct);
        }

        using (var http = f.Http(issued.Token[..^2] + "xx"))
        {
            await http.SendAsync(ServerFixture.JsonRpc(ListBody), Ct);
        }

        SqliteConnection.ClearAllPools();
        var secret = Encoding.UTF8.GetBytes(issued.Token[4..20]);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(f.Database.Path)!))
        {
            Assert.Equal(-1, File.ReadAllBytes(file).AsSpan().IndexOf(secret));
        }

        Assert.DoesNotContain(f.Audit.Query(new AuditQuery()), e => (e.Arguments ?? "").Contains("Bearer", StringComparison.OrdinalIgnoreCase));
    }
}

public class AuthorizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Structured(CallToolResult r) => Assert.IsType<JsonElement>(r.StructuredContent);

    [Fact]
    public async Task Tools_list_is_filtered_per_client()
    {
        await using var f = await ServerFixture.StartAsync();
        var reader = f.Issue("audio_get_state");
        var writer = f.Issue("audio_get_state", "audio_set_volume");
        var none = f.Issue();

        await using var c1 = await f.ClientAsync(reader.Token);
        await using var c2 = await f.ClientAsync(writer.Token);
        await using var c3 = await f.ClientAsync(none.Token);

        Assert.Equal(["audio_get_state"], (await c1.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name));
        Assert.Equal(["audio_get_state", "audio_set_volume"], (await c2.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Order());
        Assert.Empty(await c3.ListToolsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Listed_tools_carry_strict_schemas_and_hints()
    {
        await using var f = await ServerFixture.StartAsync();
        await using var c = await f.ClientAsync(f.Issue("audio_get_state", "audio_set_volume").Token);

        var set = (await c.ListToolsAsync(cancellationToken: Ct)).Single(t => t.Name == "audio_set_volume").ProtocolTool;

        Assert.False(set.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(100, set.InputSchema.GetProperty("properties").GetProperty("level").GetProperty("maximum").GetInt32());
        Assert.NotNull(set.OutputSchema);
        Assert.False(set.Annotations!.ReadOnlyHint);
    }

    [Fact]
    public async Task Direct_call_to_tool_not_granted_is_denied()
    {
        await using var f = await ServerFixture.StartAsync();
        await using var c = await f.ClientAsync(f.Issue("audio_get_state").Token);

        var result = await c.CallToolAsync("audio_set_volume", new Dictionary<string, object?> { ["level"] = 1 }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Equal(ErrorCodes.PermissionDenied, Structured(result).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0.4f, f.AudioDevice.GetEndpoint("spk").VolumeScalar);
        Assert.Contains(f.Audit.Query(new AuditQuery(Outcome: AuditOutcome.Denied)), e => e.ToolName == "audio_set_volume");
    }

    [Fact]
    public async Task Globally_disabled_tool_is_hidden_and_denied_even_if_granted()
    {
        await using var f = await ServerFixture.StartAsync();
        var issued = f.Issue("audio_get_state", "audio_set_volume");
        f.Store.SetToolEnabled("audio_set_volume", false);
        await using var c = await f.ClientAsync(issued.Token);

        Assert.DoesNotContain(await c.ListToolsAsync(cancellationToken: Ct), t => t.Name == "audio_set_volume");
        var result = await c.CallToolAsync("audio_set_volume", new Dictionary<string, object?> { ["level"] = 1 }, cancellationToken: Ct);
        Assert.Equal(ErrorCodes.ToolDisabled, Structured(result).GetProperty("error").GetProperty("code").GetString());

        // Re-enabling applies immediately, without reconnecting.
        f.Store.SetToolEnabled("audio_set_volume", true);
        Assert.Contains(await c.ListToolsAsync(cancellationToken: Ct), t => t.Name == "audio_set_volume");
    }

    [Fact]
    public async Task Test_only_tool_is_denied_without_client_grant()
    {
        await using var f = await ServerFixture.StartAsync();
        await using var c = await f.ClientAsync(f.Issue("audio_get_state").Token);

        var result = await c.CallToolAsync("system_set_block", new Dictionary<string, object?> { ["resource"] = "x" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Equal(0, f.Blocking.Started);
    }

    [Fact]
    public async Task Unknown_tool_is_a_protocol_error()
    {
        await using var f = await ServerFixture.StartAsync();
        await using var c = await f.ClientAsync(f.Issue("audio_get_state").Token);

        await Assert.ThrowsAsync<McpProtocolException>(() => c.CallToolAsync("shell_execute", new Dictionary<string, object?>(), cancellationToken: Ct).AsTask());
    }

    [Fact]
    public async Task Invalid_and_unknown_arguments_are_rejected()
    {
        await using var f = await ServerFixture.StartAsync();
        await using var c = await f.ClientAsync(f.Issue("audio_set_volume").Token);

        foreach (var args in new Dictionary<string, object?>[] { new() { ["level"] = 101 }, new() { ["level"] = "50" }, new() { ["level"] = 5, ["force"] = true }, new() })
        {
            var result = await c.CallToolAsync("audio_set_volume", args, cancellationToken: Ct);
            Assert.True(result.IsError);
            Assert.Equal(ErrorCodes.InvalidArgument, Structured(result).GetProperty("error").GetProperty("code").GetString());
        }

        Assert.Equal(0.4f, f.AudioDevice.GetEndpoint("spk").VolumeScalar);
    }

    [Fact]
    public async Task Reference_tools_work_end_to_end_and_are_audited()
    {
        await using var f = await ServerFixture.StartAsync();
        var issued = f.Issue("audio_get_state", "audio_set_volume");
        await using var c = await f.ClientAsync(issued.Token);

        var set = await c.CallToolAsync("audio_set_volume", new Dictionary<string, object?> { ["level"] = 25 }, cancellationToken: Ct);
        var get = await c.CallToolAsync("audio_get_state", new Dictionary<string, object?>(), cancellationToken: Ct);

        Assert.False(set.IsError);
        var s = Structured(set);
        Assert.Equal("success", s.GetProperty("status").GetString());
        Assert.Equal("verified", s.GetProperty("verification").GetString());
        Assert.Equal(40, s.GetProperty("data").GetProperty("previous_volume").GetInt32());
        Assert.Equal(25, s.GetProperty("data").GetProperty("current_volume").GetInt32());
        Assert.Equal(25, Structured(get).GetProperty("data").GetProperty("volume").GetInt32());
        Assert.Contains(set.Content, b => b is TextContentBlock t && t.Text.Contains("\"current_volume\":25", StringComparison.Ordinal));

        var audit = f.Audit.Query(new AuditQuery(ClientId: issued.Client.Id));
        var e = Assert.Single(audit, a => a.ToolName == "audio_set_volume");
        Assert.Equal(AuditOutcome.Success, e.Outcome);
        Assert.Equal("""{"level":25,"device":null,"direction":"output"}""", e.Arguments);
        Assert.Equal(s.GetProperty("request_id").GetString(), e.RequestId);
        Assert.Equal("mcp", e.Source);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("reissue")]
    public async Task Revocation_or_reissue_while_queued_is_enforced_before_the_side_effect(string change)
    {
        await using var f = await ServerFixture.StartAsync();
        var holder = f.Issue(f.Blocking.Name);
        var victim = f.Issue(f.Blocking.Name);
        await using var c1 = await f.ClientAsync(holder.Token);
        await using var c2 = await f.ClientAsync(victim.Token);
        var args = new Dictionary<string, object?> { ["resource"] = "r" };

        var first = c1.CallToolAsync(f.Blocking.Name, args, cancellationToken: Ct).AsTask();
        await Wait.Until(() => f.Blocking.Started == 1);
        var queued = c2.CallToolAsync(f.Blocking.Name, args, cancellationToken: Ct).AsTask();
        await Wait.Until(() => f.Executor.InFlight == 2);

        if (change == "revoke")
        {
            f.Store.RevokeClient(victim.Client.Id);
        }
        else
        {
            f.Store.ReissueClient(victim.Client.Id);
        }

        f.Blocking.Gates["r"].Release();

        Assert.False((await first).IsError);
        var result = await queued;
        Assert.True(result.IsError);
        Assert.Equal(ErrorCodes.PermissionDenied, Structured(result).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, f.Blocking.Started);
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData(null)] // SDK default (latest)
    public async Task Supported_protocol_versions_work(string? version)
    {
        await using var f = await ServerFixture.StartAsync();
        await using var c = await f.ClientAsync(f.Issue("audio_get_state").Token, version);

        var result = await c.CallToolAsync("audio_get_state", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.False(result.IsError);
    }
}

public class HttpBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ListBody = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""";

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:8765")]
    [InlineData("192.168.1.50")]
    public async Task Foreign_host_header_is_rejected(string host)
    {
        await using var f = await ServerFixture.StartAsync();
        using var http = f.Http(f.Issue("audio_get_state").Token);
        using var request = ServerFixture.JsonRpc(ListBody);
        request.Headers.Host = host;

        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("pc.tailnet.ts.net")]
    public async Task Allowed_host_names_pass(string host)
    {
        await using var f = await ServerFixture.StartAsync(new ServerEndpointOptions { Port = 0, AllowedHosts = ["pc.tailnet.ts.net"] });
        using var http = f.Http(f.Issue("audio_get_state").Token);
        using var request = ServerFixture.JsonRpc(ListBody);
        request.Headers.Host = host;

        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("null", HttpStatusCode.Forbidden)]
    [InlineData("https://app.example", HttpStatusCode.OK)]
    public async Task Origin_is_validated_when_present(string origin, HttpStatusCode expected)
    {
        await using var f = await ServerFixture.StartAsync(new ServerEndpointOptions { Port = 0, AllowedOrigins = ["https://app.example"] });
        using var http = f.Http(f.Issue("audio_get_state").Token);
        using var request = ServerFixture.JsonRpc(ListBody);
        request.Headers.Add("Origin", origin);

        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(expected, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Oversized_body_is_rejected()
    {
        await using var f = await ServerFixture.StartAsync();
        using var http = f.Http(f.Issue("audio_get_state").Token);
        var padding = new string('a', 300 * 1024);
        using var request = ServerFixture.JsonRpc("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"pad":"PAD"}}}""".Replace("PAD", padding, StringComparison.Ordinal));

        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Per_client_rate_limit_returns_429()
    {
        await using var f = await ServerFixture.StartAsync(new ServerEndpointOptions { Port = 0, RequestsPerMinutePerClient = 3 });
        using var limited = f.Http(f.Issue("audio_get_state").Token);
        using var other = f.Http(f.Issue("audio_get_state").Token);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var r = await limited.SendAsync(ServerFixture.JsonRpc(ListBody), Ct);
            codes.Add(r.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], codes);
        using var o = await other.SendAsync(ServerFixture.JsonRpc(ListBody), Ct);
        Assert.Equal(HttpStatusCode.OK, o.StatusCode); // limits are per client
    }

    [Fact]
    public async Task Authentication_failure_flood_is_throttled_and_audit_is_capped()
    {
        await using var f = await ServerFixture.StartAsync(new ServerEndpointOptions { Port = 0, AuthFailuresPerMinutePerAddress = 5 });
        using var http = f.Http("sya_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            using var r = await http.SendAsync(ServerFixture.JsonRpc(ListBody), Ct);
            codes.Add(r.StatusCode);
        }

        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        Assert.Equal(5, f.Audit.Query(new AuditQuery(Outcome: AuditOutcome.AuthFailed)).Count);
    }

    [Theory]
    [InlineData("GET", "/sse")]
    [InlineData("POST", "/message")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/health")]
    public async Task No_other_endpoints_are_exposed(string method, string path)
    {
        await using var f = await ServerFixture.StartAsync();
        using var http = f.Http(f.Issue("audio_get_state").Token);
        using var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Raw_legacy_client_can_initialize_and_list()
    {
        await using var f = await ServerFixture.StartAsync();
        using var http = f.Http(f.Issue("audio_get_state").Token);

        using var init = await http.SendAsync(ServerFixture.JsonRpc("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}"""), Ct);
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.Contains("2025-06-18", await init.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var list = await http.SendAsync(ServerFixture.JsonRpc(ListBody), Ct);
        Assert.Contains("audio_get_state", await list.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }
}

public class LifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stop_releases_the_port_and_restart_works()
    {
        await using var f = await ServerFixture.StartAsync();
        var port = f.Host.BoundPort!.Value;
        Assert.Equal(ServerState.Running, f.Host.State);
        Assert.False(Wait.IsPortFree(port));

        await f.Host.StopAsync();
        Assert.Equal(ServerState.Stopped, f.Host.State);
        Assert.True(Wait.IsPortFree(port));

        await f.Host.StartAsync(new ServerEndpointOptions { Port = port }, Ct);
        await using var c = await f.ClientAsync(f.Issue("audio_get_state").Token);
        Assert.Single(await c.ListToolsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Failed_apply_restores_previous_settings()
    {
        await using var f = await ServerFixture.StartAsync();
        var port = f.Host.BoundPort!.Value;
        using var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var error = await f.Host.ApplyAsync(new ServerEndpointOptions { Port = busyPort });

        Assert.NotNull(error);
        Assert.Contains("restored", error, StringComparison.Ordinal);
        Assert.Equal(ServerState.Running, f.Host.State);
        Assert.Equal(port, f.Host.BoundPort);
    }

    [Fact]
    public async Task Start_failure_without_previous_settings_is_reported()
    {
        await using var f = await ServerFixture.StartAsync();
        await f.Host.StopAsync();
        using var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        blocker.Start();

        await Assert.ThrowsAnyAsync<Exception>(() => f.Host.StartAsync(new ServerEndpointOptions { Port = ((IPEndPoint)blocker.LocalEndpoint).Port }, Ct));
        Assert.Equal(ServerState.Faulted, f.Host.State);
        Assert.NotNull(f.Host.LastError);
    }

    [Fact]
    public async Task Stopping_the_host_does_not_release_locks_of_running_native_calls()
    {
        await using var f = await ServerFixture.StartAsync();
        var issued = f.Issue(f.Blocking.Name);
        var c = await f.ClientAsync(issued.Token);
        _ = c.CallToolAsync(f.Blocking.Name, new Dictionary<string, object?> { ["resource"] = "r" }, cancellationToken: Ct).AsTask();
        await Wait.Until(() => f.Blocking.Started == 1);

        await f.Host.StopAsync();
        Assert.Equal(1, f.Executor.InFlight); // still held by the native call

        f.Blocking.Gates["r"].Release();
        await Wait.Until(() => f.Executor.InFlight == 0);
        await c.DisposeAsync();
    }
}
