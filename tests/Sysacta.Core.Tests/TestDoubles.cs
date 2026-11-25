using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Sysacta.Core.Auditing;
using Sysacta.Core.Devices;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;

namespace Sysacta.Core.Tests;

/// <summary>Mutable in-memory permission data. Every read reflects the latest change (like the SQLite store).</summary>
internal sealed class FakeAccessStore : IAccessStore, IDeviceAliases
{
    public ConcurrentDictionary<string, bool> EnabledTools { get; } = new();
    public ConcurrentDictionary<string, ClientRecord> Clients { get; } = new();
    public ConcurrentDictionary<string, string> Hashes { get; } = new();
    public ConcurrentDictionary<(string, string), bool> Grants { get; } = new();
    public ConcurrentDictionary<(string, string), string> Aliases { get; } = new();
    public List<(string, DateTimeOffset)> Uses { get; } = [];

    public bool IsToolEnabled(string toolName) => EnabledTools.GetValueOrDefault(toolName);

    public ClientRecord? GetClient(string clientId) => Clients.GetValueOrDefault(clientId);

    public (ClientRecord Client, string TokenHash)? FindClientByTokenHash(string tokenHash) =>
        Hashes.FirstOrDefault(kv => kv.Value == tokenHash) is { Key: not null } kv ? (Clients[kv.Key], kv.Value) : null;

    public bool IsClientToolAllowed(string clientId, string toolName) => Grants.GetValueOrDefault((clientId, toolName));

    public void RecordClientUse(string clientId, DateTimeOffset at)
    {
        lock (Uses)
        {
            Uses.Add((clientId, at));
        }
    }

    public string? Resolve(string category, string alias) => Aliases.GetValueOrDefault((category, alias));

    /// <summary>Adds an active client allowed to use <paramref name="tools"/> (and enables them globally).</summary>
    public (Caller Caller, string Token) AddClient(string id, params string[] tools)
    {
        var (token, hash) = ClientTokens.Generate();
        Clients[id] = new ClientRecord(id, id, DateTimeOffset.UnixEpoch, null, null, null);
        Hashes[id] = hash;
        foreach (var t in tools)
        {
            EnabledTools[t] = true;
            Grants[(id, t)] = true;
        }

        return (Caller.Client(id, id, "127.0.0.1", hash), token);
    }

    public void Revoke(string id) => Clients[id] = Clients[id] with { RevokedAt = DateTimeOffset.UnixEpoch };

    /// <summary>Replaces the token (old token hash no longer matches), like AccessStore.ReissueClient.</summary>
    public void Reissue(string id) => Hashes[id] = ClientTokens.Generate().Hash;
}

internal sealed class RecordingAuditLog : IAuditLog
{
    private readonly List<AuditEvent> _events = [];

    public bool Fail { get; set; }

    public IReadOnlyList<AuditEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void Write(AuditEvent auditEvent)
    {
        if (Fail)
        {
            throw new IOException("disk full");
        }

        lock (_events)
        {
            _events.Add(auditEvent);
        }
    }

    public AuditEvent Final(string requestId) => Events.Last(e => e.RequestId == requestId);
}

public sealed class EchoArgs
{
    [Range(0, 10)]
    public required int Value { get; init; }

    [StringLength(16)]
    public string? Secret { get; init; }
}

public sealed record EchoData(int Value);

/// <summary>Test-only read-only tool. Never registered in production (lives in the test assembly).</summary>
internal sealed class EchoTool(Func<EchoArgs, EchoData>? body = null) : SysactaTool<EchoArgs, EchoData>
{
    public int Calls;

    public override ToolDescriptor Descriptor { get; } = new("system_get_echo", ToolCategories.System, "Echo", "Echo", DefaultPolicy.On, ReadOnly: true, AuditedArguments: ["value"]);

    protected override Task<ToolOutcome<EchoData>> ExecuteAsync(EchoArgs args, ToolTarget target, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new ToolOutcome<EchoData>(body?.Invoke(args) ?? new EchoData(args.Value), Verification.NotApplicable));
    }
}

public sealed class GateArgs
{
    [StringLength(32, MinimumLength = 1)]
    public required string Resource { get; init; }
}

