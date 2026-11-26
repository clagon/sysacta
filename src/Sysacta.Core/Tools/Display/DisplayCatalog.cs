using System.ComponentModel;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Display;

public enum DisplaySupport
{
    /// <summary>Read from the device successfully just now.</summary>
    Supported,

    /// <summary>Definitely not available for this display.</summary>
    NotSupported,

    /// <summary>Could not be determined or used now (temporary failure or ambiguous mapping).</summary>
    Unavailable,

    /// <summary>Not queried; use display_get_state.</summary>
    Unchecked,
}

public sealed record DisplayFeatureSummary(DisplaySupport Support, string? ErrorCode);

public sealed record DisplayDeviceEntry(
    string Id,
    string Name,
    [property: Description("Only active displays are enumerated.")] string State,
    DisplayKind Kind,
    string Connection,
    bool IsPrimary,
    [property: Description("True when other displays mirror the same desktop.")] bool IsCloned,
    DisplayCurrentMode? CurrentMode,
    IReadOnlyList<string> Aliases,
    DisplayFeatureSummary Brightness,
    DisplayFeatureSummary Contrast,
    [property: Description("Read tools that can act on this display. Change support is reported by display_get_state.")] IReadOnlyList<string> Operations,
    string? ErrorCode);

public sealed record DisplayDeviceListData(
    IReadOnlyList<DisplayDeviceEntry> Devices,
    [property: Description("Number of active displays Windows reported, including ones not returned.")] int TotalCount,
    [property: Description("Active displays without a device identity; they cannot be listed or addressed.")] int UnidentifiedCount,
    [property: Description("True when displays were left out because of the result limit.")] bool Truncated);

/// <summary>Rules shared by the tools, the Windows adapter and the console's device page.</summary>
public static class DisplayCatalog
{
    public const int MaxDevices = 32;

    /// <summary>
    /// The failure for a feature that the topology alone already rules out, or null. Internal panels use Windows
    /// brightness control only; everything else uses DDC/CI, which cannot pick one monitor of a mirrored group.
    /// </summary>
    public static ToolFailureException? Blocked(DisplayInfo display, DisplayFeature feature) => display.Kind switch
    {
        DisplayKind.Virtual => new(ErrorCodes.NotSupported, "Virtual displays have no hardware brightness or contrast control."),
        DisplayKind.Internal when feature == DisplayFeature.Contrast => new(ErrorCodes.NotSupported, "Contrast control is not available for internal panels."),
        DisplayKind.Internal => null,
        _ when display.IsCloned => new(ErrorCodes.AmbiguousTarget, "The display is mirrored, so its physical monitor cannot be identified reliably."),
        _ => null,
    };

    /// <summary>
    /// The failure when the topology rules out a mode change: a mirrored source cannot be attributed to one monitor, and
    /// only internal and external monitors are changed (virtual, wireless and indirect displays are not).
    /// </summary>
    public static ToolFailureException? BlockedForMode(DisplayInfo display) => display switch
    {
        { IsCloned: true } => new(ErrorCodes.AmbiguousTarget, "The display is mirrored; its mode cannot be changed for one monitor."),
        { Kind: DisplayKind.Internal or DisplayKind.External } => null,
        _ => new(ErrorCodes.NotSupported, "Mode changes are supported for internal and external monitors only."),
    };

    /// <summary>
    /// The failure when the topology rules out the DDC/CI power and input settings: only external monitors, and only when
    /// the physical monitor can be identified (not mirrored).
    /// </summary>
    public static ToolFailureException? BlockedForSwitch(DisplayInfo display) => display switch
    {
        { Kind: not DisplayKind.External } => new(ErrorCodes.NotSupported, "Power and input control over DDC/CI is available for external monitors only."),
        { IsCloned: true } => new(ErrorCodes.AmbiguousTarget, "The display is mirrored, so its physical monitor cannot be identified reliably."),
        _ => null,
    };

    public static DisplaySupport SupportFor(string errorCode) =>
        errorCode == ErrorCodes.NotSupported ? DisplaySupport.NotSupported : DisplaySupport.Unavailable;

    public static DisplayDeviceListData List(IDisplays displays, IDeviceAliases aliases)
    {
        var topology = displays.GetTopology();
        var byDevice = aliases.List(ToolCategories.Display).ToLookup(a => a.DeviceId, a => a.Alias, StringComparer.OrdinalIgnoreCase);
        var entries = topology.Displays
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .Take(MaxDevices)
            .Select(d => new DisplayDeviceEntry(
                d.Id,
                d.Name,
                "active",
                d.Kind,
                d.Connection,
                d.IsPrimary,
                d.IsCloned,
                d.CurrentMode,
                [.. byDevice[d.Id]],
                Summary(d, DisplayFeature.Brightness),
                Summary(d, DisplayFeature.Contrast),
                d.IsCloned ? ["display_get_state"] : ["display_get_state", "display_list_modes"],
                d.ErrorCode))
            .ToList();
        var total = topology.Displays.Count + topology.UnidentifiedCount;
        return new DisplayDeviceListData(entries, total, topology.UnidentifiedCount, topology.Displays.Count > entries.Count);
    }

    private static DisplayFeatureSummary Summary(DisplayInfo display, DisplayFeature feature) =>
        Blocked(display, feature) is { } blocked
            ? new(SupportFor(blocked.Code), blocked.Code)
            : new(DisplaySupport.Unchecked, null);
}
