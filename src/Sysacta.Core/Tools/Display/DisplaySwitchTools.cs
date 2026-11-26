using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Display;

public enum DisplayPowerRequest
{
    On,

    /// <summary>The monitor's DPM off (power-saving) state, not a power cut.</summary>
    Off,
}

public sealed class DisplaySetPowerArgs
{
    [Description("Display id or alias. Required: there is no default display for power changes.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public required string Device { get; init; }

    [Description("'on' or 'off'. 'off' asks the monitor to enter its DPM off (power-saving) state; it is not a power cut.")]
    public required DisplayPowerRequest State { get; init; }
}

public sealed class DisplaySetInputArgs
{
    [Description("Display id or alias. Required: there is no default display for input changes.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public required string Device { get; init; }

    [Description("An input_id returned by display_get_state (include_switching: true) for this display.")]
    [StringLength(64, MinimumLength = 1)]
    [NoControlCharacters]
    public required string InputId { get; init; }
}

public sealed record DisplaySetPowerData(
    string DeviceId,
    string Name,
    DisplayPowerRequest RequestedState,
    [property: Description("Null when the monitor reported a value without a defined meaning.")] DisplayPowerState? PreviousState,
    [property: Description("Read back after the change. Null when it could not be read (common once a monitor is off).")] DisplayPowerState? CurrentState,
    [property: Description("Whether the state differs from before. Null when unknown.")] bool? Changed);

public sealed record DisplaySetInputData(
    string DeviceId,
    string Name,
    string RequestedInputId,
    [property: Description("Null when the monitor reported a value without a standard meaning.")] string? PreviousInputId,
    [property: Description("Read back after the change. Null when it could not be read or has no standard meaning.")] string? CurrentInputId,
    [property: Description("Whether the input differs from before. Null when unknown.")] bool? Changed);

public sealed record DisplayPowerInfo(
    DisplaySupport Support,
    [property: Description("Current MCCS power state. Null when unchecked, unreadable or not a defined state.")] DisplayPowerState? State,
    string? ErrorCode);

public sealed record DisplayInputEntry(
    string InputId,
    [property: Description("The MCCS meaning of the value (e.g. 'HDMI 1'); it may not match the port labels on the monitor.")] string Name,
    bool IsCurrent);

public sealed record DisplayInputInfo(
    DisplaySupport Support,
    [property: Description("Null when unchecked, unreadable, or the monitor reports a value without a standard meaning.")] string? CurrentInputId,
    [property: Description("Inputs the monitor declares with a standard meaning. Declared does not mean a signal is present.")] IReadOnlyList<DisplayInputEntry> Inputs,
    string? ErrorCode);

/// <summary>Reading and changing the DDC/CI power and input settings within the fixed allow-list (<see cref="Mccs"/>).</summary>
public static class DisplaySwitching
{
    public static readonly DisplayPowerInfo PowerUnchecked = new(DisplaySupport.Unchecked, null, null);
    public static readonly DisplayInputInfo InputUnchecked = new(DisplaySupport.Unchecked, null, [], null);

    private const int ReadBackAttempts = 5;
    private static readonly TimeSpan ReadBackDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Reads both settings with one capabilities query. Failures are reported per setting, never thrown.</summary>
    public static (DisplayPowerInfo Power, DisplayInputInfo Input) Read(IDisplays displays, string displayId, CancellationToken cancellationToken)
    {
        IDisplaySwitchControl control;
        MccsCapabilities capabilities;
        try
        {
            control = displays.OpenSwitch(displayId, cancellationToken);
        }
        catch (ToolFailureException ex)
        {
            return (new(DisplayCatalog.SupportFor(ex.Code), null, ex.Code), new(DisplayCatalog.SupportFor(ex.Code), null, [], ex.Code));
        }

        using (control)
        {
            try
            {
                capabilities = Mccs.ParseOrThrow(control.ReadCapabilities());
            }
            catch (ToolFailureException ex)
            {
                return (new(DisplayCatalog.SupportFor(ex.Code), null, ex.Code), new(DisplayCatalog.SupportFor(ex.Code), null, [], ex.Code));
            }

            DisplayPowerInfo power;
            try
            {
                Mccs.Usable(capabilities, DisplaySwitch.Power);
                power = new(DisplaySupport.Supported, Mccs.PowerState(control.Read(DisplaySwitch.Power)), null);
            }
            catch (ToolFailureException ex)
            {
                power = new(DisplayCatalog.SupportFor(ex.Code), null, ex.Code);
            }

            DisplayInputInfo input;
            try
            {
                var values = Mccs.Usable(capabilities, DisplaySwitch.Input);
                var current = control.Read(DisplaySwitch.Input);
                input = new(
                    DisplaySupport.Supported,
                    values.Contains(current) ? Mccs.InputId(displayId, current) : null,
                    [.. values.Select(v => new DisplayInputEntry(Mccs.InputId(displayId, v), Mccs.InputName(v)!, v == current))],
                    null);
            }
            catch (ToolFailureException ex)
            {
                input = new(DisplayCatalog.SupportFor(ex.Code), null, [], ex.Code);
            }

            return (power, input);
        }
    }

    internal enum WriteResult
    {
        /// <summary>Read back at the target value.</summary>
        Verified,

        /// <summary>Read back at another value.</summary>
        Mismatch,

        /// <summary>The write was accepted, but the monitor could not be read afterwards.</summary>
        Accepted,
    }

    /// <summary>
    /// The shared change sequence: the write is prepared, the progress saved, <see cref="ToolExecution.BeginSideEffect"/>
    /// called, then exactly one write and a bounded read-back. A failed write is never retried; its exception propagates
    /// with the side effect started, so the caller reports the result as unknown.
    /// </summary>
    internal static async Task<(WriteResult Result, byte? After)> WriteAsync(
        IDisplaySwitchControl control, DisplaySwitch setting, byte value, ToolExecution execution, Action reportStarted, CancellationToken cancellationToken)
    {
        var write = control.PrepareWrite(setting, value);
        reportStarted();
        cancellationToken.ThrowIfCancellationRequested();
        execution.BeginSideEffect();
        write();

        for (var attempt = 1; attempt <= ReadBackAttempts; attempt++)
        {
            await Task.Delay(ReadBackDelay, cancellationToken).ConfigureAwait(false);
            byte after;
            try
            {
                after = control.Read(setting);
            }
            catch (ToolFailureException)
            {
                // A monitor that switched off or to another input often stops answering on this path.
                continue;
            }

            if (after == value)
            {
                return (WriteResult.Verified, after);
            }

            if (attempt == ReadBackAttempts)
            {
                return (WriteResult.Mismatch, after);
            }
        }

        return (WriteResult.Accepted, null);
    }

    /// <summary>The capabilities and allowed values for one setting of an opened monitor.</summary>
    internal static IReadOnlyList<byte> Usable(IDisplaySwitchControl control, DisplaySwitch setting) =>
        Mccs.Usable(Mccs.ParseOrThrow(control.ReadCapabilities()), setting);

    internal static DisplayInfo Target(IDisplays displays, string id)
    {
        var display = DisplayTarget.Current(displays.GetTopology(), id);
        return DisplayCatalog.BlockedForSwitch(display) is { } blocked ? throw blocked : display;
    }
}

public sealed class DisplaySetPowerTool(IDisplays displays, IDeviceAliases aliases) : SysactaTool<DisplaySetPowerArgs, DisplaySetPowerData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "display_set_power",
        Category: ToolCategories.Display,
        Title: "Set monitor power",
        Description: "Turns one external monitor on or into its power-saving off state over DDC/CI (MCCS VCP 0xD6, values 0x01 / 0x04 only), when the monitor declares both values. 'off' is not a power cut. Many monitors stop answering DDC/CI or disappear from Windows when off, so 'on' may then be impossible from Sysacta: use the monitor's power button. Verified only when the state can be read back; 'accepted' means the monitor took the command but could not be read afterwards. Never retried.",
        DefaultPolicy: DefaultPolicy.Off,
        ReadOnly: false,
        AuditedArguments: ["state"],
        Timeout: TimeSpan.FromSeconds(15)); // capabilities query (can take seconds) + write + read-back

    protected override ValueTask<ToolTarget> ResolveTargetAsync(DisplaySetPowerArgs args, CancellationToken cancellationToken)
    {
        var id = DisplayTarget.Resolve(displays.GetTopology(), aliases, args.Device).Id;
        return ValueTask.FromResult(new ToolTarget([DisplayTarget.DeviceKey(id)], id));
    }

    protected override async Task<ToolOutcome<DisplaySetPowerData>> ExecuteAsync(DisplaySetPowerArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var display = DisplaySwitching.Target(displays, execution.TargetId!);
        using var control = displays.OpenSwitch(display.Id, cancellationToken);
        DisplaySwitching.Usable(control, DisplaySwitch.Power);
        var before = control.Read(DisplaySwitch.Power);
        var previous = Mccs.PowerState(before)
            ?? throw new ToolFailureException(ErrorCodes.OsApiError, "The monitor reported an undefined power state. Nothing was changed.");
        var target = Mccs.PowerValue(args.State);
        var data = new DisplaySetPowerData(display.Id, display.Name, args.State, previous, previous, false);
        if (before == target)
        {
            return new(data, Verification.Verified);
        }

        try
        {
            var (result, after) = await DisplaySwitching.WriteAsync(control, DisplaySwitch.Power, target, execution,
                () => ReportProgress(execution, data with { CurrentState = null, Changed = null }), cancellationToken).ConfigureAwait(false);
            return result switch
            {
                DisplaySwitching.WriteResult.Verified => new(data with { CurrentState = Mccs.PowerState(after!.Value), Changed = true }, Verification.Verified),
                DisplaySwitching.WriteResult.Accepted => new(data with { CurrentState = null, Changed = null }, Verification.Accepted),
                _ => ToolOutcome.Error(data with { CurrentState = Mccs.PowerState(after!.Value), Changed = after != before },
                    ErrorCodes.VerificationFailed, "The monitor reads back a different power state than requested."),
            };
        }
        catch (ToolFailureException ex) when (execution.HasStarted)
        {
            return ToolOutcome.Unknown(data with { CurrentState = null, Changed = null }, ex.Code, "The power command was sent but the monitor did not confirm it; its state is unknown.");
        }
    }
}

public sealed class DisplaySetInputTool(IDisplays displays, IDeviceAliases aliases) : SysactaTool<DisplaySetInputArgs, DisplaySetInputData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "display_set_input",
        Category: ToolCategories.Display,
        Title: "Set monitor input",
        Description: "Switches one external monitor to an input_id from display_get_state (include_switching: true) over DDC/CI (MCCS 2.x VCP 0x60, standard values only). Input names follow MCCS and may not match the port labels. Switching does not guarantee a signal on the new input, and the monitor may stop answering, so switching back may require the monitor's own menu. Verified only when read back; 'accepted' means the monitor took the command but could not be read afterwards. Never retried.",
        DefaultPolicy: DefaultPolicy.Off,
        ReadOnly: false,
        AuditedArguments: [],
        Timeout: TimeSpan.FromSeconds(15)); // capabilities query (can take seconds) + write + read-back

