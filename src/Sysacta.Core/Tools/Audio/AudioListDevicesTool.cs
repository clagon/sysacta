using System.ComponentModel;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Audio;

/// <summary>Listing filter. Separate from <see cref="AudioDirection"/> so that 'all' can never reach a changing tool.</summary>
public enum AudioDirectionFilter
{
    All,
    Output,
    Input,
}

public sealed class AudioListDevicesArgs
{
    [Description("Which endpoints to list.")]
    [DefaultValue(AudioDirectionFilter.All)]
    public AudioDirectionFilter Direction { get; init; } = AudioDirectionFilter.All;
}

public sealed class AudioListDevicesTool(IAudioEndpoints endpoints, IDeviceAliases aliases) : SysactaTool<AudioListDevicesArgs, AudioDeviceListData>
{
    /// <summary>Upper bound of returned devices (bounds the response size).</summary>
    public const int MaxDevices = 100;

    public override ToolDescriptor Descriptor { get; } = new(
        Name: "audio_list_devices",
        Category: ToolCategories.Audio,
        Title: "List audio devices",
        Description: "Lists audio endpoints (active ones first) with id, name, direction, state, default roles, aliases, volume (0-100) and mute. Volume and mute are null for devices that are not active. At most 100 devices; see 'truncated' and 'failed_count'.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: true,
        AuditedArguments: ["direction"]);

    protected override Task<ToolOutcome<AudioDeviceListData>> ExecuteAsync(AudioListDevicesArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        AudioDirection? direction = args.Direction switch
        {
            AudioDirectionFilter.Output => AudioDirection.Output,
            AudioDirectionFilter.Input => AudioDirection.Input,
            _ => null,
        };
        return Task.FromResult(new ToolOutcome<AudioDeviceListData>(AudioDeviceCatalog.List(endpoints, aliases, direction, MaxDevices), Verification.NotApplicable));
    }
}
