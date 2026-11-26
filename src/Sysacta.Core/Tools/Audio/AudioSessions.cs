using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Sysacta.Core.Tools.Audio;

/// <summary>Session identity shared by the session tools. A session is only ever addressed by its exact id.</summary>
public static class AudioSessions
{
    /// <summary>Upper bound of sessions read from one endpoint, and of active endpoints searched.</summary>
    internal const int MaxSearched = 512;

    private const string Ended = "The audio session was not found or has ended. List the sessions again.";

    /// <summary>
    /// Opaque id of one session: SHA-256 over the canonical endpoint id and the Core Audio session instance identifier.
    /// Deterministic, so no id table is kept; the raw identifier (which contains a file path) is never exposed.
    /// </summary>
    public static string SessionId(string deviceId, string instanceId) =>
        "as_" + Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId + "\0" + instanceId)));

    /// <summary>
    /// Finds the session with this id among the active output endpoints. Never matches by process or name, and never
    /// returns another session. When part of the search failed, "not found" is reported as an API error instead.
    /// </summary>
    internal static (string DeviceId, string InstanceId) Find(IAudioEndpoints endpoints, IAudioSessions sessions, string sessionId, CancellationToken cancellationToken)
    {
        var devices = endpoints.ListEndpoints(AudioDirection.Output, MaxSearched);
        var incomplete = devices.Failed > 0 || devices.Total > devices.Items.Count + devices.Failed;
        var matches = new List<(string, string)>();
        foreach (var device in devices.Items.Where(d => d.State.State == AudioDeviceState.Active))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AudioSessionList list;
            try
            {
                list = sessions.List(device.State.Id, MaxSearched);
            }
            catch (ToolFailureException)
            {
                incomplete = true; // e.g. the device disappeared meanwhile; keep searching the others
                continue;
            }

            incomplete |= list.Failed > 0 || list.Total > list.Items.Count + list.Failed;
            matches.AddRange(list.Items
                .Where(s => s.Status != AudioSessionStatus.Expired && SessionId(device.State.Id, s.InstanceId) == sessionId)
                .Select(s => (device.State.Id, s.InstanceId)));
        }

        return matches.Count switch
        {
            1 => matches[0],
            > 1 => throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "Several audio sessions share this id; none was changed."),
            _ when incomplete => throw new ToolFailureException(ErrorCodes.OsApiError, "Some audio sessions could not be read, so the session could not be located."),
            _ => throw new ToolFailureException(ErrorCodes.TargetNotFound, Ended),
        };
    }

    internal static ToolTarget Resolve(IAudioEndpoints endpoints, IAudioSessions sessions, string sessionId, CancellationToken cancellationToken)
    {
        var (deviceId, instanceId) = Find(endpoints, sessions, sessionId, cancellationToken);
        var id = SessionId(deviceId, instanceId);

        // Volume and mute of one session share this lock.
        return new ToolTarget([$"audio:session:{id}"], id);
    }

    /// <summary>
    /// The common part of changing one session value: locate the same instance again (after the locks), check it, skip
    /// a no-op, declare the start point, write, and read back from the same object.
    /// </summary>
    internal static (string DeviceId, T Previous, T Current, bool Changed) Change<T>(
        IAudioEndpoints endpoints,
        IAudioSessions sessions,
        ToolExecution execution,
        T requested,
        Func<AudioSessionState, T?> read,
        Action<IAudioSessionHandle> write,
        CancellationToken cancellationToken)
        where T : struct, IEquatable<T>
    {
        var (deviceId, instanceId) = Find(endpoints, sessions, execution.TargetId!, cancellationToken);
        using var session = sessions.Open(deviceId, instanceId) ?? throw new ToolFailureException(ErrorCodes.TargetNotFound, Ended);
        var before = session.Read();
        if (before.InstanceId != instanceId || before.Status == AudioSessionStatus.Expired)
        {
            throw new ToolFailureException(ErrorCodes.TargetNotFound, Ended);
        }

        if (read(before) is not { } previous)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The current value of the audio session cannot be read, so it was not changed.");
        }

        if (previous.Equals(requested))
        {
            return (deviceId, previous, previous, false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        execution.BeginSideEffect();
        write(session);

        AudioSessionState after;
        try
        {
            after = session.Read();
        }
        catch (ToolFailureException ex)
        {
            throw new ToolOutcomeUnknownException(ex.Code, "The session was changed but could not be read back to verify it.");
        }

        if (after.Status == AudioSessionStatus.Expired || read(after) is not { } current)
        {
            throw new ToolOutcomeUnknownException(ErrorCodes.TargetNotFound, "The session was changed but ended or became unreadable before it could be verified.");
        }

        if (!current.Equals(requested))
        {
            throw new ToolFailureException(ErrorCodes.VerificationFailed, $"The session reads back as {current} instead of {requested}.");
        }

        return (deviceId, previous, current, true);
    }
}
