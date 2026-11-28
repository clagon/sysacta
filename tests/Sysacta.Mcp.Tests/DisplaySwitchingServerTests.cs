using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

/// <summary>Phase 1C-2: display_set_mode / display_set_power / display_set_input and the local-only restore.</summary>
public class DisplaySwitchingServerTests
{
    private static readonly string[] Tools = ["display_set_input", "display_set_mode", "display_set_power"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Structured(CallToolResult r) => Assert.IsType<JsonElement>(r.StructuredContent);

    private static string ModeId(DisplayMode m) => DisplayListModesTool.ModeId("display-b", m);

    [Fact]
    public async Task They_are_off_on_a_new_database_and_never_enabled_or_granted_on_upgrade()
    {
        await using var f = await ServerFixture.StartAsync();
        Assert.All(Tools, t => Assert.False(f.Store.IsToolEnabled(t)));
        Assert.All(Tools, t => Assert.Equal(DefaultPolicy.Off, f.Executor.Registry.Find(t)!.Descriptor.DefaultPolicy));

        var db = new SysactaDatabase(Path.Combine(Path.GetDirectoryName(f.Database.Path)!, "switching-upgrade.db"));
        db.Migrate();
        var store = new AccessStore(db, TimeProvider.System, new AuthorizationGate());
        var displays = new FakeDisplays();
        SysactaTool[] before = [new DisplayListDevicesTool(displays, store), new DisplayGetStateTool(displays, store)];
        store.SyncTools(before.Select(t => t.Descriptor), "0.4.0");
        var existing = store.IssueClient("c1", null, PermissionPresets.InitialGrants(PermissionPreset.AllEnabled, before, store.IsToolEnabled));
        var recovery = new DisplayModeRecovery();
        var registry = new ToolRegistry(
        [
            .. before,
            new DisplaySetModeTool(displays, store, recovery),
            new DisplaySetPowerTool(displays, store),
            new DisplaySetInputTool(displays, store),
            new DisplayRestoreModeTool(displays, recovery),
        ]);
        store.SyncTools(registry.All.Select(t => t.Descriptor), "0.5.0");

        foreach (var tool in Tools)
        {
            Assert.False(store.IsToolEnabled(tool));
            store.SetToolEnabled(tool, true);
            Assert.False(store.IsClientToolAllowed(existing.Client.Id, tool));
        }

        // The local-only restore is not a stored tool at all.
        Assert.Throws<InvalidOperationException>(() => store.SetToolEnabled("display_restore_mode", true));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Ungranted_and_disabled_calls_are_refused_without_opening_a_display()
    {
        await using var f = await ServerFixture.StartAsync();
        f.Displays.OnOpen = _ => throw new InvalidOperationException("must not be reached");
        await using var ungranted = await f.ClientAsync(f.Issue("display_get_state").Token);

        foreach (var (tool, args) in Calls())
        {
            var r = await ungranted.CallToolAsync(tool, args, cancellationToken: Ct);
            Assert.True(r.IsError);
            Assert.Equal(ErrorCodes.ToolDisabled, Structured(r).GetProperty("error").GetProperty("code").GetString());
        }

        foreach (var tool in Tools)
        {
            f.Store.SetToolEnabled(tool, true);
        }

        foreach (var (tool, args) in Calls())
        {
            var r = await ungranted.CallToolAsync(tool, args, cancellationToken: Ct);
            Assert.Equal(ErrorCodes.PermissionDenied, Structured(r).GetProperty("error").GetProperty("code").GetString());
        }

        Assert.Empty(f.Displays.ModeWrites);
        Assert.Empty(f.Displays.SwitchWrites);
    }

    [Fact]
    public async Task The_restore_tool_is_neither_listed_nor_callable_over_mcp()
    {
        await using var f = await ServerFixture.StartAsync();
        foreach (var tool in Tools)
        {
            f.Store.SetToolEnabled(tool, true);
        }

        await using var c = await f.ClientAsync(f.Issue([.. Tools, "display_restore_mode"]).Token);
        var changed = await c.CallToolAsync("display_set_mode", new Dictionary<string, object?> { ["device"] = "display-b", ["mode_id"] = ModeId(FakeDisplays.Mode720p60) }, cancellationToken: Ct);
        Assert.False(changed.IsError);
        Assert.NotNull(f.Recovery.Latest);

        Assert.DoesNotContain(await c.ListToolsAsync(cancellationToken: Ct), t => t.Name == "display_restore_mode");
        await Assert.ThrowsAsync<McpProtocolException>(() => c.CallToolAsync("display_restore_mode", new Dictionary<string, object?>(), cancellationToken: Ct).AsTask());
        Assert.Single(f.Displays.ModeWrites);
        Assert.NotNull(f.Recovery.Latest);

        // The local console can.
        var restored = await f.Executor.ExecuteAsync(Caller.LocalConsole, "display_restore_mode", null, Ct);
        Assert.True(restored.IsSuccess);
        Assert.Equal(2, f.Displays.ModeWrites.Count);
    }

    [Fact]
    public async Task Results_match_the_published_schemas_and_isError()
    {
        await using var f = await ServerFixture.StartAsync();
        foreach (var tool in Tools)
        {
            f.Store.SetToolEnabled(tool, true);
        }

        f.Displays.Add("display-c", "Third");
        f.Displays.Switches["display-b"].ReadFailureAfterWrite = new ToolFailureException(ErrorCodes.OsApiError, "fixed");
        f.Displays.Switches["display-c"].ReadFailureAfterWrite = new ToolFailureException(ErrorCodes.OsApiError, "fixed");
        await using var c = await f.ClientAsync(f.Issue([.. Tools, "display_get_state"]).Token);
        var listed = (await c.ListToolsAsync(cancellationToken: Ct)).ToDictionary(t => t.Name);

        foreach (var (name, args, status, verification) in new (string, Dictionary<string, object?>, string, string)[]
        {
            ("display_get_state", new() { ["device"] = "display-b", ["include_switching"] = true }, "success", "not_applicable"),
            ("display_get_state", new() { ["device"] = "display-b" }, "success", "not_applicable"),
            ("display_set_mode", new() { ["device"] = "display-b", ["mode_id"] = ModeId(FakeDisplays.Mode720p60) }, "success", "verified"),
            ("display_set_power", new() { ["device"] = "display-b", ["state"] = "off" }, "success", "accepted"),
            ("display_set_input", new() { ["device"] = "display-c", ["input_id"] = Mccs.InputId("display-c", 0x11) }, "success", "accepted"),
            ("display_set_mode", new() { ["device"] = "display-a", ["mode_id"] = ModeId(FakeDisplays.Mode720p60) }, "error", ""),
        })
        {
            var result = await c.CallToolAsync(name, args, cancellationToken: Ct);
            var s = Structured(result);
            Assert.Equal(status, s.GetProperty("status").GetString());
            Assert.Equal(status != "success", result.IsError ?? false);
            if (verification.Length > 0)
            {
                Assert.Equal(verification, s.GetProperty("verification").GetString());
            }

            DisplayServerTests.AssertMatches(listed[name].ProtocolTool.OutputSchema!.Value, s);
        }

        var input = listed["display_set_power"].ProtocolTool.InputSchema;
        Assert.Equal(["on", "off"], input.GetProperty("properties").GetProperty("state").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["device", "state"], input.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.False(input.GetProperty("additionalProperties").GetBoolean());

        var audit = f.Audit.Query(new AuditQuery(ToolName: "display_set_power")).Single();
        Assert.Equal("""{"state":"off","target":"display-b"}""", audit.Arguments);
        Assert.Equal("""{"target":"display-c"}""", f.Audit.Query(new AuditQuery(ToolName: "display_set_input")).Single().Arguments);
    }

    [Fact]
    public async Task Synthetic_secrets_in_mode_and_input_ids_never_reach_storage_or_the_response()
    {
        const string Secret = "SYNTHETICSECRETswitch9";
        await using var f = await ServerFixture.StartAsync();
        foreach (var tool in Tools)
        {
            f.Store.SetToolEnabled(tool, true);
        }

        await using var c = await f.ClientAsync(f.Issue(Tools).Token);
        foreach (var (tool, args) in new (string, Dictionary<string, object?>)[]
        {
            ("display_set_mode", new() { ["device"] = "display-b", ["mode_id"] = Secret }),
            ("display_set_input", new() { ["device"] = "display-b", ["input_id"] = Secret }),
            ("display_set_power", new() { ["device"] = Secret, ["state"] = "off" }),
        })
        {
            var r = await c.CallToolAsync(tool, args, cancellationToken: Ct);
            Assert.True(r.IsError);
            Assert.DoesNotContain(Secret, Structured(r).GetRawText(), StringComparison.Ordinal);
        }

        var events = f.Audit.Query(new AuditQuery());
        using var csv = new StringWriter();
        AuditLog.ExportCsv(events, csv);
        Assert.DoesNotContain(Secret, csv.ToString(), StringComparison.Ordinal);
        using var liveDatabase = f.Database.Open(); // Keep SQLite from removing WAL during inspection.
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(f.Database.Path)!))
        {
            using var copy = new MemoryStream();
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.CopyTo(copy);
            }
            catch (FileNotFoundException)
            {
                continue; // SQLite removed the -wal file after a checkpoint; its content is in the database file.
            }

            Assert.True(copy.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)) < 0);
        }

        Assert.Empty(f.Displays.ModeWrites);
        Assert.Empty(f.Displays.SwitchWrites);
    }

    private static (string Tool, Dictionary<string, object?> Args)[] Calls() =>
    [
        ("display_set_mode", new() { ["device"] = "display-b", ["mode_id"] = ModeId(FakeDisplays.Mode720p60) }),
        ("display_set_power", new() { ["device"] = "display-b", ["state"] = "off" }),
        ("display_set_input", new() { ["device"] = "display-b", ["input_id"] = Mccs.InputId("display-b", 0x11) }),
    ];
}
