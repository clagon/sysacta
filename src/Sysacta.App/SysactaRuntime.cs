using System.IO;
using System.Net;
using System.Reflection;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Infrastructure;
using Sysacta.Mcp;

namespace Sysacta.App;

/// <summary>
/// Composition root: creates the long-lived services once per process. UI code talks to these objects only;
/// state-changing tool calls from the UI go through <see cref="Executor"/> like MCP calls do.
/// </summary>
public sealed class SysactaRuntime : IAsyncDisposable
{
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);

    public SysactaRuntime(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        Database = new SysactaDatabase(Path.Combine(dataDirectory, "sysacta.db"));
        Database.Migrate();

        Access = new AccessStore(Database, _time);
        Audit = new AuditLog(Database);
        SettingsStore = new SettingsStore(Database, _time);
        (Settings, InvalidSettingKeys) = SettingsStore.Load();

        Registry = new ToolRegistry(ToolCatalog.Create(Access));
        Access.SyncTools(Registry.All.Select(t => t.Descriptor), Version);
        Executor = new ToolExecutor(Registry, new AccessPolicy(Access, _time), Audit, _time);
        Host = new McpServerHost(Executor, new TokenAuthenticator(Access, _time), Audit, _time, Version);
    }

    public static string Version { get; } =
        typeof(SysactaRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public string DataDirectory { get; }
    public SysactaDatabase Database { get; }
    public AccessStore Access { get; }
    public AuditLog Audit { get; }
    public SettingsStore SettingsStore { get; }
    public ToolRegistry Registry { get; }
    public ToolExecutor Executor { get; }
    public McpServerHost Host { get; }

    /// <summary>Current settings (replaced on save).</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>Setting keys that were unreadable at startup and were reset to defaults.</summary>
    public IReadOnlyList<string> InvalidSettingKeys { get; }

    /// <summary>URL a local client should use (for "all interfaces" binds the loopback address is shown).</summary>
    public string LocalEndpointUrl
    {
        get
        {
            var address = IPAddress.Parse(Settings.BindAddress);
            var host = address.Equals(IPAddress.Any) ? "127.0.0.1"
                : address.Equals(IPAddress.IPv6Any) ? "[::1]"
                : address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]"
                : address.ToString();
            return $"http://{host}:{Host.BoundPort ?? Settings.Port}{McpServerHost.EndpointPath}";
        }
    }

    public static ServerEndpointOptions EndpointOptions(AppSettings settings) => new()
    {
        BindAddress = IPAddress.Parse(settings.BindAddress),
        Port = settings.Port,
        AllowedHosts = settings.AllowedHosts,
        AllowedOrigins = settings.AllowedOrigins,
    };

    /// <summary>Starts the server with the saved settings. Returns a user-facing error or null.</summary>
    public async Task<string?> StartServerAsync()
    {
        try
        {
            await Host.StartAsync(EndpointOptions(Settings)).ConfigureAwait(false);
            return null;
        }
        catch (Exception)
        {
            return Host.LastError ?? "サーバーを起動できませんでした。";
        }
    }

    /// <summary>Saves settings and, if the server is running, restarts it (restoring the previous settings on failure).</summary>
    /// <param name="change">
    /// Applied to the settings current at the time of saving. Saves are serialized, so concurrent saves from different
    /// pages (e.g. theme and retention) never overwrite each other's fields.
    /// </param>
    public async Task<string?> SaveSettingsAsync(Func<AppSettings, AppSettings> change, bool restartIfRunning)
    {
        await _settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SaveSettingsCoreAsync(change(Settings), restartIfRunning).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private async Task<string?> SaveSettingsCoreAsync(AppSettings settings, bool restartIfRunning)
    {
        if (settings.Validate() is { } error)
        {
            return error;
        }

        await Task.Run(() => SettingsStore.Save(settings)).ConfigureAwait(false);
        var previous = Settings;
        Settings = settings;
        var networkChanged = previous.BindAddress != settings.BindAddress || previous.Port != settings.Port
            || !previous.AllowedHosts.SequenceEqual(settings.AllowedHosts) || !previous.AllowedOrigins.SequenceEqual(settings.AllowedOrigins);
        if (restartIfRunning && networkChanged && Host.State == ServerState.Running)
        {
            var applyError = await Host.ApplyAsync(EndpointOptions(settings)).ConfigureAwait(false);
            if (applyError is not null)
            {
                // Keep the stored settings consistent with what the server actually runs with.
                await Task.Run(() => SettingsStore.Save(previous)).ConfigureAwait(false);
                Settings = previous;
            }

            return applyError;
        }

        return null;
    }

    public async ValueTask DisposeAsync() => await Host.DisposeAsync().ConfigureAwait(false);
}
