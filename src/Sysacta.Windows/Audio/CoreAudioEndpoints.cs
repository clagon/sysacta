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

    public string? GetDefaultEndpointId(AudioDirection direction) => Guard(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(ToFlow(direction), Role.Multimedia, out var device) || device is null)
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
        var direction = device.DataFlow == DataFlow.Capture ? AudioDirection.Input : AudioDirection.Output;
        if (state != AudioDeviceState.Active)
        {
            return new AudioEndpointState(device.ID, SafeName(device), direction, state, null, null);
        }

        var volume = device.AudioEndpointVolume;
        return new AudioEndpointState(device.ID, SafeName(device), direction, state, volume.MasterVolumeLevelScalar, volume.Mute);
    });

    public void SetVolumeScalar(string id, float scalar) => Guard(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = GetDevice(enumerator, id);
        if (device.State != DeviceState.Active)
        {
            throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The specified audio device is not active.");
        }

        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(scalar, 0f, 1f);
        return 0;
    });

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

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x88890004))
        {
            // AUDCLNT_E_DEVICE_INVALIDATED
            throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The audio device was removed or disabled.");
        }
        catch (COMException)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API returned an error.", retryable: false);
        }
        catch (InvalidCastException)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio API is not available.");
        }
    }

    private static DataFlow ToFlow(AudioDirection direction) => direction == AudioDirection.Input ? DataFlow.Capture : DataFlow.Render;

    private static AudioDeviceState ToState(DeviceState state) => state switch
    {
        DeviceState.Active => AudioDeviceState.Active,
        DeviceState.Disabled => AudioDeviceState.Disabled,
        DeviceState.Unplugged => AudioDeviceState.Unplugged,
        _ => AudioDeviceState.NotPresent,
    };
}
