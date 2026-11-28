using System.IO;
using System.Net;
using System.Reflection;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Core.Tools.Display;
using Sysacta.Infrastructure;
using Sysacta.Mcp;
using Sysacta.Windows.Audio;
using Sysacta.Windows.Display;
using Sysacta.Windows.Media;

namespace Sysacta.App;

/// <summary>
/// Composition root: creates the long-lived services once per process. UI code talks to these objects only;
/// state-changing tool calls from the UI go through <see cref="Executor"/> like MCP calls do.
/// </summary>
public sealed class SysactaRuntime : IAsyncDisposable
{
    private readonly TimeProvider _time = TimeProvider.System;
    /// <summary>
    /// Serializes server start/stop and settings saves (SEC-04): a start reads the settings inside the gate, so a save
    /// can never complete while a start with the old settings is half-way and then be skipped.
    /// </summary>
    private readonly SemaphoreSlim _serverGate = new(1, 1);

    public SysactaRuntime(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        Database = new SysactaDatabase(Path.Combine(dataDirectory, "sysacta.db"));
        Database.Migrate();

        // One gate shared by the permission store (updates) and the executor (start of state changes), SEC-01.
        var gate = new AuthorizationGate();
        Access = new AccessStore(Database, _time, gate);
        Audit = new AuditLog(Database);
        // Intents left by a previous process that ended mid-operation become "unknown" (SEC-06). Nothing can run yet.
        Audit.RecoverInterrupted();
        SettingsStore = new SettingsStore(Database, _time);
        (Settings, InvalidSettingKeys) = SettingsStore.Load();

        Registry = new ToolRegistry(ToolCatalog.Create(Access, AudioEndpoints, Displays, DisplayModeRecovery, MediaSessions));
        Access.SyncTools(Registry.All.Select(t => t.Descriptor), Version);
        Executor = new ToolExecutor(Registry, new AccessPolicy(Access, _time), Audit, _time, gate);
        Host = new McpServerHost(Executor, new TokenAuthenticator(Access, _time), Audit, _time, Version);
    }

    public static string Version { get; } =
        typeof(SysactaRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public string DataDirectory { get; }
    public SysactaDatabase Database { get; }
    public AccessStore Access { get; }
    /// <summary>Shared by the audio tools and the device page.</summary>
    public IAudioEndpoints AudioEndpoints { get; } = new CoreAudioEndpoints();
    /// <summary>Shared by the display tools and the device page.</summary>
    public IDisplays Displays { get; } = new WindowsDisplays();
    /// <summary>The latest display mode change, restorable from the tray menu and the device page (app lifetime only).</summary>
    public DisplayModeRecovery DisplayModeRecovery { get; } = new();
    private WindowsMediaSessions MediaSessions { get; } = new();
    public AuditLog Audit { get; }
    public SettingsStore SettingsStore { get; }
    public ToolRegistry Registry { get; }
    public ToolExecutor Executor { get; }
    public McpServerHost Host { get; }

    /// <summary>Restores the latest display mode change through the executor, as the local console (locks, audit, timeout).</summary>
    public Task<ToolResult> RestoreDisplayModeAsync() =>
        Task.Run(() => Executor.ExecuteAsync(Caller.LocalConsole, "display_restore_mode", null, CancellationToken.None));

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
        await _serverGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Host.StartAsync(EndpointOptions(Settings)).ConfigureAwait(false);
            return null;
        }
        catch (Exception)
        {
            return Host.LastError ?? "サーバーを起動できませんでした。";
        }
        finally
        {
            _serverGate.Release();
        }
    }

    public async Task StopServerAsync()
    {
        await _serverGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Host.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _serverGate.Release();
        }
    }

    /// <summary>Saves settings and, if the server is running, restarts it (restoring the previous settings on failure).</summary>
    /// <param name="change">
    /// Applied to the settings current at the time of saving. Saves are serialized, so concurrent saves from different
    /// pages (e.g. theme and retention) never overwrite each other's fields.
    /// </param>
    public async Task<string?> SaveSettingsAsync(Func<AppSettings, AppSettings> change, bool restartIfRunning)
    {
        await _serverGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SaveSettingsCoreAsync(change(Settings), restartIfRunning).ConfigureAwait(false);
        }
        finally
        {
            _serverGate.Release();
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

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync().ConfigureAwait(false);
        if (Executor.InFlight == 0)
        {
            await Task.Run(MediaSessions.Dispose).ConfigureAwait(false);
        }
        else
        {
            // Timed-out native calls retain their target until they really complete.
            _ = DisposeMediaWhenIdleAsync();
        }
    }

    private async Task DisposeMediaWhenIdleAsync()
    {
        while (Executor.InFlight != 0)
        {
            await Task.Delay(250).ConfigureAwait(false);
        }
        MediaSessions.Dispose();
    }
}
