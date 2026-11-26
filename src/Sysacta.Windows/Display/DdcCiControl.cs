using System.Runtime.InteropServices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using static Sysacta.Windows.Display.NativeMethods;

namespace Sysacta.Windows.Display;

/// <summary>
/// One physical monitor handle (Monitor Configuration API, high-level functions). The handle lives only as long as this
/// object; it is opened per call and never cached.
/// </summary>
internal sealed class DdcCiControl : IDisplayControl
{
    private const uint CapsBrightness = 0x2;
    private const uint CapsContrast = 0x4;

    // winerror.h graphics codes (messages checked against FormatMessage on Windows 11).
    private const int I2cNotSupported = unchecked((int)0xC0262580);
    private const int VcpNotSupported = unchecked((int)0xC0262584);
    private const int InvalidPhysicalMonitorHandle = unchecked((int)0xC026258C);
    private const int MonitorNoLongerExists = unchecked((int)0xC026258D);

    private readonly DisplayFeature _feature;
    private readonly IDisposable _release;
    private IntPtr _handle;
    private bool _disposed;
    private bool _hasRead;

    private DdcCiControl(IntPtr handle, DisplayFeature feature, IDisposable release)
    {
        _handle = handle;
        _feature = feature;
        _release = release;
    }

    public DisplayControlMethod Method => DisplayControlMethod.DdcCi;

    /// <summary>
    /// Opens the single physical monitor of the desktop source. More than one is ambiguous (there is no reliable
    /// mapping from a physical handle to a target), so nothing is chosen by list order.
    /// </summary>
    public static DdcCiControl Open(string gdiDeviceName, DisplayFeature feature, IDisposable release) =>
        new(OpenPhysical(gdiDeviceName), feature, release);

    /// <summary>The handle of the single physical monitor of the desktop source. The caller destroys it.</summary>
    internal static IntPtr OpenPhysical(string gdiDeviceName)
    {
        var monitor = FindMonitor(gdiDeviceName);
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count))
        {
            throw Failure(Marshal.GetLastPInvokeError());
        }

        if (count != 1)
        {
            throw count == 0
                ? new ToolFailureException(ErrorCodes.DeviceDisconnected, "No physical monitor is attached to the display.")
                : new ToolFailureException(ErrorCodes.AmbiguousTarget, "Several physical monitors share this display, so none can be chosen reliably.");
        }

        var physical = new PhysicalMonitor[1];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
        {
            throw Failure(Marshal.GetLastPInvokeError());
        }

        return physical[0].Handle;
    }

    public LevelRange Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        uint min, current, max;
        var ok = _feature == DisplayFeature.Brightness
            ? GetMonitorBrightness(_handle, out min, out current, out max)
            : GetMonitorContrast(_handle, out min, out current, out max);
        if (ok)
        {
            _hasRead = true;
            return new LevelRange(min, max, current);
        }

        var error = Marshal.GetLastPInvokeError();
        // The read result comes first; the capability flags only tell a definite lack of support from a failed query.
        // After a successful read on this handle a failure is temporary, so the slow capability query is skipped.
        if (!_hasRead && GetMonitorCapabilities(_handle, out var capabilities, out _)
            && (capabilities & (_feature == DisplayFeature.Brightness ? CapsBrightness : CapsContrast)) == 0)
        {
            throw new ToolFailureException(ErrorCodes.NotSupported, "The monitor does not report support for this setting.");
        }

        throw Failure(error);
    }

    public Action PrepareWrite(long native)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var value = checked((uint)native);
        return () =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var ok = _feature == DisplayFeature.Brightness ? SetMonitorBrightness(_handle, value) : SetMonitorContrast(_handle, value);
            if (!ok)
            {
                throw Failure(Marshal.GetLastPInvokeError());
            }
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // A failure to close cannot be acted on; the handle is not used again.
        _ = DestroyPhysicalMonitor(_handle);
        _handle = IntPtr.Zero;
        _release.Dispose();
    }

    internal static ToolFailureException Failure(int error) => error switch
    {
        I2cNotSupported or VcpNotSupported => new(ErrorCodes.NotSupported, "The monitor or its connection does not support this DDC/CI operation."),
        MonitorNoLongerExists or InvalidPhysicalMonitorHandle => new(ErrorCodes.DeviceDisconnected, "The monitor was disconnected or the display configuration changed."),
        _ => new(ErrorCodes.OsApiError, "The monitor did not answer the DDC/CI request. Check that DDC/CI is enabled in the monitor menu."),
    };

    private static IntPtr FindMonitor(string gdiDeviceName)
    {
        var found = new List<IntPtr>(1);
        bool Visit(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data)
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info) && string.Equals(info.Device, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(monitor);
            }

            return true;
        }

        MonitorEnumProc callback = Visit;
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows monitor enumeration failed.");
        }

        GC.KeepAlive(callback);
        return found.Count == 1
            ? found[0]
            : throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The display is not part of the desktop at the moment.");
    }
}
