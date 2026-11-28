using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Power;
using Sysacta.Windows.Power;
using Native = Sysacta.Windows.Power.WindowsPower.Native;

namespace Sysacta.Windows.Tests;

/// <summary>
/// Read-only checks of the Win32 power adapter and of the privilege scope. Nothing here locks, sleeps, shuts down,
/// restarts or changes the power plan: <see cref="WindowsPower.Request"/> and <see cref="WindowsPower.SetActivePlan"/>
/// are never called, and the privilege-scope tests pass a request delegate that only inspects tokens.
/// </summary>
public class WindowsPowerTests
{
    [Fact]
    public void Native_struct_layouts_match_the_SDK()
    {
        Assert.Equal(12, Marshal.SizeOf<Native.SystemPowerStatus>());
        Assert.Equal(16, Marshal.SizeOf<Native.TokenPrivileges>());
        Assert.Equal(8, Marshal.SizeOf<Native.Luid>());
    }

    [Fact]
    public void Reads_status_capabilities_and_plans_without_changing_them()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var power = new WindowsPower();

        var status = PowerStatus.Interpret(power.ReadStatus(), null, null);
        Assert.NotEqual(PowerSource.Unknown, status.AcPower);
        _ = power.ReadCapabilities();
        var active = power.ReadActivePlan();
        var plans = power.ListPlans(PowerStatus.MaxEnumerated);
        Assert.Contains(plans.Plans, p => p.PlanId == active);
        Assert.Equal(active, power.ReadActivePlan()); // unchanged
    }

    [Fact]
    public void The_privilege_is_enabled_only_on_the_calling_thread_during_the_request_and_reverted_after()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(new WindowsPower().HoldsShutdownPrivilege(), "The account does not hold SeShutdownPrivilege");
        var processBefore = ProcessShutdownAttributes();
        Assert.False(HasThreadToken());
        var order = new List<string>();
        bool? threadEnabledDuringRequest = null;
        uint? processDuringRequest = null;

        var result = WindowsPower.WithShutdownPrivilege(
            () => order.Add("begin"),
            () =>
            {
                order.Add("request");
                threadEnabledDuringRequest = ThreadShutdownEnabled();
                processDuringRequest = ProcessShutdownAttributes();
                return PowerRequestResult.Failed; // stands for the OS request; nothing is requested
            });

        Assert.Equal(PowerRequestResult.Failed, result);
        Assert.Equal(["begin", "request"], order);
        Assert.True(threadEnabledDuringRequest);
        Assert.Equal(processBefore, processDuringRequest); // the process token is never modified
        Assert.False(HasThreadToken());
        Assert.Equal(processBefore, ProcessShutdownAttributes());
    }

    [Fact]
    public void A_refusal_at_the_start_point_requests_nothing_and_leaves_no_impersonation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(new WindowsPower().HoldsShutdownPrivilege(), "The account does not hold SeShutdownPrivilege");
        var processBefore = ProcessShutdownAttributes();
        var requested = false;

        Assert.Throws<OperationCanceledException>(() => WindowsPower.WithShutdownPrivilege(
            () => throw new OperationCanceledException(),
            () =>
            {
                requested = true;
                return PowerRequestResult.Accepted;
            }));

        Assert.False(requested);
        Assert.False(HasThreadToken());
        Assert.Equal(processBefore, ProcessShutdownAttributes());
    }

    [Fact]
    public void An_exception_in_the_request_still_reverts_the_impersonation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(new WindowsPower().HoldsShutdownPrivilege(), "The account does not hold SeShutdownPrivilege");

        Assert.Throws<InvalidOperationException>(() => WindowsPower.WithShutdownPrivilege(() => { }, () => throw new InvalidOperationException()));

        Assert.False(HasThreadToken());
    }

    private static bool HasThreadToken()
    {
        var has = Native.OpenThreadToken(Native.GetCurrentThread(), Native.TokenQuery, true, out var token);
        token.Dispose();
        return has;
    }

    private static bool ThreadShutdownEnabled()
    {
        Assert.True(Native.OpenThreadToken(Native.GetCurrentThread(), Native.TokenQuery, true, out var token));
        using (token)
        {
            return (ShutdownAttributes(token) & Native.SePrivilegeEnabled) != 0;
        }
    }

    private static uint ProcessShutdownAttributes()
    {
        Assert.True(Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenQuery, out var token));
        using (token)
        {
            return ShutdownAttributes(token);
        }
    }

    private static uint ShutdownAttributes(SafeAccessTokenHandle token)
    {
        Assert.True(Native.LookupPrivilegeValueW(null, Native.SeShutdownName, out var luid));
        Native.GetTokenInformation(token, Native.TokenPrivilegesClass, IntPtr.Zero, 0, out var length);
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            Assert.True(Native.GetTokenInformation(token, Native.TokenPrivilegesClass, buffer, length, out _));
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var offset = 4 + (i * 12);
                if ((uint)Marshal.ReadInt32(buffer, offset) == luid.LowPart && Marshal.ReadInt32(buffer, offset + 4) == luid.HighPart)
                {
                    return (uint)Marshal.ReadInt32(buffer, offset + 8);
                }
            }

            throw new InvalidOperationException("SeShutdownPrivilege not found.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
