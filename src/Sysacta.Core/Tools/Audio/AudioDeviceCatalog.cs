using System.ComponentModel;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Audio;

public sealed record AudioDeviceEntry(
    string Id,
    string Name,
    AudioDirection Direction,
    AudioDeviceState State,
    [property: Description("True when this is the default device used when 'device' is omitted (multimedia role).")] bool IsDefault,
    [property: Description("Every Windows role this device is the default for.")] IReadOnlyList<AudioRole> DefaultRoles,
    [property: Description("Master volume 0-100, null when the device is not active or it cannot be read.")] int? Volume,
    bool? Muted,
    IReadOnlyList<string> Aliases,
    [property: Description("Tools that can act on this device in its current state (client permissions still apply).")] IReadOnlyList<string> Operations,
    [property: Description("Set when some details of this device could not be read.")] string? ErrorCode);

public sealed record AudioDeviceListData(
    IReadOnlyList<AudioDeviceEntry> Devices,
    [property: Description("Number of endpoints Windows reported, including ones not returned.")] int TotalCount,
    [property: Description("Endpoints skipped because they could not be read at all.")] int FailedCount,
    [property: Description("True when devices were left out because of the result limit.")] bool Truncated);

/// <summary>
/// The device listing shared by <c>audio_list_devices</c> and the console's device page, so both always show the same
/// devices, defaults and aliases.
/// </summary>
public static class AudioDeviceCatalog
{
    public static AudioDeviceListData List(IAudioEndpoints endpoints, IDeviceAliases aliases, AudioDirection? direction, int max)
    {
        var list = endpoints.ListEndpoints(direction, max);
        var defaults = new List<(AudioRole Role, string Id)>();
        foreach (var d in Enum.GetValues<AudioDirection>().Where(d => direction is null || d == direction))
        {
            foreach (var role in Enum.GetValues<AudioRole>())
            {
                if (endpoints.GetDefaultEndpointId(d, role) is { } id)
                {
                    defaults.Add((role, id));
                }
            }
        }

        var byDevice = aliases.List(ToolCategories.Audio).ToLookup(a => a.DeviceId, a => a.Alias, StringComparer.OrdinalIgnoreCase);
        var devices = list.Items.Select(entry =>
        {
            var s = entry.State;
            AudioRole[] roles = [.. defaults.Where(x => x.Id == s.Id).Select(x => x.Role)];
            var volume = s.VolumeScalar is { } v ? AudioTarget.ToLevel(v) : (int?)null;
            var operations = new List<string> { "audio_get_state" };
            if (s.State == AudioDeviceState.Active)
            {
                if (volume is not null)
                {
                    operations.Add("audio_set_volume");
                }

                if (s.IsMuted is not null)
                {
                    operations.Add("audio_set_mute");
                }

                if (s.Direction == AudioDirection.Output)
                {
                    operations.Add("audio_list_sessions");
                }
            }

            return new AudioDeviceEntry(s.Id, s.Name, s.Direction, s.State, roles.Contains(AudioRole.Multimedia), roles, volume, s.IsMuted, [.. byDevice[s.Id]], operations, entry.ErrorCode);
        }).ToList();

        return new AudioDeviceListData(devices, list.Total, list.Failed, list.Total > list.Items.Count + list.Failed);
    }
}
