using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using static Sysacta.Windows.Display.NativeMethods;

namespace Sysacta.Windows.Display;

/// <summary>
/// Mode change of one desktop source with ChangeDisplaySettingsExW: only that source (its GDI device name), only a
/// DEVMODE Windows enumerates now, only the documented mode members (never position, rotation or scaling), dynamic and
/// not saved to the registry (flags 0). Holds the adapter gate until disposed.
/// </summary>
internal sealed class DisplayModeControl(string displayId, string gdiDeviceName, IDisposable release) : IDisplayModeControl
{
    private bool _disposed;

    public DisplayModeSet ListModes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WindowsDisplays.Modes(gdiDeviceName);
    }

    public IReadOnlyList<DisplayLayout> Layout()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return [.. DisplayTopologyReader.Read().Paths.Select(p => p.Layout)];
    }

    public void Test(DisplayMode mode)
    {
        var request = Request(mode);
        var result = ChangeDisplaySettingsEx(gdiDeviceName, ref request, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (result != DispChangeSuccessful)
        {
            throw Failure(result, tested: true);
        }
    }

    public Action PrepareApply(DisplayMode mode)
    {
        var request = Request(mode);
        return () =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = ChangeDisplaySettingsEx(gdiDeviceName, ref request, IntPtr.Zero, CdsDynamic, IntPtr.Zero);
            if (result != DispChangeSuccessful)
            {
                throw Failure(result, tested: false);
            }
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            release.Dispose();
        }
    }

    /// <summary>The DEVMODE Windows enumerates now for the mode, limited to the mode members.</summary>
    private DevMode Request(DisplayMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WindowsDisplays.EnsureSameSource(displayId, gdiDeviceName);
        var request = WindowsDisplays.Find(gdiDeviceName, mode)
            ?? throw new ToolFailureException(ErrorCodes.InvalidArgument, "The mode is no longer available for this display. Nothing was changed.");
        request.DriverExtra = 0;
        request.Fields = DmBitsPerPel | DmPelsWidth | DmPelsHeight | DmDisplayFlags | DmDisplayFrequency;
        return request;
    }

    internal static ToolFailureException Failure(int result, bool tested) => result switch
    {
        DispChangeBadMode => new(ErrorCodes.NotSupported, tested ? "Windows does not accept this mode for the display. Nothing was changed." : "Windows does not support this mode for the display."),
        DispChangeRestart => new(ErrorCodes.NotSupported, tested ? "Windows can set this mode only after a restart. Nothing was changed." : "Windows can set this mode only after a restart."),
        _ => new(ErrorCodes.OsApiError, tested ? "Windows refused the mode in its test. Nothing was changed." : "Windows refused the mode change."),
    };
}
