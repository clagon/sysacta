using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Core.Tools.Display;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

public class DisplayServerTests
{
    private static readonly string[] Tools =
        ["display_get_state", "display_list_devices", "display_list_modes", "display_set_brightness", "display_set_contrast"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Structured(CallToolResult r) => Assert.IsType<JsonElement>(r.StructuredContent);

    private static Dictionary<string, object?> Args(int level, string? device = null)
    {
        var args = new Dictionary<string, object?> { ["level"] = level };
        if (device is not null)
        {
            args["device"] = device;
        }

        return args;
    }

    [Fact]
    public async Task A_new_database_enables_them_but_an_upgraded_one_neither_enables_nor_grants_them()
    {
        await using var f = await ServerFixture.StartAsync();
        Assert.All(Tools, t => Assert.True(f.Store.IsToolEnabled(t)));

        var audio = new FakeAudioEndpoints();
        var db = new SysactaDatabase(Path.Combine(Path.GetDirectoryName(f.Database.Path)!, "display-upgrade.db"));
        db.Migrate();
        var store = new AccessStore(db, TimeProvider.System, new AuthorizationGate());
        SysactaTool[] before = [new AudioGetStateTool(audio, store)];
        store.SyncTools(before.Select(t => t.Descriptor), "0.3.0");
        var existing = store.IssueClient("c1", null, PermissionPresets.InitialGrants(PermissionPreset.AllEnabled, before, store.IsToolEnabled));

        var displays = new FakeDisplays();
        SysactaTool[] after =
        [
            .. before,
            new DisplayListDevicesTool(displays, store),
            new DisplayGetStateTool(displays, store),
            new DisplaySetBrightnessTool(displays, store),
            new DisplaySetContrastTool(displays, store),
            new DisplayListModesTool(displays, store),
        ];
        store.SyncTools(after.Select(t => t.Descriptor), "0.4.0");

        foreach (var tool in Tools)
        {
            Assert.False(store.IsToolEnabled(tool));
            store.SetToolEnabled(tool, true);
            Assert.False(store.IsClientToolAllowed(existing.Client.Id, tool));
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Ungranted_and_disabled_calls_are_refused_without_touching_a_display()
    {
        await using var f = await ServerFixture.StartAsync();
        f.Displays.OnOpen = _ => throw new InvalidOperationException("must not be reached");
        await using var ungranted = await f.ClientAsync(f.Issue("audio_get_state").Token);
        await using var granted = await f.ClientAsync(f.Issue("display_set_brightness").Token);

        Assert.DoesNotContain(await ungranted.ListToolsAsync(cancellationToken: Ct), t => t.Name.StartsWith("display_", StringComparison.Ordinal));
        var denied = await ungranted.CallToolAsync("display_set_brightness", Args(10, "all"), cancellationToken: Ct);
        f.Store.SetToolEnabled("display_set_brightness", false);
        var disabled = await granted.CallToolAsync("display_set_brightness", Args(10), cancellationToken: Ct);

        Assert.True(denied.IsError);
        Assert.Equal(ErrorCodes.PermissionDenied, Structured(denied).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(ErrorCodes.ToolDisabled, Structured(disabled).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(f.Displays.Writes);
    }

    [Theory]
    [InlineData("success", AuditOutcome.Success)]
    [InlineData("partial", AuditOutcome.Partial)]
    [InlineData("error", AuditOutcome.Error)]
    [InlineData("unknown", AuditOutcome.Unknown)]
    public async Task Batch_outcomes_match_isError_audit_and_the_output_schema(string status, AuditOutcome outcome)
    {
        await using var f = await ServerFixture.StartAsync();
        var second = f.Displays.Brightness("display-b");
        if (status == "partial")
        {
            second.ReadFailure = new ToolFailureException(ErrorCodes.NotSupported, "fixed");
        }
        else if (status == "error")
        {
            f.Displays.Brightness("display-a").ReadFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed");
            second.ReadFailure = new ToolFailureException(ErrorCodes.NotSupported, "fixed");
        }
        else if (status == "unknown")
        {
            second.WriteFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed");
        }

        await using var c = await f.ClientAsync(f.Issue("display_set_brightness").Token);
        var tool = Assert.Single(await c.ListToolsAsync(cancellationToken: Ct));
        var result = await c.CallToolAsync("display_set_brightness", Args(30, "all"), cancellationToken: Ct);

        var s = Structured(result);
        Assert.Equal(status, s.GetProperty("status").GetString());
        Assert.Equal(status != "success", result.IsError ?? false);
        AssertMatches(tool.ProtocolTool.OutputSchema!.Value, s);
        var e = Assert.Single(f.Audit.Query(new AuditQuery(ToolName: "display_set_brightness")));
        Assert.Equal(outcome, e.Outcome);
        Assert.Equal("""{"level":30,"target":"all"}""", e.Arguments);
    }

    [Fact]
    public async Task Every_display_result_matches_its_published_schema()
    {
        await using var f = await ServerFixture.StartAsync();
        f.Displays.Add("display-p", "Panel", kind: DisplayKind.Internal);
        f.Displays.Modes["display-a"] = new([new(1920, 1080, 60, 32, false, 0, 0)], null);
        await using var c = await f.ClientAsync(f.Issue(Tools).Token);
        var listed = (await c.ListToolsAsync(cancellationToken: Ct)).ToDictionary(t => t.Name);

        foreach (var (name, args) in new (string, Dictionary<string, object?>)[]
        {
            ("display_list_devices", []),
            ("display_get_state", new() { ["device"] = "display-p" }),
            ("display_list_modes", new() { ["device"] = "display-a" }),
            ("display_set_brightness", Args(30)),
            ("display_set_contrast", Args(30, "display-p")),
        })
        {
            var result = await c.CallToolAsync(name, args, cancellationToken: Ct);
            AssertMatches(listed[name].ProtocolTool.OutputSchema!.Value, Structured(result));
        }
    }

    [Fact]
    public async Task Synthetic_secret_in_device_never_reaches_db_wal_csv_json_or_the_response()
    {
        const string Secret = "SYNTHETICSECRETdisplaydev7";
        await using var f = await ServerFixture.StartAsync();
        await using var c = await f.ClientAsync(f.Issue(Tools).Token);

        foreach (var tool in Tools.Where(t => t != "display_list_devices"))
        {
            var args = tool.StartsWith("display_set_", StringComparison.Ordinal) ? Args(30, Secret) : new Dictionary<string, object?> { ["device"] = Secret };
            var r = await c.CallToolAsync(tool, args, cancellationToken: Ct);
            Assert.True(r.IsError);
            Assert.DoesNotContain(Secret, Structured(r).GetRawText(), StringComparison.Ordinal);
        }

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

    /// <summary>Checks a value against the subset of JSON Schema that ToolJson generates.</summary>
    internal static void AssertMatches(JsonElement schema, JsonElement value, string path = "$")
    {
        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            Assert.True(anyOf.EnumerateArray().Any(branch => Fits(branch, value, path)), $"{path} matches no branch.");
            return;
        }

        if (schema.TryGetProperty("enum", out var allowed))
        {
            Assert.True(allowed.EnumerateArray().Any(a => a.ToString() == value.ToString()), $"{path}: '{value}' is not allowed.");
        }

        if (!schema.TryGetProperty("type", out var type))
        {
            return;
        }

        var kind = value.ValueKind;
        var fits = type.GetString() switch
        {
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "string" => kind == JsonValueKind.String,
            "integer" or "number" => kind == JsonValueKind.Number,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "null" => kind == JsonValueKind.Null,
            _ => false,
        };
        Assert.True(fits, $"{path}: expected {type}, got {kind}.");

        if (kind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            foreach (var item in value.EnumerateArray())
            {
                AssertMatches(items, item, path + "[]");
            }
        }

        if (kind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
        {
            if (schema.TryGetProperty("required", out var required))
            {
                Assert.All(required.EnumerateArray(), r => Assert.True(value.TryGetProperty(r.GetString()!, out _), $"{path}.{r} is missing."));
            }

            foreach (var property in value.EnumerateObject())
            {
                if (properties.TryGetProperty(property.Name, out var child))
                {
                    AssertMatches(child, property.Value, path + "." + property.Name);
                }
                else
                {
                    // The envelope is an open object; inside data every field must be published.
                    Assert.True(path == "$", $"{path}.{property.Name} is not in the schema.");
                }
            }
        }
    }

    private static bool Fits(JsonElement schema, JsonElement value, string path)
    {
        try
        {
            AssertMatches(schema, value, path);
            return true;
        }
        catch (Xunit.Sdk.XunitException)
        {
            return false;
        }
    }
}
