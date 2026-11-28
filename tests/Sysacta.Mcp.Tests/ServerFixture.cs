using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Core.Tools.Display;
using Sysacta.Core.Tools.Media;
using Sysacta.Core.Tools.Power;
using Sysacta.Infrastructure;
using Sysacta.Tests;

namespace Sysacta.Mcp.Tests;

/// <summary>A real Kestrel MCP host on a random loopback port, backed by a temporary SQLite database.</summary>
public sealed class ServerFixture : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sysacta-mcp-").FullName;

    public SysactaDatabase Database { get; }
    public AuthorizationGate Gate { get; }
    public AccessStore Store { get; }
    public AuditLog Audit { get; }
    public ToolExecutor Executor { get; }
    public McpServerHost Host { get; }
    public FakeAudioEndpoints AudioDevice { get; } = new();
    public FakeAudioSessions Sessions { get; } = new();
    public FakeDefaultEndpointWriter DefaultWriter { get; }
    public FakeDisplays Displays { get; } = new();
    public DisplayModeRecovery Recovery { get; } = new();
    public BlockingTool Blocking { get; } = new();
    public FakeMediaSessions Media { get; } = new();
    public FakePower Power { get; } = new();
    public PowerTransitionGate PowerGate { get; } = new();

    /// <summary>Clock of the HTTP layer (budgets, last-used time). The executor and the store use the system clock.</summary>
    public ManualTime Time { get; } = new();

    /// <summary>What the authentication layer sees of the store; counts its database calls.</summary>
    public CountingStore Counted { get; }

    private ServerFixture(AuditLimits? limits, bool includeMedia, bool includePower)
    {
        Database = new SysactaDatabase(Path.Combine(_dir, "sysacta.db"));
        Database.Migrate();
        Gate = new AuthorizationGate();
        Store = new AccessStore(Database, TimeProvider.System, Gate);
        Counted = new CountingStore(Store);
        Audit = new AuditLog(Database, limits);
        AudioDevice.Devices["spk"] = AudioDevice.Devices["spk"] with { VolumeScalar = 0.4f };
        DefaultWriter = new FakeDefaultEndpointWriter(AudioDevice);
        var registry = new ToolRegistry(
        [
            new AudioGetStateTool(AudioDevice, Store),
            new AudioSetVolumeTool(AudioDevice, Store),
            new AudioListDevicesTool(AudioDevice, Store),
            new AudioSetMuteTool(AudioDevice, Store),
            new AudioListSessionsTool(AudioDevice, Store, Sessions),
            new AudioSetAppVolumeTool(AudioDevice, Sessions),
            new AudioSetAppMuteTool(AudioDevice, Sessions),
            new AudioSetDefaultDeviceTool(AudioDevice, Store, DefaultWriter),
            new DisplayListDevicesTool(Displays, Store),
            new DisplayGetStateTool(Displays, Store),
            new DisplaySetBrightnessTool(Displays, Store),
            new DisplaySetContrastTool(Displays, Store),
            new DisplayListModesTool(Displays, Store),
            new DisplaySetModeTool(Displays, Store, Recovery),
            new DisplaySetPowerTool(Displays, Store),
            new DisplaySetInputTool(Displays, Store),
            new DisplayRestoreModeTool(Displays, Recovery),
            .. includeMedia ? new SysactaTool[] { new MediaListSessionsTool(Media), new MediaGetStateTool(Media),
                new MediaPlayTool(Media), new MediaPauseTool(Media), new MediaStopTool(Media),
                new MediaNextTrackTool(Media), new MediaPreviousTrackTool(Media), new MediaSeekTool(Media) } : [],
            .. includePower ? new SysactaTool[] { new PowerGetStateTool(Power), new PowerListPlansTool(Power), new PowerSetPlanTool(Power),
                new PowerLockTool(Power), new PowerSleepTool(Power), new PowerShutdownTool(Power), new PowerRestartTool(Power),
                new PowerReleaseGateTool(PowerGate) } : [],
            Blocking,
        ]);
        Store.SyncTools(registry.All.Select(t => t.Descriptor), "test");
        Store.SetToolEnabled(Blocking.Name, true);
        Executor = new ToolExecutor(registry, new AccessPolicy(Store, TimeProvider.System), Audit, TimeProvider.System, Gate, power: PowerGate);
        Host = new McpServerHost(Executor, new TokenAuthenticator(Counted, Time), Audit, Time, "test");
    }

    public static async Task<ServerFixture> StartAsync(ServerEndpointOptions? options = null, AuditLimits? limits = null, bool includeMedia = false, bool includePower = false)
    {
        var fixture = new ServerFixture(limits, includeMedia, includePower);
        await fixture.Host.StartAsync(options ?? new ServerEndpointOptions { Port = 0 }, TestContext.Current.CancellationToken);
        return fixture;
    }

    public Uri Endpoint => new($"http://127.0.0.1:{Host.BoundPort}/mcp");

    public IssuedClient Issue(params string[] tools) => Store.IssueClient("agent-" + Guid.NewGuid().ToString("N")[..6], null, tools);

    public HttpClient Http(string? token = null)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Host.BoundPort}") };
        if (token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return http;
    }

    public Task<McpClient> ClientAsync(string token, string? protocolVersion = null) =>
        McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            }),
            new McpClientOptions { ProtocolVersion = protocolVersion },
            cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>A minimal JSON-RPC POST as a non-SDK client would send it.</summary>
    public static HttpRequestMessage JsonRpc(string body, string protocolVersion = "2025-06-18")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", protocolVersion);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gate in Blocking.Gates.Values)
        {
            gate.Release(100);
        }

        await Host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// The system clock plus an offset that tests move forward. Tests finish far within one budget window (a minute), so
