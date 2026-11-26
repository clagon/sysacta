using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Audio;

/// <summary>Device information returned by audio tools.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, AudioDirection Direction, AudioDeviceState State, bool IsDefault);

/// <summary>
/// Target resolution shared by the audio tools (spec §8): explicit id or alias, otherwise the default device of the
/// direction. Never falls back to another device.
/// </summary>
public static class AudioTarget
{
    public static ToolTarget Resolve(IAudioEndpoints endpoints, IDeviceAliases aliases, string? device, AudioDirection direction)
    {
        if (device is null)
        {
            var defaultId = endpoints.GetDefaultEndpointId(direction)
                ?? throw new ToolFailureException(ErrorCodes.TargetNotFound, $"There is no default {Name(direction)} device.");

            // Lock both the resolved device and the multimedia default slot so default-device changes serialize with default-targeted calls.
            return new ToolTarget([DeviceKey(defaultId), DefaultRoleKey(direction, AudioRole.Multimedia)], defaultId);
        }

        var state = ResolveExplicit(endpoints, aliases, device, direction);

        // Lock and act on the id the OS returned, not the caller's spelling: Windows can resolve several
        // representations (casing, StableId on newer builds) to the same endpoint, and each would otherwise get its own lock.
        return new ToolTarget([DeviceKey(state.Id)], state.Id);
    }

    public static AudioEndpointState ResolveExplicit(IAudioEndpoints endpoints, IDeviceAliases aliases, string device, AudioDirection direction)
    {
        if (DeviceAliasRules.Reserved.Contains(device))
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "Reserved device names such as 'all' or 'default' are not supported; omit 'device' to use the default device.");
        }

        var id = aliases.Resolve(ToolCategories.Audio, device) ?? device;
        var state = endpoints.GetEndpoint(id);
        if (state.Direction != direction)
        {
            throw new ToolFailureException(ErrorCodes.TargetNotFound, $"The specified device is not an {Name(direction)} device.");
        }

        return state;
    }

    /// <summary>
    /// Re-verifies under the resource lock that a default-targeted call still targets the current multimedia default.
    /// Prevents writing to the wrong device when a default-device change ran while this call was waiting.
    /// </summary>
    public static void EnsureStillDefault(IAudioEndpoints endpoints, string? device, AudioDirection direction, string resolvedId)
    {
        if (device is null && endpoints.GetDefaultEndpointId(direction, AudioRole.Multimedia) != resolvedId)
        {
            throw new ToolFailureException(
                ErrorCodes.TargetChanged,
                "The default device changed while the request was waiting. Nothing was changed; read the devices again and retry.",
                retryable: true);
        }
    }

    public static AudioDeviceInfo Describe(IAudioEndpoints endpoints, AudioEndpointState state) =>
        new(state.Id, state.Name, state.Direction, state.State, endpoints.GetDefaultEndpointId(state.Direction) == state.Id);

    public static int ToLevel(float scalar) => (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100, MidpointRounding.AwayFromZero);

    public static string DeviceKey(string id) => $"audio:device:{id}";

    public static string DefaultRoleKey(AudioDirection direction, AudioRole role) => $"audio:default:{Name(direction)}:{RoleName(role)}";

    private static string Name(AudioDirection direction) => direction == AudioDirection.Output ? "output" : "input";

    private static string RoleName(AudioRole role) => role switch
    {
        AudioRole.Console => "console",
        AudioRole.Multimedia => "multimedia",
        AudioRole.Communications => "communications",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}
