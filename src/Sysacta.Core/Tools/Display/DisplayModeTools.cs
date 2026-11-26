using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Display;

public sealed class DisplaySetModeArgs
{
    [Description("Display id or alias. Required: there is no default display for mode changes.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public required string Device { get; init; }

    [Description("A mode_id returned by display_list_modes for this display.")]
    [StringLength(64, MinimumLength = 1)]
    [NoControlCharacters]
    public required string ModeId { get; init; }
}

public sealed class DisplayRestoreModeArgs;

public sealed record DisplayModeValue(
    string ModeId,
    int Width,
    int Height,
    [property: Description("Integer refresh rate as Windows enumerates it.")] int RefreshHz,
    int BitsPerPixel,
    bool Interlaced,
    [property: Description("Clockwise rotation in degrees.")] int Orientation,
    [property: Description("Windows fixed-output mode: 0 default, 1 stretched, 2 centered.")] int FixedOutput)
{
    public static DisplayModeValue From(string displayId, DisplayMode m) =>
        new(DisplayListModesTool.ModeId(displayId, m), m.Width, m.Height, m.RefreshHz, m.BitsPerPixel, m.Interlaced, m.Orientation, m.FixedOutput);
}

public sealed record DisplayModeChangeData(
    string DeviceId,
    string Name,
    string RequestedModeId,
    DisplayModeValue PreviousMode,
    [property: Description("The mode Windows reports after the change. Null when it could not be read. A matching mode does not prove the monitor shows a picture.")] DisplayModeValue? CurrentMode,
    [property: Description("Whether the mode differs from before. Null when unknown.")] bool? Changed,
    [property: Description("True when the local user can restore the previous mode from the Sysacta console or tray menu. There is no MCP tool for it.")] bool RecoveryAvailable,
    [property: Description("True when other displays' mode, position, rotation, mirroring or the primary display changed during the call (not necessarily caused by Sysacta). Null when unknown.")] bool? OtherDisplaysChanged);

/// <summary>What the local console can restore: the latest mode change made by Sysacta while it is running.</summary>
public sealed record DisplayModeRecord(string DisplayId, string Name, DisplayMode Original, DisplayMode Applied);

/// <summary>
/// The single recovery record (the latest mode change, app lifetime only, never persisted). Created before the change
/// starts, so a change whose result is unknown can still be restored; withdrawn when the change is known not to have
/// happened, cleared once restored.
/// </summary>
public sealed class DisplayModeRecovery
{
    private DisplayModeRecord? _latest;

    /// <summary>Raised on any thread after the record changed.</summary>
    public event EventHandler? Changed;

    public DisplayModeRecord? Latest => Volatile.Read(ref _latest);

    internal DisplayModeRecord? Replace(DisplayModeRecord record)
    {
        var previous = Interlocked.Exchange(ref _latest, record);
        Changed?.Invoke(this, EventArgs.Empty);
        return previous;
    }

    /// <summary>Puts <paramref name="previous"/> back if <paramref name="record"/> is still the latest.</summary>
    internal void Withdraw(DisplayModeRecord record, DisplayModeRecord? previous)
    {
        if (Interlocked.CompareExchange(ref _latest, previous, record) == record)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Changes the mode of one desktop source and verifies it. Only modes Windows enumerates now are used, CDS_TEST must
/// pass first, the change is dynamic (not saved to the registry) and is never retried or rolled back automatically.
/// </summary>
internal static class DisplayModeChange
{
    public static async Task<ToolOutcome<DisplayModeChangeData>> RunAsync(
        IDisplayModeControl control,
        DisplayInfo display,
        DisplayMode from,
        DisplayMode to,
        DisplayModeRecovery recovery,
        DisplayModeRecord? restoring,
        ToolExecution execution,
        Action<DisplayModeChangeData> report,
        CancellationToken cancellationToken)
    {
        var id = display.Id;
        var before = control.Layout();
        if (before.Count(l => l.Id == id) != 1)
        {
            throw new ToolFailureException(ErrorCodes.TargetChanged, "The display configuration changed. Nothing was changed; retry.", retryable: true);
        }

        control.Test(to);
        var apply = control.PrepareApply(to);

        var record = restoring ?? new DisplayModeRecord(id, display.Name, from, to);
        var data = new DisplayModeChangeData(id, display.Name, DisplayListModesTool.ModeId(id, to), DisplayModeValue.From(id, from), null, null, true, null);
        report(data);
        cancellationToken.ThrowIfCancellationRequested();

        // Recorded before the change starts: a timeout during the native call must still leave a way back.
        var previous = restoring is null ? recovery.Replace(record) : null;
        try
        {
            execution.BeginSideEffect();
        }
        catch
        {
            if (restoring is null)
            {
                recovery.Withdraw(record, previous);
            }

            throw;
        }

        ToolFailureException? refused = null;
        try
        {
            apply();
        }
        catch (ToolFailureException ex)
        {
            refused = ex; // Windows reported failure; the read-back below tells what actually happened.
        }

        DisplayMode? after;
        IReadOnlyList<DisplayLayout> layout;
        try
        {
            after = control.ListModes().Current;
            layout = control.Layout();
        }
        catch (ToolFailureException ex)
        {
            return ToolOutcome.Unknown(data, ex.Code, "The mode change was started but the resulting mode could not be read.");
        }

        if (after is null)
        {
            return ToolOutcome.Unknown(data, ErrorCodes.OsApiError, "The mode change was started but the resulting mode could not be read.");
        }

        var othersChanged = !Others(before, id).SequenceEqual(Others(layout, id));
        if (after == from && restoring is null)
        {
            recovery.Withdraw(record, previous); // nothing to restore
        }
        else if (after == to && restoring is not null)
        {
            recovery.Withdraw(record, null);
        }

        data = data with
        {
            CurrentMode = DisplayModeValue.From(id, after),
            Changed = after != from,
            RecoveryAvailable = recovery.Latest == record,
            OtherDisplaysChanged = othersChanged,
        };

        // The read-back decides: a reported failure whose mode nevertheless took effect is a change, not an error.
        if (after == to && !othersChanged)
        {
            return new(data, Verification.Verified);
        }

        if (after == to)
        {
            return ToolOutcome.Partial(data, Verification.Verified, ErrorCodes.VerificationFailed,
                "The mode was applied, but other displays changed during the call. Check the display layout; nothing else is changed automatically.");
        }

        if (after == from)
        {
            return ToolOutcome.Error(data, refused?.Code ?? ErrorCodes.VerificationFailed,
                refused?.Message ?? "Windows reported success but the mode did not change.");
        }

        return ToolOutcome.Error(data, ErrorCodes.VerificationFailed, "The display ended up in a mode other than the requested one.");
    }

    /// <summary>
    /// Everything a mode change of display <paramref name="id"/> should leave alone: the other displays entirely, and the
    /// changed display's rotation, mirroring and primary state (its own mode and position may change).
    /// </summary>
    private static IEnumerable<DisplayLayout> Others(IReadOnlyList<DisplayLayout> layout, string id) =>
        layout.Select(l => l.Id == id ? l with { Mode = null, X = 0, Y = 0 } : l).OrderBy(l => l.Id, StringComparer.Ordinal);
}

public sealed class DisplaySetModeTool(IDisplays displays, IDeviceAliases aliases, DisplayModeRecovery recovery) : SysactaTool<DisplaySetModeArgs, DisplayModeChangeData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "display_set_mode",
        Category: ToolCategories.Display,
        Title: "Set display mode",
        Description: "Changes the resolution / refresh rate of one non-primary display to a mode_id from display_list_modes. Windows tests the mode first; the change is not saved (signing out or restarting may revert it). The result is verified against the mode Windows reports, which does not prove the monitor shows a picture. If the screen goes blank, only the local user can restore the previous mode (Sysacta console or tray menu). The primary display, mirrored and virtual displays are refused. Rotation and scaling are never changed.",
        DefaultPolicy: DefaultPolicy.Off,
        ReadOnly: false,
        AuditedArguments: [],
        Timeout: TimeSpan.FromSeconds(15)); // a modeset is synchronous and can take seconds

    protected override ValueTask<ToolTarget> ResolveTargetAsync(DisplaySetModeArgs args, CancellationToken cancellationToken)
    {
        var id = DisplayTarget.Resolve(displays.GetTopology(), aliases, args.Device).Id;
        return ValueTask.FromResult(new ToolTarget([DisplayTarget.DeviceKey(id)], id));
    }

    protected override Task<ToolOutcome<DisplayModeChangeData>> ExecuteAsync(DisplaySetModeArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var display = Check(DisplayTarget.Current(displays.GetTopology(), execution.TargetId!));
        using var control = displays.OpenMode(display.Id, cancellationToken);
        // Checked again after waiting for the adapter, from the layout the change is compared against.
        switch (control.Layout().Where(l => l.Id == display.Id).ToArray())
        {
            case [var now]:
                Check(display with { IsPrimary = now.IsPrimary, IsCloned = now.IsCloned });
                break;
            default:
                throw new ToolFailureException(ErrorCodes.TargetChanged, "The display configuration changed. Nothing was changed; retry.", retryable: true);
        }

        var set = control.ListModes();
        var current = set.Current
            ?? throw new ToolFailureException(ErrorCodes.OsApiError, "The current mode cannot be read, so it could not be restored. Nothing was changed.");
        var requested = set.Modes.Append(current).Distinct().Where(m => DisplayListModesTool.ModeId(display.Id, m) == args.ModeId).ToArray() switch
        {
            [var m] => m,
            _ => throw new ToolFailureException(ErrorCodes.InvalidArgument, "The mode is not available for this display now; call display_list_modes again."),
        };

        if (requested == current)
        {
            var value = DisplayModeValue.From(display.Id, current);
            return Task.FromResult(new ToolOutcome<DisplayModeChangeData>(
                new(display.Id, display.Name, value.ModeId, value, value, false, false, false), Verification.Verified));
        }

        if (requested.Orientation != current.Orientation || requested.FixedOutput != current.FixedOutput)
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "Changing the rotation or scaling of a display is not supported; choose a mode with the current values.");
        }

        return DisplayModeChange.RunAsync(control, display, current, requested, recovery, null, execution, d => ReportProgress(execution, d), cancellationToken);
    }