/// a window rolls over only when a test advances the clock.
/// </summary>
public sealed class ManualTime : TimeProvider
{
    private TimeSpan _offset;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;

    public void Advance(TimeSpan by) => _offset += by;
}

/// <summary>
/// Counts the authentication layer's database calls. <see cref="ForgetLastUse"/> hides the stored last-used time, so
/// every request that reaches <see cref="TokenAuthenticator.RecordUse"/> writes and can be counted.
/// </summary>
public sealed class CountingStore(IAccessStore inner) : IAccessStore
{
    private int _lookups;
    private int _uses;

    public int Lookups => Volatile.Read(ref _lookups);
    public int Uses => Volatile.Read(ref _uses);
    public bool ForgetLastUse { get; set; }

    /// <summary>When set, token lookups wait for it (to hold requests inside the authentication stage).</summary>
    public SemaphoreSlim? LookupGate { get; set; }

    public bool IsToolEnabled(string toolName) => inner.IsToolEnabled(toolName);
    public ClientRecord? GetClient(string clientId) => inner.GetClient(clientId);
    public bool IsClientToolAllowed(string clientId, string toolName) => inner.IsClientToolAllowed(clientId, toolName);

    public (ClientRecord Client, string TokenHash)? FindClientByTokenHash(string tokenHash)
    {
        Interlocked.Increment(ref _lookups);
        LookupGate?.Wait(TimeSpan.FromSeconds(10));
        var found = inner.FindClientByTokenHash(tokenHash);
        return found is { } f && ForgetLastUse ? (f.Client with { LastUsedAt = null }, f.TokenHash) : found;
    }

    public void RecordClientUse(string clientId, DateTimeOffset at)
    {
        Interlocked.Increment(ref _uses);
        inner.RecordClientUse(clientId, at);
    }
}

public sealed class BlockArgs
{
    [StringLength(16, MinimumLength = 1)]
    public required string Resource { get; init; }
}

public sealed record BlockData(bool Done);

/// <summary>Test-only state-changing tool that blocks until released. Exists only in this test assembly.</summary>
public sealed class BlockingTool : SysactaTool<BlockArgs, BlockData>
{
    public ConcurrentDictionary<string, SemaphoreSlim> Gates { get; } = new();
    private int _started;

    public int Started => Volatile.Read(ref _started);

    public override ToolDescriptor Descriptor { get; } = new("system_set_block", ToolCategories.System, "Block", "Test only", DefaultPolicy.Off, ReadOnly: false, AuditedArguments: [], TimeSpan.FromSeconds(20));

    protected override ValueTask<ToolTarget> ResolveTargetAsync(BlockArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ToolTarget([$"test:{args.Resource}"], args.Resource));

    protected override Task<ToolOutcome<BlockData>> ExecuteAsync(BlockArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        execution.BeginSideEffect();
        Interlocked.Increment(ref _started);
        Gates.GetOrAdd(args.Resource, _ => new SemaphoreSlim(0)).Wait(CancellationToken.None);
        return Task.FromResult(new ToolOutcome<BlockData>(new BlockData(true), Verification.Verified));
    }
}

internal static class Wait
{
    public static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
