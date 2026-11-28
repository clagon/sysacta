using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Sysacta.Core.Tools.Apps;

/// <summary>Why an executable cannot be registered or launched. Shown to the local user; over MCP only as the fixed status.</summary>
public enum ExecutableRefusal
{
    None,
    /// <summary>Not an absolute <c>X:\...</c> path in plain Win32 form (relative, UNC, device, ADS, 8.3 alias, dot segments...).</summary>
    InvalidPath,
    /// <summary>Not an .exe file.</summary>
    NotExe,
    /// <summary>A shell, script host, interpreter, launcher, installer or administration tool (by name or version resource).</summary>
    DeniedProgram,
    /// <summary>Under the Windows directory, a packaged app folder, or Sysacta's own folder.</summary>
    ProtectedLocation,
    /// <summary>Not on a local fixed drive.</summary>
    NotLocalFixedDrive,
    /// <summary>The file or a parent folder is a reparse point (junction, symbolic link, app execution alias...).</summary>
    ReparsePoint,
    /// <summary>The handle's final path differs from the path (alias, 8.3 name, or the path changed).</summary>
    PathMismatch,
    NotFound,
    /// <summary>Not a 32/64-bit executable image for a supported machine and subsystem.</summary>
    NotPortableExecutable,
    TooLarge,
    /// <summary>The file differs from the approved one (other file id, size or SHA-256).</summary>
    IdentityChanged,
    /// <summary>Access was denied, the file is locked, or reading failed.</summary>
    Unreadable,
}

