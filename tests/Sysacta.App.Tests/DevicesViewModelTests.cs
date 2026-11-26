using System.IO;
using Sysacta.App.ViewModels;
using Sysacta.Core.Tools.Audio;

namespace Sysacta.App.Tests;

public class DevicesViewModelTests
{
    private static AudioDeviceListData List(int failed = 0) => new(
        [new("spk", "Speakers", AudioDirection.Output, AudioDeviceState.Active, true, [AudioRole.Multimedia], 40, true, ["desk"], ["audio_get_state"], null)],
        1 + failed,
        failed,
        false);

    [Fact]
    public void Aliases_of_missing_devices_are_shown_and_never_rebound_automatically()
    {
        var items = DeviceItem.Build(List(), [new("desk", "spk"), new("headset", "gone"), new("headset2", "GONE")]);

        Assert.Equal(2, items.Count);
        Assert.True(items[0].Found);
        var missing = items[1];
        Assert.False(missing.Found);
        Assert.Equal("gone", missing.Id);
        Assert.Equal(["headset", "headset2"], missing.Aliases);
        Assert.Equal("見つかりません", missing.State);
    }

    [Fact]
    public void An_incomplete_or_failed_listing_does_not_claim_a_device_is_gone()
    {
        Assert.Contains("取得できていません", DeviceItem.Build(List(failed: 1), [new("a", "gone")])[1].State, StringComparison.Ordinal);
        Assert.Equal("状態を取得できません", DeviceItem.Build(null, [new("a", "gone")])[0].State);
    }

    [Fact]
    public void Commands_require_an_explicit_selection()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows."); // message severity is a WPF UI type
        var vm = new DevicesViewModel(null!);
        vm.Show(List(), [new("desk", "spk"), new("old", "gone")]);

        Assert.False(vm.AddAliasCommand.CanExecute(null));
        vm.Selected = vm.Devices[1]; // the missing device
        vm.AliasName = "new";
        Assert.False(vm.AddAliasCommand.CanExecute(null));
        Assert.False(vm.RebindAliasCommand.CanExecute(null));
        vm.SelectedAlias = "old";
        Assert.False(vm.RebindAliasCommand.CanExecute(null)); // no target chosen yet
        Assert.Equal(["spk"], vm.RebindTargets.Select(d => d.Id));
        vm.RebindTarget = vm.RebindTargets[0];
        Assert.True(vm.RebindAliasCommand.CanExecute(null));

        vm.Selected = vm.Devices[0];
        Assert.Null(vm.SelectedAlias);
        Assert.Null(vm.RebindTarget);
    }

    /// <summary>The page's alias operation against the real store (temporary directory). The server is not started.</summary>
    [Fact]
    public async Task Adding_an_alias_persists_it_and_reports_invalid_input()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        var directory = Directory.CreateTempSubdirectory("sysacta-devices-").FullName;
        var runtime = new SysactaRuntime(directory);
        try
        {
            var vm = new DevicesViewModel(runtime);
            vm.Show(List(), []);
            vm.Selected = vm.Devices[0];
            vm.AliasName = "all";
            await vm.AddAliasCommand.ExecuteAsync(null);
            Assert.Empty(runtime.Access.List("audio"));
            Assert.Contains("予約語", vm.Message, StringComparison.Ordinal);

            vm.AliasName = "page-test";
            await vm.AddAliasCommand.ExecuteAsync(null);

            Assert.Equal("spk", runtime.Access.Resolve("audio", "page-test"));
            // "spk" does not exist on this machine, so the reloaded page lists it as a registered, missing device.
            Assert.Contains(vm.Devices, d => d.Id == "spk" && !d.Found && d.Aliases.Contains("page-test"));
        }
        finally
        {
            await runtime.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
