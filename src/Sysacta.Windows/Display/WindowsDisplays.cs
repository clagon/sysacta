using System.Runtime.InteropServices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using static Sysacta.Windows.Display.NativeMethods;

namespace Sysacta.Windows.Display;

/// <summary>
/// Display adapter: DisplayConfig for the topology, EnumDisplaySettingsEx for modes, DDC/CI for external monitors and
/// WMI for internal panels.
/// <list type="bullet">
/// <item>Threading: called on thread-pool threads by the executor and the device page; never on the WPF UI thread.</item>
/// <item>Exclusion: one process-wide gate covers every value access from open to dispose. A native call that hangs keeps
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

    public DisplayModeSet ListModes(string displayId)
    {
        var path = Find(displayId);
        if (path.GdiDeviceName.Length == 0)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The display source cannot be identified.");
        }

        var modes = new HashSet<DisplayMode>();
        for (uint i = 0; i < MaxModeIndex; i++)
        {
            if (Mode(path.GdiDeviceName, i) is not { } mode)
            {
                break;
            }

            modes.Add(mode);
        }

        if (modes.Count == 0)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "Windows returned no display modes.");
        }

        return new DisplayModeSet([.. modes], Mode(path.GdiDeviceName, EnumCurrentSettings));
    }

    public IDisplayControl Open(string displayId, DisplayFeature feature, CancellationToken cancellationToken)
    {
        _gate.Wait(cancellationToken);
        var release = new Release(_gate);
        try
        {
            // Resolved again under the gate: the topology may have changed while waiting.
            var path = Find(displayId);
            if (DisplayCatalog.Blocked(path.Info, feature) is { } blocked)
            {
                throw blocked;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (path.Info.Kind == DisplayKind.Internal)
            {
                var instanceId = WmiPanelControl.InstanceId(path.DevicePath)
                    ?? throw new ToolFailureException(ErrorCodes.NotSupported, "The panel cannot be matched to a Windows brightness control.");
                return new WmiPanelControl(instanceId, release);
            }

            var control = DdcCiControl.Open(path.GdiDeviceName, feature, release);
            try
            {
                // The monitor handle was found by source name; make sure that name still belongs to the same target.
                var again = Find(displayId);
                if (again.GdiDeviceName != path.GdiDeviceName || again.Info.IsCloned)
                {
                    throw new ToolFailureException(ErrorCodes.TargetChanged, "The display configuration changed. Nothing was changed; retry.", retryable: true);
                }

                return control;
            }
            catch
            {
                control.Dispose();
                throw;
            }
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

    private static DisplayPath Find(string displayId)
    {
        var matches = DisplayTopologyReader.Read().Paths.Where(p => p.Info.Id == displayId).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The display is no longer connected."),
            _ => throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "The display cannot be identified uniquely."),
        };
    }

    private static DisplayMode? Mode(string gdiDeviceName, uint index)
    {
        var devMode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettingsEx(gdiDeviceName, index, ref devMode, 0)
            ? new DisplayMode(
                (int)devMode.PelsWidth, (int)devMode.PelsHeight, (int)devMode.DisplayFrequency, (int)devMode.BitsPerPel,
                (devMode.DisplayFlags & DmInterlaced) != 0, (int)(devMode.DisplayOrientation * 90), (int)devMode.DisplayFixedOutput)
            : null;
    }

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
