using System.Globalization;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;

namespace Sysacta.App;

/// <summary>Japanese texts for restoring a display mode (tray menu and device page). Never shows tool messages.</summary>
internal static class DisplayModeRestoreText
{
    public const string MenuHeader = "直前の表示モード変更を元に戻す";

    /// <summary>What a restore would do now, or null when there is nothing to restore.</summary>
    public static string? Pending(DisplayModeRecovery recovery) =>
        recovery.Latest is { } r ? $"{r.Name}: {Mode(r.Applied)} → {Mode(r.Original)}" : null;

    public static (bool Ok, string Title, string Message) Describe(ToolResult result) => result.Status switch
    {
        ToolStatus.Success when result.Data?["changed"]?.GetValue<bool>() == false => (true, "表示モードはすでに元の状態です", ""),
        ToolStatus.Success => (true, "表示モードを元に戻しました", ""),
        ToolStatus.Partial => (false, "表示モードを元に戻しましたが、ほかのディスプレイも変わりました", "Windows の設定でディスプレイの配置を確認してください。"),
        ToolStatus.Unknown => (false, "元に戻せたか確認できませんでした", "Windows の設定で表示モードを確認してください。"),
        _ => (false, "表示モードを元に戻せませんでした", result.Error?.Code switch
        {
            ErrorCodes.TargetNotFound => "元に戻せる変更はありません。",
            ErrorCodes.DeviceDisconnected => "対象のディスプレイが接続されていません。",
            ErrorCodes.TargetChanged => "変更後に表示モードが変わったため、元に戻しませんでした。",
            ErrorCodes.NotSupported => "Windows が元の表示モードを受け付けませんでした。",
            ErrorCodes.Busy or ErrorCodes.Timeout => "ほかの操作が実行中のため完了できませんでした。もう一度お試しください。",
            { } code => $"エラーコード: {code}",
            null => "",
        }),
    };

    private static string Mode(DisplayMode m) => string.Create(CultureInfo.InvariantCulture, $"{m.Width}×{m.Height} {m.RefreshHz} Hz");
}
