using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysacta.App.Platform;
using Sysacta.Core.Tools;
using Sysacta.Infrastructure;
using Sysacta.Mcp;
using Wpf.Ui.Controls;

namespace Sysacta.App.ViewModels;

public sealed partial class OverviewViewModel(SysactaRuntime runtime) : PageViewModel
{
    [ObservableProperty]
    private string _serverStatus = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _endpointUrl = "";

    [ObservableProperty]
    private string _toolSummary = "";

    [ObservableProperty]
    private string _clientSummary = "";

    [ObservableProperty]
    private string _activity = "";

    [ObservableProperty]
    private bool _hasRecentEvents;

    [ObservableProperty]
    private bool _isPowerGateActive;

    [ObservableProperty]
    private bool _canReleasePowerGate;

    [ObservableProperty]
    private string _powerGateText = "";

    public string PrivilegeText { get; } = Privilege.IsElevated ? "管理者権限(非推奨)" : "標準ユーザー権限";

    public ObservableCollection<AuditRow> RecentEvents { get; } = [];

    public override async Task RefreshAsync()
    {
        var host = runtime.Host;
        IsRunning = host.State == ServerState.Running;
        ServerStatus = host.State switch
        {
            ServerState.Running => "実行中",
            ServerState.Starting => "起動中…",
            ServerState.Stopping => "停止中…",
            ServerState.Faulted => "エラーで停止",
            _ => "停止",
        };
        EndpointUrl = runtime.LocalEndpointUrl;
        ApplyPowerGate(runtime.PowerGate.State);
        Activity = $"処理中のHTTPリクエスト {host.ActiveRequests} 件 / 実行・待機中のツール呼び出し {runtime.Executor.InFlight} 件";
        if (host.State == ServerState.Faulted && host.LastError is { } error)
        {
            Notify(InfoBarSeverity.Error, "MCPサーバーが停止しています", error);
        }

        var (enabled, total, active, clients, recent) = await Task.Run(() =>
        {
            var tools = runtime.Registry.All;
            var enabledCount = tools.Count(t => runtime.Access.IsToolEnabled(t.Name) && t.GetAvailability().IsAvailable);
            var all = runtime.Access.ListClients();
            var now = DateTimeOffset.UtcNow;
            return (enabledCount, tools.Count, all.Count(c => c.IsActive(now)), all.Count, runtime.Audit.Query(new AuditQuery(Limit: 8)));
        });

        ToolSummary = $"{enabled} / {total} ツールが公開可能";
        ClientSummary = clients == 0 ? "クライアント未登録" : $"有効 {active} / 登録 {clients}";
        RecentEvents.Clear();
        foreach (var e in recent)
        {
            RecentEvents.Add(new AuditRow(e));
        }

        HasRecentEvents = RecentEvents.Count > 0;
    }

    [RelayCommand]
    private Task StartServerAsync() => RunAsync(async () =>
    {
        ClearMessage();
        if (await runtime.StartServerAsync() is { } error)
        {
            Notify(InfoBarSeverity.Error, "MCPサーバーを起動できませんでした", error);
        }

        await RefreshAsync();
    });

    [RelayCommand]
    private Task StopServerAsync() => RunAsync(async () =>
    {
        ClearMessage();
        await runtime.StopServerAsync();
        await RefreshAsync();
    });

    private void ApplyPowerGate(PowerGateState state)
    {
        IsPowerGateActive = state.Phase != PowerGatePhase.Idle;
        CanReleasePowerGate = state.Phase == PowerGatePhase.Held;
        var name = state.Transition switch
        {
            PowerTransition.Lock => "ロック",
            PowerTransition.Sleep => "スリープ",
            PowerTransition.Shutdown => "シャットダウン",
            PowerTransition.Restart => "再起動",
            _ => "電源操作",
        };
        PowerGateText = state.Phase switch
        {
            PowerGatePhase.Running => $"{name}を実行しています。終わるまで、ほかの状態変更は受け付けません。",
            PowerGatePhase.Held when state.Transition == PowerTransition.Sleep =>
                "スリープを要求しました。Windows の復帰を検知するまで、状態変更は受け付けません。PC がスリープしなかった場合や、復帰後もこの表示が残る場合は解除できます。",
            PowerGatePhase.Held => $"{name}を要求しました。状態変更は受け付けていません。{name}が取り消されて PC が動き続ける場合は、解除すると状態変更を再開できます。",
            _ => "",
        };
    }

    [RelayCommand]
    private async Task ReleasePowerGateAsync()
    {
        if (!await ConfirmAsync("状態変更を再開しますか?", "PC が停止・スリープしないことを確認してから解除してください。解除すると、MCP クライアントからの状態変更を再び受け付けます。Windows に送った停止要求は取り消されません。", "解除"))
        {
            return;
        }

        await RunAsync(async () =>
        {
            ClearMessage();
            var result = await runtime.ReleasePowerGateAsync();
            if (result.IsSuccess)
            {
                Notify(InfoBarSeverity.Success, "状態変更を再開しました");
            }
            else
            {
                Notify(InfoBarSeverity.Warning, "解除できませんでした", "電源操作の処理がまだ終わっていません。しばらくしてからやり直してください。");
            }

            ApplyPowerGate(runtime.PowerGate.State);
        });
    }

    [RelayCommand]
    private void CopyEndpoint()
    {
        if (ClipboardHelper.TrySetText(EndpointUrl))
        {
            Notify(InfoBarSeverity.Success, "接続URLをコピーしました");
        }
    }
}
