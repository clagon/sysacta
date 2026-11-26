using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

public class AudioDefaultDeviceServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Structured(CallToolResult r) => Assert.IsType<JsonElement>(r.StructuredContent);

    [Fact]
    public async Task Experimental_tool_is_disabled_on_fresh_and_upgraded_db_and_not_auto_granted()
    {
        await using var f = await ServerFixture.StartAsync();
        Assert.False(f.Store.IsToolEnabled("audio_set_default_device"));

        // Upgrade from a DB without audio_set_default_device.
        var audio = new FakeAudioEndpoints();
        var writer = new FakeDefaultEndpointWriter(audio);
        var gate = new AuthorizationGate();
        var path = Path.Combine(Path.GetDirectoryName(f.Database.Path)!, "exp-upgrade.db");
        var db = new SysactaDatabase(path);
        db.Migrate();
        var store = new AccessStore(db, TimeProvider.System, gate);
        SysactaTool[] prev = [new AudioGetStateTool(audio, store)];
        store.SyncTools(prev.Select(t => t.Descriptor), "0.2.0");
        var existing = store.IssueClient("c1", null, PermissionPresets.InitialGrants(PermissionPreset.AllEnabled, prev, store.IsToolEnabled));

        SysactaTool[] next = [.. prev, new AudioSetDefaultDeviceTool(audio, store, writer)];
        store.SyncTools(next.Select(t => t.Descriptor), "0.3.0");

        Assert.False(store.IsToolEnabled("audio_set_default_device"));
        store.SetToolEnabled("audio_set_default_device", true);
        Assert.False(store.IsClientToolAllowed(existing.Client.Id, "audio_set_default_device"));

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Capability_global_and_client_permissions_control_list_and_direct_calls()
    {
        await using var f = await ServerFixture.StartAsync();
        f.AudioDevice.Devices["spk2"] = new("spk2", "Speakers 2", AudioDirection.Output, AudioDeviceState.Active, 0.7f, false);

        var ungranted = await f.ClientAsync(f.Issue("audio_get_state").Token);
        var granted = await f.ClientAsync(f.Issue("audio_set_default_device").Token);

        // Globally disabled by default.
        Assert.DoesNotContain(await granted.ListToolsAsync(cancellationToken: Ct), t => t.Name == "audio_set_default_device");
        var disabledCall = await granted.CallToolAsync("audio_set_default_device", new Dictionary<string, object?>
        {
            ["device"] = "spk2",
            ["direction"] = "output",
            ["role"] = "console",
        }, cancellationToken: Ct);
        Assert.True(disabledCall.IsError);
        Assert.Equal(ErrorCodes.ToolDisabled, Structured(disabledCall).GetProperty("error").GetProperty("code").GetString());

        // Globally enabled, but ungranted client is denied.
        f.Store.SetToolEnabled("audio_set_default_device", true);
        Assert.DoesNotContain(await ungranted.ListToolsAsync(cancellationToken: Ct), t => t.Name == "audio_set_default_device");
        var deniedCall = await ungranted.CallToolAsync("audio_set_default_device", new Dictionary<string, object?>
        {
            ["device"] = "spk2",
            ["direction"] = "output",
            ["role"] = "console",
        }, cancellationToken: Ct);
        Assert.True(deniedCall.IsError);
        Assert.Equal(ErrorCodes.PermissionDenied, Structured(deniedCall).GetProperty("error").GetProperty("code").GetString());

        // Unavailable environment hides from tools/list and rejects direct call.
        f.DefaultWriter.Availability = ToolAvailability.Unavailable(ErrorCodes.NotSupported, "Requires Windows 11.");
        Assert.DoesNotContain(await granted.ListToolsAsync(cancellationToken: Ct), t => t.Name == "audio_set_default_device");
        var unavailableCall = await granted.CallToolAsync("audio_set_default_device", new Dictionary<string, object?>
        {
            ["device"] = "spk2",
            ["direction"] = "output",
            ["role"] = "console",
        }, cancellationToken: Ct);
        Assert.True(unavailableCall.IsError);
        Assert.Equal(ErrorCodes.NotSupported, Structured(unavailableCall).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Partial_result_over_mcp_sets_isError_and_audits_only_allowed_fields()
    {
        await using var f = await ServerFixture.StartAsync();
        f.AudioDevice.Devices["spk2"] = new("spk2", "Speakers 2", AudioDirection.Output, AudioDeviceState.Active, 0.7f, false);
        f.Store.SetToolEnabled("audio_set_default_device", true);
        f.DefaultWriter.FailWith[AudioRole.Multimedia] = new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio policy interface returned an error.");

        await using var c = await f.ClientAsync(f.Issue("audio_set_default_device").Token);
        var result = await c.CallToolAsync("audio_set_default_device", new Dictionary<string, object?>
        {
            ["device"] = "spk2",
            ["direction"] = "output",
            ["role"] = "all",
        }, cancellationToken: Ct);

        Assert.True(result.IsError);
        var s = Structured(result);
        Assert.Equal("partial", s.GetProperty("status").GetString());
        Assert.Equal("verified", s.GetProperty("verification").GetString());
        Assert.Equal("spk2", s.GetProperty("data").GetProperty("device_id").GetString());
        var e = Assert.Single(f.Audit.Query(new AuditQuery(ToolName: "audio_set_default_device")));
        Assert.Equal(AuditOutcome.Partial, e.Outcome);
        Assert.Equal("""{"direction":"output","role":"all","target":"spk2"}""", e.Arguments);
    }

    [Fact]
    public async Task Synthetic_secret_in_device_never_reaches_db_wal_csv_or_json()
    {
        const string Secret = "SYNTHETICSECRETdefaultdev9";
        await using var f = await ServerFixture.StartAsync();
        f.Store.SetToolEnabled("audio_set_default_device", true);
        await using var c = await f.ClientAsync(f.Issue("audio_set_default_device").Token);

        var r = await c.CallToolAsync("audio_set_default_device", new Dictionary<string, object?>
        {
            ["device"] = Secret,
            ["direction"] = "output",
            ["role"] = "console",
        }, cancellationToken: Ct);

        Assert.True(r.IsError);
        Assert.DoesNotContain(Secret, Structured(r).GetRawText(), StringComparison.Ordinal);

        var events = f.Audit.Query(new AuditQuery());
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

        foreach (var content in contents)
        {
            Assert.True(content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)) < 0);
            Assert.True(content.AsSpan().IndexOf(Encoding.Unicode.GetBytes(Secret)) < 0);
        }
    }
}
