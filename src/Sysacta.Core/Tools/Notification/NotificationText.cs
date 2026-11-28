using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;

namespace Sysacta.Core.Tools.Notification;

/// <summary>
/// Plain notification text. Rejects (never repairs or truncates) text that is blank, is not valid UTF-16, contains
/// control characters (a body may contain LF / CRLF), line or paragraph separators, noncharacters or invisible format
/// characters such as bidirectional controls, or that Windows could read as a resource reference or data binding.
/// ZWNJ, ZWJ and emoji tag sequences stay usable. Length is limited separately (UTF-16 code units).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotificationTextAttribute(bool multiline = false) : ValidationAttribute
{
    private const int BlackFlag = 0x1F3F4;
    private const int CancelTag = 0xE007F;

    public override bool IsValid(object? value) => value is not string s || IsAllowed(s, multiline);

    public static bool IsAllowed(string text, bool multiline)
    {
        var visible = false;
        var inTagSequence = false;
        var previous = 0;
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var length) != OperationStatus.Done)
            {
                return false; // unpaired surrogate
            }

            var c = rune.Value;
            i += length;
            if (c is >= 0xE0020 and <= CancelTag)
            {
                // Invisible tag characters only as part of an emoji flag: black flag, tags, cancel tag.
                if (!(inTagSequence || previous == BlackFlag) || (c == CancelTag && !inTagSequence))
                {
                    return false;
                }

                inTagSequence = c != CancelTag;
                previous = c;
                continue;
            }

            if (inTagSequence)
            {
                return false;
            }

            previous = c;
            if (multiline && (c == '\n' || (c == '\r' && i < text.Length && text[i] == '\n')))
            {
                continue;
            }

            if ((c & 0xFFFE) == 0xFFFE || c is >= 0xFDD0 and <= 0xFDEF)
            {
                return false; // noncharacter (U+FFFE / U+FFFF are not even valid XML)
            }

            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Format when c is not (0x200C or 0x200D):
                    return false;
                case UnicodeCategory.Format:
                case UnicodeCategory.SpaceSeparator:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                    break;
                default:
                    visible = true;
                    break;
            }
        }

        var trimmed = text.Trim();
        return visible && !inTagSequence
            && !trimmed.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase)
            && !(trimmed.StartsWith('{') && trimmed.EndsWith('}'));
    }
}
