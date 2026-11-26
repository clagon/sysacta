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

    private int _permissionLoadGeneration;

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

    async partial void OnSelectedChanged(ClientItem? value) => await LoadPermissionsAsync(value);

    /// <summary>
    /// Only the most recent load may fill <see cref="Permissions"/>: selecting A, B, A quickly starts two loads for A,
    /// and letting both append would show duplicate switches with conflicting states.
    /// </summary>
    internal async Task LoadPermissionsAsync(ClientItem? value)
    {
        var generation = ++_permissionLoadGeneration;

        // Clear right away: switches of the previous client must never stay editable while another client is selected.
        Permissions.Clear();
        if (value is null)
        {
            return;
        }

        var (grants, enabled) = await Task.Run(() => (
            runtime.Access.GetClientGrants(value.Id),
            runtime.Registry.All.ToDictionary(t => t.Name, t => runtime.Access.IsToolEnabled(t.Name))));
        if (generation != _permissionLoadGeneration)
        {
            return; // superseded by a newer selection or reload
        }

        Permissions.Clear();
        foreach (var tool in runtime.Registry.All.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            // Items are kept per client and tool: a save still running for this switch keeps its value and its single
            // saver, instead of being replaced by a switch built from a (possibly stale) read.
            if (_permissionItems.TryGetValue((value.Id, tool.Name), out var item))
            {
                item.ApplyStoredValue(grants.Contains(tool.Name), enabled[tool.Name]);
            }
            else
            {
                item = new PermissionItem(this, value.Id, tool.Name, tool.Descriptor.Title, enabled[tool.Name], grants.Contains(tool.Name));
                _permissionItems[(value.Id, tool.Name)] = item;
            }

            Permissions.Add(item);
        }
    }

    private readonly Dictionary<(string ClientId, string ToolName), PermissionItem> _permissionItems = [];

    internal Task<bool> SetGrantAsync(string clientId, string toolName, bool allowed) => RunAsync(async () =>
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
        foreach (var key in _permissionItems.Keys.Where(k => k.ClientId == client.Id).ToList())
        {
            _permissionItems.Remove(key);
        }

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
    private const string DisabledNote = "サーバー全体で無効のため、許可しても利用できません";
    private readonly LatestValueSaver<bool> _saver;
    private bool _applyingStoredValue;

    public PermissionItem(ClientsViewModel owner, string clientId, string toolName, string title, bool globallyEnabled, bool allowed)
    {
        ToolName = toolName;
        Title = title;
        _note = globallyEnabled ? "" : DisabledNote;
        _isAllowed = allowed;
        _saver = new LatestValueSaver<bool>(() => IsAllowed, v => owner.SetGrantAsync(clientId, toolName, v), () => owner.LoadPermissionsAsync(owner.Selected));
    }

    public string ToolName { get; }
    public string Title { get; }

    [ObservableProperty]
    private string _note;

    [ObservableProperty]
    private bool _isAllowed;

    /// <summary>Shows the stored grant without saving it again; ignored while this switch's own save is running.</summary>
    internal void ApplyStoredValue(bool allowed, bool globallyEnabled)
    {
        Note = globallyEnabled ? "" : DisabledNote;
        if (_saver.IsSaving)
        {
            return;
        }

        _applyingStoredValue = true;
        IsAllowed = allowed;
        _applyingStoredValue = false;
    }

    partial void OnIsAllowedChanged(bool value)
    {
        if (!_applyingStoredValue)
        {
            _ = _saver.ChangedAsync();
        }
    }
}