/// <summary>
/// OS-independent part of the executable safety rules, applied identically at registration and before every launch.
/// The name list is a supplementary defense, not the boundary: the boundary is the local user's explicit approval of
/// one exact file (identity + hash) with fixed arguments.
/// </summary>
public static class ExecutablePolicy
{
    /// <summary>
    /// Generic shells, script hosts, interpreters and runtimes, launchers that run other programs or URLs, and system
    /// administration tools. Compared with the file name and the version resource's original/internal name (first part
    /// before a dot, case-insensitive), so a renamed copy of a known binary is still refused.
    /// </summary>
    private static readonly HashSet<string> DeniedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "command", "powershell", "powershell_ise", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32",
        "msiexec", "py", "pyw", "node", "deno", "bun", "wsl", "wslhost", "bash", "sh", "zsh", "wt", "windowsterminal",
        "conhost", "openconsole", "explorer", "msbuild", "installutil", "regasm", "regsvcs", "csc", "vbc", "jsc", "dotnet",
        "wmic", "schtasks", "sc", "reg", "regedit", "regedt32", "certutil", "bitsadmin", "runas", "psexec", "psexec64",
        "mmc", "taskmgr", "cmstp", "forfiles", "hh", "msdt", "curl", "wget", "git", "git-bash", "ssh", "scp", "sudo",
        "cmdkey", "netsh", "bcdedit", "vssadmin", "wevtutil", "msxsl", "odbcconf", "pcalua", "presentationhost",
        "ieexec", "infdefaultinstall", "control", "sysacta", "electron",
    };

    /// <summary>Interpreter families with versioned file names (python3.12, perl5.38, java...).</summary>
    private static readonly string[] DeniedPrefixes = ["python", "pypy", "ruby", "perl", "php", "java", "lua", "tclsh", "wish", "powershell", "pwsh"];

    /// <summary>Installers, uninstallers and updaters (Squirrel's Update.exe runs arbitrary packaged commands).</summary>
    private static readonly string[] DeniedSubstrings = ["setup", "install", "unins", "update"];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// String rules: a plain absolute path <c>X:\dir\name.exe</c> below MAX_PATH, without UNC/device prefixes, alternate
    /// data streams, wildcards, quotes, dot segments, trailing dots/spaces (silently stripped by Win32), reserved
    /// device names, '~' (8.3 aliases), environment variables or control characters; the file name not denied.
    /// The OS checks (handle, final path, drive type, reparse points, PE, identity) follow in the adapter.
    /// </summary>
    public static ExecutableRefusal CheckPath(string? path)
    {
        if (path is null || path.Length is < 8 or > AppRules.MaxPathLength
            || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            || path.IndexOfAny(['/', '"', '<', '>', '|', '?', '*', '%', '~'], 3) >= 0
            || path.IndexOf(':', 2) >= 0)
        {
            return ExecutableRefusal.InvalidPath;
        }

        var segments = path[3..].Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') || segment.StartsWith(' ')
                || ReservedDeviceNames.Contains(segment.Split('.')[0].TrimEnd()))
            {
                return ExecutableRefusal.InvalidPath;
            }
        }

        var fileName = segments[^1];
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || fileName.Length <= 4)
        {
            return ExecutableRefusal.NotExe;
        }

        if (segments.Any(s => s.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)))
        {
            return ExecutableRefusal.ProtectedLocation;
        }

        return IsDeniedName(fileName) ? ExecutableRefusal.DeniedProgram : ExecutableRefusal.None;
    }

    /// <summary>File name or version resource name (e.g. <c>PowerShell.EXE.MUI</c>): compares the part before the first dot.</summary>
    public static bool IsDeniedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var stem = name.Trim().Split('.')[0];
        return DeniedNames.Contains(stem)
            || DeniedPrefixes.Any(p => stem.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || DeniedSubstrings.Any(s => stem.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Bytes of the file start needed by <see cref="IsLaunchableImage"/>.</summary>
    public const int HeaderBytes = 4096;

    /// <summary>
    /// True for an executable image (not a DLL) for x86, x64 or ARM64 with the Windows GUI or console subsystem, judged
    /// from the DOS and PE headers. A file renamed to .exe, a DLL, a driver or a truncated file is refused.
    /// </summary>
    public static bool IsLaunchableImage(ReadOnlySpan<byte> header)
    {
        if (header.Length < 0x40 || header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            return false;
        }

        var pe = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        // PE signature (4) + COFF header (20) + optional header up to Subsystem (70).
        if (pe < 0x40 || pe > header.Length - 94)
        {
            return false;
        }

        if (!header.Slice(pe, 4).SequenceEqual("PE\0\0"u8))
        {
            return false;
        }

        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 4)..]);
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 22)..]);
        var optionalMagic = BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 24)..]);
        var subsystem = BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 24 + 68)..]);
        const ushort ExecutableImage = 0x0002, Dll = 0x2000, System = 0x1000;
        return machine is 0x014C or 0x8664 or 0xAA64
            && (characteristics & ExecutableImage) != 0 && (characteristics & (Dll | System)) == 0
            && optionalMagic is 0x10B or 0x20B
            && subsystem is 2 or 3;
    }

    /// <summary>
    /// Command line for CreateProcessW: the quoted program path followed by each fixed argument, quoted so that
    /// CommandLineToArgvW and the Microsoft C runtime parse back exactly the given list (empty arguments, spaces, quotes
    /// and backslashes before quotes are handled). No shell is involved, so cmd metacharacters have no meaning. The
    /// target app may still interpret its own arguments in any way.
    /// </summary>
    public static string BuildCommandLine(string path, IReadOnlyList<string> args)
    {
        if (path.Contains('"'))
        {
            throw new ArgumentException("The program path cannot contain quotes.", nameof(path));
        }

        var builder = new StringBuilder().Append('"').Append(path).Append('"');
        foreach (var arg in args)
        {
            builder.Append(' ');
            AppendQuoted(builder, arg);
        }

        return builder.ToString();
    }

    private static void AppendQuoted(StringBuilder builder, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            builder.Append(arg);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless they precede a quote: then each is doubled and the quote escaped.
            builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');
    }
}

/// <summary>
/// The exact process creation time used with a pid (spec §7.6, PID reuse): UTC FILETIME (100 ns ticks since 1601)
/// written as <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>. That format represents every FILETIME losslessly; input must be
/// exactly that text (no other precision, offset or rounding), compared as the same 100 ns value.
/// </summary>
public static class ProcessStartTime
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    public const string Pattern = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{7}Z$";

    public static string ToText(long fileTimeUtc) =>
        DateTime.FromFileTimeUtc(fileTimeUtc).ToString(Format, CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out long fileTimeUtc)
    {
        fileTimeUtc = 0;
        if (text is not { Length: 28 }
            || !DateTime.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            || value.Year < 1601)
        {
            return false;
        }

        fileTimeUtc = value.ToFileTimeUtc();
        return string.Equals(ToText(fileTimeUtc), text, StringComparison.Ordinal);
    }
}
