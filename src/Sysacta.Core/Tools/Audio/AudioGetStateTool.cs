using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Audio;

public sealed class AudioGetStateArgs
{
    [Description("Device id or alias. Omit to use the default device for the direction.")]
    [StringLength(512, MinimumLength = 1)]
    public string? Device { get; init; }

    [Description("Endpoint direction.")]
    [DefaultValue(AudioDirection.Output)]
    public AudioDirection Direction { get; init; } = AudioDirection.Output;
}

public sealed record AudioGetStateData(
    AudioDeviceInfo Device,
    [property: Description("Master volume 0-100, null when the device is not active.")] int? Volume,
    bool? Muted);

/// <summary>
/// REFERENCE IMPLEMENTATION of a read-only tool. Copy this shape for new state-reading tools.
/// Only domain logic lives here; auth, validation, audit and timeouts come from <see cref="ToolExecutor"/>.
/// </summary>
public sealed class AudioGetStateTool(IAudioEndpoints endpoints, IDeviceAliases aliases) : SysactaTool<AudioGetStateArgs, AudioGetStateData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "audio_get_state",
        Category: ToolCategories.Audio,
        Title: "Get audio state",
        Description: "Returns the master volume (0-100), mute state and identity of an audio endpoint. Uses the default device when 'device' is omitted.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: true,
        AuditedArguments: ["device", "direction"]);

    protected override ValueTask<ToolTarget> ResolveTargetAsync(AudioGetStateArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AudioTarget.Resolve(endpoints, aliases, args.Device, args.Direction));

    protected override Task<ToolOutcome<AudioGetStateData>> ExecuteAsync(AudioGetStateArgs args, ToolTarget target, CancellationToken cancellationToken)
    {
        var state = endpoints.GetEndpoint(target.TargetId!);
        var data = new AudioGetStateData(
            AudioTarget.Describe(endpoints, state),
            state.VolumeScalar is { } v ? AudioTarget.ToLevel(v) : null,
            state.IsMuted);
        return Task.FromResult(new ToolOutcome<AudioGetStateData>(data, Verification.NotApplicable));
    }
}