    protected override ValueTask<ToolTarget> ResolveTargetAsync(DisplaySetInputArgs args, CancellationToken cancellationToken)
    {
        var id = DisplayTarget.Resolve(displays.GetTopology(), aliases, args.Device).Id;
        return ValueTask.FromResult(new ToolTarget([DisplayTarget.DeviceKey(id)], id));
    }

    protected override async Task<ToolOutcome<DisplaySetInputData>> ExecuteAsync(DisplaySetInputArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var display = DisplaySwitching.Target(displays, execution.TargetId!);
        using var control = displays.OpenSwitch(display.Id, cancellationToken);
        var values = DisplaySwitching.Usable(control, DisplaySwitch.Input);
        // Re-derived from what this monitor declares now: an id of another display or a vanished input never matches.
        var target = values.Where(v => Mccs.InputId(display.Id, v) == args.InputId).ToArray() switch
        {
            [var v] => v,
            _ => throw new ToolFailureException(ErrorCodes.InvalidArgument, "The input is not available for this display now; call display_get_state with include_switching again."),
        };

        var before = control.Read(DisplaySwitch.Input);
        string? IdOf(byte v) => values.Contains(v) ? Mccs.InputId(display.Id, v) : null;
        var data = new DisplaySetInputData(display.Id, display.Name, args.InputId, IdOf(before), IdOf(before), false);
        if (before == target)
        {
            return new(data, Verification.Verified);
        }

        try
        {
            var (result, after) = await DisplaySwitching.WriteAsync(control, DisplaySwitch.Input, target, execution,
                () => ReportProgress(execution, data with { CurrentInputId = null, Changed = null }), cancellationToken).ConfigureAwait(false);
            return result switch
            {
                DisplaySwitching.WriteResult.Verified => new(data with { CurrentInputId = IdOf(after!.Value), Changed = true }, Verification.Verified),
                DisplaySwitching.WriteResult.Accepted => new(data with { CurrentInputId = null, Changed = null }, Verification.Accepted),
                _ => ToolOutcome.Error(data with { CurrentInputId = IdOf(after!.Value), Changed = after != before },
                    ErrorCodes.VerificationFailed, "The monitor reads back a different input than requested."),
            };
        }
        catch (ToolFailureException ex) when (execution.HasStarted)
        {
            return ToolOutcome.Unknown(data with { CurrentInputId = null, Changed = null }, ex.Code, "The input command was sent but the monitor did not confirm it; its input is unknown.");
        }
    }
}
