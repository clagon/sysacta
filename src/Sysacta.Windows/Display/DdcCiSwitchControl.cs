using System.Runtime.InteropServices;
using System.Text;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using static Sysacta.Windows.Display.NativeMethods;

namespace Sysacta.Windows.Display;

/// <summary>
/// One physical monitor handle for the MCCS power (VCP 0xD6) and input (VCP 0x60) settings, through the low-level
/// Monitor Configuration API. These two codes are the only ones this class can address, and only allow-listed values are
/// written. The handle lives only as long as this object.
/// </summary>
internal sealed class DdcCiSwitchControl(IntPtr handle, IDisposable release) : IDisplaySwitchControl
{
    private IntPtr _handle = handle;
    private bool _disposed;

    public string ReadCapabilities()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GetCapabilitiesStringLength(_handle, out var length))
        {
            throw DdcCiControl.Failure(Marshal.GetLastPInvokeError());
        }

        // The length includes the terminating null.
        if (length is 0 or > Mccs.MaxCapabilitiesLength + 1)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The monitor reported an unusable capabilities string length.");
        }

        var buffer = new byte[length];
        if (!CapabilitiesRequestAndCapabilitiesReply(_handle, buffer, length))
        {
            throw DdcCiControl.Failure(Marshal.GetLastPInvokeError());
        }

        var end = Array.IndexOf(buffer, (byte)0);
        // Non-ASCII bytes become '?' and the string is then refused by the parser.
        return Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    public byte Read(DisplaySwitch setting)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GetVCPFeatureAndVCPFeatureReply(_handle, Code(setting), IntPtr.Zero, out var current, out _))
        {
            throw DdcCiControl.Failure(Marshal.GetLastPInvokeError());
        }

        // Both are simple non-continuous features in MCCS 2.x: the value is the SL byte.
        return (byte)(current & 0xFF);
    }

    public Action PrepareWrite(DisplaySwitch setting, byte value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Mccs.IsAllowed(setting, value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The value is not on the allow-list.");
        }

        var code = Code(setting);
        return () =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!SetVCPFeature(_handle, code, value))
            {
                throw DdcCiControl.Failure(Marshal.GetLastPInvokeError());
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
        _ = DestroyPhysicalMonitor(_handle);
        _handle = IntPtr.Zero;
        release.Dispose();
    }

    internal static byte Code(DisplaySwitch setting) => setting switch
    {
        DisplaySwitch.Power => 0xD6,
        DisplaySwitch.Input => 0x60,
        _ => throw new ArgumentOutOfRangeException(nameof(setting)),
    };
}
