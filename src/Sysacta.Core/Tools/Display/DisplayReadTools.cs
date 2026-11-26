using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Sysacta.Core.Devices;

namespace Sysacta.Core.Tools.Display;

public sealed class DisplayListDevicesArgs;

public sealed class DisplayListDevicesTool(IDisplays displays, IDeviceAliases aliases) : SysactaTool<DisplayListDevicesArgs, DisplayDeviceListData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "display_list_devices",
        Category: ToolCategories.Display,
        Title: "List displays",
        Description: "Lists the active displays (primary first) with id, name, kind, connection, current desktop mode and aliases. Monitors are not queried, so brightness/contrast support is reported as 'unchecked' unless it is already ruled out; use display_get_state. At most 32 displays.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: true,
        AuditedArguments: []);

    protected override Task<ToolOutcome<DisplayDeviceListData>> ExecuteAsync(DisplayListDevicesArgs args, ToolExecution execution, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolOutcome<DisplayDeviceListData>(DisplayCatalog.List(displays, aliases), Verification.NotApplicable));
}

public sealed record NativeRange(long Min, long Max);

public sealed record DisplayFeatureState(
    DisplaySupport Support,
    [property: Description("Value 0-100, null when it cannot be read.")] int? Level,
    NativeRange? NativeRange,
    [property: Description("'ddc_ci': value held by the monitor hardware. 'wmi': internal panel brightness controlled by Windows.")] DisplayControlMethod? Method,
    string? ErrorCode)
{
    /// <summary>Reads one value without changing anything. Per-feature failures are reported, not thrown.</summary>
    public static DisplayFeatureState Read(IDisplays displays, string displayId, DisplayFeature feature, CancellationToken cancellationToken)
    {
        try
        {
            using var control = displays.Open(displayId, feature, cancellationToken);
            var range = control.Read().Validated();
            return new(DisplaySupport.Supported, range.ToLevel(range.Current), new(range.Min, range.Max), control.Method, null);
        }
        catch (ToolFailureException ex)
        {
            return new(DisplayCatalog.SupportFor(ex.Code), null, null, null, ex.Code);
        }
    }
}

public sealed class DisplayGetStateArgs
{
    [Description("Display id or alias. Omit for the primary display.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public string? Device { get; init; }

    [Description("Also query the monitor's DDC/CI power state and input sources (slower: the capabilities query can take seconds). Default false: 'power' and 'input' are 'unchecked'.")]
    [DefaultValue(false)]
    public bool IncludeSwitching { get; init; }
}

public sealed record DisplayGetStateData(
    string DeviceId,
    string Name,
    string State,
    DisplayKind Kind,
    string Connection,
    bool IsPrimary,
    bool IsCloned,
    [property: Description("Desktop resolution and refresh rate, not necessarily the panel's native resolution.")] DisplayCurrentMode? CurrentMode,
    DisplayFeatureState Brightness,
    DisplayFeatureState Contrast,
    [property: Description("DDC/CI power state (external monitors). 'unchecked' unless include_switching is true.")] DisplayPowerInfo Power,
    [property: Description("DDC/CI input sources (external monitors). 'unchecked' unless include_switching is true.")] DisplayInputInfo Input);

public sealed class DisplayGetStateTool(IDisplays displays, IDeviceAliases aliases) : SysactaTool<DisplayGetStateArgs, DisplayGetStateData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "display_get_state",
        Category: ToolCategories.Display,
        Title: "Get display state",
        Description: "Returns the identity, current desktop mode, brightness and contrast (0-100) of a display. With include_switching: true it also returns the DDC/CI power state and the input sources (input_id values for display_set_input). A value that is unsupported or cannot be read is null with a support status and error code. Uses the primary display when 'device' is omitted.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: true,
        AuditedArguments: [],
        Timeout: TimeSpan.FromSeconds(15)); // DDC/CI; the optional capabilities query can take seconds

    protected override ValueTask<ToolTarget> ResolveTargetAsync(DisplayGetStateArgs args, CancellationToken cancellationToken)
    {
        var id = DisplayTarget.Resolve(displays.GetTopology(), aliases, args.Device).Id;
        return ValueTask.FromResult(new ToolTarget([DisplayTarget.DeviceKey(id)], id));
    }

