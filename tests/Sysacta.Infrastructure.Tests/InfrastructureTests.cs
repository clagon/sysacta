using System.Text;
using Microsoft.Data.Sqlite;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;

namespace Sysacta.Infrastructure.Tests;

public sealed class TempDatabase : IDisposable
{
    private readonly string _dir = System.IO.Directory.CreateTempSubdirectory("sysacta-test-").FullName;

    public TempDatabase()
    {
        Database = new SysactaDatabase(Path.Combine(_dir, "sysacta.db"));
    }

    public SysactaDatabase Database { get; }

    public string Directory => _dir;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class MigrationTests
{
    [Fact]
    public void New_database_is_migrated_to_latest_and_is_idempotent()
    {
        using var t = new TempDatabase();
        Assert.Equal(0, t.Database.Migrate());
        Assert.Equal(SysactaDatabase.Migrations.Count, t.Database.Migrate());

        using var c = t.Database.Open();
        foreach (var table in new[] { "settings", "tools", "clients", "client_permissions", "device_aliases", "registered_apps", "audit_events" })
        {
            Assert.Equal(1L, SysactaDatabase.Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $n", ("$n", table)));
        }
    }

    [Fact]
    public void Upgrade_keeps_data_and_writes_backup()
    {
        using var t = new TempDatabase();
        t.Database.Migrate([SysactaDatabase.Migrations[0]]);
        new SettingsStore(t.Database, TimeProvider.System).Save(new AppSettings { Port = 9000 });

        var before = t.Database.Migrate([SysactaDatabase.Migrations[0], "ALTER TABLE settings ADD COLUMN note TEXT;"]);

        Assert.Equal(1, before);
        Assert.Equal(9000, new SettingsStore(t.Database, TimeProvider.System).Load().Settings.Port);
        Assert.True(File.Exists(t.Database.Path + ".v1.bak"));
    }

    [Fact]
    public void Newer_schema_is_refused()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        using (var c = t.Database.Open())
        {
            SysactaDatabase.Execute(c, "PRAGMA user_version = 99;");
        }

        Assert.Throws<InvalidOperationException>(() => t.Database.Migrate());
    }

    [Fact]
    public void Failed_migration_is_rolled_back()
    {
        using var t = new TempDatabase();
        t.Database.Migrate([SysactaDatabase.Migrations[0]]);
        Assert.ThrowsAny<SqliteException>(() => t.Database.Migrate([SysactaDatabase.Migrations[0], "CREATE TABLE x (a INT); THIS IS NOT SQL;"]));

        using var c = t.Database.Open();
        Assert.Equal(1L, SysactaDatabase.Scalar(c, "PRAGMA user_version;"));
        Assert.Equal(0L, SysactaDatabase.Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'x'"));
    }
}

public class AccessStoreTests
{
    private static ToolDescriptor Tool(string name, DefaultPolicy policy, bool readOnly = true) =>
        new(name, name.Split('_')[0], name, name, policy, readOnly, []);

    [Fact]
    public void Tool_defaults_apply_only_to_a_new_database()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var store = new AccessStore(t.Database, TimeProvider.System, new AuthorizationGate());

        store.SyncTools([Tool("audio_get_state", DefaultPolicy.On), Tool("power_sleep", DefaultPolicy.Off), Tool("audio_set_default", DefaultPolicy.Experimental)], "0.1.0");
        Assert.True(store.IsToolEnabled("audio_get_state"));
        Assert.False(store.IsToolEnabled("power_sleep"));
        Assert.False(store.IsToolEnabled("audio_set_default"));

        // A tool introduced by a later version is disabled even if its default is ON.
        store.SyncTools([Tool("audio_get_state", DefaultPolicy.On), Tool("media_play", DefaultPolicy.On)], "0.2.0");
        Assert.False(store.IsToolEnabled("media_play"));
        Assert.True(store.IsToolEnabled("audio_get_state"));
        Assert.False(store.IsToolEnabled("unknown_tool"));
    }

    [Fact]
    public void Issue_stores_only_hash_and_grants()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var store = new AccessStore(t.Database, TimeProvider.System, new AuthorizationGate());

        var issued = store.IssueClient("Agent", null, ["audio_get_state"]);

        Assert.True(store.IsClientToolAllowed(issued.Client.Id, "audio_get_state"));
        Assert.False(store.IsClientToolAllowed(issued.Client.Id, "audio_set_volume"));
        var found = store.FindClientByTokenHash(ClientTokens.Hash(issued.Token));
        Assert.Equal(issued.Client.Id, found!.Value.Client.Id);

        // The token text must not exist anywhere in the database file.
        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(t.Database.Path);
        var wal = File.Exists(t.Database.Path + "-wal") ? File.ReadAllBytes(t.Database.Path + "-wal") : [];
        var secret = Encoding.UTF8.GetBytes(issued.Token[ClientTokens.Prefix.Length..]);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(secret));
        Assert.Equal(-1, wal.AsSpan().IndexOf(secret));
    }

    [Fact]
    public void Revoke_reissue_expire_and_authenticate()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var store = new AccessStore(t.Database, TimeProvider.System, new AuthorizationGate());
        var auth = new TokenAuthenticator(store, TimeProvider.System);
        var issued = store.IssueClient("Agent", null, ["audio_get_state"]);

        Assert.True(auth.Authenticate("Bearer " + issued.Token).Succeeded);
        Assert.NotNull(store.GetClient(issued.Client.Id)!.LastUsedAt);

        var reissued = store.ReissueClient(issued.Client.Id);
        Assert.Equal(AuthFailure.UnknownToken, auth.Authenticate("Bearer " + issued.Token).Failure);
        Assert.True(auth.Authenticate("Bearer " + reissued.Token).Succeeded);
        Assert.True(store.IsClientToolAllowed(issued.Client.Id, "audio_get_state"));

        store.SetClientExpiry(issued.Client.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(AuthFailure.Expired, auth.Authenticate("Bearer " + reissued.Token).Failure);

        store.SetClientExpiry(issued.Client.Id, null);
        store.RevokeClient(issued.Client.Id);
        Assert.Equal(AuthFailure.Revoked, auth.Authenticate("Bearer " + reissued.Token).Failure);
        Assert.Throws<InvalidOperationException>(() => store.ReissueClient(issued.Client.Id));

        store.DeleteClient(issued.Client.Id);
        Assert.Null(store.GetClient(issued.Client.Id));
        Assert.Empty(store.GetClientGrants(issued.Client.Id));
    }

    [Fact]
    public void Grants_change_immediately()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var store = new AccessStore(t.Database, TimeProvider.System, new AuthorizationGate());
        var id = store.IssueClient("Agent", null, []).Client.Id;

        store.SetClientGrant(id, "audio_set_volume", true);
        Assert.True(store.IsClientToolAllowed(id, "audio_set_volume"));
        store.SetClientGrant(id, "audio_set_volume", false);
        Assert.False(store.IsClientToolAllowed(id, "audio_set_volume"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("DEFAULT")]
    [InlineData("dev-1")]
    public void Invalid_aliases_are_rejected(string alias)
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var store = new AccessStore(t.Database, TimeProvider.System, new AuthorizationGate());
        Assert.Throws<ArgumentException>(() => store.SetAlias("audio", "dev-1", alias, ["dev-1"]));
    }

    [Fact]
    public void Aliases_resolve_case_insensitively_within_category()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var store = new AccessStore(t.Database, TimeProvider.System, new AuthorizationGate());
        store.SetAlias("audio", "dev-1", "Desk", ["dev-1"]);

        Assert.Equal("dev-1", store.Resolve("audio", "desk"));
        Assert.Null(store.Resolve("display", "desk"));
        Assert.ThrowsAny<SqliteException>(() => store.SetAlias("audio", "dev-2", "DESK", ["dev-1", "dev-2"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public void Invalid_client_names_are_rejected(string name)
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        Assert.Throws<ArgumentException>(() => new AccessStore(t.Database, TimeProvider.System, new AuthorizationGate()).IssueClient(name, null, []));
    }
}

public class AuditLogTests
{
    private static AuditEvent Event(string id, DateTimeOffset ts, AuditOutcome outcome = AuditOutcome.Success, string? tool = "audio_get_state", string? args = null) => new()
    {
        RequestId = id,
        Timestamp = ts,
        ClientId = "c1",
        ClientName = "Agent",
        ToolName = tool,
        Source = "mcp",
        Outcome = outcome,
        Arguments = args,
    };

    [Fact]
    public void Pending_record_is_replaced_by_final_outcome()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var log = new AuditLog(t.Database);
        var now = DateTimeOffset.UtcNow;

        log.Write(Event("r1", now, AuditOutcome.Pending));
        log.Write(Event("r1", now, AuditOutcome.Error) with { ErrorCode = "OS_API_ERROR", DurationMs = 12 });

        var e = Assert.Single(log.Query(new AuditQuery()));
        Assert.Equal(AuditOutcome.Error, e.Outcome);
        Assert.Equal("OS_API_ERROR", e.ErrorCode);
        Assert.Equal(12, e.DurationMs);
    }

    [Fact]
    public void Query_filters_and_escapes_like_wildcards()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var log = new AuditLog(t.Database);
        var now = DateTimeOffset.UtcNow;
        log.Write(Event("a", now.AddMinutes(-3), AuditOutcome.Success, "audio_get_state"));
        log.Write(Event("b", now.AddMinutes(-2), AuditOutcome.Denied, "audio_set_volume"));
        log.Write(Event("c", now.AddMinutes(-1), AuditOutcome.AuthFailed, null) with { ClientId = null, ClientName = null, Source = "http" });

        Assert.Equal(["c", "b", "a"], log.Query(new AuditQuery()).Select(e => e.RequestId));
        Assert.Equal(["b"], log.Query(new AuditQuery(Text: "set_vol")).Select(e => e.RequestId));
        Assert.Empty(log.Query(new AuditQuery(Text: "%")));
        Assert.Equal(["c"], log.Query(new AuditQuery(Outcome: AuditOutcome.AuthFailed)).Select(e => e.RequestId));
        Assert.Equal(["a"], log.Query(new AuditQuery(ToolName: "audio_get_state")).Select(e => e.RequestId));
        Assert.Equal(["b", "a"], log.Query(new AuditQuery(From: now.AddMinutes(-4), To: now.AddMinutes(-1.5))).Select(e => e.RequestId));
        Assert.Equal(["c"], log.Query(new AuditQuery(Limit: 1)).Select(e => e.RequestId));
    }

    [Fact]
    public void Purge_removes_records_older_than_retention()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var log = new AuditLog(t.Database);
        var now = DateTimeOffset.UtcNow;
        log.Write(Event("old", now.AddDays(-31)));
        log.Write(Event("new", now.AddDays(-29)));

        Assert.Equal(1, log.Purge(30, now));
        Assert.Equal(["new"], log.Query(new AuditQuery()).Select(e => e.RequestId));
    }

    [Theory]
    [InlineData("=1+1", "\"'=1+1\"")]
    [InlineData("+cmd", "\"'+cmd\"")]
    [InlineData("-2", "\"'-2\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\tx", "\"'\tx\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("plain", "\"plain\"")]
    [InlineData(null, "")]
    public void Csv_cells_are_protected_against_formula_injection(string? value, string expected) =>
        Assert.Equal(expected, AuditLog.CsvCell(value));

    [Fact]
    public void Export_csv_and_json()
    {
        var events = new[] { Event("r1", DateTimeOffset.UnixEpoch, tool: "=HYPERLINK(\"x\")", args: """{"level":3}""") };

        using var csv = new StringWriter();
        AuditLog.ExportCsv(events, csv);
        var lines = csv.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("request_id,timestamp_utc", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", lines[1], StringComparison.Ordinal);

        using var json = new MemoryStream();
        AuditLog.ExportJson(events, json);
        var doc = System.Text.Json.JsonDocument.Parse(json.ToArray());
        Assert.Equal("r1", doc.RootElement[0].GetProperty("request_id").GetString());
        Assert.Equal("success", doc.RootElement[0].GetProperty("result").GetString());
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Defaults_are_safe()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var (s, invalid) = new SettingsStore(t.Database, TimeProvider.System).Load();
        Assert.Equal("127.0.0.1", s.BindAddress);
        Assert.Equal(8765, s.Port);
        Assert.Empty(s.AllowedOrigins);
        Assert.Equal(30, s.AuditRetentionDays);
        Assert.Empty(invalid);
    }

    [Fact]
    public void Round_trip()
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        var store = new SettingsStore(t.Database, TimeProvider.System);
        var settings = new AppSettings { BindAddress = "0.0.0.0", Port = 9999, AllowedHosts = ["pc.example.ts.net"], AllowedOrigins = ["https://app.example"], AuditRetentionDays = 7, Theme = ThemePreference.Dark, ExternalUrl = "https://pc.example.ts.net/mcp" };

        store.Save(settings);
        var loaded = store.Load().Settings;

        Assert.Equal(settings with { AllowedHosts = loaded.AllowedHosts, AllowedOrigins = loaded.AllowedOrigins }, loaded);
        Assert.Equal(settings.AllowedHosts, loaded.AllowedHosts);
        Assert.Equal(settings.AllowedOrigins, loaded.AllowedOrigins);
    }

    [Theory]
    [InlineData("port", "abc")]
    [InlineData("port", "70000")]
    [InlineData("bind_address", "not-an-ip")]
    [InlineData("theme", "Neon")]
    [InlineData("allowed_origins", "*")]
    public void Corrupt_values_fall_back_to_defaults(string key, string value)
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        using (var c = t.Database.Open())
        {
            SysactaDatabase.Execute(c, "INSERT INTO settings (key, value, updated_at) VALUES ($k, $v, 'x')", ("$k", key), ("$v", value));
        }

        var (s, invalid) = new SettingsStore(t.Database, TimeProvider.System).Load();
        Assert.Null(s.Validate());
        Assert.NotEmpty(invalid);
        Assert.Equal("127.0.0.1", s.BindAddress);
        Assert.Empty(s.AllowedOrigins);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("")]
    public void Invalid_settings_are_not_saved(string bind)
    {
        using var t = new TempDatabase();
        t.Database.Migrate();
        Assert.Throws<ArgumentException>(() => new SettingsStore(t.Database, TimeProvider.System).Save(new AppSettings { BindAddress = bind }));
    }
}
