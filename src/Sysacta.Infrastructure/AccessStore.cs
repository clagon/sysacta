using Microsoft.Data.Sqlite;
using Sysacta.Core.Devices;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using static Sysacta.Infrastructure.SysactaDatabase;

namespace Sysacta.Infrastructure;

/// <summary>A newly issued or re-issued client. <see cref="Token"/> is shown once and never stored.</summary>
public sealed record IssuedClient(ClientRecord Client, string Token);

/// <summary>
/// SQLite store for tool policy, clients, client permissions and device aliases.
/// The request pipeline sees it only as <see cref="IAccessStore"/> / <see cref="IDeviceAliases"/> (read-only);
/// the management methods are used by the console only. Reads always hit the database, so changes apply immediately.
/// </summary>
/// <remarks>
/// Every write that changes what a client may do runs inside <see cref="AuthorizationGate.BeginUpdate"/> of the gate
/// shared with the executor, so it is ordered against the start of state-changing operations (SEC-01).
/// </remarks>
public sealed class AccessStore(SysactaDatabase database, TimeProvider time, AuthorizationGate gate) : IAccessStore, IDeviceAliases
{
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    // ---- IAccessStore (pipeline, read side) ----

    public bool IsToolEnabled(string toolName)
    {
        using var c = database.Open();
        return Scalar(c, "SELECT enabled FROM tools WHERE tool_name = $n", ("$n", toolName)) is long v && v == 1;
    }

