using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Power;

namespace Sysacta.Windows.Power;

/// <summary>
/// Win32 power adapter: GetSystemPowerStatus, GetPwrCapabilities, PowerEnumerate / PowerReadFriendlyName /
/// PowerGetActiveScheme / PowerSetActiveScheme, LockWorkStation, SetSuspendState and InitiateShutdownW.
/// No external command, no forced flag, no undocumented API (docs/phase-1e-progress.md).
/// </summary>
/// <remarks>
/// SeShutdownPrivilege is never enabled on the process token. For sleep, shutdown and restart a duplicate of the process
/// token is made, the privilege is enabled on that duplicate only (failing when the account does not hold it), and the
/// duplicate is set as the calling thread's impersonation token for the single OS request, then reverted and closed.
/// Everything happens synchronously on one thread, so no other code runs with the privilege.
/// </remarks>
public sealed class WindowsPower : IPower
{
    public PowerStatusReading ReadStatus()
    {
        if (!Native.GetSystemPowerStatus(out var s))
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The power status could not be read.", retryable: true);
        }

        return new(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent, s.SystemStatusFlag, s.BatteryLifeTime, s.BatteryFullLifeTime);
    }

    public PowerCapabilitiesReading ReadCapabilities()
    {
        // SYSTEM_POWER_CAPABILITIES is 76 bytes (winnt.h); a larger buffer guards against layout growth.
        var buffer = new byte[128];
        if (!Native.GetPwrCapabilities(buffer))
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The power capabilities could not be read.", retryable: true);
        }

        return new(buffer[Native.CapS1] != 0, buffer[Native.CapS2] != 0, buffer[Native.CapS3] != 0, buffer[Native.CapS4] != 0,
            buffer[Native.CapHiberFilePresent] != 0, buffer[Native.CapAoAc] != 0);
    }

    public Guid ReadActivePlan()
    {
        if (Native.PowerGetActiveScheme(IntPtr.Zero, out var pointer) != Native.ErrorSuccess || pointer == IntPtr.Zero)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The active power plan could not be read.", retryable: true);
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            Native.LocalFree(pointer);
        }
    }

    public PowerPlanEnumeration ListPlans(int max)
    {
        var plans = new List<PowerPlanEntry>();
        for (uint index = 0; ; index++)
        {
            var guid = Guid.Empty;
            var size = (uint)Marshal.SizeOf<Guid>();
            var error = Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Native.AccessScheme, index, ref guid, ref size);
            if (error == Native.ErrorNoMoreItems)
            {
                return new(plans, false);
            }

            if (error != Native.ErrorSuccess)
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "The power plans could not be enumerated.", retryable: true);
            }

            if (plans.Count == max)
            {
                return new(plans, true); // one more exists
            }

            plans.Add(new(guid, ReadName(guid)));
        }
    }

    public PowerRequestResult SetActivePlan(Guid planId) =>
        Native.PowerSetActiveScheme(IntPtr.Zero, ref planId) switch
        {
            Native.ErrorSuccess => PowerRequestResult.Accepted,
            Native.ErrorAccessDenied => PowerRequestResult.Refused,
            _ => PowerRequestResult.Failed,
        };

    public bool HoldsShutdownPrivilege()
    {
        if (!Native.LookupPrivilegeValueW(null, Native.SeShutdownName, out var luid)
            || !Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenQuery, out var token))
        {
            throw new InvalidOperationException("The process token could not be queried.");
        }

        using (token)
        {
            Native.GetTokenInformation(token, Native.TokenPrivilegesClass, IntPtr.Zero, 0, out var length);
            if (length is 0 or > 64 * 1024)
            {
                throw new InvalidOperationException("The process token could not be queried.");
            }

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!Native.GetTokenInformation(token, Native.TokenPrivilegesClass, buffer, length, out _))
                {
                    throw new InvalidOperationException("The process token could not be queried.");
                }

                // TOKEN_PRIVILEGES: DWORD PrivilegeCount, then LUID_AND_ATTRIBUTES[] (LUID 8 bytes + DWORD, 4-byte packing).
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count && 4 + ((i + 1) * 12) <= length; i++)
                {
                    var offset = 4 + (i * 12);
                    if ((uint)Marshal.ReadInt32(buffer, offset) == luid.LowPart && Marshal.ReadInt32(buffer, offset + 4) == luid.HighPart)
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public PowerRequestResult Request(PowerTransition transition, Action beginSideEffect)
    {
        switch (transition)
        {
            case PowerTransition.Lock:
                beginSideEffect();
                return Native.LockWorkStation() ? PowerRequestResult.Accepted : Failure(Marshal.GetLastPInvokeError());
            case PowerTransition.Sleep:
                // bHibernate=FALSE (sleep, never hibernate), bForce has no effect, bWakeupEventsDisabled=FALSE (keep wake events).
                return WithShutdownPrivilege(beginSideEffect, static () =>
                    Native.SetSuspendState(false, false, false) ? PowerRequestResult.Accepted : Failure(Marshal.GetLastPInvokeError()));
            case PowerTransition.Shutdown:
                return WithShutdownPrivilege(beginSideEffect, static () => Map((int)Native.InitiateShutdownW(null, null, 0, Native.ShutdownPowerOff, Native.ReasonOtherPlanned)));
            case PowerTransition.Restart:
                return WithShutdownPrivilege(beginSideEffect, static () => Map((int)Native.InitiateShutdownW(null, null, 0, Native.ShutdownRestart, Native.ReasonOtherPlanned)));
            default:
                throw new ToolFailureException(ErrorCodes.NotSupported, "Unsupported power transition.");
        }
    }

    /// <summary>
    /// Prepares SeShutdownPrivilege on a duplicated impersonation token (throws <see cref="ToolFailureException"/>, nothing
    /// requested), calls <paramref name="beginSideEffect"/> (the final authorization; may throw), impersonates for the
    /// single request and always reverts. The process token is never modified.
    /// </summary>
    internal static PowerRequestResult WithShutdownPrivilege(Action beginSideEffect, Func<PowerRequestResult> request)
    {
        // A thread that already impersonates would lose that context on RevertToSelf; never expected on a pool thread.
        if (Native.OpenThreadToken(Native.GetCurrentThread(), Native.TokenQuery, true, out var existing))
        {
            existing.Dispose();
            throw new ToolFailureException(ErrorCodes.InternalError, "The power request could not be prepared.");
        }

        existing.Dispose();
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenDuplicate | Native.TokenQuery, out var process))
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The power request could not be prepared.");
        }

        SafeAccessTokenHandle duplicate;
        using (process)
        {
            if (!Native.DuplicateTokenEx(process, Native.TokenImpersonate | Native.TokenQuery | Native.TokenAdjustPrivileges, IntPtr.Zero,
                Native.SecurityImpersonation, Native.TokenTypeImpersonation, out duplicate))
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "The power request could not be prepared.");
            }
        }

        using (duplicate)
        {
            if (!Native.LookupPrivilegeValueW(null, Native.SeShutdownName, out var luid))
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "The power request could not be prepared.");
            }

            var privileges = new Native.TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = Native.SePrivilegeEnabled };
            // Success with ERROR_NOT_ALL_ASSIGNED means the account does not hold the privilege (nothing was enabled).
            if (!Native.AdjustTokenPrivileges(duplicate, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                || Marshal.GetLastPInvokeError() == Native.ErrorNotAllAssigned)
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "Windows did not grant the shutdown privilege to this account.");
            }

            beginSideEffect();
            if (!Native.SetThreadToken(IntPtr.Zero, duplicate))
            {
                return PowerRequestResult.Failed; // started, but nothing was requested
            }

            try
            {
                return request();
            }
            finally
            {
                if (!Native.RevertToSelf())
                {
                    // Documented: a thread that cannot revert must not keep running in the other context.
                    Environment.FailFast("RevertToSelf failed after a power request.");
                }
            }
        }
    }

    internal static PowerRequestResult Map(int error) => error switch
    {
        Native.ErrorSuccess => PowerRequestResult.Accepted,
        Native.ErrorAccessDenied or Native.ErrorPrivilegeNotHeld or Native.ErrorShutdownUsersLoggedOn => PowerRequestResult.Refused,
        Native.ErrorShutdownInProgress or Native.ErrorShutdownIsScheduled => PowerRequestResult.Busy,
        _ => PowerRequestResult.Failed,
    };

    /// <summary>A BOOL API returned FALSE: never report acceptance, even if the last error was left at 0.</summary>
    internal static PowerRequestResult Failure(int error) => Map(error) is PowerRequestResult.Accepted ? PowerRequestResult.Failed : Map(error);

    private static string? ReadName(Guid plan)
    {
        uint size = 0;
        if (Native.PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, null, ref size) != Native.ErrorSuccess || size is 0 or > 2048)
        {
            return null;
        }

        var buffer = new byte[size];
        if (Native.PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != Native.ErrorSuccess)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length) & ~1);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end >= 0 ? text[..end] : text;
    }

    internal static class Native
    {
        public const int ErrorSuccess = 0;
        public const int ErrorAccessDenied = 5;
        public const int ErrorNoMoreItems = 259;
        public const int ErrorShutdownInProgress = 1115;
        public const int ErrorShutdownIsScheduled = 1190;
        public const int ErrorShutdownUsersLoggedOn = 1191;
        public const int ErrorNotAllAssigned = 1300;
        public const int ErrorPrivilegeNotHeld = 1314;

        public const int AccessScheme = 16;

        // SYSTEM_POWER_CAPABILITIES byte offsets (winnt.h, all members before them are BOOLEAN/BYTE).
        public const int CapS1 = 3;
        public const int CapS2 = 4;
        public const int CapS3 = 5;
        public const int CapS4 = 6;
        public const int CapHiberFilePresent = 8;
        public const int CapAoAc = 20;

        public const uint TokenDuplicate = 0x0002;
        public const uint TokenImpersonate = 0x0004;
        public const uint TokenQuery = 0x0008;
        public const uint TokenAdjustPrivileges = 0x0020;
        public const int SecurityImpersonation = 2;
        public const int TokenTypeImpersonation = 2;
        public const int TokenPrivilegesClass = 3;
        public const uint SePrivilegeEnabled = 0x2;
        public const string SeShutdownName = "SeShutdownPrivilege";

        public const uint ShutdownRestart = 0x4;
        public const uint ShutdownPowerOff = 0x8;
        /// <summary>SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_MINOR_OTHER | SHTDN_REASON_FLAG_PLANNED: "Other (Planned)".</summary>
        public const uint ReasonOtherPlanned = 0x80000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        /// <summary>TOKEN_PRIVILEGES with exactly one LUID_AND_ATTRIBUTES (16 bytes).</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [DllImport("powrprof.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool GetPwrCapabilities([Out] byte[] capabilities);

        [DllImport("powrprof.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("powrprof.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid, int accessFlags, uint index, ref Guid buffer, ref uint bufferSize);

        [DllImport("powrprof.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr settingGuid, [Out] byte[]? buffer, ref uint bufferSize);

        [DllImport("powrprof.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LockWorkStation();

        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint InitiateShutdownW(string? machineName, string? message, uint gracePeriod, uint flags, uint reason);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr GetCurrentThread();

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenThreadToken(IntPtr thread, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint desiredAccess, IntPtr attributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle duplicate);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetThreadToken(IntPtr thread, SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RevertToSelf();

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, uint length, out uint returnLength);
    }
}
