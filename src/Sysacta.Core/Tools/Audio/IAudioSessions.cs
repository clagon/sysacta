namespace Sysacta.Core.Tools.Audio;

public enum AudioSessionStatus
{
    Inactive,
    Active,
    Expired,
}

/// <summary>
/// Snapshot of one render audio session. <paramref name="InstanceId"/> is the raw Core Audio session instance
/// identifier: it contains the executable path, so it never leaves Core (see <see cref="AudioSessions.SessionId"/>).
/// <paramref name="Name"/> is already safe to return. Unreadable optional values are null.
/// </summary>
public sealed record AudioSessionState(
    string InstanceId,
    string? Name,
    int? ProcessId,
    AudioSessionStatus Status,
    float? VolumeScalar,
    bool? IsMuted,
    bool IsSystemSounds);

/// <summary>Sessions of one endpoint. <paramref name="Failed"/> counts sessions whose identity or state could not be read.</summary>
public sealed record AudioSessionList(IReadOnlyList<AudioSessionState> Items, int Total, int Failed);

/// <summary>
/// Windows Core Audio session adapter contract (render sessions only; no audio data is read).
/// Same threading and error rules as <see cref="IAudioEndpoints"/>.
/// </summary>
public interface IAudioSessions
{
    /// <summary>Sessions of an active output endpoint, reading at most <paramref name="max"/> of them.</summary>
    AudioSessionList List(string deviceId, int max);

    /// <summary>
    /// Holds one session object so that checking it, writing to it and reading it back all act on the same instance.
    /// Returns null when no session has that instance id; throws AMBIGUOUS_TARGET when several have it.
    /// </summary>
    IAudioSessionHandle? Open(string deviceId, string instanceId);
}

/// <summary>One open session. Use on the thread that opened it and dispose it.</summary>
public interface IAudioSessionHandle : IDisposable
{
    AudioSessionState Read();

    /// <summary>Sets the session master volume scalar (0.0–1.0). The endpoint volume is not changed.</summary>
    void SetVolumeScalar(float scalar);

    /// <summary>Mutes or unmutes this session only.</summary>
    void SetMute(bool muted);
}
