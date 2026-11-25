using System.Globalization;
using System.Net;
using static Sysacta.Infrastructure.SysactaDatabase;

namespace Sysacta.Infrastructure;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>User settings. Only changeable from the console (never via MCP).</summary>
public sealed record AppSettings
{
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8765;
    public bool StartServerOnLaunch { get; init; } = true;
    /// <summary>Extra Host header values to accept, e.g. the reverse-proxy name (host or host:port).</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];
    /// <summary>Browser origins to accept (scheme://host[:port]). Empty = reject every request carrying an Origin header.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
    /// <summary>Optional external URL shown in connection settings (e.g. https://pc.tailnet.ts.net/mcp).</summary>
    public string? ExternalUrl { get; init; }
    public int AuditRetentionDays { get; init; } = AuditLog.DefaultRetentionDays;
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>Returns null when valid, otherwise a user-facing reason.</summary>
    public string? Validate()
    {
        if (!IPAddress.TryParse(BindAddress, out _))
        {
            return "Bind address must be an IP address.";
        }

        if (Port is < 1 or > 65535)
        {
            return "Port must be between 1 and 65535.";
        }

        if (AuditRetentionDays is < 1 or > 3650)
        {
            return "Retention must be between 1 and 3650 days.";
        }

        if (AllowedHosts.Any(h => h.Length is 0 or > 255 || h.Any(ch => char.IsWhiteSpace(ch) || ch is '/' or '*')))
        {
            return "Allowed hosts must be plain host names (no wildcards, paths or spaces).";
        }

        if (AllowedOrigins.Any(o => !Uri.TryCreate(o, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.AbsolutePath != "/" || o.Contains('*', StringComparison.Ordinal)))
        {
            return "Allowed origins must be http(s)://host[:port] without wildcards or paths.";
        }

        if (ExternalUrl is { } url && (!Uri.TryCreate(url, UriKind.Absolute, out var ext) || ext.Scheme is not ("http" or "https")))
        {
            return "External URL must be an absolute http(s) URL.";
        }

        return null;
    }
}

/// <summary>Key/value persistence of <see cref="AppSettings"/>. Unreadable values fall back to defaults and are reported.</summary>
public sealed class SettingsStore(SysactaDatabase database, TimeProvider time)
{
    public (AppSettings Settings, IReadOnlyList<string> InvalidKeys) Load()
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var c = database.Open())
        using (var cmd = Command(c, "SELECT key, value FROM settings"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                raw[r.GetString(0)] = r.GetString(1);
            }
        }

        var defaults = new AppSettings();
        var invalid = new List<string>();
        T Get<T>(string key, T fallback, Func<string, (bool Ok, T Value)> parse)
        {
            if (!raw.TryGetValue(key, out var text))
            {
                return fallback;
            }

            var (ok, value) = parse(text);
            if (!ok)
            {
                invalid.Add(key);
                return fallback;
            }

            return value;
        }

        static (bool, int) Int(string s) => (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v), v);
        static (bool, bool) Bool(string s) => (bool.TryParse(s, out var v), v);
        static (bool, IReadOnlyList<string>) List(string s) => (true, s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var settings = new AppSettings
        {
            BindAddress = Get("bind_address", defaults.BindAddress, s => (true, s)),
            Port = Get("port", defaults.Port, Int),
            StartServerOnLaunch = Get("start_server_on_launch", defaults.StartServerOnLaunch, Bool),
            AllowedHosts = Get<IReadOnlyList<string>>("allowed_hosts", defaults.AllowedHosts, List),
            AllowedOrigins = Get<IReadOnlyList<string>>("allowed_origins", defaults.AllowedOrigins, List),
            ExternalUrl = raw.TryGetValue("external_url", out var ext) && ext.Length > 0 ? ext : null,
            AuditRetentionDays = Get("audit_retention_days", defaults.AuditRetentionDays, Int),
            Theme = Get("theme", defaults.Theme, s => (Enum.TryParse<ThemePreference>(s, out var t) && Enum.IsDefined(t), t)),
        };

        if (settings.Validate() is not null)
        {
            // A combination is invalid (e.g. hand-edited database). Fall back to safe defaults for the network part.
            invalid.Add("network");
            settings = settings with { BindAddress = defaults.BindAddress, Port = defaults.Port, AllowedHosts = [], AllowedOrigins = [], ExternalUrl = null };
            if (settings.Validate() is not null)
            {
                settings = defaults;
            }
        }

        return (settings, invalid);
    }

    public void Save(AppSettings settings)
    {
        if (settings.Validate() is { } error)
        {
            throw new ArgumentException(error, nameof(settings));
        }

        var values = new Dictionary<string, string>
        {
            ["bind_address"] = settings.BindAddress,
            ["port"] = settings.Port.ToString(CultureInfo.InvariantCulture),
            ["start_server_on_launch"] = settings.StartServerOnLaunch.ToString(),
            ["allowed_hosts"] = string.Join('\n', settings.AllowedHosts),
            ["allowed_origins"] = string.Join('\n', settings.AllowedOrigins),
            ["external_url"] = settings.ExternalUrl ?? "",
            ["audit_retention_days"] = settings.AuditRetentionDays.ToString(CultureInfo.InvariantCulture),
            ["theme"] = settings.Theme.ToString(),
        };

        var now = ToDb(time.GetUtcNow());
        using var c = database.Open();
        using var tx = c.BeginTransaction();
        foreach (var (key, value) in values)
        {
            ExecuteTx(c, tx, "INSERT INTO settings (key, value, updated_at) VALUES ($k, $v, $t) ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
                ("$k", key), ("$v", value), ("$t", now));
        }

        tx.Commit();
    }
}
