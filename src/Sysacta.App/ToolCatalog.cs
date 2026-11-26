using Sysacta.Core.Devices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Core.Tools.Display;
using Sysacta.Windows.Audio;
using Sysacta.Windows.Experimental.Audio;

namespace Sysacta.App;

/// <summary>
/// The production tool list. Register every new tool here (and only here). Test-only tools live in test projects
/// and can never be published by this build. See docs/tools.md.
/// </summary>
internal static class ToolCatalog
{
    public static IReadOnlyList<SysactaTool> Create(IDeviceAliases aliases, IAudioEndpoints audio, IDisplays displays, DisplayModeRecovery recovery)
    {
        var sessions = new CoreAudioSessions();
        var defaultEndpoints = new PolicyConfigDefaultEndpoints();
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
            // Local console only (tray menu / device page); never listed or callable over MCP.
            new DisplayRestoreModeTool(displays, recovery),
        ];
    }
}
