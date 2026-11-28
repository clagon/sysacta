using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sysacta.Core.Security;
using Sysacta.Core.Tools.Apps;
using static Sysacta.Infrastructure.SysactaDatabase;

namespace Sysacta.Infrastructure;

/// <summary>One row as shown in the console. <see cref="App"/> is null for a row in an unknown, legacy or corrupt format.</summary>
public sealed record StoredApp(string Id, string DisplayName, bool Allowed, RegisteredApp? App);

/// <summary>
/// SQLite store of the app allow-list. The tools see it only as <see cref="IAppRegistry"/> (read-only); registration,
/// approval, allowing and deletion are console methods. Reads always hit the database, so changes apply immediately.
/// </summary>
/// <remarks>
/// Every write runs inside <see cref="AuthorizationGate.BeginUpdate"/> of the gate shared with the executor, so it is
/// ordered against the start point of app_launch / app_close / process_terminate, whose final check
/// (<see cref="IsCurrent"/>) runs in the shared side of the same gate. Hashing and OS checks happen before, outside the
/// gate (the caller passes their result). Messages of thrown exceptions are Japanese text for the local user.
/// </remarks>
public sealed class AppRegistryStore(SysactaDatabase database, AuthorizationGate gate) : IAppRegistry
{
    private const string Kind = AppRules.Win32ExeKind;
    private const string Select = "SELECT id, display_name, launch_target, fixed_args, allowed, generation, volume_serial, file_id, file_size, sha256, kind FROM registered_apps";

    // ---- IAppRegistry (tools, read side) ----

    public IReadOnlyList<RegisteredApp> ListAllowed() =>
        [.. Read($"{Select} WHERE allowed = 1 AND kind = '{Kind}' ORDER BY id").Select(s => s.App).OfType<RegisteredApp>()];

    public bool IsCurrent(string appId, long generation)
    {
        using var c = database.Open();
        return Scalar(c, $"SELECT 1 FROM registered_apps WHERE id = $id AND allowed = 1 AND kind = '{Kind}' AND generation = $g",
            ("$id", appId), ("$g", generation)) is not null;
    }

    // ---- Console ----

    public IReadOnlyList<StoredApp> ListAll() => Read($"{Select} ORDER BY display_name COLLATE NOCASE, id");

    /// <summary>
    /// Registers an inspected executable (identity with SHA-256) as a new, not allowed app. The same file (volume + file
    /// id) cannot be registered twice. Returns the new permanent id.
    /// </summary>
    public string Register(string canonicalPath, string displayName, IReadOnlyList<string> fixedArgs, ExecutableIdentity identity)
    {
        displayName = displayName.Trim();
        Validate(canonicalPath, displayName, fixedArgs, identity);
        var id = AppRules.NewAppId();
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        using var tx = c.BeginTransaction();
        if (Convert.ToInt64(ScalarTx(c, tx, "SELECT COUNT(*) FROM registered_apps")) >= AppRules.MaxApps)
        {
            throw new InvalidOperationException($"登録できるアプリは {AppRules.MaxApps} 件までです。");
        }

        WriteUnique(() => ExecuteTx(c, tx,
            $"INSERT INTO registered_apps (id, display_name, launch_target, fixed_args, allowed, kind, generation, volume_serial, file_id, file_size, sha256) VALUES ($id, $n, $p, $a, 0, '{Kind}', 1, $v, $f, $s, $h)",
            ("$id", id), ("$n", displayName), ("$p", canonicalPath), ("$a", JsonSerializer.Serialize(fixedArgs)),
            ("$v", identity.VolumeSerial), ("$f", identity.FileId), ("$s", identity.Size), ("$h", identity.Sha256)));
        tx.Commit();
        return id;
    }

    /// <summary>
    /// Allows or disallows launching/closing. Refused when the row changed since <paramref name="generation"/> was read.
    /// Disallowing also starts a new generation, so a call that resolved the app before cannot run even if the app is
    /// allowed again before that call reaches its start point. Returns the generation after the change.
    /// </summary>
    public long SetAllowed(string appId, long generation, bool allowed)
    {
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        var next = allowed ? generation : generation + 1;
        if (Execute(c, $"UPDATE registered_apps SET allowed = $a, generation = $n WHERE id = $id AND generation = $g AND kind = '{Kind}'",
            ("$a", allowed ? 1 : 0), ("$n", next), ("$id", appId), ("$g", generation)) == 0)
        {
            throw new InvalidOperationException("登録が変更されたか削除されました。再読み込みしてください。");
        }

        return next;
    }

