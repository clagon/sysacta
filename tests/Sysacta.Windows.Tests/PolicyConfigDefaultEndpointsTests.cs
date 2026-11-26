using Sysacta.Core.Tools.Audio;
using Sysacta.Windows.Audio;
using Sysacta.Windows.Experimental.Audio;

namespace Sysacta.Windows.Tests;

[Collection("real audio device")]
public class PolicyConfigDefaultEndpointsTests
{
    private static Dictionary<(AudioDirection, AudioRole), string?> ReadAll(CoreAudioEndpoints audio)
    {
        var map = new Dictionary<(AudioDirection, AudioRole), string?>(6);
        foreach (var d in Enum.GetValues<AudioDirection>())
        {
            foreach (var r in Enum.GetValues<AudioRole>())
            {
                map[(d, r)] = audio.GetDefaultEndpointId(d, r);
            }
        }

        return map;
    }

    [Fact]
    public void Availability_and_open_dispose_without_writes_leave_defaults_unchanged()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");

        var audio = new CoreAudioEndpoints();
        var writer = new PolicyConfigDefaultEndpoints();

        var availability = writer.GetAvailability();
        TestContext.Current.TestOutputHelper?.WriteLine($"Availability: {availability.IsAvailable} ({availability.ErrorCode}) {availability.Reason}");

        var before = ReadAll(audio);
        if (availability.IsAvailable)
        {
            using var change = writer.Open();
        }

        var after = ReadAll(audio);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Set_default_changes_and_restores_when_explicitly_approved()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("SYSACTA_HARDWARE_TESTS") == "1",
            "Set SYSACTA_HARDWARE_TESTS=1 and SYSACTA_DEFAULT_DEVICE_TEST_TARGET to run.");
        var targetId = Environment.GetEnvironmentVariable("SYSACTA_DEFAULT_DEVICE_TEST_TARGET");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(targetId), "SYSACTA_DEFAULT_DEVICE_TEST_TARGET is not set.");

        var audio = new CoreAudioEndpoints();
        var writer = new PolicyConfigDefaultEndpoints();
        Assert.SkipUnless(writer.GetAvailability().IsAvailable, "IPolicyConfig is not available.");

        var target = audio.GetEndpoint(targetId!);
        Assert.SkipWhen(target.State != AudioDeviceState.Active, "Target device is not active.");

        var dir = target.Direction;
        var originals = Enum.GetValues<AudioRole>().ToDictionary(r => r, r => audio.GetDefaultEndpointId(dir, r));
        foreach (var (role, origId) in originals)
        {
            Assert.SkipWhen(origId is null || audio.GetEndpoint(origId).State != AudioDeviceState.Active, $"Original default for {role} cannot be restored.");
        }

        try
        {
            using var change = writer.Open();
            foreach (var role in Enum.GetValues<AudioRole>())
            {
                change.SetDefault(targetId!, role);
                Assert.Equal(targetId, audio.GetDefaultEndpointId(dir, role));
            }
        }
        finally
        {
            using var restore = writer.Open();
            foreach (var (role, origId) in originals)
            {
                if (origId is not null)
                {
                    restore.SetDefault(origId, role);
                }
            }
        }

        foreach (var (role, origId) in originals)
        {
            Assert.Equal(origId, audio.GetDefaultEndpointId(dir, role));
        }
    }
}
