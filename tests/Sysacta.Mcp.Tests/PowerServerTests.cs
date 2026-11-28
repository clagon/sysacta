using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Power;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

/// <summary>Phase 1E over HTTP/MCP with a fake power adapter: no real power state is changed.</summary>
public class PowerServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Names = ["power_get_state", "power_list_plans", "power_set_plan", "power_lock", "power_sleep", "power_shutdown", "power_restart"];
    private static readonly string[] Changes = ["power_set_plan", "power_lock", "power_sleep", "power_shutdown", "power_restart"];

    private static Dictionary<string, object?> ArgsFor(string name) =>
        name == "power_set_plan" ? new() { ["plan_id"] = FakePower.Saver.ToString() } : [];

    private static async Task SetEnabled(ServerFixture f, params string[] names)
    {
        foreach (var name in names)
        {
            await Task.Run(() => f.Store.SetToolEnabled(name, true), Ct);
        }
    }

    [Fact]
    public async Task Schemas_are_strict_and_the_local_release_is_not_an_MCP_tool()
    {
        await using var f = await ServerFixture.StartAsync(includePower: true);
        await SetEnabled(f, Names);
        await using var client = await f.ClientAsync(f.Issue([.. Names, "power_release_gate"]).Token);

        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).ToDictionary(x => x.Name, x => x.ProtocolTool);

        Assert.Equal(Names.Order(), tools.Keys.Where(k => k.StartsWith("power_", StringComparison.Ordinal)).Order());
        foreach (var tool in tools.Where(t => t.Key.StartsWith("power_", StringComparison.Ordinal)).Select(t => t.Value))
        {
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.NotNull(tool.OutputSchema);
            Assert.Equal(tool.Name is "power_get_state" or "power_list_plans", tool.Annotations!.ReadOnlyHint);
        }

        Assert.Contains("not that it completed", tools["power_shutdown"].Description, StringComparison.Ordinal);
        await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("power_release_gate", new Dictionary<string, object?>(), cancellationToken: Ct).AsTask());
    }

    [Theory]
    [MemberData(nameof(ChangeNames))]
    public async Task Calls_without_grant_or_global_permission_do_not_reach_the_adapter(string name)
    {
        await using var f = await ServerFixture.StartAsync(includePower: true);
        await SetEnabled(f, Names);
        await using (var client = await f.ClientAsync(f.Issue("power_get_state").Token))
        {
            var result = await client.CallToolAsync(name, ArgsFor(name), cancellationToken: Ct);
            Assert.True(result.IsError);
            Assert.Equal(ErrorCodes.PermissionDenied, Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("error").GetProperty("code").GetString());
        }

        var token = f.Issue(name).Token;
        await Task.Run(() => f.Store.SetToolEnabled(name, false), Ct);
        await using (var client = await f.ClientAsync(token))
        {
            var result = await client.CallToolAsync(name, ArgsFor(name), cancellationToken: Ct);
            Assert.Equal(ErrorCodes.ToolDisabled, Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("error").GetProperty("code").GetString());
        }

        Assert.Equal(0, f.Power.Requests);
        Assert.Equal(0, f.Power.PlanWrites);
        Assert.Equal(PowerGatePhase.Idle, f.PowerGate.State.Phase);
    }

    [Fact]
    public async Task A_held_shutdown_refuses_every_category_until_the_local_console_releases_it()
    {
        await using var f = await ServerFixture.StartAsync(includePower: true);
        await SetEnabled(f, "power_shutdown", "audio_set_volume", "power_get_state");
        await using var client = await f.ClientAsync(f.Issue("power_shutdown", "audio_set_volume", "power_get_state").Token);

        var shutdown = await client.CallToolAsync("power_shutdown", new Dictionary<string, object?>(), cancellationToken: Ct);
        var body = Assert.IsType<JsonElement>(shutdown.StructuredContent);
        Assert.False(shutdown.IsError);
        Assert.Equal("accepted", body.GetProperty("verification").GetString());
        Assert.True(body.GetProperty("data").GetProperty("accepted").GetBoolean());

        var volume = await client.CallToolAsync("audio_set_volume", new Dictionary<string, object?> { ["level"] = 10 }, cancellationToken: Ct);
        Assert.True(volume.IsError);
        Assert.Equal(ErrorCodes.Busy, Assert.IsType<JsonElement>(volume.StructuredContent).GetProperty("error").GetProperty("code").GetString());
        Assert.False((await client.CallToolAsync("power_get_state", new Dictionary<string, object?>(), cancellationToken: Ct)).IsError);
        var local = await f.Executor.ExecuteAsync(Caller.LocalConsole, "display_restore_mode", null, Ct);
        Assert.Equal(ErrorCodes.Busy, local.Error!.Code);

        // A host restart does not reset the gate.
        await f.Host.StopAsync();
        await f.Host.StartAsync(new ServerEndpointOptions { Port = 0 }, Ct);
        Assert.Equal(PowerGatePhase.Held, f.PowerGate.State.Phase);

        Assert.True((await f.Executor.ExecuteAsync(Caller.LocalConsole, "power_release_gate", null, Ct)).IsSuccess);
        await using var again = await f.ClientAsync(f.Issue("audio_set_volume").Token);
        Assert.False((await again.CallToolAsync("audio_set_volume", new Dictionary<string, object?> { ["level"] = 10 }, cancellationToken: Ct)).IsError);

        var released = f.Audit.Query(new AuditQuery(ToolName: "power_release_gate"));
        Assert.Equal(AuditOutcome.Success, Assert.Single(released).Outcome);
        Assert.Equal("local_console", released[0].Source);
    }

    [Fact]
    public async Task The_audit_log_holds_only_the_resolved_plan_id_and_no_plan_names()
    {
        await using var f = await ServerFixture.StartAsync(includePower: true);
        f.Power.Plans[1] = new(FakePower.Saver, "secret plan name");
        await SetEnabled(f, "power_set_plan", "power_list_plans", "power_restart");
        await using var client = await f.ClientAsync(f.Issue("power_set_plan", "power_list_plans", "power_restart").Token);

        var list = await client.CallToolAsync("power_list_plans", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.Contains("secret plan name", Assert.IsType<JsonElement>(list.StructuredContent).GetRawText(), StringComparison.Ordinal);
        var set = await client.CallToolAsync("power_set_plan", ArgsFor("power_set_plan"), cancellationToken: Ct);
        Assert.False(set.IsError);
        Assert.False((await client.CallToolAsync("power_restart", new Dictionary<string, object?>(), cancellationToken: Ct)).IsError);

        var records = f.Audit.Query(new AuditQuery(ToolName: "power_set_plan"));
        Assert.Equal($$"""{"target":"{{FakePower.Saver}}"}""", Assert.Single(records).Arguments);
        Assert.Null(Assert.Single(f.Audit.Query(new AuditQuery(ToolName: "power_restart"))).Arguments);

        var events = f.Audit.Query(new AuditQuery());
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
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("secret plan name")) < 0);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("secret plan name")) < 0);
        }
    }

    [Fact]
    public async Task Intent_records_are_durable_and_an_interrupted_transition_is_recovered_as_unknown()
    {
        await using var f = await ServerFixture.StartAsync(includePower: true);
        using (var c = f.Database.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA synchronous;";
            Assert.Equal(2L, cmd.ExecuteScalar()); // FULL
        }

        await SetEnabled(f, "power_shutdown");
        await using var client = await f.ClientAsync(f.Issue("power_shutdown").Token);
        string? pendingSeenBy = null;
        // The OS stops before the final record: simulate by reading the committed state at the request.
        f.Power.DuringRequest = () =>
        {
            var reader = new AuditLog(new SysactaDatabase(f.Database.Path));
            pendingSeenBy = reader.Query(new AuditQuery(ToolName: "power_shutdown")).SingleOrDefault()?.Outcome.ToString();
        };
        Assert.False((await client.CallToolAsync("power_shutdown", new Dictionary<string, object?>(), cancellationToken: Ct)).IsError);
        Assert.Equal(nameof(AuditOutcome.Pending), pendingSeenBy);

        // A pending record left by a process that ended mid-transition becomes unknown at the next start.
        f.Audit.Write(new AuditEvent { RequestId = "left", Timestamp = DateTimeOffset.UtcNow, ToolName = "power_restart", Source = "mcp", Outcome = AuditOutcome.Pending });
        Assert.Equal(1, f.Audit.RecoverInterrupted());
        var left = f.Audit.Query(new AuditQuery(ToolName: "power_restart")).Single();
        Assert.Equal((AuditOutcome.Unknown, AuditLog.InterruptedCode), (left.Outcome, left.ErrorCode));
    }

    [Fact]
    public async Task New_install_enables_only_reads_and_lock_and_an_upgrade_adds_all_power_tools_disabled()
    {
        await using var f = await ServerFixture.StartAsync();
        var dir = Path.GetDirectoryName(f.Database.Path)!;
        var power = new FakePower();
        SysactaTool[] powerTools = [new PowerGetStateTool(power), new PowerListPlansTool(power), new PowerSetPlanTool(power),
            new PowerLockTool(power), new PowerSleepTool(power), new PowerShutdownTool(power), new PowerRestartTool(power)];

        var freshDb = new SysactaDatabase(Path.Combine(dir, "power-fresh.db"));
        freshDb.Migrate();
        var fresh = new AccessStore(freshDb, TimeProvider.System, new AuthorizationGate());
        fresh.SyncTools(powerTools.Select(t => t.Descriptor), "after");
        Assert.Equal(["power_get_state", "power_list_plans", "power_lock"], Names.Where(fresh.IsToolEnabled));

        var oldDb = new SysactaDatabase(Path.Combine(dir, "power-upgrade.db"));
        oldDb.Migrate();
        var store = new AccessStore(oldDb, TimeProvider.System, new AuthorizationGate());
        SysactaTool[] initial = [new Core.Tools.Audio.AudioGetStateTool(f.AudioDevice, store)];
        store.SyncTools(initial.Select(t => t.Descriptor), "before");
        var old = store.IssueClient("old", null, ["audio_get_state"]);
        store.SyncTools([.. initial.Select(t => t.Descriptor), .. powerTools.Select(t => t.Descriptor)], "after");
        Assert.All(Names, name =>
        {
            Assert.False(store.IsToolEnabled(name));
            Assert.False(store.IsClientToolAllowed(old.Client.Id, name));
        });
        Assert.Throws<InvalidOperationException>(() => fresh.SetToolEnabled("power_release_gate", true));
    }

    public static TheoryData<string> ChangeNames() => [.. Changes];
}
