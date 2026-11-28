using System.Globalization;

namespace Sysacta.Core.Tools.Apps;

/// <summary>
/// The identity of an executable file as read from an open handle: volume serial and 128-bit file id (which name the
/// file object itself, so a rename, a hard link or a replaced parent folder cannot point the same id at other bytes),
/// size, and SHA-256 of the content. Hex, lowercase. Never returned over MCP or written to the audit log.
/// </summary>
public sealed record ExecutableIdentity(string VolumeSerial, string FileId, long Size, string Sha256)
{
    /// <summary>Same file object (volume + file id); says nothing about the content.</summary>
    public bool SameFile(ExecutableIdentity other) =>
        string.Equals(VolumeSerial, other.VolumeSerial, StringComparison.Ordinal) && string.Equals(FileId, other.FileId, StringComparison.Ordinal);

    public bool IsWellFormed() => AppRules.IsHex(VolumeSerial, 16) && AppRules.IsHex(FileId, 32) && Size > 0 && AppRules.IsHex(Sha256, 64);
}

/// <summary>
/// A registered app in the current format, approved by the local user for one exact executable and fixed argument list.
/// <see cref="Generation"/> changes whenever the approved identity or arguments change, so a call that verified one
/// generation can never run another.
/// </summary>
/// <param name="Path">Canonical absolute path (the final path of the file handle at approval). Local display only.</param>
public sealed record RegisteredApp(string Id, string DisplayName, string Path, IReadOnlyList<string> FixedArgs, bool Allowed, long Generation, ExecutableIdentity Identity)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Read side of the app allow-list, as seen by the tools. There is deliberately no write operation: registration,
/// approval, allowing, disabling and deletion exist only in the local console (spec §6, §7.6).
/// Implementations read the persisted state on every call (no caching), so a disable or delete applies immediately.
/// </summary>
public interface IAppRegistry
{
    /// <summary>Allowed apps in the current format, ordered by id. Unknown, legacy or corrupt rows are never returned.</summary>
    IReadOnlyList<RegisteredApp> ListAllowed();

    /// <summary>
    /// True while the app exists, is allowed and still has <paramref name="generation"/>. Called inside the authorization
    /// gate at the start point, so it must be one short read.
    /// </summary>
    bool IsCurrent(string appId, long generation);
}

/// <summary>Formats and limits shared by the registry, the tools and the console.</summary>
public static class AppRules
{
    public const int MaxApps = 256;
    public const int MaxDisplayName = 64;
    public const int MaxFixedArgs = 16;
    public const int MaxFixedArgLength = 256;
    public const int MaxFixedArgsTotal = 2048;

    /// <summary>Paths stay below MAX_PATH so CreateProcessW's lpApplicationName never depends on long-path support.</summary>
    public const int MaxPathLength = 259;

    /// <summary>Files are hashed completely before every launch; larger executables are not supported.</summary>
    public const long MaxExecutableBytes = 512L * 1024 * 1024;

    public const string AppIdPattern = "^app_[0-9a-f]{32}$";

    /// <summary>The only registration kind: a local Win32 executable (stored in the database and returned as <c>kind</c>).</summary>
    public const string Win32ExeKind = "win32_exe";

    public static string NewAppId() => "app_" + Guid.NewGuid().ToString("N");

    public static bool IsAppId(string? value) => value is { Length: 36 } && value.StartsWith("app_", StringComparison.Ordinal) && IsHex(value[4..], 32);

    internal static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Returns a Japanese message for the local user, or null when valid. The name is display text only.</summary>
    public static string? ValidateDisplayName(string name)
    {
        if (name.Length is 0 or > MaxDisplayName || string.IsNullOrWhiteSpace(name) || name.Any(IsUnsafeChar) || name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return $"表示名は 1〜{MaxDisplayName} 文字で、制御文字・書式文字・「/ \\ :」を含めないでください。";
        }

        return null;
    }

    /// <summary>
    /// Fixed arguments are a structured list approved locally; there is no command-line string to interpret. Returns a
    /// Japanese message for the local user, or null when valid. Empty strings are allowed (quoted as "").
    /// </summary>
    public static string? ValidateFixedArgs(IReadOnlyList<string> args)
    {
        if (args.Count > MaxFixedArgs)
        {
            return $"固定引数は {MaxFixedArgs} 個までです。";
        }

        if (args.Any(a => a.Length > MaxFixedArgLength) || args.Sum(a => a.Length) > MaxFixedArgsTotal)
        {
            return $"固定引数は 1 個 {MaxFixedArgLength} 文字以内、合計 {MaxFixedArgsTotal} 文字以内にしてください。";
        }

        if (args.Any(a => a.Any(IsUnsafeChar) || !IsValidUtf16(a)))
        {
            return "固定引数に制御文字・書式文字・不正な文字を含めることはできません。";
        }

        return null;
    }

    private static bool IsUnsafeChar(char c) =>
        char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

    private static bool IsValidUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}
