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

/// <summary>
/// Windows Core Audio adapter contract (implemented in Sysacta.Windows/Audio).
/// Implementations are synchronous, create and release their COM objects per call, and translate failures into
/// <see cref="ToolFailureException"/> with TARGET_NOT_FOUND / DEVICE_DISCONNECTED / OS_API_ERROR.
/// </summary>
public interface IAudioEndpoints
{
    /// <summary>Id of the default endpoint for the direction (multimedia role), or null when none exists.</summary>
    string? GetDefaultEndpointId(AudioDirection direction);

    /// <summary>Reads the endpoint. Throws TARGET_NOT_FOUND if the id is unknown.</summary>
    AudioEndpointState GetEndpoint(string id);

    /// <summary>Sets the master volume scalar (0.0–1.0). The device must be active.</summary>
    void SetVolumeScalar(string id, float scalar);
}
