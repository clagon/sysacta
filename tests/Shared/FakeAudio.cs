using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;

namespace Sysacta.Tests;

/// <summary>In-memory Core Audio endpoints, shared by Core.Tests and Mcp.Tests.</summary>
public sealed class FakeAudioEndpoints : IAudioEndpoints
{
    // Case-insensitive like a lookup that accepts several spellings of one endpoint; GetEndpoint returns the canonical id.
    public Dictionary<string, AudioEndpointState> Devices { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spk"] = new("spk", "Speakers", AudioDirection.Output, AudioDeviceState.Active, 0.5f, false),
        ["hp"] = new("hp", "Headphones", AudioDirection.Output, AudioDeviceState.Unplugged, null, null),
        ["mic"] = new("mic", "Microphone", AudioDirection.Input, AudioDeviceState.Active, 0.8f, true),
    };

    public string? DefaultOutput { get; set; } = "spk";
    public string? DefaultInput { get; set; } = "mic";

    /// <summary>Defaults of non-multimedia roles; missing entries follow the multimedia default.</summary>
    public Dictionary<(AudioDirection, AudioRole), string?> RoleDefaults { get; } = [];

    public List<(string, float)> SetCalls { get; } = [];
    public List<(string, bool)> MuteCalls { get; } = [];

    /// <summary>Calls that changed the (fake) device.</summary>
    public int Writes
    {
        get
        {
            lock (Devices)
            {
                return SetCalls.Count + MuteCalls.Count;
            }
        }
    }

    public Func<float, float> ApplyTransform { get; set; } = v => v;
    public Func<bool, bool> ApplyMute { get; set; } = m => m;
    public Exception? SetFailure { get; set; }
    public Exception? ListFailure { get; set; }

    /// <summary>Ids whose details fail during enumeration.</summary>
    public HashSet<string> BrokenDevices { get; } = [];

    /// <summary>Endpoints counted by the enumeration that could not be read at all.</summary>
    public int UnreadableDevices { get; set; }

    /// <summary>When set, reads fail after the first successful write (device removed during read-back).</summary>
    public bool FailReadAfterSet { get; set; }

    /// <summary>When set, GetDefaultEndpointId throws OS_API_ERROR.</summary>
    public Exception? DefaultReadFailure { get; set; }

    /// <summary>Runs on every endpoint read (e.g. to change permissions just before a write).</summary>
    public Action? OnRead { get; set; }

    /// <summary>Runs inside every write, before it is applied.</summary>
    public Action<string>? OnWrite { get; set; }

    public string? GetDefaultEndpointId(AudioDirection direction, AudioRole role = AudioRole.Multimedia)
    {
        if (DefaultReadFailure is not null)
        {
            throw DefaultReadFailure;
        }

        lock (Devices)
        {
            return RoleDefaults.TryGetValue((direction, role), out var id) ? id : direction == AudioDirection.Output ? DefaultOutput : DefaultInput;
        }
    }

    public AudioEndpointState GetEndpoint(string id)
    {
        OnRead?.Invoke();
        lock (Devices)
        {
            if (FailReadAfterSet && Writes > 0)
            {
                throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The audio device was removed or disabled.");
            }

            return Devices.TryGetValue(id, out var d) ? d : throw new ToolFailureException(ErrorCodes.TargetNotFound, "The specified audio device was not found.");
        }
    }

    public AudioEndpointList ListEndpoints(AudioDirection? direction, int max)
    {
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        lock (Devices)
        {
            var all = Devices.Values.Where(d => direction is null || d.Direction == direction).OrderBy(d => d.State).ThenBy(d => d.Direction).ToList();
            AudioEndpointEntry[] items =
            [
                .. all.Take(max).Select(d => BrokenDevices.Contains(d.Id)
                    ? new AudioEndpointEntry(d with { VolumeScalar = null, IsMuted = null }, ErrorCodes.OsApiError)
                    : new AudioEndpointEntry(d)),
            ];
            return new AudioEndpointList(items, all.Count + UnreadableDevices, UnreadableDevices);
        }
    }

