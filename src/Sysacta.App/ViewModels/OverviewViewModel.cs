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
        PowerGateText = Sysacta.App.PowerGateText.Describe(state);
    }

    [RelayCommand]
    private async Task ReleasePowerGateAsync()
    {
        // The confirmation applies to the transition shown now; a later one is refused by the tool (TARGET_CHANGED).
        var confirmed = runtime.PowerGate.State;
        if (confirmed.Phase != PowerGatePhase.Held || !await ConfirmAsync(Sysacta.App.PowerGateText.ConfirmTitle, Sysacta.App.PowerGateText.ConfirmMessage, Sysacta.App.PowerGateText.ConfirmAction))
        {
            return;
        }

        await RunAsync(async () =>
        {
            ClearMessage();
            var (ok, title, message) = Sysacta.App.PowerGateText.DescribeRelease(await runtime.ReleasePowerGateAsync(confirmed.Generation));
            Notify(ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning, title, message);

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
