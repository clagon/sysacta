using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;

namespace Sysacta.Tests;

/// <summary>One adjustable value of a fake display.</summary>
public sealed class FakeLevel
{
    public long Min { get; set; }
    public long Max { get; set; } = 100;
    public long Current { get; set; } = 50;
    public IReadOnlyList<long>? Allowed { get; set; }

    /// <summary>Thrown by every read (query failure or definite lack of support).</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>Thrown by the write, after <see cref="Apply"/> ran (a failed API call may still change the device).</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>Thrown by reads once a write happened.</summary>
    public Exception? ReadFailureAfterWrite { get; set; }

    /// <summary>The value the device stores for a written value.</summary>
    public Func<long, long> Apply { get; set; } = v => v;

    /// <summary>Number of reads after a write that still return the old value.</summary>
    public int ApplyDelayReads { get; set; }

    internal long? Pending;
    internal int PendingReads;
    internal bool Written;
}

/// <summary>In-memory display adapter, shared by Core.Tests and Mcp.Tests.</summary>
public sealed class FakeDisplays : IDisplays
{
    private readonly Lock _sync = new();
    private int _open;

    public FakeDisplays()
    {
        Add("display-a", "DELL S2422HG", primary: true);
        Add("display-b", "DELL P2314H");
    }

    public List<DisplayInfo> Displays { get; } = [];
    public int Unidentified { get; set; }
    public Dictionary<(string, DisplayFeature), FakeLevel> Levels { get; } = [];
    public List<(string Id, DisplayFeature Feature, long Value)> Writes { get; } = [];
    public Dictionary<string, DisplayModeSet> Modes { get; } = [];
    public Exception? TopologyFailure { get; set; }
    public Exception? ModesFailure { get; set; }

    /// <summary>Open controls that have not been disposed.</summary>
    public int OpenControls => Volatile.Read(ref _open);

    public Action<string>? OnOpen { get; set; }
    public Action<string>? OnRead { get; set; }

    /// <summary>Runs inside the preparation of a write (nothing is changed yet).</summary>
    public Action<string>? OnPrepare { get; set; }

    /// <summary>Runs inside the native write, before it is applied.</summary>
    public Action<string>? OnWrite { get; set; }

    public DisplayInfo Add(string id, string name, bool primary = false, DisplayKind kind = DisplayKind.External, bool cloned = false)
    {
        var info = new DisplayInfo(id, name, kind, kind == DisplayKind.Internal ? "internal" : "displayport", primary, cloned,
            new DisplayCurrentMode(1920, 1080, 60000, 1001));
        Displays.Add(info);
        Levels[(id, DisplayFeature.Brightness)] = new FakeLevel();
        Levels[(id, DisplayFeature.Contrast)] = new FakeLevel { Current = 75 };
        return info;
    }

    public void Replace(string id, Func<DisplayInfo, DisplayInfo> change)
    {
        var i = Displays.FindIndex(d => d.Id == id);
        Displays[i] = change(Displays[i]);
    }

    public FakeLevel Brightness(string id) => Levels[(id, DisplayFeature.Brightness)];

    public DisplayTopology GetTopology() => TopologyFailure is { } f ? throw f : new DisplayTopology([.. Displays], Unidentified);

    public DisplayModeSet ListModes(string displayId) => ModesFailure is { } f ? throw f : Modes[displayId];

    public IDisplayControl Open(string displayId, DisplayFeature feature, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnOpen?.Invoke(displayId);
        var info = Displays.FirstOrDefault(d => d.Id == displayId)
            ?? throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The display is no longer connected.");
        if (DisplayCatalog.Blocked(info, feature) is { } blocked)
        {
            throw blocked;
        }

        Interlocked.Increment(ref _open);
        return new Control(this, info, feature);
    }

    private sealed class Control(FakeDisplays owner, DisplayInfo info, DisplayFeature feature) : IDisplayControl
    {
        private bool _disposed;

        public DisplayControlMethod Method => info.Kind == DisplayKind.Internal ? DisplayControlMethod.Wmi : DisplayControlMethod.DdcCi;

        public LevelRange Read()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner.OnRead?.Invoke(info.Id);
            var level = owner.Levels[(info.Id, feature)];
            if ((level.Written ? level.ReadFailureAfterWrite : null) is { } after)
            {
                throw after;
            }

            if (level.ReadFailure is { } failure)
            {
                throw failure;
            }

            if (level.Pending is { } pending && level.PendingReads-- <= 0)
            {
                level.Current = pending;
                level.Pending = null;
            }

            return new LevelRange(level.Min, level.Max, level.Current, level.Allowed);
        }

        public Action PrepareWrite(long native)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner.OnPrepare?.Invoke(info.Id);
            return () => Write(native);
        }

        private void Write(long native)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner.OnWrite?.Invoke(info.Id);
            var level = owner.Levels[(info.Id, feature)];
            lock (owner._sync)
            {
                owner.Writes.Add((info.Id, feature, native));
            }

            level.Written = true;
            level.Pending = level.Apply(native);
            level.PendingReads = level.ApplyDelayReads;
            if (level.WriteFailure is { } failure)
            {
                throw failure;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Interlocked.Decrement(ref owner._open);
            }
        }
    }
}