/// <summary>
/// Test-only state-changing tool that blocks in a non-cancellable "native" call until released,
/// recording how many executions overlap per resource.
/// </summary>
internal sealed class GateTool(TimeSpan? timeout = null) : SysactaTool<GateArgs, EchoData>
{
    private readonly ConcurrentDictionary<string, int> _active = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    public int MaxOverlapPerResource;
    public int Started;
    public int Completed;
    public readonly ConcurrentQueue<string> Entered = new();

    public override ToolDescriptor Descriptor { get; } = new("power_set_gate", ToolCategories.Power, "Gate", "Gate", DefaultPolicy.Off, ReadOnly: false, AuditedArguments: ["resource"], timeout ?? TimeSpan.FromSeconds(10));

    public SemaphoreSlim Gate(string resource) => _gates.GetOrAdd(resource, _ => new SemaphoreSlim(0));

    protected override ValueTask<ToolTarget> ResolveTargetAsync(GateArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ToolTarget([$"test:{args.Resource}"], args.Resource));

    protected override Task<ToolOutcome<EchoData>> ExecuteAsync(GateArgs args, ToolTarget target, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Started);
        Entered.Enqueue(args.Resource);
        var now = _active.AddOrUpdate(args.Resource, 1, (_, v) => v + 1);
        InterlockedMax(ref MaxOverlapPerResource, now);
        Gate(args.Resource).Wait(CancellationToken.None); // deliberately ignores cancellation, like a native call
        _active.AddOrUpdate(args.Resource, 0, (_, v) => v - 1);
        Interlocked.Increment(ref Completed);
        return Task.FromResult(new ToolOutcome<EchoData>(new EchoData(1), Verification.Verified));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

internal sealed class FakeAudioEndpoints : IAudioEndpoints
{
    public Dictionary<string, AudioEndpointState> Devices { get; } = new()
    {
        ["spk"] = new("spk", "Speakers", AudioDirection.Output, AudioDeviceState.Active, 0.5f, false),
        ["hp"] = new("hp", "Headphones", AudioDirection.Output, AudioDeviceState.Unplugged, null, null),
        ["mic"] = new("mic", "Microphone", AudioDirection.Input, AudioDeviceState.Active, 0.8f, true),
    };

    public string? DefaultOutput { get; set; } = "spk";
    public string? DefaultInput { get; set; } = "mic";
    public List<(string, float)> SetCalls { get; } = [];
    public Func<float, float> ApplyTransform { get; set; } = v => v;
    public Exception? SetFailure { get; set; }

    /// <summary>When set, reads fail after the first successful SetVolumeScalar (device removed during read-back).</summary>
    public bool FailReadAfterSet { get; set; }

    public string? GetDefaultEndpointId(AudioDirection direction) => direction == AudioDirection.Output ? DefaultOutput : DefaultInput;

    public AudioEndpointState GetEndpoint(string id)
    {
        lock (Devices)
        {
            if (FailReadAfterSet && SetCalls.Count > 0)
            {
                throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The audio device was removed or disabled.");
            }

            return Devices.TryGetValue(id, out var d) ? d : throw new ToolFailureException(ErrorCodes.TargetNotFound, "The specified audio device was not found.");
        }
    }

    public void SetVolumeScalar(string id, float scalar)
    {
        if (SetFailure is not null)
        {
            throw SetFailure;
        }

        lock (Devices)
        {
            SetCalls.Add((id, scalar));
            Devices[id] = Devices[id] with { VolumeScalar = ApplyTransform(scalar) };
        }
    }
}

internal static class TestJson
{
    public static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

internal sealed class Harness
{
    public FakeAccessStore Store { get; } = new();
    public RecordingAuditLog Audit { get; } = new();
    public ToolExecutor Executor { get; }

    public Harness(ExecutorLimits? limits = null, params SysactaTool[] tools)
        : this(_ => tools, limits)
    {
    }

    public Harness(Func<FakeAccessStore, IEnumerable<SysactaTool>> tools, ExecutorLimits? limits = null)
    {
        Executor = new ToolExecutor(new ToolRegistry(tools(Store)), new AccessPolicy(Store, TimeProvider.System), Audit, TimeProvider.System, limits);
    }
}
