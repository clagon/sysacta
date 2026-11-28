using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Sysacta.Infrastructure;

/// <summary>
/// SQLite database file with forward-only migrations tracked in <c>PRAGMA user_version</c>.
/// Each connection is opened per operation (pooled). Default location: %LOCALAPPDATA%\Sysacta\sysacta.db, which is
/// protected by the per-user profile ACL.
/// </summary>
public sealed class SysactaDatabase
{
    /// <summary>
    /// Append-only list. Never edit a shipped migration; add a new entry instead. Index + 1 = schema version.
    /// </summary>
    internal static readonly IReadOnlyList<string> Migrations =
    [
        // v1: Phase 0 schema (spec §11).
        """
        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL);
        CREATE TABLE tools (
            tool_name TEXT PRIMARY KEY,
            enabled INTEGER NOT NULL,
            first_seen_version TEXT NOT NULL);
        CREATE TABLE clients (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            token_hash TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,
            expires_at TEXT,
            revoked_at TEXT,
            last_used_at TEXT);
        CREATE TABLE client_permissions (
            client_id TEXT NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
            tool_name TEXT NOT NULL,
            allowed INTEGER NOT NULL,
            PRIMARY KEY (client_id, tool_name));
        CREATE TABLE device_aliases (
            category TEXT NOT NULL,
            device_id TEXT NOT NULL,
            alias TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (category, alias));
        CREATE TABLE registered_apps (
            id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            launch_target TEXT NOT NULL,
            fixed_args TEXT,
            allowed INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE audit_events (
            request_id TEXT PRIMARY KEY,
            timestamp_utc TEXT NOT NULL,
            client_id TEXT,
            client_name TEXT,
            tool_name TEXT,
            source TEXT NOT NULL,
            sanitized_args TEXT,
            result TEXT NOT NULL,
            error_code TEXT,
            duration_ms INTEGER,
            remote_address TEXT);
        CREATE INDEX ix_audit_events_timestamp ON audit_events (timestamp_utc);
        """,

        // v2: audit storage limits (SEC-06). size_bytes is the logical size of a record (AuditLog.SizeOf: 128 + the
        // UTF-8 bytes of its text columns); audit_usage keeps the totals in step through triggers, so checking a limit
        // never scans the table. aggregate_count marks records that count rejections not recorded individually.
        """
        ALTER TABLE audit_events ADD COLUMN aggregate_count INTEGER;
        ALTER TABLE audit_events ADD COLUMN size_bytes INTEGER NOT NULL DEFAULT 0;
        UPDATE audit_events SET size_bytes = 128
            + length(CAST(request_id AS BLOB)) + length(CAST(timestamp_utc AS BLOB)) + length(CAST(source AS BLOB)) + length(CAST(result AS BLOB))
            + COALESCE(length(CAST(client_id AS BLOB)), 0) + COALESCE(length(CAST(client_name AS BLOB)), 0)
            + COALESCE(length(CAST(tool_name AS BLOB)), 0) + COALESCE(length(CAST(sanitized_args AS BLOB)), 0)
            + COALESCE(length(CAST(error_code AS BLOB)), 0) + COALESCE(length(CAST(remote_address AS BLOB)), 0);
        CREATE INDEX ix_audit_events_source_timestamp ON audit_events (source, timestamp_utc);
        CREATE TABLE audit_usage (
            scope TEXT PRIMARY KEY,
            row_count INTEGER NOT NULL,
            byte_count INTEGER NOT NULL,
            evicted_count INTEGER NOT NULL DEFAULT 0,
            last_evicted_at TEXT);
        INSERT INTO audit_usage (scope, row_count, byte_count) SELECT 'all', COUNT(*), COALESCE(SUM(size_bytes), 0) FROM audit_events;
        INSERT INTO audit_usage (scope, row_count, byte_count) SELECT 'http', COUNT(*), COALESCE(SUM(size_bytes), 0) FROM audit_events WHERE source = 'http';
        CREATE TRIGGER audit_events_usage_insert AFTER INSERT ON audit_events BEGIN
            UPDATE audit_usage SET row_count = row_count + 1, byte_count = byte_count + NEW.size_bytes
            WHERE scope = 'all' OR (scope = 'http' AND NEW.source = 'http');
        END;
        CREATE TRIGGER audit_events_usage_delete AFTER DELETE ON audit_events BEGIN
            UPDATE audit_usage SET row_count = row_count - 1, byte_count = byte_count - OLD.size_bytes
            WHERE scope = 'all' OR (scope = 'http' AND OLD.source = 'http');
        END;
        CREATE TRIGGER audit_events_usage_update AFTER UPDATE OF size_bytes, source ON audit_events BEGIN
            UPDATE audit_usage SET row_count = row_count - 1, byte_count = byte_count - OLD.size_bytes
            WHERE scope = 'all' OR (scope = 'http' AND OLD.source = 'http');
            UPDATE audit_usage SET row_count = row_count + 1, byte_count = byte_count + NEW.size_bytes
            WHERE scope = 'all' OR (scope = 'http' AND NEW.source = 'http');
        END;
        """,
    ];

    private readonly string _connectionString;

    public SysactaDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>%LOCALAPPDATA%\Sysacta</summary>
    public static string DefaultDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sysacta");

    /// <summary>
    /// The WAL file is cut back to this size whenever a checkpoint resets it (SEC-06). Without it the file keeps its
    /// largest size, e.g. after growing while a long read transaction prevented checkpoints. Not persistent in SQLite,
    /// so it is set on every connection.
    /// </summary>
    public const long WalSizeLimitBytes = 4L * 1024 * 1024;

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, $"PRAGMA journal_size_limit = {WalSizeLimitBytes};");
        // Every commit is synced to disk before it returns (WAL + FULL), so an intent record written before a power
        // transition survives the shutdown it may cause. Set explicitly instead of relying on the build default.
        Execute(connection, "PRAGMA synchronous = FULL;");
        return connection;
    }

    /// <summary>Applies pending migrations. Returns the schema version before migrating (0 = new database).</summary>
    public int Migrate() => Migrate(Migrations);

    internal int Migrate(IReadOnlyList<string> migrations)
    {
        using var connection = Open();
        var current = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (current > migrations.Count)
        {
            throw new InvalidOperationException(
                $"The database schema (v{current}) is newer than this version of Sysacta supports (v{migrations.Count}). Use a newer version.");
        }

        if (current == migrations.Count)
        {
            return current;
        }

        if (current > 0)
        {
            // Keep a copy of the pre-migration database next to it.
            using var backup = new SqliteConnection($"Data Source={Path}.v{current}.bak;Pooling=False");
            connection.BackupDatabase(backup);
        }

        Execute(connection, "PRAGMA journal_mode = WAL;");
        for (var version = current; version < migrations.Count; version++)
        {
            using var transaction = connection.BeginTransaction();
            ExecuteTx(connection, transaction, migrations[version]);
            ExecuteTx(connection, transaction, $"PRAGMA user_version = {version + 1};");
            transaction.Commit();
        }

        return current;
    }

    internal static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    internal static int Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        ExecuteTx(connection, null, sql, parameters);

    internal static int ExecuteTx(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.Transaction = transaction;
        return command.ExecuteNonQuery();
    }

    internal static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Fixed-width, lexicographically sortable UTC timestamp.</summary>
    internal static string ToDb(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static string? ToDb(DateTimeOffset? value) => value is { } v ? ToDb(v) : null;

    internal static DateTimeOffset FromDb(string value) =>
        DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    internal static DateTimeOffset? FromDbNullable(object value) => value is string s ? FromDb(s) : null;
}
