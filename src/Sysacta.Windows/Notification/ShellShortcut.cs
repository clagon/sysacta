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
    /// Creates the shortcut, or accepts an existing one only when it already is this copy's registration (same id, same
    /// target, no arguments). One with our id whose target no longer exists (a moved portable copy) is repointed.
    /// Anything else, including an unreadable file, an empty target or another existing copy, belongs to someone else:
    /// it is left untouched and false is returned. Throws when reading or writing fails.
    /// </summary>
    public static bool Ensure(string path, string target, string appUserModelId)
    {
        if (!File.Exists(path))
        {
            Create(path, target, appUserModelId);
            return true;
        }

        return Open(path, link =>
        {
            if (link is null || !string.Equals(ReadId(link), appUserModelId, StringComparison.Ordinal) || ReadArguments(link).Length != 0)
            {
                return false;
            }

            var current = ReadTarget(link);
            if (SameFile(current, target))
            {
                return true;
            }

            if (current.Length == 0 || File.Exists(Environment.ExpandEnvironmentVariables(current)))
            {
                return false;
            }

            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            ((IPersistFile)link).Save(path, true);
            return true;
        });
    }

    /// <summary>Read-only: true when the shortcut is this copy's registration (same id and target, no arguments).</summary>
    public static bool IsRegistration(string path, string target, string appUserModelId) =>
        File.Exists(path) && Open(path, link => link is not null
            && string.Equals(ReadId(link), appUserModelId, StringComparison.Ordinal)
            && ReadArguments(link).Length == 0
            && SameFile(ReadTarget(link), target));

    /// <summary>
    /// Read-only, for app discovery: the raw target path and the arguments, or null when the file is not a readable
    /// shortcut. Never resolves the link (no search, network access or UI) and never runs anything.
    /// </summary>
    internal static (string Target, string Arguments)? ReadLink(string path) =>
        Open(path, link => link is null ? ((string, string)?)null : (ReadTarget(link), ReadArguments(link)));

    /// <summary>Writes a new shortcut. An empty <paramref name="target"/> is only used by tests.</summary>
    internal static void Create(string path, string target, string appUserModelId, string arguments = "")
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            if (target.Length != 0)
            {
                link.SetPath(target);
                link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            }

            link.SetArguments(arguments);
            WriteId((IPropertyStore)link, appUserModelId);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(link);
        }
    }

    internal static (string? Id, string Target, string WorkingDirectory) Read(string path) => Open(path, link =>
    {
        var directory = new StringBuilder(32_768);
        link!.GetWorkingDirectory(directory, directory.Capacity);
        return (ReadId(link), ReadTarget(link), directory.ToString());
    });

    /// <summary>Loads the file read-only; <paramref name="use"/> receives null when it is not a readable shortcut.</summary>
    private static T Open<T>(string path, Func<IShellLinkW?, T> use)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            var loaded = true;
            try
            {
                ((IPersistFile)link).Load(path, 0 /* STGM_READ */);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                loaded = false;
            }

            return use(loaded ? link : null);
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(link);
        }
    }

    private static bool SameFile(string current, string target) =>
        current.Length != 0 && string.Equals(Environment.ExpandEnvironmentVariables(current), target, StringComparison.OrdinalIgnoreCase);

    private static string ReadArguments(IShellLinkW link)
    {
        var buffer = new StringBuilder(32_768);
        link.GetArguments(buffer, buffer.Capacity);
        return buffer.ToString();
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
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
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