    /// <summary>
    /// Approves a re-inspected executable at the same path and/or new fixed arguments: a new generation, and launching is
    /// disallowed until the user allows it again (approval is never inherited). Another path means another app: register
    /// it as a new id.
    /// </summary>
    public void Reapprove(string appId, long generation, string canonicalPath, IReadOnlyList<string> fixedArgs, ExecutableIdentity identity)
    {
        var stored = ListAll().FirstOrDefault(a => a.Id == appId)?.App
            ?? throw new InvalidOperationException("登録が見つからないか、旧形式のため再承認できません。削除して登録し直してください。");
        if (!string.Equals(stored.Path, canonicalPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("登録時と異なる場所の実行ファイルは再承認できません。別のアプリとして登録してください。");
        }

        Validate(canonicalPath, stored.DisplayName, fixedArgs, identity);
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        var changed = WriteUnique(() => Execute(c,
            $"UPDATE registered_apps SET fixed_args = $a, volume_serial = $v, file_id = $f, file_size = $s, sha256 = $h, generation = generation + 1, allowed = 0 WHERE id = $id AND generation = $g AND kind = '{Kind}'",
            ("$a", JsonSerializer.Serialize(fixedArgs)), ("$v", identity.VolumeSerial), ("$f", identity.FileId), ("$s", identity.Size),
            ("$h", identity.Sha256), ("$id", appId), ("$g", generation)));
        if (changed == 0)
        {
            throw new InvalidOperationException("登録が変更されたか削除されました。再読み込みしてください。");
        }
    }

    /// <summary>Removes the registration (also legacy rows). Ids are random and never reused.</summary>
    public void Delete(string appId)
    {
        using var update = gate.BeginUpdate();
        using var c = database.Open();
        Execute(c, "DELETE FROM registered_apps WHERE id = $id", ("$id", appId));
    }

    private static void Validate(string path, string displayName, IReadOnlyList<string> fixedArgs, ExecutableIdentity identity)
    {
        if (AppRules.ValidateDisplayName(displayName) is { } nameError)
        {
            throw new ArgumentException(nameError);
        }

        if (AppRules.ValidateFixedArgs(fixedArgs) is { } argsError)
        {
            throw new ArgumentException(argsError);
        }

        if (ExecutablePolicy.CheckPath(path) != ExecutableRefusal.None || !identity.IsWellFormed())
        {
            throw new ArgumentException("この実行ファイルは登録できません。");
        }
    }

    private static int WriteUnique(Func<int> write)
    {
        try
        {
            return write();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new ArgumentException("この実行ファイルは別の登録ですでに使われています。");
        }
    }

    private List<StoredApp> Read(string sql)
    {
        using var c = database.Open();
        using var cmd = Command(c, sql);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredApp>();
        while (r.Read())
        {
            var id = r.GetString(0);
            var name = r.GetString(1);
            var allowed = r.GetInt64(4) == 1;
            list.Add(new StoredApp(id, name, allowed, Parse(r, id, name, allowed)));
        }

        return list;
    }

    /// <summary>Null for anything not exactly in the current format (never repaired or enabled automatically).</summary>
    private static RegisteredApp? Parse(SqliteDataReader r, string id, string name, bool allowed)
    {
        if (r.GetValue(10) is not Kind || !AppRules.IsAppId(id) || AppRules.ValidateDisplayName(name) is not null
            || r.GetValue(2) is not string path || ExecutablePolicy.CheckPath(path) != ExecutableRefusal.None
            || r.GetValue(3) is not string argsJson || r.GetInt64(5) <= 0
            || r.GetValue(6) is not string volume || r.GetValue(7) is not string fileId || r.GetValue(8) is not long size || r.GetValue(9) is not string sha)
        {
            return null;
        }

        string[]? args;
        try
        {
            args = JsonSerializer.Deserialize<string[]>(argsJson);
        }
        catch (JsonException)
        {
            return null;
        }

        var identity = new ExecutableIdentity(volume, fileId, size, sha);
        return args is null || args.Any(a => a is null) || AppRules.ValidateFixedArgs(args) is not null || !identity.IsWellFormed()
            ? null
            : new RegisteredApp(id, name, path, args, allowed, r.GetInt64(5), identity);
    }

    private static object? ScalarTx(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = Command(c, sql);
        cmd.Transaction = tx;
        return cmd.ExecuteScalar();
    }
}