    public void SetVolumeScalar(string id, float scalar)
    {
        Before(id);
        lock (Devices)
        {
            SetCalls.Add((id, scalar));
            Devices[id] = Devices[id] with { VolumeScalar = ApplyTransform(scalar) };
        }
    }

    public void SetMute(string id, bool muted)
    {
        Before(id);
        lock (Devices)
        {
            MuteCalls.Add((id, muted));
            Devices[id] = Devices[id] with { IsMuted = ApplyMute(muted) };
        }
    }

    private void Before(string id)
    {
        if (SetFailure is not null)
        {
            throw SetFailure;
        }

        OnWrite?.Invoke(id);
    }
}

/// <summary>In-memory render sessions per device.</summary>
public sealed class FakeAudioSessions : IAudioSessions
{
    /// <summary>Instance identifiers look like the real ones: they contain an executable path.</summary>
    public const string Player = @"spk|\Device\HarddiskVolume3\Users\someone\player.exe%b{0}|1%b100";

    public const string PlayerSecond = @"spk|\Device\HarddiskVolume3\Users\someone\player.exe%b{0}|2%b100";

    private readonly object _gate = new();

    public Dictionary<string, List<AudioSessionState>> Devices { get; } = new()
    {
        ["spk"] =
        [
            new(Player, "player", 100, AudioSessionStatus.Active, 0.6f, false, false),
            new(PlayerSecond, "player", 100, AudioSessionStatus.Inactive, 0.2f, false, false),
        ],
    };

    /// <summary>(device, instance, "volume:x" or "mute:x") of every write.</summary>
    public List<(string Device, string Instance, string Change)> Writes { get; } = [];

    public HashSet<string> BrokenDevices { get; } = [];
    public int UnreadableSessions { get; set; }
    public Exception? SetFailure { get; set; }
    public bool FailReadAfterSet { get; set; }
    public bool ExpireOnSet { get; set; }
    public bool IgnoreWrites { get; set; }

    /// <summary>Runs on every read of an open session (e.g. to change permissions just before a write).</summary>
    public Action? OnRead { get; set; }

    /// <summary>Runs inside every write, before it is applied.</summary>
    public Action<string>? OnWrite { get; set; }

    /// <summary>Runs before a session is opened (e.g. the session ends between resolution and execution).</summary>
    public Action? OnOpen { get; set; }

    private int _openHandles;

    public int OpenHandles => Volatile.Read(ref _openHandles);

    public AudioSessionList List(string deviceId, int max)
    {
        lock (_gate)
        {
            if (BrokenDevices.Contains(deviceId))
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API returned an error.");
            }

            var all = Devices.GetValueOrDefault(deviceId) ?? [];
            return new AudioSessionList([.. all.Take(max)], all.Count + UnreadableSessions, UnreadableSessions);
        }
    }

    public IAudioSessionHandle? Open(string deviceId, string instanceId)
    {
        OnOpen?.Invoke();
        lock (_gate)
        {
            var found = (Devices.GetValueOrDefault(deviceId) ?? []).Where(s => s.InstanceId == instanceId && s.Status != AudioSessionStatus.Expired).ToList();
            if (found.Count == 0)
            {
                return null;
            }

            Interlocked.Increment(ref _openHandles);
            return new Handle(this, deviceId, instanceId);
        }
    }

    private sealed class Handle(FakeAudioSessions owner, string deviceId, string instanceId) : IAudioSessionHandle
    {
        private bool _written;

        public AudioSessionState Read()
        {
            owner.OnRead?.Invoke();
            lock (owner._gate)
            {
                if (_written && owner.FailReadAfterSet)
                {
                    throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API returned an error.");
                }

                return owner.Devices[deviceId].First(s => s.InstanceId == instanceId);
            }
        }

        public void SetVolumeScalar(float scalar) => Write($"volume:{AudioTarget.ToLevel(scalar)}", s => s with { VolumeScalar = scalar });

        public void SetMute(bool muted) => Write($"mute:{muted}", s => s with { IsMuted = muted });

        private void Write(string change, Func<AudioSessionState, AudioSessionState> apply)
        {
            if (owner.SetFailure is not null)
            {
                throw owner.SetFailure;
            }

            owner.OnWrite?.Invoke(instanceId);
            lock (owner._gate)
            {
                owner.Writes.Add((deviceId, instanceId, change));
                _written = true;
                var list = owner.Devices[deviceId];
                var index = list.FindIndex(s => s.InstanceId == instanceId);
                var next = owner.IgnoreWrites ? list[index] : apply(list[index]);
                list[index] = owner.ExpireOnSet ? next with { Status = AudioSessionStatus.Expired } : next;
            }
        }

        public void Dispose() => Interlocked.Decrement(ref owner._openHandles);
    }
}

