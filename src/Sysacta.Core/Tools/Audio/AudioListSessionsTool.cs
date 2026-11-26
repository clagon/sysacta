using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Audio;

public sealed class AudioListSessionsArgs
{
    [Description("Output device id or alias. Omit to use the default output device.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public string? Device { get; init; }
}

public sealed record AudioSessionEntry(
    [property: Description("Opaque id for audio_set_app_volume / audio_set_app_mute. It stops working when the session ends, is recreated or moves to another device.")] string SessionId,
    string DeviceId,
    [property: Description("Display name or process name, null when neither is available.")] string? Name,
    int? ProcessId,
    AudioSessionStatus State,
    [property: Description("Session volume 0-100 relative to the device volume, null when it cannot be read.")] int? Volume,
    bool? Muted,
    bool IsSystemSounds,
    [property: Description("Tools that can act on this session (client permissions still apply).")] IReadOnlyList<string> Operations);

public sealed record AudioSessionListData(
    string DeviceId,
    IReadOnlyList<AudioSessionEntry> Sessions,
    [property: Description("Number of sessions Windows reported, including ones not returned.")] int TotalCount,
    [property: Description("Sessions skipped because they could not be read.")] int FailedCount,
    [property: Description("True when sessions were left out because of the result limit.")] bool Truncated);

public sealed class AudioListSessionsTool(IAudioEndpoints endpoints, IDeviceAliases aliases, IAudioSessions sessions) : SysactaTool<AudioListSessionsArgs, AudioSessionListData>
{
    /// <summary>Upper bound of returned sessions (bounds the response size).</summary>
    public const int MaxSessions = 100;

    public override ToolDescriptor Descriptor { get; } = new(
        Name: "audio_list_sessions",
        Category: ToolCategories.Audio,
        Title: "List app audio sessions",
        Description: "Lists the playback (per-app) audio sessions of one output device: session_id, name, process id, state, volume (0-100) and mute. One app can have several sessions. A session_id is only valid while that session exists on that device; list again after an app restarts or the device changes. At most 100 sessions.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: true,
        AuditedArguments: []);

    protected override ValueTask<ToolTarget> ResolveTargetAsync(AudioListSessionsArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AudioTarget.Resolve(endpoints, aliases, args.Device, AudioDirection.Output));

    protected override Task<ToolOutcome<AudioSessionListData>> ExecuteAsync(AudioListSessionsArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var deviceId = execution.TargetId!;
        if (endpoints.GetEndpoint(deviceId).State != AudioDeviceState.Active)
        {
            throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The specified audio device is not active.");
        }

        // Read more than is returned so that expired sessions are dropped first when the limit applies.
        var list = sessions.List(deviceId, AudioSessions.MaxSearched);
        var entries = list.Items
            .OrderBy(s => s.Status switch { AudioSessionStatus.Active => 0, AudioSessionStatus.Inactive => 1, _ => 2 })
            .Take(MaxSessions)
            .Select(s => Entry(deviceId, s))
            .ToList();
        var data = new AudioSessionListData(deviceId, entries, list.Total, list.Failed, list.Total > entries.Count + list.Failed);
        return Task.FromResult(new ToolOutcome<AudioSessionListData>(data, Verification.NotApplicable));
    }

    private static AudioSessionEntry Entry(string deviceId, AudioSessionState s)
    {
        var volume = s.VolumeScalar is { } v ? AudioTarget.ToLevel(v) : (int?)null;
        var operations = new List<string>(2);
        if (s.Status != AudioSessionStatus.Expired)
        {
            if (volume is not null)
            {
                operations.Add("audio_set_app_volume");
            }

            if (s.IsMuted is not null)
            {
                operations.Add("audio_set_app_mute");
            }
        }

        return new(AudioSessions.SessionId(deviceId, s.InstanceId), deviceId, s.Name, s.ProcessId, s.Status, volume, s.IsMuted, s.IsSystemSounds, operations);
    }
}
