using System.ComponentModel;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using static Sysacta.Windows.Audio.CoreAudioEndpoints;
using AudioSessionState = Sysacta.Core.Tools.Audio.AudioSessionState;

namespace Sysacta.Windows.Audio;

/// <summary>
/// Render audio sessions (IAudioSessionManager2 / IAudioSessionControl2 / ISimpleAudioVolume) via NAudio.
/// Ownership: the device owns its session manager (disposing the device releases it and its enumerator); every session
/// control returned by the collection is owned by the caller and disposed here, including on partial failures.
/// Only identity, state, volume and mute are read; never audio data, window titles, command lines or full paths.
/// </summary>
public sealed class CoreAudioSessions : IAudioSessions
{
    private const int MaxNameLength = 128;

    public AudioSessionList List(string deviceId, int max) => Guard(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = GetActiveDevice(enumerator, deviceId);
        var collection = device.AudioSessionManager.Sessions;
        var total = collection.Count;
        var failed = 0;
        var items = new List<AudioSessionState>();
        for (var i = 0; i < Math.Min(total, max); i++)
        {
            try
            {
                using var control = collection[i];
                items.Add(Read(control));
            }
            catch (Exception ex) when (IsApiFailure(ex))
            {
                failed++;
            }
        }

        return new AudioSessionList(items, total, failed);
    });

    public IAudioSessionHandle? Open(string deviceId, string instanceId) => Guard<IAudioSessionHandle?>(() =>
    {
        var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        AudioSessionControl? match = null;
        var keep = false;
        try
        {
            device = GetActiveDevice(enumerator, deviceId);
            var collection = device.AudioSessionManager.Sessions;
            var total = collection.Count;
            var unreadable = false;
            for (var i = 0; i < total; i++)
            {
                AudioSessionControl? control = null;
                try
                {
                    control = collection[i];
                    if (control.GetSessionInstanceIdentifier != instanceId || control.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                    {
                        continue;
                    }

                    if (match is not null)
                    {
                        throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "Several audio sessions share this id; none was changed.");
                    }

                    match = control;
                    control = null; // ownership moved to 'match'
                }
                catch (Exception ex) when (IsApiFailure(ex))
                {
                    unreadable = true;
                }
                finally
                {
                    control?.Dispose();
                }
            }

            if (match is null)
            {
                return unreadable
                    ? throw new ToolFailureException(ErrorCodes.OsApiError, "Some audio sessions could not be read, so the session could not be located.")
                    : null;
            }

            keep = true;
            return new Handle(enumerator, device, match);
        }
        finally
        {
            if (!keep)
            {
                match?.Dispose();
                device?.Dispose();
                enumerator.Dispose();
            }
        }
    });

    private static AudioSessionState Read(AudioSessionControl control)
    {
        var instanceId = control.GetSessionInstanceIdentifier;
        var status = control.State switch
        {
            NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive => Core.Tools.Audio.AudioSessionStatus.Active,
            NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired => Core.Tools.Audio.AudioSessionStatus.Expired,
            _ => Core.Tools.Audio.AudioSessionStatus.Inactive,
        };
        var systemSounds = Optional(() => (bool?)control.IsSystemSoundsSession) ?? false;
        var pid = Optional(() => control.GetProcessID is var p and > 0 and <= int.MaxValue ? (int?)p : null);
        var name = systemSounds
            ? "System sounds"
            : SafeName(Optional(() => control.DisplayName)) ?? SafeName(ProcessName(pid));
        return new AudioSessionState(
            instanceId,
            name,
            systemSounds ? null : pid,
            status,
            Optional(() => (float?)control.SimpleAudioVolume.Volume),
            Optional(() => (bool?)control.SimpleAudioVolume.Mute),
            systemSounds);
    }

    private static T? Optional<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (IsApiFailure(ex))
        {
            return default;
        }
    }

    /// <summary>The executable's base name only (no path, no window title); null when the process is gone or hidden.</summary>
    private static string? ProcessName(int? pid)
    {
        if (pid is not { } id)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Display names are set by applications. Resource references ("@%SystemRoot%\...") and anything that looks like a
    /// path are dropped; control characters are removed and the length is bounded.
    /// </summary>
    internal static string? SafeName(string? name)
    {
        if (name is null || name.StartsWith('@') || name.Contains('\\') || name.Contains('/'))
        {
            return null;
        }

        var clean = new string([.. name.Where(c => !char.IsControl(c)).Take(MaxNameLength)]).Trim();
        return clean.Length == 0 ? null : clean;
    }

    private sealed class Handle(MMDeviceEnumerator enumerator, MMDevice device, AudioSessionControl control) : IAudioSessionHandle
    {
        public AudioSessionState Read() => Guard(() => CoreAudioSessions.Read(control));

        public void SetVolumeScalar(float scalar) => Guard(() =>
        {
            control.SimpleAudioVolume.Volume = Math.Clamp(scalar, 0f, 1f);
            return 0;
        });

        public void SetMute(bool muted) => Guard(() =>
        {
            control.SimpleAudioVolume.Mute = muted;
            return 0;
        });

        public void Dispose()
        {
            control.Dispose();
            device.Dispose();
            enumerator.Dispose();
        }
    }
}
