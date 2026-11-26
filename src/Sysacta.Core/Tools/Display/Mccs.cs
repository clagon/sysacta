using System.Security.Cryptography;
using System.Text;

namespace Sysacta.Core.Tools.Display;

/// <summary>The parts of a monitor's MCCS capabilities string that Sysacta uses.</summary>
/// <param name="MajorVersion">Major number of <c>mccs_ver</c>, or null when the string does not declare it.</param>
/// <param name="Vcp">Declared VCP codes and, when listed, their allowed values (null: no value list).</param>
public sealed record MccsCapabilities(int? MajorVersion, IReadOnlyDictionary<byte, IReadOnlyList<byte>?> Vcp);

/// <summary>Power states MCCS 2.x defines for VCP 0xD6 that can be read back.</summary>
public enum DisplayPowerState
{
    /// <summary>0x01: DPM on.</summary>
    On,

    /// <summary>0x02: DPMS standby.</summary>
    Standby,

    /// <summary>0x03: DPMS suspend.</summary>
    Suspend,

    /// <summary>0x04: DPM off (a power-saving state, not a power cut).</summary>
    Off,
}

/// <summary>
/// The fixed allow-list of DDC/CI settings and values (VESA MCCS 2.x, cross-checked with the value tables of ddcutil;
/// see docs/phase-1c-2-progress.md). Nothing outside this list is ever written, whatever a monitor declares.
/// </summary>
public static class Mccs
{
    /// <summary>Longer capabilities strings are refused before parsing.</summary>
    public const int MaxCapabilitiesLength = 8192;

    /// <summary>More inputs than this are not offered.</summary>
    public const int MaxInputs = 18;

    private const int MaxDepth = 4;
    private const int MaxNodes = 2048;

    public const byte PowerOn = 0x01;

    /// <summary>"DPM: Off, DPMS: Off". 0x05 (hard power off, often not wakeable over DDC/CI) is never used.</summary>
    public const byte PowerOff = 0x04;

    /// <summary>MCCS 2.x VCP 0x60 values whose meaning is defined by the standard (manufacturer values are excluded).</summary>
    private static readonly Dictionary<byte, string> InputNames = new()
    {
        [0x01] = "VGA 1",
        [0x02] = "VGA 2",
        [0x03] = "DVI 1",
        [0x04] = "DVI 2",
        [0x05] = "Composite 1",
        [0x06] = "Composite 2",
        [0x07] = "S-Video 1",
        [0x08] = "S-Video 2",
        [0x09] = "Tuner 1",
        [0x0A] = "Tuner 2",
        [0x0B] = "Tuner 3",
        [0x0C] = "Component 1",
        [0x0D] = "Component 2",
        [0x0E] = "Component 3",
        [0x0F] = "DisplayPort 1",
        [0x10] = "DisplayPort 2",
        [0x11] = "HDMI 1",
        [0x12] = "HDMI 2",
    };

    /// <summary>Whether Sysacta may ever write <paramref name="value"/> to <paramref name="setting"/>.</summary>
    public static bool IsAllowed(DisplaySwitch setting, byte value) => setting switch
    {
        DisplaySwitch.Power => value is PowerOn or PowerOff,
        DisplaySwitch.Input => InputNames.ContainsKey(value),
        _ => false,
    };

    public static byte PowerValue(DisplayPowerRequest state) => state == DisplayPowerRequest.On ? PowerOn : PowerOff;

    /// <summary>The state for a read value, or null when MCCS gives it no readable meaning.</summary>
    public static DisplayPowerState? PowerState(byte value) => value switch
    {
        0x01 => DisplayPowerState.On,
        0x02 => DisplayPowerState.Standby,
        0x03 => DisplayPowerState.Suspend,
        0x04 => DisplayPowerState.Off,
        _ => null,
    };

    public static string? InputName(byte value) => InputNames.GetValueOrDefault(value);

