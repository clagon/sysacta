using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Sysacta.Core.Auditing;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

/// <summary>The Phase 1B audio tools over real HTTP/MCP with SQLite permissions and audit log.</summary>
public class AudioPhase1BServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] NewTools = ["audio_list_devices", "audio_set_mute", "audio_list_sessions", "audio_set_app_volume", "audio_set_app_mute"];

    private static string PlayerId => AudioSessions.SessionId("spk", FakeAudioSessions.Player);

    private static JsonElement Structured(CallToolResult r) => Assert.IsType<JsonElement>(r.StructuredContent);

    private static Dictionary<string, object?> Args(string tool) => tool switch
    {
        "audio_set_mute" => new() { ["enabled"] = true },
        "audio_set_app_volume" => new() { ["session_id"] = PlayerId, ["level"] = 10 },
        "audio_set_app_mute" => new() { ["session_id"] = PlayerId, ["enabled"] = true },
        _ => [],
    };

    [Fact]
    public async Task New_tools_publish_strict_schemas_and_hints()
    {
        await using var f = await ServerFixture.StartAsync();
        await using var c = await f.ClientAsync(f.Issue(NewTools).Token);

        var tools = (await c.ListToolsAsync(cancellationToken: Ct)).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.Equal(NewTools.Order(), tools.Keys.Order());
        foreach (var tool in tools.Values)
        {
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.NotNull(tool.OutputSchema);
            Assert.Equal(tool.Name.StartsWith("audio_list", StringComparison.Ordinal), tool.Annotations!.ReadOnlyHint);
        }

        static string[] Required(Tool t) => t.InputSchema.TryGetProperty("required", out var r) ? [.. r.EnumerateArray().Select(x => x.GetString()!).Order()] : [];
        Assert.Empty(Required(tools["audio_list_devices"]));
        Assert.Equal(["enabled"], Required(tools["audio_set_mute"]));
        Assert.Equal(["level", "session_id"], Required(tools["audio_set_app_volume"]));
        Assert.Equal(["enabled", "session_id"], Required(tools["audio_set_app_mute"]));
        var direction = tools["audio_set_mute"].InputSchema.GetProperty("properties").GetProperty("direction");
        Assert.Equal(["output", "input"], direction.GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData("audio_list_devices")]
    [InlineData("audio_set_mute")]
    [InlineData("audio_list_sessions")]
    [InlineData("audio_set_app_volume")]
    [InlineData("audio_set_app_mute")]
    public async Task Direct_call_without_a_grant_is_denied_and_writes_nothing(string tool)
    {
        await using var f = await ServerFixture.StartAsync();
        await using var c = await f.ClientAsync(f.Issue("audio_get_state").Token);

        var result = await c.CallToolAsync(tool, Args(tool), cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Equal(ErrorCodes.PermissionDenied, Structured(result).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, f.AudioDevice.Writes);
        Assert.Empty(f.Sessions.Writes);
        Assert.Contains(f.Audit.Query(new AuditQuery(Outcome: AuditOutcome.Denied)), e => e.ToolName == tool);
    }

    [Fact]
    public async Task New_tools_work_end_to_end_and_audit_only_allowed_values()
    {
        await using var f = await ServerFixture.StartAsync();
        var issued = f.Issue(NewTools);
        await using var c = await f.ClientAsync(issued.Token);

        var devices = Structured(await c.CallToolAsync("audio_list_devices", new Dictionary<string, object?>(), cancellationToken: Ct));
        var sessions = Structured(await c.CallToolAsync("audio_list_sessions", new Dictionary<string, object?>(), cancellationToken: Ct));
        Assert.Equal(3, devices.GetProperty("data").GetProperty("devices").GetArrayLength());
        Assert.Equal(PlayerId, sessions.GetProperty("data").GetProperty("sessions")[0].GetProperty("session_id").GetString());

        foreach (var (tool, expected) in new[]
        {
            ("audio_set_mute", """{"enabled":true,"direction":"output","target":"spk"}"""),
            ("audio_set_app_volume", $$"""{"level":10,"target":"{{PlayerId}}"}"""),
            ("audio_set_app_mute", $$"""{"enabled":true,"target":"{{PlayerId}}"}"""),
        })
        {
            var result = await c.CallToolAsync(tool, Args(tool), cancellationToken: Ct);
            Assert.False(result.IsError);
            Assert.Equal("verified", Structured(result).GetProperty("verification").GetString());
            Assert.True(Structured(result).GetProperty("data").GetProperty("changed").GetBoolean());
            var e = Assert.Single(f.Audit.Query(new AuditQuery(ToolName: tool)));
            Assert.Equal(AuditOutcome.Success, e.Outcome);
            Assert.Equal(expected, e.Arguments);
        }
    }

    [Fact]
    public async Task Client_text_and_raw_session_identifiers_are_not_stored_or_returned()
    {
        const string Secret = "SYNTHETICSECRETaudio7";
        await using var f = await ServerFixture.StartAsync();
        f.Sessions.Devices["spk"][0] = f.Sessions.Devices["spk"][0] with { InstanceId = $@"spk|\Device\{Secret}\player.exe", Name = null };
        var id = AudioSessions.SessionId("spk", f.Sessions.Devices["spk"][0].InstanceId);
        await using var c = await f.ClientAsync(f.Issue(NewTools).Token);

        var listed = await c.CallToolAsync("audio_list_sessions", new Dictionary<string, object?>(), cancellationToken: Ct);
        var changed = await c.CallToolAsync("audio_set_app_volume", new Dictionary<string, object?> { ["session_id"] = id, ["level"] = 11 }, cancellationToken: Ct);
        foreach (var (tool, args) in new (string, Dictionary<string, object?>)[]
        {
            ("audio_set_app_volume", new() { ["session_id"] = Secret, ["level"] = 1 }),
            ("audio_set_app_mute", new() { ["session_id"] = "x" + Secret, ["enabled"] = true }),
            ("audio_set_mute", new() { ["device"] = Secret, ["enabled"] = true }),
            ("audio_list_sessions", new() { ["device"] = Secret }),
            ("audio_list_devices", new() { ["direction"] = Secret }),
            ("audio_set_mute", new() { ["enabled"] = Secret }),
        })
        {
            var r = await c.CallToolAsync(tool, args, cancellationToken: Ct);
            Assert.True(r.IsError);
            Assert.DoesNotContain(Secret, Structured(r).GetRawText(), StringComparison.Ordinal);
        }

        Assert.False(changed.IsError);
        Assert.DoesNotContain(Secret, Structured(listed).GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Structured(changed).GetRawText(), StringComparison.Ordinal);

        var events = f.Audit.Query(new AuditQuery());
        using var csv = new StringWriter();
        AuditLog.ExportCsv(events, csv);
        using var json = new MemoryStream();
        AuditLog.ExportJson(events, json);
        var contents = new List<byte[]> { Encoding.UTF8.GetBytes(csv.ToString()), json.ToArray() };
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(f.Database.Path)!))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            contents.Add(copy.ToArray());
        }

        foreach (var content in contents)
        {
            Assert.True(content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)) < 0);
            Assert.True(content.AsSpan().IndexOf(Encoding.Unicode.GetBytes(Secret)) < 0);
        }
    }

    [Theory]
    [InlineData("audio_set_mute")]
    [InlineData("audio_set_app_volume")]
    [InlineData("audio_set_app_mute")]
    public async Task A_full_audit_log_refuses_the_change_without_side_effect(string tool)
    {
        await using var f = await ServerFixture.StartAsync(limits: new AuditLimits(MaxRows: 2, ReservedRows: 1, LowPriorityRows: 1));
        await using var c = await f.ClientAsync(f.Issue(tool, f.Blocking.Name).Token);
        var a = c.CallToolAsync(f.Blocking.Name, new Dictionary<string, object?> { ["resource"] = "a" }, cancellationToken: Ct).AsTask();
        var b = c.CallToolAsync(f.Blocking.Name, new Dictionary<string, object?> { ["resource"] = "b" }, cancellationToken: Ct).AsTask();
        await Wait.Until(() => f.Blocking.Started == 2);

        var refused = await c.CallToolAsync(tool, Args(tool), cancellationToken: Ct);

        Assert.Equal(ErrorCodes.AuditUnavailable, Structured(refused).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, f.AudioDevice.Writes);
        Assert.Empty(f.Sessions.Writes);
        f.Blocking.Gates["a"].Release();
        f.Blocking.Gates["b"].Release();
        await Task.WhenAll(a, b);
    }

    [Fact]
    public async Task Upgrading_an_existing_database_adds_the_new_tools_disabled_and_grants_nothing()
    {
        await using var f = await ServerFixture.StartAsync();
        var audio = new FakeAudioEndpoints();
        var sessions = new FakeAudioSessions();
        var gate = new Core.Security.AuthorizationGate();
        var path = Path.Combine(Path.GetDirectoryName(f.Database.Path)!, "upgrade.db");
        var database = new SysactaDatabase(path);
        database.Migrate();
        var store = new AccessStore(database, TimeProvider.System, gate);
        SysactaTool[] phase0 = [new AudioGetStateTool(audio, store), new AudioSetVolumeTool(audio, store)];
        store.SyncTools(phase0.Select(t => t.Descriptor), "0.1.0");
        var existing = store.IssueClient("old", null, Core.Security.PermissionPresets.InitialGrants(Core.Security.PermissionPreset.AllEnabled, phase0, store.IsToolEnabled));

        SysactaTool[] all =
        [
            .. phase0,
            new AudioListDevicesTool(audio, store),
            new AudioSetMuteTool(audio, store),
            new AudioListSessionsTool(audio, store, sessions),
            new AudioSetAppVolumeTool(audio, sessions),
            new AudioSetAppMuteTool(audio, sessions),
        ];
        store.SyncTools(all.Select(t => t.Descriptor), "0.2.0");

        Assert.All(NewTools, t => Assert.False(store.IsToolEnabled(t)));
        Assert.Equal(["audio_get_state", "audio_set_volume"], store.GetClientGrants(existing.Client.Id).Order());
        // Enabling a tool globally still does not grant it to an existing client.
        store.SetToolEnabled("audio_set_mute", true);
        Assert.False(store.IsClientToolAllowed(existing.Client.Id, "audio_set_mute"));

        // A new installation enables all of them (spec §7: ON).
        var fresh = new SysactaDatabase(path + ".new");
        fresh.Migrate();
        var freshStore = new AccessStore(fresh, TimeProvider.System, gate);
        freshStore.SyncTools(all.Select(t => t.Descriptor), "0.2.0");
        Assert.All(NewTools, t => Assert.True(freshStore.IsToolEnabled(t)));
    }

    [Fact]
    public async Task Alias_listing_matches_the_local_catalog_and_resolution()
    {
        await using var f = await ServerFixture.StartAsync();
        f.Store.SetAlias("audio", "spk", "Desk", ["spk"]);
        await using var c = await f.ClientAsync(f.Issue("audio_list_devices", "audio_set_mute").Token);

        var listed = Structured(await c.CallToolAsync("audio_list_devices", new Dictionary<string, object?> { ["direction"] = "output" }, cancellationToken: Ct));
        var local = AudioDeviceCatalog.List(f.AudioDevice, f.Store, AudioDirection.Output, 512);
        var muted = await c.CallToolAsync("audio_set_mute", new Dictionary<string, object?> { ["device"] = "desk", ["enabled"] = true }, cancellationToken: Ct);

        Assert.Equal(
            JsonSerializer.Serialize(local, ToolJson.Options),
            listed.GetProperty("data").GetRawText());
        Assert.False(muted.IsError);
        Assert.Equal("spk", Assert.Single(f.AudioDevice.MuteCalls).Item1);
    }
}
