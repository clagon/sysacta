using System.ComponentModel;

namespace Sysacta.Core.Tools.Display;

public enum DisplayKind
{
    Internal,
    External,
    Virtual,
    Unknown,
}

public enum DisplayFeature
{
    Brightness,
    Contrast,
}

/// <summary>How a value is read and written. Decided by the kind of display; there is no fallback between them.</summary>
public enum DisplayControlMethod
{
    /// <summary>Value held by the monitor, reached over DDC/CI (Monitor Configuration API).</summary>
    DdcCi,

    /// <summary>Internal panel brightness controlled by Windows (WMI).</summary>
    Wmi,
}

/// <summary>Resolution of the desktop source and refresh rate of the target. Not the panel's native resolution.</summary>
public sealed record DisplayCurrentMode(
    int Width,
    int Height,
    [property: Description("Refresh rate as the exact fraction Windows reports, e.g. 60000/1001. Null when unknown.")] long? RefreshNumerator,
    long? RefreshDenominator);

/// <summary>One physical target of the active desktop topology.</summary>
/// <param name="Id">Opaque id derived from the OS device path of the target (stable while the device and port stay the same).</param>
/// <param name="IsCloned">True when other targets show the same desktop source (mirrored).</param>
public sealed record DisplayInfo(
    string Id,
    string Name,
    DisplayKind Kind,
    string Connection,
    bool IsPrimary,
    bool IsCloned,
    DisplayCurrentMode? CurrentMode,
    string? ErrorCode = null);

/// <param name="UnidentifiedCount">Active targets Windows reported without a device identity; they cannot be addressed.</param>
public sealed record DisplayTopology(IReadOnlyList<DisplayInfo> Displays, int UnidentifiedCount);

/// <summary>A mode Windows enumerates for a desktop source. Refresh is the integer value Windows reports.</summary>
public sealed record DisplayMode(int Width, int Height, int RefreshHz, int BitsPerPixel, bool Interlaced, int Orientation, int FixedOutput);

public sealed record DisplayModeSet(IReadOnlyList<DisplayMode> Modes, DisplayMode? Current);

/// <summary>Native values reported by the device.</summary>
/// <param name="Allowed">The only values the device accepts, or null when every integer in the range is accepted.</param>
public sealed record LevelRange(long Min, long Max, long Current, IReadOnlyList<long>? Allowed = null)
{
    public int ToLevel(long native) => (int)Math.Round((native - Min) * 100.0 / (Max - Min), MidpointRounding.AwayFromZero);

    /// <summary>The native value for a public level 0-100: scaled to the range, then the nearest allowed value (ties: lower).</summary>
    public long NativeFor(int level)
    {
        var scaled = Min + (long)Math.Round(level * (double)(Max - Min) / 100.0, MidpointRounding.AwayFromZero);
        return Allowed is null ? scaled : Allowed.OrderBy(a => Math.Abs(a - scaled)).ThenBy(a => a).First();
    }

    /// <summary>Throws when the device data cannot be used to read or change the value.</summary>
    public LevelRange Validated()
    {
        if (Max == Min)
        {
            throw new ToolFailureException(ErrorCodes.NotSupported, "The display reports no adjustable range.");
        }

        if (Max < Min || Current < Min || Current > Max || (Allowed is not null && (Allowed.Count == 0 || Allowed.Any(a => a < Min || a > Max))))
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The display reported an invalid value range.");
        }

        return this;
    }
}

/// <summary>
/// Exclusive, short-lived access to one value of one display. While it is open no other DDC/CI or panel access runs, so
/// a read, a write and the read-back are not interleaved with other callers. Handles are released by Dispose.
/// </summary>
public interface IDisplayControl : IDisposable
{
    DisplayControlMethod Method { get; }

    /// <summary>Reads the current value and range from the device.</summary>
    LevelRange Read();

    /// <summary>Writes a native value. A failure does not prove the device is unchanged.</summary>
    void Write(long native);
}

/// <summary>
/// OS adapter for displays. Calls are synchronous and may be slow. Failures are <see cref="ToolFailureException"/>:
/// NOT_SUPPORTED only for a definite lack of support, DEVICE_DISCONNECTED / OS_API_ERROR for temporary trouble.
/// </summary>
public interface IDisplays
{
    /// <summary>The active topology. Cheap: never queries the monitors themselves (no DDC/CI, no WMI).</summary>
    DisplayTopology GetTopology();

    /// <summary>Modes of the desktop source showing this display. Does not apply or test any mode.</summary>
    DisplayModeSet ListModes(string displayId);

    /// <summary>
    /// Waits (until cancelled) for exclusive access, re-resolves the display in the current topology and opens it.
    /// Never opens a different display than <paramref name="displayId"/>.
    /// </summary>
    IDisplayControl Open(string displayId, DisplayFeature feature, CancellationToken cancellationToken);
}