/// <summary>In-memory writer for <see cref="AudioSetDefaultDeviceTool"/> tests.</summary>
public sealed class FakeDefaultEndpointWriter(FakeAudioEndpoints audio) : IAudioDefaultEndpointWriter
{
    private readonly object _gate = new();

    public FakeAudioEndpoints Audio { get; } = audio;
    public ToolAvailability Availability { get; set; } = ToolAvailability.Available;
    public Exception? OpenFailure { get; set; }
    public Dictionary<AudioRole, Exception> FailWith { get; } = [];
    public HashSet<AudioRole> ApplyDespiteFailure { get; } = [];
    public HashSet<AudioRole> Ignore { get; } = [];
    public Dictionary<AudioRole, AudioRole> Couple { get; } = [];
    public bool CrossDirectionSideEffect { get; set; }
    public bool FailReadAfterWrite { get; set; }
    public Action<string, AudioRole>? OnWrite { get; set; }
    public Dictionary<AudioRole, ManualResetEventSlim> BlockGates { get; } = [];
    public List<(string EndpointId, AudioRole Role)> Writes { get; } = [];

    private int _openHandles;
    public int OpenHandles => Volatile.Read(ref _openHandles);

    public ToolAvailability GetAvailability() => Availability;

    public IAudioDefaultEndpointChange Open()
    {
        if (OpenFailure is not null)
        {
            throw OpenFailure;
        }

        Interlocked.Increment(ref _openHandles);
        return new Change(this);
    }

    private sealed class Change(FakeDefaultEndpointWriter owner) : IAudioDefaultEndpointChange
    {
        private int _disposed;

        public void SetDefault(string endpointId, AudioRole role)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (owner.BlockGates.TryGetValue(role, out var block))
            {
                block.Wait(CancellationToken.None);
            }

            owner.OnWrite?.Invoke(endpointId, role);

            Exception? failure;
            bool applyDespiteFailure;
            lock (owner._gate)
            {
                owner.Writes.Add((endpointId, role));
                owner.FailWith.TryGetValue(role, out failure);
                applyDespiteFailure = owner.ApplyDespiteFailure.Contains(role);
            }

            if (failure is null || applyDespiteFailure)
            {
                if (!owner.Ignore.Contains(role))
                {
                    lock (owner.Audio.Devices)
                    {
                        var dir = owner.Audio.Devices[endpointId].Direction;
                        owner.Audio.RoleDefaults[(dir, role)] = endpointId;
                        if (owner.Couple.TryGetValue(role, out var coupled))
                        {
                            owner.Audio.RoleDefaults[(dir, coupled)] = endpointId;
                        }

                        if (owner.CrossDirectionSideEffect)
                        {
                            var otherDir = dir == AudioDirection.Output ? AudioDirection.Input : AudioDirection.Output;
                            owner.Audio.RoleDefaults[(otherDir, role)] = endpointId;
                        }
                    }
                }
            }

            if (owner.FailReadAfterWrite)
            {
                owner.Audio.DefaultReadFailure = new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API returned an error.");
            }

            if (failure is not null)
            {
                throw failure;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._openHandles);
            }
        }
    }
}
