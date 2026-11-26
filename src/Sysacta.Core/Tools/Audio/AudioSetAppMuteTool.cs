using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Sysacta.Core.Tools.Audio;

public sealed class AudioSetAppMuteArgs
{
    [Description("Session id from audio_list_sessions.")]
    [StringLength(64, MinimumLength = 1)]
    [NoControlCharacters]
    public required string SessionId { get; init; }

    [Description("true mutes, false unmutes. This is not a toggle.")]
    public required bool Enabled { get; init; }
}

public sealed record AudioSetAppMuteData(string SessionId, string DeviceId, bool PreviousMuted, bool CurrentMuted, bool Changed);

public sealed class AudioSetAppMuteTool(IAudioEndpoints endpoints, IAudioSessions sessions) : SysactaTool<AudioSetAppMuteArgs, AudioSetAppMuteData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "audio_set_app_mute",
        Category: ToolCategories.Audio,
        Title: "Set app mute",
        Description: "Mutes (enabled=true) or unmutes (enabled=false) one app audio session identified by session_id (from audio_list_sessions) and verifies it by reading it back. Does not change the device or other sessions. An unknown or ended session_id is refused.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: false,
        AuditedArguments: ["enabled"]); // the session is recorded as the resolved target id

    protected override ValueTask<ToolTarget> ResolveTargetAsync(AudioSetAppMuteArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AudioSessions.Resolve(endpoints, sessions, args.SessionId, cancellationToken));

    protected override Task<ToolOutcome<AudioSetAppMuteData>> ExecuteAsync(AudioSetAppMuteArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var (deviceId, previous, current, changed) = AudioSessions.Change(
            endpoints,
            sessions,
            execution,
            args.Enabled,
            s => s.IsMuted,
            h => h.SetMute(args.Enabled),
            cancellationToken);
        var data = new AudioSetAppMuteData(execution.TargetId!, deviceId, previous, current, changed);
        return Task.FromResult(new ToolOutcome<AudioSetAppMuteData>(data, Verification.Verified));
    }
}
