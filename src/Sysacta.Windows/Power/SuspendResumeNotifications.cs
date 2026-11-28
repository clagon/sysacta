using System.Runtime.InteropServices;

namespace Sysacta.Windows.Power;

/// <summary>
/// PowerRegisterSuspendResumeNotification (DEVICE_NOTIFY_CALLBACK, Windows 8+): PBT_APMSUSPEND, and both
/// PBT_APMRESUMEAUTOMATIC (every resume, also an unattended wake) and PBT_APMRESUMESUSPEND (after user input).
/// Needs no window or message pump. The callback runs on a system thread and must not throw.
/// </summary>
public sealed class SuspendResumeNotifications : IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtApmSuspend = 4;
    private const uint PbtApmResumeSuspend = 7;
    private const uint PbtApmResumeAutomatic = 18;

    private readonly Callback _callback; // kept alive while registered
    private IntPtr _parameters;
    private IntPtr _registration;

    public SuspendResumeNotifications(Action suspend, Action resume)
    {
        _callback = (_, type, _) =>
        {
            try
            {
                switch (type)
                {
                    case PbtApmSuspend:
                        suspend();
                        break;
                    case PbtApmResumeAutomatic or PbtApmResumeSuspend:
                        resume();
                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never let an exception reach the native caller.
            }

            return 0;
        };

        // DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { Callback; Context; }, kept until unregistered.
        _parameters = Marshal.AllocHGlobal(2 * IntPtr.Size);
        Marshal.WriteIntPtr(_parameters, Marshal.GetFunctionPointerForDelegate(_callback));
        Marshal.WriteIntPtr(_parameters, IntPtr.Size, IntPtr.Zero);
        if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _parameters, out _registration) != 0)
        {
            Marshal.FreeHGlobal(_parameters);
            _parameters = IntPtr.Zero;
            throw new InvalidOperationException("Suspend/resume notifications could not be registered.");
        }
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            _ = PowerUnregisterSuspendResumeNotification(_registration);
            _registration = IntPtr.Zero;
        }

        if (_parameters != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_parameters);
            _parameters = IntPtr.Zero;
        }

        GC.KeepAlive(_callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Callback(IntPtr context, uint type, IntPtr setting);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
