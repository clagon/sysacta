using System.Security.Cryptography;
using System.Text;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using static Sysacta.Windows.Display.NativeMethods;

namespace Sysacta.Windows.Display;

/// <summary>An active target with the OS names needed to reach it. The names never leave the adapter.</summary>
internal sealed record DisplayPath(DisplayInfo Info, string GdiDeviceName, string DevicePath);

internal sealed record DisplayPaths(IReadOnlyList<DisplayPath> Paths, int UnidentifiedCount);

/// <summary>The active topology from QueryDisplayConfig. Nothing is cached and no monitor is queried.</summary>
internal static class DisplayTopologyReader
{
    private const int Attempts = 3;

    public static DisplayPaths Read()
    {
        var (paths, modes) = Query();
        var result = new List<DisplayPath>(paths.Length);
        var unidentified = 0;
        foreach (var path in paths)
        {
            var target = new TargetDeviceName { Header = Header<TargetDeviceName>(DeviceInfoGetTargetName, path.Target.AdapterId, path.Target.Id) };
            var source = new SourceDeviceName { Header = Header<SourceDeviceName>(DeviceInfoGetSourceName, path.Source.AdapterId, path.Source.Id) };
            if (GetTargetName(ref target) != ErrorSuccess || string.IsNullOrEmpty(target.MonitorDevicePath))
            {
                unidentified++;
                continue;
            }

            var gdiName = GetSourceName(ref source) == ErrorSuccess ? source.ViewGdiDeviceName : "";
            var idx = path.Source.ModeInfoIdx;
            ModeInfo? sourceMode = idx != PathModeIdxInvalid && idx < modes.Length && modes[idx].InfoType == ModeInfoTypeSource ? modes[idx] : null;
            var rate = path.Target.RefreshRate;
            var mode = sourceMode is { } m
                ? new DisplayCurrentMode((int)m.SourceWidth, (int)m.SourceHeight,
                    rate.Denominator == 0 ? null : rate.Numerator, rate.Denominator == 0 ? null : rate.Denominator)
                : null;
            var cloned = paths.Count(p => SameSource(p.Source, path.Source)) > 1;
            var name = string.IsNullOrWhiteSpace(target.MonitorFriendlyDeviceName)
                ? "(unnamed display)"
                : new string([.. target.MonitorFriendlyDeviceName.Where(c => !char.IsControl(c))]);
            var (kind, connection) = Classify(target.OutputTechnology);
            var info = new DisplayInfo(
                CanonicalId(target.MonitorDevicePath),
                name,
                kind,
                connection,
                // The primary desktop source is the one at the origin of the virtual screen.
                IsPrimary: sourceMode is { SourceX: 0, SourceY: 0 },
                cloned,
                mode,
                gdiName.Length == 0 || mode is null ? ErrorCodes.OsApiError : null);
            result.Add(new DisplayPath(info, gdiName, target.MonitorDevicePath));
        }

        return new DisplayPaths(result, unidentified);
    }

    /// <summary>Opaque, deterministic id: a hash of the target's device interface path (not of names or list order).</summary>
    public static string CanonicalId(string devicePath) =>
        "display-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(devicePath.ToUpperInvariant())).AsSpan(0, 16));

    /// <summary>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY to kind and connection name.</summary>
    public static (DisplayKind Kind, string Connection) Classify(uint technology) => technology switch
    {
        0 => (DisplayKind.External, "vga"),
        1 or 2 or 3 or 8 or 9 or 14 => (DisplayKind.External, "other"),
        4 => (DisplayKind.External, "dvi"),
        5 => (DisplayKind.External, "hdmi"),
        6 => (DisplayKind.Internal, "lvds"),
        10 => (DisplayKind.External, "displayport"),
        11 => (DisplayKind.Internal, "embedded_displayport"),
        12 => (DisplayKind.External, "udi"),
        13 => (DisplayKind.Internal, "embedded_udi"),
        15 => (DisplayKind.Unknown, "miracast"),
        16 => (DisplayKind.Unknown, "indirect_wired"),
        17 => (DisplayKind.Virtual, "indirect_virtual"),
        18 => (DisplayKind.External, "displayport_usb_tunnel"),
        0x80000000 => (DisplayKind.Internal, "internal"),
        _ => (DisplayKind.Unknown, "other"),
    };

    private static bool SameSource(PathSourceInfo a, PathSourceInfo b) =>
        a.Id == b.Id && a.AdapterId.LowPart == b.AdapterId.LowPart && a.AdapterId.HighPart == b.AdapterId.HighPart;

    private static DeviceInfoHeader Header<T>(uint type, Luid adapterId, uint id) =>
        new() { Type = type, Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<T>(), AdapterId = adapterId, Id = id };

    private static (PathInfo[] Paths, ModeInfo[] Modes) Query()
    {
        var status = ErrorSuccess;
        // The topology can change between the size query and the query itself.
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            status = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
            if (status != ErrorSuccess)
            {
                break;
            }

            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            status = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status == ErrorSuccess)
            {
                return (paths[..(int)pathCount], modes[..(int)modeCount]);
            }

            if (status != ErrorInsufficientBuffer)
            {
                break;
            }
        }

        throw status == ErrorAccessDenied
            ? new ToolFailureException(ErrorCodes.NoActiveSession, "The display configuration is not accessible from this session.")
            : new ToolFailureException(ErrorCodes.OsApiError, "The Windows display configuration API returned an error.");
    }
}
