using System.Security.Principal;

namespace Sysacta.App.Platform;

internal static class Privilege
{
    /// <summary>True when the process token is elevated (Administrators group enabled).</summary>
    public static bool IsElevated { get; } = Detect();

    private static bool Detect()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
