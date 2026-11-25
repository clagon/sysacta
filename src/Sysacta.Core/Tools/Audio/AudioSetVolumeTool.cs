using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Audio;

public sealed class AudioSetVolumeArgs
{
    [Description("Absolute master volume, integer 0-100.")]
    [Range(0, 100)]
    public required int Level { get; init; }

    [Description("Device id or alias. Omit to use the default device for the direction.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public string? Device { get; init; }

    [Description("Endpoint direction.")]
    [DefaultValue(AudioDirection.Output)]
    public AudioDirection Direction { get; init; } = AudioDirection.Output;
}

public sealed record AudioSetVolumeData(AudioDeviceInfo Device, int PreviousVolume, int CurrentVolume, bool Changed);

/// <summary>
/// REFERENCE IMPLEMENTATION of a state-changing tool. Copy this shape for new state-changing tools:
/// resolve a concrete target (lock keys), read the previous state, no-op if already there, apply, read back, verify.
/// </summary>
public sealed class AudioSetVolumeTool(IAudioEndpoints endpoints, IDeviceAliases aliases) : SysactaTool<AudioSetVolumeArgs, AudioSetVolumeData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "audio_set_volume",
        Category: ToolCategories.Audio,
        Title: "Set audio volume",
        Description: "Sets the absolute master volume (0-100) of an audio endpoint and verifies it by reading it back. Uses the default device when 'device' is omitted. Does not change mute.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: false,
        AuditedArguments: ["level", "device", "direction"]);

    protected override ValueTask<ToolTarget> ResolveTargetAsync(AudioSetVolumeArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AudioTarget.Resolve(endpoints, aliases, args.Device, args.Direction));

    protected override Task<ToolOutcome<AudioSetVolumeData>> ExecuteAsync(AudioSetVolumeArgs args, ToolTarget target, CancellationToken cancellationToken)
    {
        var id = target.TargetId!;
        var before = endpoints.GetEndpoint(id);
        if (before.State != AudioDeviceState.Active || before.VolumeScalar is not { } previousScalar)
        {
            throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The specified audio device is not active.");
        }

        var device = AudioTarget.Describe(endpoints, before);
        var previous = AudioTarget.ToLevel(previousScalar);
        if (previous == args.Level)
        {
            return Task.FromResult(new ToolOutcome<AudioSetVolumeData>(new(device, previous, previous, Changed: false), Verification.Verified));
        }

        // Last point before the side effect. Cancellation after this line yields status "unknown" in the executor.
        cancellationToken.ThrowIfCancellationRequested();
        endpoints.SetVolumeScalar(id, args.Level / 100f);

        AudioEndpointState after;
        try
        {
            after = endpoints.GetEndpoint(id);
        }
        catch (ToolFailureException ex)
        {
            // The change may already be applied (e.g. the device was removed right after the call).
            throw new ToolOutcomeUnknownException(ex.Code, "The volume was set but could not be read back to verify it.");
        }

        if (after.VolumeScalar is not { } s)
        {
            throw new ToolOutcomeUnknownException(ErrorCodes.DeviceDisconnected, "The volume was set but the device became inactive before it could be verified.");
        }

        var current = AudioTarget.ToLevel(s);
        if (current != args.Level)
        {
            throw new ToolFailureException(ErrorCodes.VerificationFailed, $"Volume was set but reads back as {current} instead of {args.Level}.");
        }

        return Task.FromResult(new ToolOutcome<AudioSetVolumeData>(new(device, previous, current, Changed: true), Verification.Verified));
    }
}
