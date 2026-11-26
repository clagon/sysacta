using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Display;

/// <summary>Target resolution shared by the display tools (spec §8). Never falls back to another display.</summary>
public static class DisplayTarget
{
    /// <summary>The only multi-display target, accepted by display_set_brightness alone.</summary>
    public const string All = "all";

    public static string DeviceKey(string id) => $"display:device:{id}";

    /// <summary>The display named by an id or alias, or the primary display when <paramref name="device"/> is null.</summary>
    public static DisplayInfo Resolve(DisplayTopology topology, IDeviceAliases aliases, string? device)
    {
        if (device is null)
        {
            return Single([.. topology.Displays.Where(d => d.IsPrimary)], ErrorCodes.TargetNotFound, "There is no primary display that can be identified.",
                "The primary display consists of several monitors; specify 'device'.");
        }

        if (DeviceAliasRules.Reserved.Contains(device))
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "This reserved device name is not supported by this tool; omit 'device' to use the primary display.");
        }

        var aliased = aliases.Resolve(ToolCategories.Display, device);
        var id = aliased ?? device;
        return Single(
            Matching(topology, id),
            aliased is null ? ErrorCodes.TargetNotFound : ErrorCodes.DeviceDisconnected,
            aliased is null ? "The specified display was not found." : "The display registered under this alias is not connected.",
            "The specified display cannot be identified uniquely.");
    }

    /// <summary>The display with this resolved id in the current topology (re-check after waiting for locks).</summary>
    public static DisplayInfo Current(DisplayTopology topology, string id) =>
        Single(Matching(topology, id), ErrorCodes.DeviceDisconnected, "The display is no longer connected.", "The display can no longer be identified uniquely.");

    /// <summary>A primary-targeted call must still be aimed at the primary display after waiting.</summary>
    public static void EnsureStillPrimary(DisplayTopology topology, string? device, string resolvedId)
    {
        if (device is null && (topology.Displays.Where(d => d.IsPrimary).Select(d => d.Id).ToArray() is not [var id] || id != resolvedId))
        {
            throw new ToolFailureException(
                ErrorCodes.TargetChanged,
                "The primary display changed while the request was waiting. Nothing was changed; read the displays again and retry.",
                retryable: true);
        }
    }

    private static DisplayInfo[] Matching(DisplayTopology topology, string id) =>
        [.. topology.Displays.Where(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))];

    private static DisplayInfo Single(DisplayInfo[] matches, string noneCode, string noneMessage, string manyMessage) => matches.Length switch
    {
        1 => matches[0],
        0 => throw new ToolFailureException(noneCode, noneMessage),
        _ => throw new ToolFailureException(ErrorCodes.AmbiguousTarget, manyMessage),
    };
}
