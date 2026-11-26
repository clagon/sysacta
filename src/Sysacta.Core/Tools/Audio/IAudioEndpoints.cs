namespace Sysacta.Core.Tools.Audio;

public enum AudioDirection
{
    Output,
    Input,
}

public enum AudioDeviceState
{
    Active,
    Disabled,
    Unplugged,
    NotPresent,
}

/// <summary>Snapshot of one audio endpoint. Volume and mute are null when the device is not active.</summary>
public sealed record AudioEndpointState(
    string Id,
    string Name,
    AudioDirection Direction,
    AudioDeviceState State,
    float? VolumeScalar,
    bool? IsMuted);

/// <summary>Windows default-device roles. Omitted-device calls use <see cref="Multimedia"/>.</summary>
public enum AudioRole
{
    Console,
    Multimedia,
    Communications,
}

/// <summary>One endpoint of an enumeration. <paramref name="ErrorCode"/> is set when some details could not be read.</summary>
public sealed record AudioEndpointEntry(AudioEndpointState State, string? ErrorCode = null);

/// <summary>
/// Result of an enumeration. <paramref name="Total"/> counts every endpoint the OS reported;
/// <paramref name="Failed"/> counts those that had to be skipped because not even their identity could be read.
/// </summary>
public sealed record AudioEndpointList(IReadOnlyList<AudioEndpointEntry> Items, int Total, int Failed);

/// <summary>
/// Windows Core Audio adapter contract (implemented in Sysacta.Windows/Audio).
/// Implementations are synchronous, create and release their COM objects per call, and translate failures into
/// <see cref="ToolFailureException"/> with TARGET_NOT_FOUND / DEVICE_DISCONNECTED / OS_API_ERROR.
/// </summary>
public interface IAudioEndpoints
{
    /// <summary>Id of the default endpoint for the direction and role, or null when none exists.</summary>
    string? GetDefaultEndpointId(AudioDirection direction, AudioRole role = AudioRole.Multimedia);

    /// <summary>
    /// Endpoints of the direction (null = both) in every state, active ones first, with details for at most
    /// <paramref name="max"/> of them. Throws OS_API_ERROR when the enumeration itself fails (never an empty list).
    /// </summary>
    AudioEndpointList ListEndpoints(AudioDirection? direction, int max);

    /// <summary>Reads the endpoint. Throws TARGET_NOT_FOUND if the id is unknown.</summary>
    AudioEndpointState GetEndpoint(string id);

    /// <summary>Sets the master volume scalar (0.0–1.0). The device must be active.</summary>
    void SetVolumeScalar(string id, float scalar);

    /// <summary>Mutes or unmutes the endpoint. The device must be active. Does not change the volume.</summary>
    void SetMute(string id, bool muted);
}
