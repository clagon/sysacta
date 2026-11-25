using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysacta.Core.Security;
using Sysacta.Infrastructure;
using Wpf.Ui.Controls;

namespace Sysacta.App.ViewModels;

public sealed partial class ClientsViewModel(SysactaRuntime runtime) : PageViewModel
{
    public static IReadOnlyList<string> ExpiryOptions { get; } = ["無期限", "30日", "90日", "365日"];
    public static IReadOnlyList<string> PresetOptions { get; } = ["現在有効なすべてのツール", "読み取り専用ツールのみ", "なし(個別に許可)"];

    public ObservableCollection<ClientItem> Clients { get; } = [];

    public ObservableCollection<PermissionItem> Permissions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(ReissueCommand), nameof(RevokeCommand), nameof(DeleteCommand))]
    private ClientItem? _selected;

    [ObservableProperty]
    private bool _hasClients;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IssueCommand))]
    private string _newName = "";

    [ObservableProperty]
    private int _expiryIndex;

    [ObservableProperty]
    private int _presetIndex;

    /// <summary>Token text, held only until the user dismisses it. Never persisted or logged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssuedToken))]
    private string? _issuedToken;

    [ObservableProperty]
    private string _issuedFor = "";

    public bool HasIssuedToken => IssuedToken is not null;

    public bool HasSelection => Selected is not null;

    public override async Task RefreshAsync()
    {
        var selectedId = Selected?.Id;
        var clients = await Task.Run(runtime.Access.ListClients);
        Clients.Clear();
        foreach (var c in clients)
        {
            Clients.Add(new ClientItem(c));
        }

        HasClients = Clients.Count > 0;
        Selected = Clients.FirstOrDefault(c => c.Id == selectedId);
    }

    async partial void OnSelectedChanged(ClientItem? value)
    {
        Permissions.Clear();
        if (value is null)
        {
            return;
        }

        var grants = await Task.Run(() => runtime.Access.GetClientGrants(value.Id));
        var enabled = await Task.Run(() => runtime.Registry.All.ToDictionary(t => t.Name, t => runtime.Access.IsToolEnabled(t.Name)));
        if (Selected?.Id != value.Id)
        {
            return;
        }

        foreach (var tool in runtime.Registry.All.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            Permissions.Add(new PermissionItem(this, value.Id, tool.Name, tool.Descriptor.Title, enabled[tool.Name], grants.Contains(tool.Name)));
        }
    }

    internal Task SetGrantAsync(string clientId, string toolName, bool allowed) => RunAsync(async () =>
    {
        await Task.Run(() => runtime.Access.SetClientGrant(clientId, toolName, allowed));
        Notify(InfoBarSeverity.Success, allowed ? $"{toolName} を許可しました" : $"{toolName} の許可を取り消しました", "次のリクエストから即時に反映されます。");
    });

    private bool CanIssue() => !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanIssue))]
    private Task IssueAsync() => RunAsync(async () =>
    {
        ClearMessage();
        var name = NewName.Trim();
        DateTimeOffset? expires = ExpiryIndex switch
        {
            1 => DateTimeOffset.UtcNow.AddDays(30),
            2 => DateTimeOffset.UtcNow.AddDays(90),
            3 => DateTimeOffset.UtcNow.AddDays(365),
            _ => null,
        };
        var preset = PresetIndex switch
        {
            1 => PermissionPreset.ReadOnly,
            2 => PermissionPreset.None,
            _ => PermissionPreset.AllEnabled,
        };

        var issued = await Task.Run(() =>
        {
            var grants = PermissionPresets.InitialGrants(preset, runtime.Registry.All, runtime.Access.IsToolEnabled);
            return runtime.Access.IssueClient(name, expires, grants);
        });

        ShowToken(issued);
        NewName = "";
        await RefreshAsync();
        Selected = Clients.FirstOrDefault(c => c.Id == issued.Client.Id);
    }, "クライアントを発行できませんでした");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ReissueAsync() => RunAsync(async () =>
    {
        if (Selected is not { } client || !await ConfirmAsync("トークンを再発行しますか?", $"「{client.Name}」の現在のトークンは直ちに使えなくなります。権限は維持されます。", "再発行"))
        {
            return;
        }

        ShowToken(await Task.Run(() => runtime.Access.ReissueClient(client.Id)));
        await RefreshAsync();
    }, "再発行できませんでした");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RevokeAsync() => RunAsync(async () =>
    {
        if (Selected is not { } client || !await ConfirmAsync("クライアントを失効させますか?", $"「{client.Name}」からのリクエストは直ちに拒否されます。元に戻せません。", "失効"))
        {
            return;
        }

        await Task.Run(() => runtime.Access.RevokeClient(client.Id));
        Notify(InfoBarSeverity.Success, $"「{client.Name}」を失効させました");
        await RefreshAsync();
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (Selected is not { } client || !await ConfirmAsync("クライアントを削除しますか?", $"「{client.Name}」と権限設定を削除します。監査ログは残ります。", "削除"))
        {
            return;
        }

        await Task.Run(() => runtime.Access.DeleteClient(client.Id));
        Selected = null;
        Notify(InfoBarSeverity.Success, $"「{client.Name}」を削除しました");
        await RefreshAsync();
    });

    [RelayCommand]
    private void CopyToken()
    {
        if (IssuedToken is { } token && ClipboardHelper.TrySetText(token))
        {
            Notify(InfoBarSeverity.Success, "トークンをコピーしました");
        }
    }

    [RelayCommand]
    private void CopyConfig()
    {
        if (IssuedToken is { } token && ClipboardHelper.TrySetText(ConnectionViewModel.ConfigFor(ConnectionViewModel.PreferredUrl(runtime), token)))
        {
            Notify(InfoBarSeverity.Success, "接続設定をコピーしました", "クライアントの設定ファイルに貼り付けてください。");
        }
    }

    [RelayCommand]
    private void DismissToken()
    {
        IssuedToken = null;
        IssuedFor = "";
    }

    private void ShowToken(IssuedClient issued)
    {
        IssuedFor = issued.Client.Name;
        IssuedToken = issued.Token;
    }

    private static async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = action,
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "キャンセル",
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}

public sealed record ClientItem(ClientRecord Record)
{
    public string Id => Record.Id;
    public string Name => Record.Name;

    public string Status => Record.IsRevoked ? "失効" : Record.IsExpired(DateTimeOffset.UtcNow) ? "期限切れ" : "有効";

    public bool IsActive => Record.IsActive(DateTimeOffset.UtcNow);

    public string Created => Format(Record.CreatedAt);

    public string Expires => Record.ExpiresAt is { } e ? Format(e) : "無期限";

    public string LastUsed => Record.LastUsedAt is { } u ? Format(u) : "未使用";

    private static string Format(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}

public sealed partial class PermissionItem : ObservableObject
{
    private readonly ClientsViewModel _owner;
    private readonly string _clientId;

    public PermissionItem(ClientsViewModel owner, string clientId, string toolName, string title, bool globallyEnabled, bool allowed)
    {
        _owner = owner;
        _clientId = clientId;
        ToolName = toolName;
        Title = title;
        Note = globallyEnabled ? "" : "サーバー全体で無効のため、許可しても利用できません";
        _isAllowed = allowed;
    }

    public string ToolName { get; }
    public string Title { get; }
    public string Note { get; }

    [ObservableProperty]
    private bool _isAllowed;

    partial void OnIsAllowedChanged(bool value) => _ = _owner.SetGrantAsync(_clientId, ToolName, value);
}
