using System.IO;
using Sysacta.App.ViewModels;
using Sysacta.Core.Tools.Audio;
using Sysacta.Core.Tools.Display;

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

    private static DisplayDeviceListData Displays(int unidentified = 0) => new(
        [new("display-0123456789", "Monitor", "active", DisplayKind.External, "hdmi", true, false, new(1920, 1080, 60000, 1001), ["main"],
            new(DisplaySupport.Unchecked, null), new(DisplaySupport.Unchecked, null), ["display_get_state"], null)],
        1 + unidentified,
        unidentified,
        false);

    [Fact]
    public void Displays_are_listed_with_primary_and_missing_aliases_are_kept()
    {
        var items = DeviceItem.BuildDisplays(Displays(), [new("main", "display-0123456789"), new("old", "display-gone")]);

        Assert.Equal("外部（hdmi） · 接続中 · プライマリ · …456789", items[0].Summary);
        Assert.StartsWith("1920×1080", items[0].Level, StringComparison.Ordinal);
        Assert.False(items[1].Found);
        Assert.Equal("見つかりません", items[1].State);
        Assert.Contains("取得できていません", DeviceItem.BuildDisplays(Displays(unidentified: 1), [new("old", "display-gone")])[1].State, StringComparison.Ordinal);
        Assert.Equal("状態を取得できません", DeviceItem.BuildDisplays(null, [new("old", "display-gone")])[0].State);
    }

    [Fact]
    public void Feature_text_never_presents_a_missing_value_as_zero()
    {
        Assert.Equal("輝度: 非対応（NOT_SUPPORTED）",
            DevicesViewModel.FeatureText(DisplayFeature.Brightness, new(DisplaySupport.NotSupported, null, null, null, "NOT_SUPPORTED")));
        Assert.Equal("コントラスト: 取得できません（OS_API_ERROR）",
            DevicesViewModel.FeatureText(DisplayFeature.Contrast, new(DisplaySupport.Unavailable, null, null, null, "OS_API_ERROR")));
    }

    [Fact]
    public void Switching_the_category_drops_the_rows_of_the_other_one()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        var vm = new DevicesViewModel(null!);
        vm.Show(List(), [new("desk", "spk")]);
        vm.Selected = vm.Devices[0];

        vm.Category = vm.Categories[1];

        Assert.Empty(vm.Devices);
        Assert.Null(vm.Selected);
        Assert.False(vm.AddAliasCommand.CanExecute(null));
    }

    /// <summary>Display aliases are stored under their own category and do not touch audio aliases.</summary>
    [Fact]
    public async Task Display_aliases_are_separate_from_audio_aliases()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        var directory = Directory.CreateTempSubdirectory("sysacta-displays-").FullName;
        var runtime = new SysactaRuntime(directory);
        try
        {
            runtime.Access.SetAlias("audio", "spk", "main", ["spk"]);
            var vm = new DevicesViewModel(runtime);
            vm.Category = vm.Categories[1];
            await vm.RefreshAsync();
            vm.ShowDisplays(Displays(), []);
            vm.Selected = vm.Devices[0];
            vm.AliasName = "default";
            await vm.AddAliasCommand.ExecuteAsync(null);
            Assert.Empty(runtime.Access.List("display"));

            vm.AliasName = "main";
            await vm.AddAliasCommand.ExecuteAsync(null);

            Assert.Equal("display-0123456789", runtime.Access.Resolve("display", "main"));
            Assert.Equal("spk", runtime.Access.Resolve("audio", "main"));
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
