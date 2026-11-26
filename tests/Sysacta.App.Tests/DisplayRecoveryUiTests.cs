using System.Text.Json.Nodes;
using Sysacta.App.ViewModels;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;

namespace Sysacta.App.Tests;

/// <summary>Phase 1C-2: the local recovery of a display mode change and the enabling of the cautious display tools.</summary>
public class DisplayRecoveryUiTests
{
    internal static readonly DisplayModeRecord Record = new(
        "display-b", "DELL P2314H", new DisplayMode(1920, 1080, 60, 32, false, 0, 0), new DisplayMode(1280, 720, 60, 32, false, 0, 0));

    [Fact]
    public void The_pending_restore_names_the_display_and_both_modes()
    {
        var recovery = new DisplayModeRecovery();
        Assert.Null(DisplayModeRestoreText.Pending(recovery));

        recovery.Replace(Record);

        Assert.Equal("DELL P2314H: 1280×720 60 Hz → 1920×1080 60 Hz", DisplayModeRestoreText.Pending(recovery));
    }

    [Theory]
    [InlineData(ToolStatus.Success, true, null, "表示モードを元に戻しました")]
    [InlineData(ToolStatus.Partial, false, "VERIFICATION_FAILED", "ほかのディスプレイも変わりました")]
    [InlineData(ToolStatus.Unknown, false, "TIMEOUT", "確認できませんでした")]
    [InlineData(ToolStatus.Error, false, "TARGET_CHANGED", "元に戻せませんでした")]
    [InlineData(ToolStatus.Error, false, "SOMETHING_NEW", "元に戻せませんでした")]
    public void Restore_results_are_shown_in_fixed_japanese_text(ToolStatus status, bool ok, string? code, string title)
    {
        const string ToolMessage = "SYNTHETIC tool message";
        var result = new ToolResult
        {
            Status = status,
            Data = new JsonObject { ["changed"] = true },
            Error = code is null ? null : new ToolError(code, ToolMessage, false),
        };

        var (shownOk, shownTitle, message) = DisplayModeRestoreText.Describe(result);

        Assert.Equal(ok, shownOk);
        Assert.Contains(title, shownTitle, StringComparison.Ordinal);
        Assert.DoesNotContain(ToolMessage, shownTitle + message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enable_all_never_enables_tools_that_are_off_by_default()
    {
        var tools = new ToolsViewModel(null!);
        var on = new ToolItem(tools, new DisplaySetBrightnessTool(null!, null!), enabled: true);
        var modes = new ToolItem(tools, new DisplaySetModeTool(null!, null!, null!), enabled: false);
        var power = new ToolItem(tools, new DisplaySetPowerTool(null!, null!), enabled: false);
        var input = new ToolItem(tools, new DisplaySetInputTool(null!, null!), enabled: false);
        var group = new ToolCategoryGroup("ディスプレイ", [on, modes, power, input]);

        group.EnableAllCommand.Execute(null);

        Assert.True(on.IsEnabled);
        Assert.All([modes, power, input], i => Assert.False(i.IsEnabled));
        Assert.All([modes, power, input], i => Assert.True(i.HasCaution));
        Assert.False(on.HasCaution);
    }
}
