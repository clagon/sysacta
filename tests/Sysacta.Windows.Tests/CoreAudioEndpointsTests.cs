using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Windows.Audio;

namespace Sysacta.Windows.Tests;

/// <summary>
/// Runs against the real Windows Core Audio API. Skipped on non-Windows hosts and on machines without a default
/// output device (e.g. most CI runners). Tests that change the volume additionally require
/// SYSACTA_HARDWARE_TESTS=1 and restore the original value. See docs/manual-tests.md.
/// </summary>
public class CoreAudioEndpointsTests
{
    private static string RequireDefaultOutput(CoreAudioEndpoints audio)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        var id = audio.GetDefaultEndpointId(AudioDirection.Output);
        Assert.SkipWhen(id is null, "No default output device on this machine.");
        return id!;
    }

    [Fact]
    public void Default_output_device_can_be_read()
    {
        var audio = new CoreAudioEndpoints();
        var id = RequireDefaultOutput(audio);

        var state = audio.GetEndpoint(id);

        Assert.Equal(id, state.Id);
        Assert.Equal(AudioDirection.Output, state.Direction);
        Assert.Equal(AudioDeviceState.Active, state.State);
        Assert.InRange(state.VolumeScalar!.Value, 0f, 1f);
        Assert.NotNull(state.IsMuted);
        Assert.False(string.IsNullOrWhiteSpace(state.Name));
    }

    [Fact]
    public void Unknown_device_id_is_target_not_found()
    {
        var audio = new CoreAudioEndpoints();
        RequireDefaultOutput(audio);

        var ex = Assert.Throws<ToolFailureException>(() => audio.GetEndpoint("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}"));
        Assert.Equal(ErrorCodes.TargetNotFound, ex.Code);
    }

    [Fact]
    public void Set_volume_changes_and_restores_the_real_device()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SYSACTA_HARDWARE_TESTS") == "1", "Set SYSACTA_HARDWARE_TESTS=1 to change the real volume.");
        var audio = new CoreAudioEndpoints();
        var id = RequireDefaultOutput(audio);
        var original = audio.GetEndpoint(id).VolumeScalar!.Value;
        var target = original >= 0.5f ? original - 0.02f : original + 0.02f;
        try
        {
            audio.SetVolumeScalar(id, target);
            Assert.Equal(AudioTarget.ToLevel(target), AudioTarget.ToLevel(audio.GetEndpoint(id).VolumeScalar!.Value));
        }
        finally
        {
            audio.SetVolumeScalar(id, original);
        }

        Assert.Equal(AudioTarget.ToLevel(original), AudioTarget.ToLevel(audio.GetEndpoint(id).VolumeScalar!.Value));
    }
}
