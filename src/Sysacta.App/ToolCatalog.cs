using Sysacta.Core.Devices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Windows.Audio;

namespace Sysacta.App;

/// <summary>
/// The production tool list. Register every new tool here (and only here). Test-only tools live in test projects
/// and can never be published by this build. See docs/tools.md.
/// </summary>
internal static class ToolCatalog
{
    public static IReadOnlyList<SysactaTool> Create(IDeviceAliases aliases, IAudioEndpoints audio)
    {
        var sessions = new CoreAudioSessions();
        return
        [
            new AudioGetStateTool(audio, aliases),
            new AudioListDevicesTool(audio, aliases),
            new AudioSetVolumeTool(audio, aliases),
            new AudioSetMuteTool(audio, aliases),
            new AudioListSessionsTool(audio, aliases, sessions),
            new AudioSetAppVolumeTool(audio, sessions),
            new AudioSetAppMuteTool(audio, sessions),
        ];
    }
}
