using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Display;

public sealed class DisplaySetBrightnessArgs : DisplaySetLevelArgs
{
    [Description("Display id or alias, or 'all' for every active display. Omit for the primary display.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public string? Device { get; init; }

    internal override string? Target => Device;
}

public sealed class DisplaySetContrastArgs : DisplaySetLevelArgs
{
    [Description("Display id or alias. Omit for the primary display. 'all' is not supported.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public string? Device { get; init; }

    internal override string? Target => Device;
}

public abstract class DisplaySetLevelArgs
{
    [Description("Absolute level, integer 0-100, scaled to the range the display reports.")]
    [Range(0, 100)]
    public required int Level { get; init; }

    internal abstract string? Target { get; }
}

public enum DisplayChangeStatus
{
    /// <summary>Written and read back at the effective target.</summary>
    Changed,

    /// <summary>Already at the effective target; nothing was written.</summary>
    Unchanged,

    /// <summary>The target was not reached and the state is known (see error_code and changed).</summary>
    Failed,

    /// <summary>A write was started and its effect is unknown.</summary>
    Unknown,

    /// <summary>Nothing was written to this display.</summary>
    NotAttempted,
}

public sealed record DisplayLevelResult(
    string DeviceId,
    string Name,
    DisplayChangeStatus Status,
    [property: Description("Level the display was expected to reach after rounding to its native values.")] int? EffectiveLevel,
    int? PreviousLevel,
    int? CurrentLevel,
    [property: Description("Whether the value differs from before. Null when unknown.")] bool? Changed,
    NativeRange? NativeRange,
    string? ErrorCode);

public sealed record DisplaySetLevelData(
    int RequestedLevel,
    IReadOnlyList<DisplayLevelResult> Devices,
    [property: Description("With 'all': active displays without a device identity, which were not changed.")] int UnidentifiedDisplays);

/// <summary>
/// The single execution path of brightness and contrast, for one display or for 'all' (brightness only). Displays are
/// changed one after another; each write is authorized separately and never retried or rolled back. The run continues
/// after a failure only when that display's state is known (nothing written, or read back and different); it stops after
/// an unknown result, a refused or cancelled next change.
/// </summary>
public abstract class DisplaySetLevelTool<TArgs>(IDisplays displays, IDeviceAliases aliases) : SysactaTool<TArgs, DisplaySetLevelData>
    where TArgs : DisplaySetLevelArgs
{
    /// <summary>More displays than this are refused for 'all' before anything is changed.</summary>
    public const int MaxBatch = 8;

    private const int ReadBackAttempts = 5;
    private static readonly TimeSpan ReadBackDelay = TimeSpan.FromMilliseconds(100);

    private protected abstract DisplayFeature Feature { get; }

    private bool IsAll(TArgs args) => Feature == DisplayFeature.Brightness && args.Target == DisplayTarget.All;

    protected override ValueTask<ToolTarget> ResolveTargetAsync(TArgs args, CancellationToken cancellationToken)
    {
        var topology = displays.GetTopology();
        if (!IsAll(args))
        {
            var id = DisplayTarget.Resolve(topology, aliases, args.Target).Id;
            return ValueTask.FromResult(new ToolTarget([DisplayTarget.DeviceKey(id)], id));
        }

        // The batch is fixed here: displays connected (or becoming identifiable) later never join it or affect it.
        var batch = topology.Displays.DistinctBy(d => d.Id, StringComparer.Ordinal).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        return batch.Length switch
        {
            0 => throw new ToolFailureException(ErrorCodes.TargetNotFound, "There is no display that can be identified."),
            > MaxBatch => throw new ToolFailureException(ErrorCodes.InvalidArgument, $"'all' supports at most {MaxBatch} displays; specify one display."),
            _ => ValueTask.FromResult<ToolTarget>(new BatchTarget(
                [.. batch.Select(d => DisplayTarget.DeviceKey(d.Id))], batch, topology.UnidentifiedCount)),
        };
    }

    /// <summary>What 'all' resolved to: the displays to change and how many could not be identified at that moment.</summary>
    private sealed record BatchTarget(IReadOnlyList<string> Keys, IReadOnlyList<DisplayInfo> Displays, int Unidentified)
        : ToolTarget(Keys, DisplayTarget.All);

    protected override async Task<ToolOutcome<DisplaySetLevelData>> ExecuteAsync(TArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var batch = execution.Target as BatchTarget;
        var all = batch is not null;
        IReadOnlyList<DisplayInfo> targets;
        if (batch is not null)
        {
            targets = batch.Displays;
        }
        else
        {
            var topology = displays.GetTopology();
            DisplayTarget.EnsureStillPrimary(topology, args.Target, execution.TargetId!);
            targets = [DisplayTarget.Current(topology, execution.TargetId!)];
        }

        var results = targets.Select(d => new DisplayLevelResult(
            d.Id, d.Name, DisplayChangeStatus.NotAttempted, null, null, null, false, null, null)).ToArray();
        var unidentified = batch?.Unidentified ?? 0;
        DisplaySetLevelData Snapshot() => new(args.Level, [.. results], unidentified);

        ToolError? failure = null; // the first reason a display did not reach the target
        var started = false;
        var stop = false;
        for (var i = 0; i < results.Length && !stop; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var control = displays.Open(results[i].DeviceId, Feature, cancellationToken);
                var before = control.Read().Validated();
                var target = before.NativeFor(args.Level);
                // Everything slow that changes nothing happens before the start point of the change.
                var write = before.Current == target ? null : control.PrepareWrite(target);
                if (!all)
                {
                    // Open can wait a long time for the adapter, and reads and preparation are slow: check the default target again.
                    DisplayTarget.EnsureStillPrimary(displays.GetTopology(), args.Target, results[i].DeviceId);
                }

                var known = results[i] with
                {
                    EffectiveLevel = before.ToLevel(target),
                    PreviousLevel = before.ToLevel(before.Current),
                    NativeRange = new(before.Min, before.Max),
                };
                if (write is null)
                {
                    results[i] = known with { Status = DisplayChangeStatus.Unchanged, CurrentLevel = known.PreviousLevel };
                }
                else
                {
                    // Saved before the write, so that a timeout during the native call never reports "not attempted".
                    results[i] = known with { Status = DisplayChangeStatus.Unknown, Changed = null };
                    ReportProgress(execution, Snapshot());
                    if (!started)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        execution.BeginSideEffect();
                        started = true;
                    }
                    else if (execution.BeginNextSideEffect(cancellationToken) is { } refused)
                    {
                        results[i] = known with { Status = DisplayChangeStatus.NotAttempted, CurrentLevel = known.PreviousLevel, ErrorCode = refused.Code };
                        failure ??= refused;
                        break;
                    }

                    try
                    {
                        write();
                        var after = await ReadBackAsync(control, target, cancellationToken).ConfigureAwait(false);
                        var matched = after.Current == target;
                        results[i] = known with
                        {
                            Status = matched ? DisplayChangeStatus.Changed : DisplayChangeStatus.Failed,
                            CurrentLevel = after.ToLevel(after.Current),
                            Changed = after.Current != before.Current,
                            ErrorCode = matched ? null : ErrorCodes.VerificationFailed,
                        };
                        if (!matched)
                        {
                            failure ??= new(ErrorCodes.VerificationFailed, "A value was set but reads back differently from the requested level.", false);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // The write may or may not have taken effect. Stop: no further changes.
                        var code = ex switch { ToolFailureException f => f.Code, OperationCanceledException => ErrorCodes.Cancelled, _ => ErrorCodes.InternalError };
                        results[i] = results[i] with { ErrorCode = code };
                        failure = new(code, "A change was started but its result could not be confirmed.", false);
                        stop = true;
                    }
                }
            }
            catch (ToolFailureException ex)
            {
                // Failed before writing to this display, so the remaining displays can still be changed.
                results[i] = results[i] with { Status = DisplayChangeStatus.Failed, ErrorCode = ex.Code };
                failure ??= new(ex.Code, ex.Message, ex.Retryable);
            }

            ReportProgress(execution, Snapshot());
        }

        var data = Snapshot();
        if (data.Devices.Any(d => d.Status == DisplayChangeStatus.Unknown))
        {
            return ToolOutcome.Unknown(data, failure!.Code, failure.Message);
        }

        if (unidentified == 0 && data.Devices.All(d => d.Status is DisplayChangeStatus.Changed or DisplayChangeStatus.Unchanged))
        {
            return new(data, Verification.Verified);
        }

        failure ??= new(ErrorCodes.NotSupported, "Some active displays cannot be identified and were not changed.", false);
        return data.Devices.Any(d => d.Status == DisplayChangeStatus.Changed)
            ? ToolOutcome.Partial(data, Verification.Verified, failure.Code, all ? "Not every display reached the requested level." : failure.Message)
            : ToolOutcome.Error(data, failure.Code, failure.Message, failure.Retryable && !started);
    }

    /// <summary>Monitors can apply a value late: re-read for a bounded time. The write is never repeated.</summary>
    private static async Task<LevelRange> ReadBackAsync(IDisplayControl control, long target, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            LevelRange? after = null;
            try
            {
                after = control.Read().Validated();
            }
            catch (ToolFailureException) when (attempt < ReadBackAttempts)
            {
                // Some monitors do not answer right after a change.
            }

            if (after is not null && (after.Current == target || attempt >= ReadBackAttempts))
            {
                return after;
            }

            await Task.Delay(ReadBackDelay, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class DisplaySetBrightnessTool(IDisplays displays, IDeviceAliases aliases) : DisplaySetLevelTool<DisplaySetBrightnessArgs>(displays, aliases)
{
    private protected override DisplayFeature Feature => DisplayFeature.Brightness;

    public override ToolDescriptor Descriptor { get; } = new(
        Name: "display_set_brightness",
        Category: ToolCategories.Display,
        Title: "Set display brightness",
        Description: "Sets the absolute hardware brightness (0-100) of a display and verifies it by reading it back. Values are rounded to what the display accepts (see effective_level). Uses the primary display when 'device' is omitted; 'all' changes every active display one by one and reports each result (status may be partial or unknown).",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: false,
        AuditedArguments: ["level"],
        Timeout: TimeSpan.FromSeconds(20)); // DDC/CI; budget for up to MaxBatch displays
}

public sealed class DisplaySetContrastTool(IDisplays displays, IDeviceAliases aliases) : DisplaySetLevelTool<DisplaySetContrastArgs>(displays, aliases)
{
    private protected override DisplayFeature Feature => DisplayFeature.Contrast;

    public override ToolDescriptor Descriptor { get; } = new(
        Name: "display_set_contrast",
        Category: ToolCategories.Display,
        Title: "Set display contrast",
        Description: "Sets the absolute hardware contrast (0-100) of one display over DDC/CI and verifies it by reading it back. Not available for internal panels. Uses the primary display when 'device' is omitted.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: false,
        AuditedArguments: ["level"],
        Timeout: TimeSpan.FromSeconds(10)); // DDC/CI
}
