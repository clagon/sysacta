using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Sysacta.Core.Tools.Audio;

public sealed class AudioSetAppVolumeArgs
{
    [Description("Session id from audio_list_sessions.")]
    [StringLength(64, MinimumLength = 1)]
    [NoControlCharacters]
    public required string SessionId { get; init; }

    [Description("Absolute session volume, integer 0-100, relative to the device volume.")]
    [Range(0, 100)]
    public required int Level { get; init; }
}

public sealed record AudioSetAppVolumeData(string SessionId, string DeviceId, int PreviousVolume, int CurrentVolume, bool Changed);

public sealed class AudioSetAppVolumeTool(IAudioEndpoints endpoints, IAudioSessions sessions) : SysactaTool<AudioSetAppVolumeArgs, AudioSetAppVolumeData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "audio_set_app_volume",
        Category: ToolCategories.Audio,
        Title: "Set app volume",
        Description: "Sets the absolute volume (0-100) of one app audio session identified by session_id (from audio_list_sessions) and verifies it by reading it back. Does not change the device volume, mute or other sessions. An unknown or ended session_id is refused.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: false,
        AuditedArguments: ["level"]); // the session is recorded as the resolved target id

    protected override ValueTask<ToolTarget> ResolveTargetAsync(AudioSetAppVolumeArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AudioSessions.Resolve(endpoints, sessions, args.SessionId, cancellationToken));

    protected override Task<ToolOutcome<AudioSetAppVolumeData>> ExecuteAsync(AudioSetAppVolumeArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var (deviceId, previous, current, changed) = AudioSessions.Change(
            endpoints,
            sessions,
            execution,
            args.Level,
            s => s.VolumeScalar is { } v ? AudioTarget.ToLevel(v) : null,
            h => h.SetVolumeScalar(args.Level / 100f),
            cancellationToken);
        var data = new AudioSetAppVolumeData(execution.TargetId!, deviceId, previous, current, changed);
        return Task.FromResult(new ToolOutcome<AudioSetAppVolumeData>(data, Verification.Verified));
    }
}
