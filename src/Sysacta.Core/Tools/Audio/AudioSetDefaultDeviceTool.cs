using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Audio;

public enum AudioDefaultRoleSelection
{
    Console,
    Multimedia,
    Communications,
    All,
}

public enum AudioDefaultRoleStatus
{
    Unchanged,
    Changed,
    Failed,
    Unknown,
    NotAttempted,
}

public sealed class AudioSetDefaultDeviceArgs
{
    [Description("Device id or registered alias. Must be an active device of the requested direction.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public required string Device { get; init; }

    [Description("Endpoint direction.")]
    public required AudioDirection Direction { get; init; }

    [Description("Default role to assign: console, multimedia, communications, or all (all three roles for the direction).")]
    public required AudioDefaultRoleSelection Role { get; init; }
}

public sealed record AudioDefaultRoleResult(
    AudioRole Role,
    string? PreviousDeviceId,
    string? CurrentDeviceId,
    [property: Description("True when the role changed to the target, false when unchanged, null when unknown.")] bool? Changed,
    AudioDefaultRoleStatus Status,
    string? ErrorCode);

public sealed record AudioDefaultRoleChange(
    AudioDirection Direction,
    AudioRole Role,
    string? PreviousDeviceId,
    string? CurrentDeviceId);

public sealed record AudioSetDefaultDeviceData(
    string DeviceId,
    AudioDirection Direction,
    AudioDefaultRoleSelection RequestedRole,
    IReadOnlyList<AudioDefaultRoleResult> Roles,
    [property: Description("Roles or directions not requested that also changed during execution.")] IReadOnlyList<AudioDefaultRoleChange> UnrequestedChanges);

/// <summary>
/// Experimental tool that sets the session-wide default audio endpoint for one or all roles of a direction.
/// Uses an isolated writer (<see cref="IAudioDefaultEndpointWriter"/>) and verifies every role via <see cref="IAudioEndpoints"/>.
/// </summary>
public sealed class AudioSetDefaultDeviceTool(
    IAudioEndpoints endpoints,
    IDeviceAliases aliases,
    IAudioDefaultEndpointWriter writer) : SysactaTool<AudioSetDefaultDeviceArgs, AudioSetDefaultDeviceData>
{
    private static readonly AudioRole[] AllRoles = [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications];

    public override ToolDescriptor Descriptor { get; } = new(
        Name: "audio_set_default_device",
        Category: ToolCategories.Audio,
        Title: "Set default audio device",
        Description: "EXPERIMENTAL (uses an undocumented Windows COM interface; not verified on real hardware). Sets the default audio device for the specified direction and role(s), which affects calls, meetings, and recordings. Reads back all roles after each change and reports per-role results with partial or unknown status on incomplete or uncertain outcomes.",
        DefaultPolicy: DefaultPolicy.Experimental,
        ReadOnly: false,
        AuditedArguments: ["direction", "role"]);

    public override ToolAvailability GetAvailability() => writer.GetAvailability();

    protected override ValueTask<ToolTarget> ResolveTargetAsync(AudioSetDefaultDeviceArgs args, CancellationToken cancellationToken)
    {
        var state = AudioTarget.ResolveExplicit(endpoints, aliases, args.Device, args.Direction);
        if (state.State != AudioDeviceState.Active)
        {
            throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The specified audio device is not active.");
        }

        // Lock the target device plus all three role keys for the direction so any default-targeted operation
        // serializes with this call even if the undocumented API couples roles internally.
        string[] keys =
        [
            AudioTarget.DeviceKey(state.Id),
            .. AllRoles.Select(r => AudioTarget.DefaultRoleKey(args.Direction, r)),
        ];
        return ValueTask.FromResult(new ToolTarget(keys, state.Id));
    }

    protected override Task<ToolOutcome<AudioSetDefaultDeviceData>> ExecuteAsync(AudioSetDefaultDeviceArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var id = execution.TargetId!;
        var endpoint = endpoints.GetEndpoint(id);
        if (endpoint.Direction != args.Direction)
        {
            throw new ToolFailureException(ErrorCodes.TargetNotFound, "The specified device does not match the requested direction.");
        }

        if (endpoint.State != AudioDeviceState.Active)
        {
            throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The specified audio device is not active.");
        }

        var before = ReadAllDefaults(endpoints);
        AudioRole[] requested = args.Role == AudioDefaultRoleSelection.All ? AllRoles : [ToAudioRole(args.Role)];
        var results = new Dictionary<AudioRole, AudioDefaultRoleResult>(requested.Length);
        foreach (var r in requested)
        {
            var prev = before[(args.Direction, r)];
            results[r] = prev == id
                ? new(r, prev, prev, false, AudioDefaultRoleStatus.Unchanged, null)
                : new(r, prev, null, null, AudioDefaultRoleStatus.NotAttempted, null);
        }

        if (results.Values.All(x => x.Status == AudioDefaultRoleStatus.Unchanged))
        {
            return Task.FromResult(new ToolOutcome<AudioSetDefaultDeviceData>(
                BuildData(id, args, requested, results, []),
                Verification.Verified));
        }

        using var change = writer.Open();
        var latest = new Dictionary<(AudioDirection, AudioRole), string?>(before);
        var unrequested = new Dictionary<(AudioDirection, AudioRole), AudioDefaultRoleChange>();
        string? stopCode = null;
        string? stopMessage = null;
        var firstWrite = true;

        foreach (var r in requested)
        {
            if (results[r].Status is AudioDefaultRoleStatus.Unchanged or AudioDefaultRoleStatus.Changed)
            {
                continue;
            }

            // An earlier role write in this call may have coupled this role to the target already.
            if (latest[(args.Direction, r)] == id)
            {
                results[r] = new(r, before[(args.Direction, r)], id, true, AudioDefaultRoleStatus.Changed, null);
                ReportProgress(execution, BuildData(id, args, requested, results, unrequested.Values));
                continue;
            }

            if (firstWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();
                execution.BeginSideEffect();
                firstWrite = false;
            }
            else if (execution.BeginNextSideEffect(cancellationToken) is { } stop)
            {
                stopCode = stop.Code;
                stopMessage = stop.Message;
                break;
            }

            results[r] = new(r, before[(args.Direction, r)], null, null, AudioDefaultRoleStatus.Unknown, null);
            ReportProgress(execution, BuildData(id, args, requested, results, unrequested.Values));

            string? writeError = null;
            try
            {
                change.SetDefault(id, r);
            }
            catch (ToolFailureException ex)
            {
                writeError = ex.Code;
            }

            Dictionary<(AudioDirection, AudioRole), string?> after;
            try
            {
                after = ReadAllDefaults(endpoints);
            }
            catch (ToolFailureException ex)
            {
                results[r] = new(r, before[(args.Direction, r)], null, null, AudioDefaultRoleStatus.Unknown, ex.Code);
                stopCode = ex.Code;
                stopMessage = "The default audio device change may have been applied, but the resulting state could not be read back.";
                ReportProgress(execution, BuildData(id, args, requested, results, unrequested.Values));
                break;
            }

            latest = after;
            var prev = before[(args.Direction, r)];
            var curr = after[(args.Direction, r)];
            if (curr == id)
            {
                results[r] = new(r, prev, curr, true, AudioDefaultRoleStatus.Changed, writeError);
            }
            else if (curr == prev)
            {
                results[r] = new(r, prev, curr, false, AudioDefaultRoleStatus.Failed, writeError ?? ErrorCodes.VerificationFailed);
            }
            else
            {
                results[r] = new(r, prev, curr, true, AudioDefaultRoleStatus.Failed, writeError ?? ErrorCodes.VerificationFailed);
            }

            var drifted = false;
            foreach (var other in requested)
            {
                if (other == r)
                {
                    continue;
                }

                var otherPrev = before[(args.Direction, other)];
                var otherCurr = after[(args.Direction, other)];
                if (results[other].Status == AudioDefaultRoleStatus.NotAttempted)
                {
                    if (otherCurr != otherPrev)
                    {
                        if (otherCurr == id)
                        {
                            // Coupled by an earlier write to the requested target.
                            results[other] = new(other, otherPrev, otherCurr, true, AudioDefaultRoleStatus.Changed, null);
                        }
                        else
                        {
                            // Changed to an unexpected device before we attempted it.
                            results[other] = new(other, otherPrev, otherCurr, true, AudioDefaultRoleStatus.Failed, ErrorCodes.VerificationFailed);
                            drifted = true;
                        }
                    }

                    continue;
                }

                if (results[other].Status is AudioDefaultRoleStatus.Changed or AudioDefaultRoleStatus.Unchanged && otherCurr != id)
                {
                    results[other] = new(other, otherPrev, otherCurr, otherCurr != otherPrev, AudioDefaultRoleStatus.Failed, results[other].ErrorCode ?? ErrorCodes.VerificationFailed);
                    drifted = true;
                }
            }

            foreach (var (key, currentVal) in after)
            {
                if (key.Item1 == args.Direction && requested.Contains(key.Item2))
                {
                    continue;
                }

                var previousVal = before[key];
                if (currentVal != previousVal)
                {
                    unrequested[key] = new(key.Item1, key.Item2, previousVal, currentVal);
                }
            }

            ReportProgress(execution, BuildData(id, args, requested, results, unrequested.Values));

            if (unrequested.Count > 0)
            {
                stopCode = ErrorCodes.VerificationFailed;
                stopMessage = "An unrequested audio role or direction also changed.";
                break;
            }

            if (drifted)
            {
                stopCode = ErrorCodes.VerificationFailed;
                stopMessage = "A previously verified audio role changed away from the requested target.";
                break;
            }

            if (results[r].Status != AudioDefaultRoleStatus.Changed || writeError is not null)
            {
                stopCode = results[r].ErrorCode ?? writeError ?? ErrorCodes.VerificationFailed;
                stopMessage = writeError is not null
                    ? "The Windows audio policy interface returned an error."
                    : "The default audio device did not match the requested target after the change.";
                break;
            }
        }

        var data = BuildData(id, args, requested, results, unrequested.Values);
        if (results.Values.Any(x => x.Status == AudioDefaultRoleStatus.Unknown))
        {
            return Task.FromResult(ToolOutcome.Unknown(
                data,
                stopCode ?? ErrorCodes.OsApiError,
                stopMessage ?? "Whether the default audio device was changed is unknown."));
        }

        var anyChanged = results.Values.Any(x => x.Status == AudioDefaultRoleStatus.Changed || x.Changed == true) || unrequested.Count > 0;
        var anyIncomplete = results.Values.Any(x => x.Status is AudioDefaultRoleStatus.Failed or AudioDefaultRoleStatus.NotAttempted) || unrequested.Count > 0;

        if (anyChanged && anyIncomplete)
        {
            return Task.FromResult(ToolOutcome.Partial(
                data,
                Verification.Verified,
                stopCode ?? ErrorCodes.VerificationFailed,
                stopMessage ?? "Each reported role was read back, but the request was not fully achieved."));
        }

        if (!anyIncomplete)
        {
            return Task.FromResult(new ToolOutcome<AudioSetDefaultDeviceData>(data, Verification.Verified));
        }

        throw new ToolFailureException(
            stopCode ?? ErrorCodes.VerificationFailed,
            stopMessage ?? "The default audio device was not changed.");
    }

    private static Dictionary<(AudioDirection, AudioRole), string?> ReadAllDefaults(IAudioEndpoints endpoints)
    {
        var snapshot = new Dictionary<(AudioDirection, AudioRole), string?>(6);
        foreach (var direction in Enum.GetValues<AudioDirection>())
        {
            foreach (var role in AllRoles)
            {
                snapshot[(direction, role)] = endpoints.GetDefaultEndpointId(direction, role);
            }
        }

        return snapshot;
    }

    private static AudioSetDefaultDeviceData BuildData(
        string deviceId,
        AudioSetDefaultDeviceArgs args,
        AudioRole[] requested,
        Dictionary<AudioRole, AudioDefaultRoleResult> results,
        IEnumerable<AudioDefaultRoleChange> unrequested) =>
        new(
            deviceId,
            args.Direction,
            args.Role,
            [.. requested.Select(r => results[r])],
            [.. unrequested]);

    private static AudioRole ToAudioRole(AudioDefaultRoleSelection selection) => selection switch
    {
        AudioDefaultRoleSelection.Console => AudioRole.Console,
        AudioDefaultRoleSelection.Multimedia => AudioRole.Multimedia,
        AudioDefaultRoleSelection.Communications => AudioRole.Communications,
        _ => throw new ArgumentOutOfRangeException(nameof(selection)),
    };
}
