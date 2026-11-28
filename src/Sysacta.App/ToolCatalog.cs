using Sysacta.Core.Devices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Core.Tools.Display;
using Sysacta.Core.Tools.Media;
using Sysacta.Core.Tools.Notification;
using Sysacta.Core.Tools.Power;
using Sysacta.Core.Tools.SystemMonitoring;
using Sysacta.Windows.Audio;
using Sysacta.Windows.Experimental.Audio;
using Sysacta.Windows.SystemMonitoring;

namespace Sysacta.App;

/// <summary>
/// The production tool list. Register every new tool here (and only here). Test-only tools live in test projects
/// and can never be published by this build. See docs/tools.md.
/// </summary>
internal static class ToolCatalog
{
    public static IReadOnlyList<SysactaTool> Create(IDeviceAliases aliases, IAudioEndpoints audio, IDisplays displays, DisplayModeRecovery recovery, IMediaSessions media, IPower power, PowerTransitionGate powerGate, IDesktopNotifications notifications)
    {
        var sessions = new CoreAudioSessions();
        var defaultEndpoints = new PolicyConfigDefaultEndpoints();
        var monitor = new WindowsSystemMonitor();
        return
        [
            new AudioGetStateTool(audio, aliases),
            new AudioListDevicesTool(audio, aliases),
            new AudioSetVolumeTool(audio, aliases),
            new AudioSetMuteTool(audio, aliases),
            new AudioListSessionsTool(audio, aliases, sessions),
            new AudioSetAppVolumeTool(audio, sessions),
            new AudioSetAppMuteTool(audio, sessions),
            new AudioSetDefaultDeviceTool(audio, aliases, defaultEndpoints),
            new DisplayListDevicesTool(displays, aliases),
            new DisplayGetStateTool(displays, aliases),
            new DisplaySetBrightnessTool(displays, aliases),
            new DisplaySetContrastTool(displays, aliases),
            new DisplayListModesTool(displays, aliases),
            new DisplaySetModeTool(displays, aliases, recovery),
            new DisplaySetPowerTool(displays, aliases),
            new DisplaySetInputTool(displays, aliases),
            new MediaListSessionsTool(media),
            new MediaGetStateTool(media),
            new MediaPlayTool(media),
            new MediaPauseTool(media),
            new MediaStopTool(media),
            new MediaNextTrackTool(media),
            new MediaPreviousTrackTool(media),
            new MediaSeekTool(media),
            new PowerGetStateTool(power),
            new PowerListPlansTool(power),
            new PowerSetPlanTool(power),
            new PowerLockTool(power),
            new PowerSleepTool(power),
            new PowerShutdownTool(power),
            new PowerRestartTool(power),
            new SystemGetStatusTool(monitor),
            new SystemGetHardwareTool(monitor),
            new SystemGetTemperaturesTool(monitor),
            new SystemGetStorageTool(monitor),
            new NotificationSendTool(notifications, TimeProvider.System),
            // Local console only (tray menu / device page / overview); never listed or callable over MCP.
            new DisplayRestoreModeTool(displays, recovery),
            new PowerReleaseGateTool(powerGate),
        ];
    }
}
