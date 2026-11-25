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

            // Also lock the "default" slot so a future default-device change serializes with default-targeted calls.
            return new ToolTarget([DeviceKey(defaultId), $"audio:default:{Name(direction)}"], defaultId);
        }

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

        return new ToolTarget([DeviceKey(id)], id);
    }

    public static AudioDeviceInfo Describe(IAudioEndpoints endpoints, AudioEndpointState state) =>
        new(state.Id, state.Name, state.Direction, state.State, endpoints.GetDefaultEndpointId(state.Direction) == state.Id);

    public static int ToLevel(float scalar) => (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100, MidpointRounding.AwayFromZero);

    private static string DeviceKey(string id) => $"audio:device:{id}";

    private static string Name(AudioDirection direction) => direction == AudioDirection.Output ? "output" : "input";
}