    protected override Task<ToolOutcome<DisplayGetStateData>> ExecuteAsync(DisplayGetStateArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var topology = displays.GetTopology();
        DisplayTarget.EnsureStillPrimary(topology, args.Device, execution.TargetId!);
        var d = DisplayTarget.Current(topology, execution.TargetId!);
        var brightness = DisplayFeatureState.Read(displays, d.Id, DisplayFeature.Brightness, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var contrast = DisplayFeatureState.Read(displays, d.Id, DisplayFeature.Contrast, cancellationToken);
        var (power, input) = (DisplaySwitching.PowerUnchecked, DisplaySwitching.InputUnchecked);
        if (args.IncludeSwitching)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (power, input) = DisplaySwitching.Read(displays, d.Id, cancellationToken);
        }

        var data = new DisplayGetStateData(d.Id, d.Name, "active", d.Kind, d.Connection, d.IsPrimary, d.IsCloned, d.CurrentMode, brightness, contrast, power, input);
        return Task.FromResult(new ToolOutcome<DisplayGetStateData>(data, Verification.NotApplicable));
    }
}

public sealed class DisplayListModesArgs
{
    [Description("Display id or alias.")]
    [StringLength(512, MinimumLength = 1)]
    [NoControlCharacters]
    public required string Device { get; init; }
}

public sealed record DisplayModeEntry(
    [property: Description("Deterministic id of this mode on this display. Invalid once the display id changes.")] string ModeId,
    int Width,
    int Height,
    [property: Description("Integer refresh rate as Windows enumerates it (59.94 Hz typically appears as 59).")] int RefreshHz,
    int BitsPerPixel,
    bool Interlaced,
    [property: Description("Clockwise rotation in degrees.")] int Orientation,
    [property: Description("Windows fixed-output mode: 0 default, 1 stretched, 2 centered.")] int FixedOutput,
    bool IsCurrent);

public sealed record DisplayListModesData(string DeviceId, string Name, IReadOnlyList<DisplayModeEntry> Modes, int TotalCount, bool Truncated);

public sealed class DisplayListModesTool(IDisplays displays, IDeviceAliases aliases) : SysactaTool<DisplayListModesArgs, DisplayListModesData>
{
    public const int MaxModes = 512;

    public override ToolDescriptor Descriptor { get; } = new(
        Name: "display_list_modes",
        Category: ToolCategories.Display,
        Title: "List display modes",
        Description: "Lists the resolution / refresh rate combinations Windows enumerates for a display, largest first. Listing a mode does not guarantee it can be applied. No mode is applied or tested. At most 512 modes; see 'truncated'. Mirrored displays are refused.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: true,
        AuditedArguments: []);

    protected override ValueTask<ToolTarget> ResolveTargetAsync(DisplayListModesArgs args, CancellationToken cancellationToken)
    {
        var id = DisplayTarget.Resolve(displays.GetTopology(), aliases, args.Device).Id;
        return ValueTask.FromResult(new ToolTarget([DisplayTarget.DeviceKey(id)], id));
    }

    protected override Task<ToolOutcome<DisplayListModesData>> ExecuteAsync(DisplayListModesArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var display = DisplayTarget.Current(displays.GetTopology(), execution.TargetId!);
        if (display.IsCloned)
        {
            throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "The display is mirrored; its modes cannot be attributed to one monitor.");
        }

        var set = displays.ListModes(display.Id);
        var all = set.Modes.Append(set.Current).OfType<DisplayMode>().Distinct()
            .OrderByDescending(m => m.Width).ThenByDescending(m => m.Height).ThenByDescending(m => m.RefreshHz)
            .ThenByDescending(m => m.BitsPerPixel).ThenBy(m => m.Interlaced).ThenBy(m => m.Orientation).ThenBy(m => m.FixedOutput)
            .ToList();
        var kept = all.Take(MaxModes).ToList();
        if (set.Current is { } current && !kept.Contains(current))
        {
            kept[^1] = current; // the current mode is always reported
        }

        var modes = kept.Select(m => new DisplayModeEntry(
            ModeId(display.Id, m), m.Width, m.Height, m.RefreshHz, m.BitsPerPixel, m.Interlaced, m.Orientation, m.FixedOutput, m == set.Current)).ToList();
        var data = new DisplayListModesData(display.Id, display.Name, modes, all.Count, all.Count > modes.Count);
        return Task.FromResult(new ToolOutcome<DisplayListModesData>(data, Verification.NotApplicable));
    }

    /// <summary>Every attribute that distinguishes two modes is part of the id.</summary>
    public static string ModeId(string displayId, DisplayMode m)
    {
        var canonical = FormattableString.Invariant(
            $"{displayId}|{m.Width}|{m.Height}|{m.RefreshHz}|{m.BitsPerPixel}|{(m.Interlaced ? 1 : 0)}|{m.Orientation}|{m.FixedOutput}");
        return "mode-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)).AsSpan(0, 12));
    }
}
