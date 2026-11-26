using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Audio;

public sealed class AudioSetMuteArgs
{
    [Description("true mutes, false unmutes. This is not a toggle.")]
    public required bool Enabled { get; init; }

    [Description("Device id or alias. Omit to use the default device for the direction.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public string? Device { get; init; }

    [Description("Endpoint direction.")]
    [DefaultValue(AudioDirection.Output)]
    public AudioDirection Direction { get; init; } = AudioDirection.Output;
}

public sealed record AudioSetMuteData(AudioDeviceInfo Device, bool PreviousMuted, bool CurrentMuted, bool Changed);

public sealed class AudioSetMuteTool(IAudioEndpoints endpoints, IDeviceAliases aliases) : SysactaTool<AudioSetMuteArgs, AudioSetMuteData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "audio_set_mute",
        Category: ToolCategories.Audio,
        Title: "Set audio mute",
        Description: "Mutes (enabled=true) or unmutes (enabled=false) an audio endpoint and verifies it by reading it back. Uses the default device when 'device' is omitted. Does not change the volume.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: false,
        AuditedArguments: ["enabled", "direction"]);

    protected override ValueTask<ToolTarget> ResolveTargetAsync(AudioSetMuteArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AudioTarget.Resolve(endpoints, aliases, args.Device, args.Direction));

    protected override Task<ToolOutcome<AudioSetMuteData>> ExecuteAsync(AudioSetMuteArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var id = execution.TargetId!;
        var before = endpoints.GetEndpoint(id);
        if (before.State != AudioDeviceState.Active || before.IsMuted is not { } previous)
        {
            throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The specified audio device is not active.");
        }

        var device = AudioTarget.Describe(endpoints, before);
        if (previous == args.Enabled)
        {
            return Task.FromResult(new ToolOutcome<AudioSetMuteData>(new(device, previous, previous, Changed: false), Verification.Verified));
        }

        cancellationToken.ThrowIfCancellationRequested();
        execution.BeginSideEffect();
        endpoints.SetMute(id, args.Enabled);

        AudioEndpointState after;
        try
        {
            after = endpoints.GetEndpoint(id);
        }
        catch (ToolFailureException ex)
        {
            throw new ToolOutcomeUnknownException(ex.Code, "The mute state was set but could not be read back to verify it.");
        }

        if (after.IsMuted is not { } current)
        {
            throw new ToolOutcomeUnknownException(ErrorCodes.DeviceDisconnected, "The mute state was set but the device became inactive before it could be verified.");
        }

        if (current != args.Enabled)
        {
            throw new ToolFailureException(ErrorCodes.VerificationFailed, "Mute was set but reads back unchanged.");
        }

        return Task.FromResult(new ToolOutcome<AudioSetMuteData>(new(device, previous, current, Changed: true), Verification.Verified));
    }
}
