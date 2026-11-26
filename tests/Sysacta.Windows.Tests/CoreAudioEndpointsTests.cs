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

/// <summary>
/// Phase 1B adapters against the real Core Audio API. Only reads run by default; writes need
/// SYSACTA_HARDWARE_TESTS=1, name their target in the test output and restore the original value.
/// </summary>
public class CoreAudioPhase1BTests
{
    private static string RequireDefaultOutput(CoreAudioEndpoints audio)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        var id = audio.GetDefaultEndpointId(AudioDirection.Output);
        Assert.SkipWhen(id is null, "No default output device on this machine.");
        return id!;
    }

    private static void RequireHardwareOptIn() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SYSACTA_HARDWARE_TESTS") == "1", "Set SYSACTA_HARDWARE_TESTS=1 to change real audio state.");

    [Fact]
    public void Endpoints_and_default_roles_can_be_enumerated()
    {
        // Runs on CI runners too: a machine without audio devices must give an empty list, not an error.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        var audio = new CoreAudioEndpoints();

        var list = audio.ListEndpoints(null, 512);

        Assert.True(list.Total >= list.Items.Count);
        TestContext.Current.TestOutputHelper?.WriteLine($"{list.Items.Count} endpoints, {list.Failed} failed");
        Assert.All(list.Items, e => Assert.False(string.IsNullOrEmpty(e.State.Id)));
        Assert.All(list.Items.Where(e => e.State.State != AudioDeviceState.Active), e => Assert.Null(e.State.VolumeScalar));
        foreach (var role in Enum.GetValues<AudioRole>())
        {
            foreach (var direction in Enum.GetValues<AudioDirection>())
            {
                if (audio.GetDefaultEndpointId(direction, role) is { } id)
                {
                    Assert.Contains(list.Items, e => e.State.Id == id && e.State.Direction == direction);
                }
            }
        }

        Assert.All(audio.ListEndpoints(AudioDirection.Input, 512).Items, e => Assert.Equal(AudioDirection.Input, e.State.Direction));
    }

    [Fact]
    public void Sessions_of_the_default_output_can_be_listed_and_opened()
    {
        var id = RequireDefaultOutput(new CoreAudioEndpoints());
        var sessions = new CoreAudioSessions();

        var list = sessions.List(id, 512);

        TestContext.Current.TestOutputHelper?.WriteLine($"{list.Items.Count} sessions, {list.Failed} failed");
        Assert.All(list.Items, s => Assert.False(string.IsNullOrEmpty(s.InstanceId)));
        Assert.All(list.Items, s => Assert.DoesNotContain('\\', s.Name ?? ""));
        Assert.Null(sessions.Open(id, "no such session"));
        var ids = list.Items.Where(s => s.Status != Core.Tools.Audio.AudioSessionStatus.Expired).Select(s => AudioSessions.SessionId(id, s.InstanceId)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count()); // live sessions are distinguishable
        var live = list.Items.FirstOrDefault(s => s.Status != Core.Tools.Audio.AudioSessionStatus.Expired);
        Assert.SkipWhen(live is null, "No audio session on the default output.");
        using var handle = sessions.Open(id, live!.InstanceId);
        Assert.Equal(live.InstanceId, handle!.Read().InstanceId);
    }

    [Theory]
    [InlineData("@%SystemRoot%\\System32\\AudioSrv.Dll,-202", null)]
    [InlineData("C:\\Users\\someone\\a.exe", null)]
    [InlineData("a/b", null)]
    [InlineData("  ", null)]
    [InlineData(" Player\r\n ", "Player")]
    public void Display_names_never_carry_paths_or_control_characters(string name, string? expected) =>
        Assert.Equal(expected, CoreAudioSessions.SafeName(name));

    [Fact]
    public void Set_mute_changes_and_restores_the_real_device()
    {
        RequireHardwareOptIn();
        var audio = new CoreAudioEndpoints();
        var id = RequireDefaultOutput(audio);
        var original = audio.GetEndpoint(id);
        TestContext.Current.TestOutputHelper?.WriteLine($"Target: {original.Name}");
        try
        {
            audio.SetMute(id, !original.IsMuted!.Value);
            Assert.Equal(!original.IsMuted.Value, audio.GetEndpoint(id).IsMuted);
            Assert.Equal(original.VolumeScalar!.Value, audio.GetEndpoint(id).VolumeScalar!.Value, 0.001f);
        }
        finally
        {
            audio.SetMute(id, original.IsMuted!.Value);
        }

        Assert.Equal(original.IsMuted, audio.GetEndpoint(id).IsMuted);
    }

    /// <summary>
    /// Changes only the system sounds session of the default output (no application's audio, no recording, no network),
    /// and restores it.
    /// </summary>
    [Fact]
    public void System_sounds_session_volume_and_mute_change_and_restore()
    {
        RequireHardwareOptIn();
        var id = RequireDefaultOutput(new CoreAudioEndpoints());
        var sessions = new CoreAudioSessions();
        var system = sessions.List(id, 512).Items.FirstOrDefault(s => s.IsSystemSounds && s.VolumeScalar is not null && s.IsMuted is not null);
        Assert.SkipWhen(system is null, "No system sounds session on the default output.");

        using var handle = sessions.Open(id, system!.InstanceId)!;
        var original = handle.Read();
        var target = original.VolumeScalar >= 0.5f ? original.VolumeScalar!.Value - 0.02f : original.VolumeScalar!.Value + 0.02f;
        try
        {
            handle.SetVolumeScalar(target);
            handle.SetMute(!original.IsMuted!.Value);
            Assert.Equal(AudioTarget.ToLevel(target), AudioTarget.ToLevel(handle.Read().VolumeScalar!.Value));
            Assert.Equal(!original.IsMuted.Value, handle.Read().IsMuted);
        }
        finally
        {
            handle.SetVolumeScalar(original.VolumeScalar!.Value);
            handle.SetMute(original.IsMuted!.Value);
        }

        Assert.Equal(AudioTarget.ToLevel(original.VolumeScalar!.Value), AudioTarget.ToLevel(handle.Read().VolumeScalar!.Value));
    }
}
