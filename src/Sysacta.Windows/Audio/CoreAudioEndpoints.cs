using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;

namespace Sysacta.Windows.Audio;

/// <summary>
/// Core Audio (MMDevice API) adapter via NAudio.
/// <list type="bullet">
/// <item>Threading: called on thread-pool (MTA) threads by the executor; never on the WPF UI thread.</item>
/// <item>Lifetime: every call creates and disposes its own enumerator/device so COM references are released promptly.</item>
/// <item>Errors: COM failures become OS_API_ERROR without the raw message; an unknown id is TARGET_NOT_FOUND.</item>
/// </list>
/// </summary>
public sealed class CoreAudioEndpoints : IAudioEndpoints
{
    // E_NOTFOUND (HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) from IMMDeviceEnumerator::GetDevice.
    private const int ENotFound = unchecked((int)0x80070490);

    // AUDCLNT_E_DEVICE_INVALIDATED
    private const int DeviceInvalidated = unchecked((int)0x88890004);

    public string? GetDefaultEndpointId(AudioDirection direction, AudioRole role = AudioRole.Multimedia) => Guard(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(ToFlow(direction), ToRole(role), out var device) || device is null)
        {
            return null;
        }

        using (device)
        {
            return device.ID;
        }
    });

    public AudioEndpointState GetEndpoint(string id) => Guard(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = GetDevice(enumerator, id);
        var state = ToState(device.State);
        var direction = ToDirection(device.DataFlow);
        if (state != AudioDeviceState.Active)
        {
            return new AudioEndpointState(device.ID, SafeName(device), direction, state, null, null);
        }

        var volume = device.AudioEndpointVolume;
        return new AudioEndpointState(device.ID, SafeName(device), direction, state, volume.MasterVolumeLevelScalar, volume.Mute);
    });

    public AudioEndpointList ListEndpoints(AudioDirection? direction, int max) => Guard(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        var flow = direction is { } d ? ToFlow(d) : DataFlow.All;
        using var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.All);
        var total = collection.Count;
        var failed = 0;
        var found = new List<(MMDevice Device, string Id, AudioDirection Direction, AudioDeviceState State)>(total);
        try
        {
            // Identity and state first (cheap), so that active devices are kept when the limit applies.
            for (var i = 0; i < total; i++)
            {
                MMDevice? device = null;
                try
                {
                    device = collection[i];
                    found.Add((device, device.ID, ToDirection(device.DataFlow), ToState(device.State)));
                }
                catch (Exception ex) when (IsApiFailure(ex))
                {
                    device?.Dispose();
                    failed++;
                }
            }

            AudioEndpointEntry[] items = [.. found.OrderBy(f => f.State).ThenBy(f => f.Direction).Take(max).Select(f => Entry(f.Device, f.Id, f.Direction, f.State))];
            return new AudioEndpointList(items, total, failed);
        }
        finally
        {
            foreach (var f in found)
            {
                f.Device.Dispose();
            }
        }
    });

    public void SetVolumeScalar(string id, float scalar) => Guard(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = GetActiveDevice(enumerator, id);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(scalar, 0f, 1f);
        return 0;
    });

    public void SetMute(string id, bool muted) => Guard(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = GetActiveDevice(enumerator, id);
        device.AudioEndpointVolume.Mute = muted;
        return 0;
    });

    private static AudioEndpointEntry Entry(MMDevice device, string id, AudioDirection direction, AudioDeviceState state)
    {
        var name = "(unknown)";
        float? scalar = null;
        bool? muted = null;
        string? error = null;
        try
        {
            name = device.FriendlyName;
            if (state == AudioDeviceState.Active)
            {
                var volume = device.AudioEndpointVolume;
                scalar = volume.MasterVolumeLevelScalar;
                muted = volume.Mute;
            }
        }
        catch (Exception ex) when (IsApiFailure(ex))
        {
            // One broken device must not hide the others.
            error = ex.HResult == DeviceInvalidated ? ErrorCodes.DeviceDisconnected : ErrorCodes.OsApiError;
        }

        return new AudioEndpointEntry(new AudioEndpointState(id, name, direction, state, scalar, muted), error);
    }

    internal static MMDevice GetActiveDevice(MMDeviceEnumerator enumerator, string id)
    {
        var device = GetDevice(enumerator, id);
        try
        {
            if (device.State != DeviceState.Active)
            {
                throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The specified audio device is not active.");
            }

            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static MMDevice GetDevice(MMDeviceEnumerator enumerator, string id)
    {
        try
        {
            return enumerator.GetDevice(id);
        }
        catch (COMException ex) when (ex.HResult == ENotFound)
        {
            throw new ToolFailureException(ErrorCodes.TargetNotFound, "The specified audio device was not found.");
        }
        catch (ArgumentException)
        {
            throw new ToolFailureException(ErrorCodes.TargetNotFound, "The specified audio device was not found.");
        }
    }

    private static string SafeName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (COMException)
        {
            return "(unknown)";
        }
    }

    /// <summary>Failures of the audio API itself (NAudio reports a missing interface as the last two).</summary>
    internal static bool IsApiFailure(Exception ex) => ex is COMException or InvalidCastException or NotSupportedException or InvalidOperationException;

    internal static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (COMException ex) when (ex.HResult == DeviceInvalidated)
        {
            throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The audio device was removed or disabled.");
        }
        catch (COMException)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API returned an error.", retryable: false);
        }
        catch (Exception ex) when (IsApiFailure(ex))
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API is not available.");
        }
    }

    private static AudioDirection ToDirection(DataFlow flow) => flow == DataFlow.Capture ? AudioDirection.Input : AudioDirection.Output;

    private static Role ToRole(AudioRole role) => role switch
    {
        AudioRole.Console => Role.Console,
        AudioRole.Communications => Role.Communications,
        _ => Role.Multimedia,
    };

    private static DataFlow ToFlow(AudioDirection direction) => direction == AudioDirection.Input ? DataFlow.Capture : DataFlow.Render;

    private static AudioDeviceState ToState(DeviceState state) => state switch
    {
        DeviceState.Active => AudioDeviceState.Active,
        DeviceState.Disabled => AudioDeviceState.Disabled,
        DeviceState.Unplugged => AudioDeviceState.Unplugged,
        _ => AudioDeviceState.NotPresent,
    };
}
