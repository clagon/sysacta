using System.Text.RegularExpressions;

namespace Sysacta.Core.Tools;

/// <summary>
/// Display text from the OS or other apps (media titles, power plan names) before it reaches an MCP response. Control and
/// format characters are removed, then the cleaned value is checked: anything that looks like a path (any '/' or '\',
/// a drive letter), an absolute URI or a known scheme is omitted, because an omission is better than leaking a path.
/// </summary>
public static partial class UntrustedText
{
    public static string? Sanitize(string? value, int maxLength)
    {
        if (value is null || value.Length > 4096 || string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        if (cleaned.IndexOfAny(['/', '\\']) >= 0 || Uri.TryCreate(cleaned, UriKind.Absolute, out _) || SensitiveText().IsMatch(cleaned)) return null;
        var result = new string(cleaned.Take(maxLength).ToArray());
        if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result = result[..^1];
        return result.Length == 0 ? null : result;
    }

    [GeneratedRegex(@"(?i)(?:(?:^|\s)[a-z]:|\b(?:file|mailto|data|javascript|http|https|ftp|smb):)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveText();
}
