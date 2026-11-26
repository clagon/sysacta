using System.Runtime.InteropServices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using static Sysacta.Windows.Display.NativeMethods;

namespace Sysacta.Windows.Display;

/// <summary>
/// Display adapter: DisplayConfig for the topology, EnumDisplaySettingsEx / ChangeDisplaySettingsEx for modes, DDC/CI for
/// external monitors and WMI for internal panels.
/// <list type="bullet">
/// <item>Threading: called on thread-pool threads by the executor and the device page; never on the WPF UI thread.</item>
/// <item>Exclusion: one process-wide gate covers every value access and mode change from open to dispose. A native call that hangs keeps
/// the gate; later callers wait only until their own cancellation.</item>
/// <item>Lifetime: physical monitor handles and WMI objects are created per call and disposed.</item>
/// </list>
/// </summary>
public sealed class WindowsDisplays : IDisplays
{
    private const int MaxModeIndex = 4096;

    // ponytail: one gate for all monitors, because the shared bus (dock, adapter) cannot be identified reliably.
    // Per-bus gates if serial access ever becomes too slow.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DisplayTopology GetTopology()
    {
        var paths = DisplayTopologyReader.Read();
        return new DisplayTopology([.. paths.Paths.Select(p => p.Info)], paths.UnidentifiedCount);
    }

    public DisplayModeSet ListModes(string displayId) => Modes(Find(displayId).GdiDeviceName);

    public IDisplayControl Open(string displayId, DisplayFeature feature, CancellationToken cancellationToken) =>
        Acquire<IDisplayControl>(displayId, (path, release) =>
        {
            if (DisplayCatalog.Blocked(path.Info, feature) is { } blocked)
            {
                throw blocked;
            }

            if (path.Info.Kind == DisplayKind.Internal)
            {
                var instanceId = WmiPanelControl.InstanceId(path.DevicePath)
                    ?? throw new ToolFailureException(ErrorCodes.NotSupported, "The panel cannot be matched to a Windows brightness control.");
                return new WmiPanelControl(instanceId, release);
            }

            return Checked(DdcCiControl.Open(path.GdiDeviceName, feature, release), displayId, path.GdiDeviceName);
        }, cancellationToken);

    public IDisplayModeControl OpenMode(string displayId, CancellationToken cancellationToken) =>
        Acquire<IDisplayModeControl>(displayId, (path, release) =>
        {
            if (DisplayCatalog.BlockedForMode(path.Info) is { } blocked)
            {
                throw blocked;
            }

            return path.GdiDeviceName.Length == 0
                ? throw new ToolFailureException(ErrorCodes.OsApiError, "The display source cannot be identified.")
                : new DisplayModeControl(displayId, path.GdiDeviceName, release);
        }, cancellationToken);

    public IDisplaySwitchControl OpenSwitch(string displayId, CancellationToken cancellationToken) =>
        Acquire<IDisplaySwitchControl>(displayId, (path, release) =>
            DisplayCatalog.BlockedForSwitch(path.Info) is { } blocked
                ? throw blocked
                : Checked(new DdcCiSwitchControl(DdcCiControl.OpenPhysical(path.GdiDeviceName), release), displayId, path.GdiDeviceName),
            cancellationToken);

    /// <summary>
    /// Waits for the gate, re-resolves the display under it (the topology may have changed while waiting) and opens it.
    /// The gate is released with the returned control, or here when opening fails.
    /// </summary>
    private T Acquire<T>(string displayId, Func<DisplayPath, IDisposable, T> open, CancellationToken cancellationToken)
    {
        _gate.Wait(cancellationToken);
        var release = new Release(_gate);
        try
        {
            var path = Find(displayId);
            cancellationToken.ThrowIfCancellationRequested();
            return open(path, release);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            release.Dispose();
            throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows monitor API is not available.");
        }
        catch
        {
            release.Dispose();
            throw;
        }
    }

    /// <summary>A physical monitor handle was found by source name; make sure that name still belongs to the same target.</summary>
    private static T Checked<T>(T control, string displayId, string gdiDeviceName)
        where T : IDisposable
    {
        try
        {
            EnsureSameSource(displayId, gdiDeviceName);
            return control;
        }
        catch
        {
            control.Dispose();
            throw;
        }
    }

    internal static void EnsureSameSource(string displayId, string gdiDeviceName)
    {
        var again = Find(displayId);
        if (again.GdiDeviceName != gdiDeviceName || again.Info.IsCloned)
        {
            throw new ToolFailureException(ErrorCodes.TargetChanged, "The display configuration changed. Nothing was changed; retry.", retryable: true);
        }
    }

    internal static DisplayModeSet Modes(string gdiDeviceName)
    {
        if (gdiDeviceName.Length == 0)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The display source cannot be identified.");
        }

        var modes = new HashSet<DisplayMode>();
        for (uint i = 0; i < MaxModeIndex; i++)
        {
            if (Raw(gdiDeviceName, i) is not { } raw)
            {
                break;
            }

            modes.Add(ToMode(raw));
        }

        if (modes.Count == 0)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "Windows returned no display modes.");
        }

        return new DisplayModeSet([.. modes], Raw(gdiDeviceName, EnumCurrentSettings) is { } current ? ToMode(current) : null);
    }

    /// <summary>The first DEVMODE Windows enumerates now for this mode, or null.</summary>
    internal static DevMode? Find(string gdiDeviceName, DisplayMode mode)
    {
        for (uint i = 0; i < MaxModeIndex; i++)
        {
            if (Raw(gdiDeviceName, i) is not { } raw)
            {
                return null;
            }

            if (ToMode(raw) == mode)
            {
                return raw;
            }
        }

        return null;
    }

    internal static DisplayPath Find(string displayId)
    {
        var matches = DisplayTopologyReader.Read().Paths.Where(p => p.Info.Id == displayId).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The display is no longer connected."),
            _ => throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "The display cannot be identified uniquely."),
        };
    }

    private static DevMode? Raw(string gdiDeviceName, uint index)
    {
        var devMode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettingsEx(gdiDeviceName, index, ref devMode, 0) ? devMode : null;
    }

    private static DisplayMode ToMode(DevMode devMode) => new(
        (int)devMode.PelsWidth, (int)devMode.PelsHeight, (int)devMode.DisplayFrequency, (int)devMode.BitsPerPel,
        (devMode.DisplayFlags & DmInterlaced) != 0, (int)(devMode.DisplayOrientation * 90), (int)devMode.DisplayFixedOutput);

    /// <summary>Releases the gate once.</summary>
    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
