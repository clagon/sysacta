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

/// <summary>The DDC/CI power and input settings of a fake monitor.</summary>
public sealed class FakeSwitch
{
    public string Capabilities { get; set; } = "(prot(monitor)type(lcd)model(TEST)cmds(01 02 03 07 0C E3 F3)vcp(02 04 10 12 60(0F 11 12) D6(01 04 05))mccs_ver(2.1))";
    public byte Power { get; set; } = 0x01;
    public byte Input { get; set; } = 0x0F;
    public Exception? CapabilitiesFailure { get; set; }
    public Exception? ReadFailure { get; set; }
    public Exception? ReadFailureAfterWrite { get; set; }

    /// <summary>Thrown by the write, after <see cref="Apply"/> ran.</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>The value the monitor stores for a written value (null: ignored).</summary>
    public Func<DisplaySwitch, byte, byte?> Apply { get; set; } = (_, v) => v;

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
    public Dictionary<string, FakeSwitch> Switches { get; } = [];
    public List<(string Id, DisplaySwitch Setting, byte Value)> SwitchWrites { get; } = [];
    public List<(string Id, DisplayMode Mode)> ModeWrites { get; } = [];
    public List<(string Id, DisplayMode Mode)> ModeTests { get; } = [];
    public Dictionary<string, (int X, int Y)> Positions { get; } = [];

    /// <summary>Thrown by CDS_TEST.</summary>
    public Exception? ModeTestFailure { get; set; }

    /// <summary>Thrown by the mode change, after <see cref="ApplyMode"/> ran.</summary>
    public Exception? ModeApplyFailure { get; set; }

    /// <summary>The mode the display ends up in for a requested mode (default: the requested one; null: unchanged).</summary>
    public Func<string, DisplayMode, DisplayMode?> ApplyMode { get; set; } = (_, m) => m;

    /// <summary>Runs inside the mode change (e.g. to change other displays).</summary>
    public Action<string>? OnModeApply { get; set; }

    /// <summary>Thrown by mode reads once a mode change happened.</summary>
    public Exception? ModeReadFailureAfterApply { get; set; }

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
        Switches[id] = new FakeSwitch();
        Positions[id] = (Displays.Count * 1920 - 1920, 0);
        Modes[id] = new DisplayModeSet([Mode1080p60, Mode720p60, Mode1080p144], Mode1080p60);
        return info;
    }

    public void Replace(string id, Func<DisplayInfo, DisplayInfo> change)
    {
        var i = Displays.FindIndex(d => d.Id == id);
        Displays[i] = change(Displays[i]);
    }

    public static readonly DisplayMode Mode1080p60 = new(1920, 1080, 60, 32, false, 0, 0);
    public static readonly DisplayMode Mode720p60 = new(1280, 720, 60, 32, false, 0, 0);
    public static readonly DisplayMode Mode1080p144 = new(1920, 1080, 144, 32, false, 0, 0);

    public FakeLevel Brightness(string id) => Levels[(id, DisplayFeature.Brightness)];

    public DisplayMode? CurrentMode(string id) => Modes[id].Current;

    private DisplayInfo Opened(string displayId, Func<DisplayInfo, ToolFailureException?> blocked, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnOpen?.Invoke(displayId);
        var info = Displays.FirstOrDefault(d => d.Id == displayId)
            ?? throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The display is no longer connected.");
        if (blocked(info) is { } failure)
        {
            throw failure;
        }

        Interlocked.Increment(ref _open);
        return info;
    }

    public IDisplayModeControl OpenMode(string displayId, CancellationToken cancellationToken) =>
        new ModeControl(this, Opened(displayId, DisplayCatalog.BlockedForMode, cancellationToken).Id);

    public IDisplaySwitchControl OpenSwitch(string displayId, CancellationToken cancellationToken) =>
        new SwitchControl(this, Opened(displayId, DisplayCatalog.BlockedForSwitch, cancellationToken).Id);

    private sealed class ModeControl(FakeDisplays owner, string id) : IDisplayModeControl
    {
        private bool _disposed;
        private bool _applied;

        public DisplayModeSet ListModes()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_applied && owner.ModeReadFailureAfterApply is { } failure)
            {
                throw failure;
            }

            return owner.ModesFailure is { } f ? throw f : owner.Modes[id];
        }

        public IReadOnlyList<DisplayLayout> Layout()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return [.. owner.Displays.Select(d => new DisplayLayout(d.Id, d.IsPrimary, d.IsCloned, d.CurrentMode,
                owner.Positions.GetValueOrDefault(d.Id).X, owner.Positions.GetValueOrDefault(d.Id).Y, 0))];
        }

        public void Test(DisplayMode mode)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner.ModeTests.Add((id, mode));
            if (owner.ModeTestFailure is { } failure)
            {
                throw failure;
            }
        }

        public Action PrepareApply(DisplayMode mode)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner.OnPrepare?.Invoke(id);
            return () =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                owner.OnModeApply?.Invoke(id);
                lock (owner._sync)
                {
                    owner.ModeWrites.Add((id, mode));
                }

                _applied = true;
                if (owner.ApplyMode(id, mode) is { } now)
                {
                    owner.Modes[id] = owner.Modes[id] with { Current = now };
                }

                if (owner.ModeApplyFailure is { } failure)
                {
                    throw failure;
                }
            };
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

    private sealed class SwitchControl(FakeDisplays owner, string id) : IDisplaySwitchControl
    {
        private bool _disposed;

        private FakeSwitch State => owner.Switches[id];

        public string ReadCapabilities()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return State.CapabilitiesFailure is { } failure ? throw failure : State.Capabilities;
        }

        public byte Read(DisplaySwitch setting)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner.OnRead?.Invoke(id);
            if ((State.Written ? State.ReadFailureAfterWrite : null) is { } after)
            {
                throw after;
            }

            return State.ReadFailure is { } failure ? throw failure : setting == DisplaySwitch.Power ? State.Power : State.Input;
        }

        public Action PrepareWrite(DisplaySwitch setting, byte value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Mccs.IsAllowed(setting, value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            owner.OnPrepare?.Invoke(id);
            return () =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                owner.OnWrite?.Invoke(id);
                lock (owner._sync)
                {
                    owner.SwitchWrites.Add((id, setting, value));
                }

                State.Written = true;
                if (State.Apply(setting, value) is { } stored)
                {
                    if (setting == DisplaySwitch.Power)
                    {
                        State.Power = stored;
                    }
                    else
                    {
                        State.Input = stored;
                    }
                }

                if (State.WriteFailure is { } failure)
                {
                    throw failure;
                }
            };
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
