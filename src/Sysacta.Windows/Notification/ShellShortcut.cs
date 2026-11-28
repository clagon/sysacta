using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Sysacta.Windows.Notification;

/// <summary>
/// A shortcut with System.AppUserModel.ID, the documented registration that lets a desktop app show notifications
/// (learn.microsoft.com/windows/win32/shell/enable-desktop-toast-with-appusermodelid). Call on an STA thread.
/// </summary>
internal static class ShellShortcut
{
    private const ushort VtLpwstr = 31;
    private const uint SlgpRawPath = 4;

    /// <summary>
    /// Creates the shortcut, or accepts an existing one with the same id (repointing it only when its target no longer
    /// exists, for example a moved portable copy). A file with another or no id, or one that cannot be read, belongs to
    /// someone else: it is left untouched and false is returned. Throws when reading the id or writing fails.
    /// </summary>
    public static bool Ensure(string path, string target, string appUserModelId)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            var file = (IPersistFile)link;
            if (File.Exists(path))
            {
                try
                {
                    file.Load(path, 0 /* STGM_READ */);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return false;
                }

                if (!string.Equals(ReadId(link), appUserModelId, StringComparison.Ordinal))
                {
                    return false;
                }

                var current = ReadTarget(link);
                if (current.Length == 0 || File.Exists(Environment.ExpandEnvironmentVariables(current)))
                {
                    return true;
                }

                link.SetPath(target);
                file.Save(path, true);
                return true;
            }

            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            WriteId((IPropertyStore)link, appUserModelId);
            file.Save(path, true);
            return true;
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(link);
        }
    }

    internal static (string? Id, string Target) Read(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            return (ReadId(link), ReadTarget(link));
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(link);
        }
    }

    private static string ReadTarget(IShellLinkW link)
    {
        var buffer = new StringBuilder(32_768);
        link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SlgpRawPath);
        return buffer.ToString();
    }

    private static string? ReadId(IShellLinkW link)
    {
        var key = PropertyKey.AppUserModelId;
        ((IPropertyStore)link).GetValue(ref key, out var value);
        try
        {
            return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            _ = PropVariantClear(ref value);
        }
    }

    private static void WriteId(IPropertyStore store, string id)
    {
        var key = PropertyKey.AppUserModelId;
        var value = new PropVariant { Type = VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(id) };
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            _ = PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;

        public static PropertyKey AppUserModelId => new() { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Padding;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    /// <summary>Only the used members are declared fully; the others only keep their vtable slots.</summary>
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, IntPtr findData, uint flags);
        void GetIDList();
        void SetIDList();
        void GetDescription();
        void SetDescription();
        void GetWorkingDirectory();
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments();
        void SetArguments();
        void GetHotkey();
        void SetHotkey();
        void GetShowCmd();
        void SetShowCmd();
        void GetIconLocation();
        void SetIconLocation();
        void SetRelativePath();
        void Resolve();
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount();
        void GetAt();
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