    private static DisplayInfo Check(DisplayInfo display)
    {
        if (DisplayCatalog.BlockedForMode(display) is { } blocked)
        {
            throw blocked;
        }

        // The tray menu and console used for recovery live on the primary display, so that one is never changed.
        return display.IsPrimary
            ? throw new ToolFailureException(ErrorCodes.NotSupported, "The primary display's mode cannot be changed: the local recovery would depend on the display being changed.")
            : display;
    }
}

/// <summary>
/// Restores the latest mode change. Local console only (tray menu and device page): MCP clients can neither list nor
/// call it. It applies only the recorded original mode, only to the same display, and only while that display still
/// shows the mode Sysacta applied.
/// </summary>
public sealed class DisplayRestoreModeTool(IDisplays displays, DisplayModeRecovery recovery) : SysactaTool<DisplayRestoreModeArgs, DisplayModeChangeData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "display_restore_mode",
        Category: ToolCategories.Display,
        Title: "Restore display mode",
        Description: "Restores the display mode that was active before Sysacta's latest mode change. Local console only.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: false,
        AuditedArguments: [],
        Timeout: TimeSpan.FromSeconds(15),
        LocalOnly: true);

    private sealed record RestoreTarget(DisplayModeRecord Record) : ToolTarget([DisplayTarget.DeviceKey(Record.DisplayId)], Record.DisplayId);

    protected override ValueTask<ToolTarget> ResolveTargetAsync(DisplayRestoreModeArgs args, CancellationToken cancellationToken)
    {
        var record = recovery.Latest ?? throw new ToolFailureException(ErrorCodes.TargetNotFound, "There is no display mode change to restore.");
        DisplayTarget.Current(displays.GetTopology(), record.DisplayId);
        return ValueTask.FromResult<ToolTarget>(new RestoreTarget(record));
    }

    protected override async Task<ToolOutcome<DisplayModeChangeData>> ExecuteAsync(DisplayRestoreModeArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var record = ((RestoreTarget)execution.Target).Record;
        if (recovery.Latest != record)
        {
            throw new ToolFailureException(ErrorCodes.TargetChanged, "A newer mode change was made while waiting. Nothing was changed; retry.", retryable: true);
        }

        var display = DisplayTarget.Current(displays.GetTopology(), record.DisplayId);
        if (DisplayCatalog.BlockedForMode(display) is { } blocked)
        {
            throw blocked;
        }

        using var control = displays.OpenMode(display.Id, cancellationToken);
        var set = control.ListModes();
        if (set.Current == record.Original)
        {
            recovery.Withdraw(record, null);
            var value = DisplayModeValue.From(display.Id, record.Original);
            return new(new(display.Id, display.Name, value.ModeId, value, value, false, false, false), Verification.Verified);
        }

        if (set.Current != record.Applied)
        {
            // Changed by someone else since: an old record is never applied over a newer state.
            recovery.Withdraw(record, null);
            throw new ToolFailureException(ErrorCodes.TargetChanged, "The display mode changed after Sysacta's change, so it is not restored. Nothing was changed.");
        }

        if (!set.Modes.Contains(record.Original))
        {
            throw new ToolFailureException(ErrorCodes.NotSupported, "Windows no longer offers the previous mode for this display. Nothing was changed.");
        }

        return await DisplayModeChange.RunAsync(control, display, record.Applied, record.Original, recovery, record, execution, d => ReportProgress(execution, d), cancellationToken).ConfigureAwait(false);
    }
}