    public ClientRecord? GetClient(string clientId)
    {
        using var c = database.Open();
        using var cmd = Command(c, $"{ClientSelect} WHERE id = $id", ("$id", clientId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadClient(r) : null;
    }

    public (ClientRecord Client, string TokenHash)? FindClientByTokenHash(string tokenHash)
    {
        using var c = database.Open();
        using var cmd = Command(c, "SELECT id, name, created_at, expires_at, revoked_at, last_used_at, token_hash FROM clients WHERE token_hash = $h", ("$h", tokenHash));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (ReadClient(r), r.GetString(6)) : null;
    }

    public bool IsClientToolAllowed(string clientId, string toolName)
    {
        using var c = database.Open();
        return Scalar(c, "SELECT allowed FROM client_permissions WHERE client_id = $c AND tool_name = $t", ("$c", clientId), ("$t", toolName)) is long v && v == 1;
    }

    public void RecordClientUse(string clientId, DateTimeOffset at)
    {
        using var c = database.Open();
        Execute(c, "UPDATE clients SET last_used_at = $at WHERE id = $id AND (last_used_at IS NULL OR last_used_at < $threshold)",
            ("$at", ToDb(at)), ("$id", clientId), ("$threshold", ToDb(at - LastUsedResolution)));
    }

    // ---- Tool policy (console) ----

    /// <summary>
    /// Registers tools not yet known. On a new database the tools get their default policy (ON enabled);
    /// tools introduced by a later version are disabled until the user enables them (spec §7).
    /// </summary>
    public void SyncTools(IEnumerable<ToolDescriptor> tools, string appVersion)
    {
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        using var tx = c.BeginTransaction();
        var fresh = Convert.ToInt64(ScalarTx(c, tx, "SELECT COUNT(*) FROM tools")) == 0;
        foreach (var tool in tools)
        {
            var enabled = fresh && tool.DefaultPolicy == DefaultPolicy.On;
            ExecuteTx(c, tx, "INSERT OR IGNORE INTO tools (tool_name, enabled, first_seen_version) VALUES ($n, $e, $v)",
                ("$n", tool.Name), ("$e", enabled ? 1 : 0), ("$v", appVersion));
        }

        tx.Commit();
    }

    public void SetToolEnabled(string toolName, bool enabled)
    {
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        if (Execute(c, "UPDATE tools SET enabled = $e WHERE tool_name = $n", ("$e", enabled ? 1 : 0), ("$n", toolName)) == 0)
        {
            throw new InvalidOperationException("Unknown tool.");
        }
    }

    // ---- Clients (console) ----

    public IssuedClient IssueClient(string name, DateTimeOffset? expiresAt, IEnumerable<string> grantedTools)
    {
        using var update = gate.BeginUpdate();
        name = ValidateName(name);
        var (token, hash) = ClientTokens.Generate();
        var client = new ClientRecord(Guid.NewGuid().ToString("N"), name, time.GetUtcNow(), expiresAt, null, null);
        using var c = database.Open();
        using var tx = c.BeginTransaction();
        ExecuteTx(c, tx, "INSERT INTO clients (id, name, token_hash, created_at, expires_at) VALUES ($id, $n, $h, $c, $e)",
            ("$id", client.Id), ("$n", client.Name), ("$h", hash), ("$c", ToDb(client.CreatedAt)), ("$e", ToDb(expiresAt)));
        foreach (var tool in grantedTools.Distinct(StringComparer.Ordinal))
        {
            ExecuteTx(c, tx, "INSERT INTO client_permissions (client_id, tool_name, allowed) VALUES ($c, $t, 1)", ("$c", client.Id), ("$t", tool));
        }

        tx.Commit();
        return new IssuedClient(client, token);
    }

    /// <summary>Replaces the token; the old token stops working immediately. Permissions are kept.</summary>
    public IssuedClient ReissueClient(string clientId)
    {
        var client = GetClient(clientId) ?? throw new InvalidOperationException("Unknown client.");
        if (client.IsRevoked)
        {
            throw new InvalidOperationException("A revoked client cannot be re-issued. Issue a new client instead.");
        }

        var (token, hash) = ClientTokens.Generate();
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        Execute(c, "UPDATE clients SET token_hash = $h WHERE id = $id", ("$h", hash), ("$id", clientId));
        return new IssuedClient(client, token);
    }

    public void RevokeClient(string clientId)
    {
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        Execute(c, "UPDATE clients SET revoked_at = $t WHERE id = $id AND revoked_at IS NULL", ("$t", ToDb(time.GetUtcNow())), ("$id", clientId));
    }

    public void DeleteClient(string clientId)
    {
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        Execute(c, "DELETE FROM clients WHERE id = $id", ("$id", clientId));
    }

    public void SetClientExpiry(string clientId, DateTimeOffset? expiresAt)
    {
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        Execute(c, "UPDATE clients SET expires_at = $e WHERE id = $id", ("$e", ToDb(expiresAt)), ("$id", clientId));
    }

    public IReadOnlyList<ClientRecord> ListClients()
    {
        using var c = database.Open();
        using var cmd = Command(c, $"{ClientSelect} ORDER BY created_at");
        using var r = cmd.ExecuteReader();
        var list = new List<ClientRecord>();
        while (r.Read())
        {
            list.Add(ReadClient(r));
        }

        return list;
    }

    public IReadOnlySet<string> GetClientGrants(string clientId)
    {
        using var c = database.Open();
        using var cmd = Command(c, "SELECT tool_name FROM client_permissions WHERE client_id = $c AND allowed = 1", ("$c", clientId));
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read())
        {
            set.Add(r.GetString(0));
        }

        return set;
    }

    public void SetClientGrant(string clientId, string toolName, bool allowed)
    {
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        Execute(c, "INSERT INTO client_permissions (client_id, tool_name, allowed) VALUES ($c, $t, $a) ON CONFLICT (client_id, tool_name) DO UPDATE SET allowed = excluded.allowed",
            ("$c", clientId), ("$t", toolName), ("$a", allowed ? 1 : 0));
    }

    // ---- Device aliases ----

    public string? Resolve(string category, string alias)
    {
        using var c = database.Open();
        return Scalar(c, "SELECT device_id FROM device_aliases WHERE category = $c AND alias = $a", ("$c", category), ("$a", alias)) as string;
    }

    public IReadOnlyList<DeviceAlias> List(string category)
    {
        using var c = database.Open();
        using var cmd = Command(c, "SELECT alias, device_id FROM device_aliases WHERE category = $c ORDER BY alias", ("$c", category));
        using var r = cmd.ExecuteReader();
        var list = new List<DeviceAlias>();
        while (r.Read())
        {
            list.Add(new DeviceAlias(r.GetString(0), r.GetString(1)));
        }

        return list;
    }

    // Alias writes (console only). They do not change permissions, so they need no authorization gate; a call that is
    // already queued keeps the device it resolved. Uniqueness and lookup both use the column's NOCASE collation.

    /// <summary>Registers an alias. Rejects reserved words, duplicates and aliases that equal a device id (spec §8).</summary>
    public void SetAlias(string category, string deviceId, string alias, IReadOnlyCollection<string> knownDeviceIds)
    {
        alias = ValidateAlias(category, alias, knownDeviceIds);
        WriteUnique("INSERT INTO device_aliases (category, device_id, alias) VALUES ($c, $d, $a)",
            ("$c", category), ("$d", deviceId), ("$a", alias));
    }

    public void RenameAlias(string category, string alias, string newAlias, IReadOnlyCollection<string> knownDeviceIds)
    {
        newAlias = ValidateAlias(category, newAlias, knownDeviceIds);
        WriteUnique("UPDATE device_aliases SET alias = $n WHERE category = $c AND alias = $a",
            ("$n", newAlias), ("$c", category), ("$a", alias));
    }

    /// <summary>Points an existing alias at another device. Only ever done on the user's explicit choice (spec §8).</summary>
    public void RebindAlias(string category, string alias, string newDeviceId) =>
        WriteUnique("UPDATE device_aliases SET device_id = $d WHERE category = $c AND alias = $a",
            ("$d", newDeviceId), ("$c", category), ("$a", alias));

    public void RemoveAlias(string category, string alias)
    {
        using var c = database.Open();
        Execute(c, "DELETE FROM device_aliases WHERE category = $c AND alias = $a", ("$c", category), ("$a", alias));
    }

    /// <summary>The messages are shown to the local user as they are.</summary>
    private string ValidateAlias(string category, string alias, IReadOnlyCollection<string> knownDeviceIds)
    {
        alias = alias.Trim();
        if (alias.Length is 0 or > 64 || alias.Any(char.IsControl))
        {
            throw new ArgumentException("別名は制御文字を含まない1〜64文字で入力してください。");
        }

        if (DeviceAliasRules.Reserved.Contains(alias))
        {
            throw new ArgumentException("「all」と「default」は予約語のため別名にできません。");
        }

        // Ids already registered count as known even while their device is not connected.
        if (knownDeviceIds.Concat(List(category).Select(a => a.DeviceId)).Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("デバイスIDと同じ文字列は別名にできません。");
        }

        return alias;
    }

    private void WriteUnique(string sql, params (string Name, object? Value)[] parameters)
    {
        using var c = database.Open();
        int changed;
        try
        {
            changed = Execute(c, sql, parameters);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new ArgumentException("同じ別名がすでに登録されています。");
        }

        if (changed == 0)
        {
            throw new InvalidOperationException("別名が見つかりません。再読み込みしてください。");
        }
    }

    private const string ClientSelect = "SELECT id, name, created_at, expires_at, revoked_at, last_used_at FROM clients";

    private static ClientRecord ReadClient(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        FromDb(r.GetString(2)),
        FromDbNullable(r.GetValue(3)),
        FromDbNullable(r.GetValue(4)),
        FromDbNullable(r.GetValue(5)));

    private static object? ScalarTx(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = Command(c, sql);
        cmd.Transaction = tx;
        return cmd.ExecuteScalar();
    }

    private static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 64 || name.Any(char.IsControl))
        {
            throw new ArgumentException("Client name must be 1-64 printable characters.", nameof(name));
        }

        return name;
    }
}
