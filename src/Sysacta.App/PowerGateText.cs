using Sysacta.Core.Tools;

namespace Sysacta.App;

/// <summary>Shared wording of the power transition status and its local release (overview page and tray menu).</summary>
internal static class PowerGateText
{
    public const string MenuHeader = "電源操作の後の状態変更を再開";
    public const string ConfirmTitle = "状態変更を再開しますか?";
    public const string ConfirmMessage = "PC が停止・スリープしないことを確認してから解除してください。解除すると、MCP クライアントからの状態変更を再び受け付けます。Windows に送った停止要求は取り消されません。";
    public const string ConfirmAction = "解除";

    /// <summary>User-facing outcome of a release (never the tool's own message).</summary>
    public static (bool Ok, string Title, string Message) DescribeRelease(ToolResult result) => result switch
    {
        { IsSuccess: true } when (result.Data?["result"] as System.Text.Json.Nodes.JsonValue)?.TryGetValue<string>(out var r) == true && r == "not_held" => (true, "解除は不要です", "状態変更はすでに受け付けています。"),
        { IsSuccess: true } => (true, "状態変更を再開しました", ""),
        { Error.Code: ErrorCodes.Busy } => (false, "解除できませんでした", "電源操作の処理がまだ終わっていません。しばらくしてからやり直してください。"),
        _ => (false, "解除できませんでした", "操作をやり直してください。続く場合は監査ログを確認してください。"),
    };

    public static string Describe(PowerGateState state)
    {
        var name = state.Transition switch
        {
            PowerTransition.Lock => "ロック",
            PowerTransition.Sleep => "スリープ",
            PowerTransition.Shutdown => "シャットダウン",
            PowerTransition.Restart => "再起動",
            _ => "電源操作",
        };
        return state.Phase switch
        {
            PowerGatePhase.Running => $"{name}を実行しています。終わるまで、ほかの状態変更は受け付けません。",
            PowerGatePhase.Held when state.Transition == PowerTransition.Sleep =>
                "スリープを要求しました。Windows の復帰を検知するまで、状態変更は受け付けません。PC がスリープしなかった場合や、復帰後もこの表示が残る場合は解除できます。",
            PowerGatePhase.Held => $"{name}を要求しました。状態変更は受け付けていません。{name}が取り消されて PC が動き続ける場合は、解除すると状態変更を再開できます。",
            _ => "",
        };
    }
}