    /// <summary>Opaque id of an input of one display: bound to the display id and the MCCS value.</summary>
    public static string InputId(string displayId, byte value) =>
        "input-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{displayId}|60|{value:x2}"))).AsSpan(0, 12));

    /// <summary>
    /// Checks what the capabilities allow for <paramref name="setting"/> and returns the values Sysacta may use, in the
    /// monitor's order. Throws NOT_SUPPORTED when the monitor does not declare MCCS 2.x, the code, a value list, or
    /// (power) both on and off; nothing is inferred from a missing or partial declaration.
    /// </summary>
    public static IReadOnlyList<byte> Usable(MccsCapabilities capabilities, DisplaySwitch setting)
    {
        // MCCS 3.0 redefines 0x60 as a table; only 2.x semantics are known.
        if (capabilities.MajorVersion != 2)
        {
            throw new ToolFailureException(ErrorCodes.NotSupported, "The monitor does not declare MCCS version 2.x, so its power and input values are not used.");
        }

        var code = setting == DisplaySwitch.Power ? (byte)0xD6 : (byte)0x60;
        if (!capabilities.Vcp.TryGetValue(code, out var values) || values is null)
        {
            throw new ToolFailureException(ErrorCodes.NotSupported, setting == DisplaySwitch.Power
                ? "The monitor does not declare its power modes over DDC/CI."
                : "The monitor does not declare its input sources over DDC/CI.");
        }

        var usable = values.Where(v => IsAllowed(setting, v)).Distinct().ToArray();
        if (setting == DisplaySwitch.Power ? !(usable.Contains(PowerOn) && usable.Contains(PowerOff)) : usable.Length == 0)
        {
            throw new ToolFailureException(ErrorCodes.NotSupported, setting == DisplaySwitch.Power
                ? "The monitor does not declare both the on and off power modes."
                : "The monitor declares no input source with a standard meaning.");
        }

        return usable.Take(MaxInputs).ToArray();
    }

    /// <summary>Parses a capabilities string, or throws OS_API_ERROR when it is too long or malformed.</summary>
    public static MccsCapabilities ParseOrThrow(string text) =>
        Parse(text) ?? throw new ToolFailureException(ErrorCodes.OsApiError, "The monitor returned a capabilities string that cannot be parsed.");

    /// <summary>
    /// Parses <c>name(children)</c> lists with bounded length, depth and node count. Returns null for anything malformed:
    /// unbalanced parentheses, non-printable characters, a missing or repeated <c>vcp</c> list, codes or values that are
    /// not two hex digits, or repeated codes.
    /// </summary>
    public static MccsCapabilities? Parse(string text)
    {
        if (text.Length is 0 or > MaxCapabilitiesLength || text.Any(c => c is < ' ' or > '~'))
        {
            return null;
        }

        var position = 0;
        var nodes = 0;
        var items = ParseList(text, ref position, 0, ref nodes);
        if (items is null || position != text.Length)
        {
            return null;
        }

        // The whole string is usually wrapped in one pair of parentheses.
        if (items is [{ Name: "", Children: { } inner }])
        {
            items = inner;
        }

        var vcpNodes = items.Where(i => i.Name.Equals("vcp", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (vcpNodes is not [{ Children: { } codes }])
        {
            return null;
        }

        var vcp = new Dictionary<byte, IReadOnlyList<byte>?>();
        foreach (var code in codes)
        {
            if (Hex(code.Name) is not { } key || vcp.ContainsKey(key))
            {
                return null;
            }

            List<byte>? values = null;
            if (code.Children is { } children)
            {
                values = [];
                foreach (var child in children)
                {
                    if (child.Children is not null || Hex(child.Name) is not { } value)
                    {
                        return null;
                    }

                    values.Add(value);
                }
            }

            vcp[key] = values;
        }

        int? major = null;
        var versions = items.Where(i => i.Name.Equals("mccs_ver", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (versions is [{ Children: [{ Name: var version, Children: null }] }])
        {
            var dot = version.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && int.TryParse(version.AsSpan(0, dot), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var m))
            {
                major = m;
            }
        }

        return new MccsCapabilities(major, vcp);
    }

    private sealed record Node(string Name, List<Node>? Children);

    private static List<Node>? ParseList(string s, ref int i, int depth, ref int nodes)
    {
        var list = new List<Node>();
        while (i < s.Length)
        {
            if (s[i] == ' ')
            {
                i++;
                continue;
            }

            if (s[i] == ')')
            {
                return depth == 0 ? null : list;
            }

            var start = i;
            while (i < s.Length && s[i] is not (' ' or '(' or ')'))
            {
                i++;
            }

            var name = s[start..i];
            List<Node>? children = null;
            if (i < s.Length && s[i] == '(')
            {
                if (depth >= MaxDepth)
                {
                    return null;
                }

                i++;
                children = ParseList(s, ref i, depth + 1, ref nodes);
                if (children is null || i >= s.Length || s[i] != ')')
                {
                    return null;
                }

                i++;
            }

            if (++nodes > MaxNodes)
            {
                return null;
            }

            list.Add(new Node(name, children));
        }

        return depth == 0 ? list : null;
    }

    private static byte? Hex(string token) =>
        token.Length == 2 && byte.TryParse(token, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : null;
}
