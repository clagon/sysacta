using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysacta.Core.Devices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Wpf.Ui.Controls;

namespace Sysacta.App.ViewModels;

/// <summary>
/// Audio devices and their aliases. The list comes from <see cref="AudioDeviceCatalog"/>, the same code as
/// audio_list_devices. Aliases are changed only here (never through MCP); nothing is re-bound automatically.
/// </summary>
public sealed partial class DevicesViewModel(SysactaRuntime runtime) : PageViewModel
{
    private const int MaxDevices = 512;
    private int _loadGeneration;

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    [ObservableProperty]
    private bool _hasDevices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(RebindTargets))]
    [NotifyCanExecuteChangedFor(nameof(AddAliasCommand), nameof(RenameAliasCommand), nameof(RemoveAliasCommand), nameof(RebindAliasCommand))]
    private DeviceItem? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameAliasCommand), nameof(RemoveAliasCommand), nameof(RebindAliasCommand))]
    private string? _selectedAlias;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddAliasCommand), nameof(RenameAliasCommand))]
    private string _aliasName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebindAliasCommand))]
    private DeviceItem? _rebindTarget;

    public bool HasSelection => Selected is not null;

    /// <summary>Connected or known endpoints an alias can be moved to, chosen by the user.</summary>
    public IReadOnlyList<DeviceItem> RebindTargets => [.. Devices.Where(d => d.Found && d.Id != Selected?.Id)];

    public override Task RefreshAsync()
    {
        ClearMessage();
        return RunAsync(LoadAsync, "デバイスを読み込めませんでした");
    }

    private async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        var (list, aliases) = await Task.Run(() =>
        {
            var stored = runtime.Access.List(ToolCategories.Audio);
            try
            {
                return (AudioDeviceCatalog.List(runtime.AudioEndpoints, runtime.Access, null, MaxDevices), stored);
            }
            catch (ToolFailureException)
            {
                return ((AudioDeviceListData?)null, stored); // registered aliases stay manageable
            }
        });
        if (generation == _loadGeneration)
        {
            Show(list, aliases);
        }
    }

    /// <param name="list">Null when the enumeration failed.</param>
    internal void Show(AudioDeviceListData? list, IReadOnlyList<DeviceAlias> aliases)
    {
        var selectedId = Selected?.Id;
        var selectedAlias = SelectedAlias;
        var items = DeviceItem.Build(list, aliases);
        Devices.Clear();
        foreach (var item in items)
        {
            Devices.Add(item);
        }

        HasDevices = Devices.Count > 0;
        Selected = Devices.FirstOrDefault(d => d.Id == selectedId);
        SelectedAlias = Selected?.Aliases.FirstOrDefault(a => a == selectedAlias);
        OnPropertyChanged(nameof(RebindTargets));

        if (list is null)
        {
            Notify(InfoBarSeverity.Error, "オーディオデバイスを取得できませんでした", "登録済みの別名のみを表示しています。Windows Audio サービスの状態を確認して再読み込みしてください。");
        }
        else if (list.FailedCount > 0 || list.Truncated || list.Devices.Any(d => d.ErrorCode is not null))
        {
            Notify(InfoBarSeverity.Warning, "一部の情報を取得できませんでした", $"取得できなかった機器 {list.FailedCount} 件 / 検出 {list.TotalCount} 件{(list.Truncated ? "（表示件数の上限を超えています）" : "")}");
        }
    }

    partial void OnSelectedChanged(DeviceItem? value)
    {
        SelectedAlias = null;
        AliasName = "";
        RebindTarget = null;
    }

    partial void OnSelectedAliasChanged(string? value)
    {
        if (value is not null)
        {
            AliasName = value;
        }
    }

    private string[] KnownIds() => [.. Devices.Select(d => d.Id)];

    private bool CanAdd() => Selected is { Found: true } && !string.IsNullOrWhiteSpace(AliasName);

    private bool CanRename() => SelectedAlias is not null && !string.IsNullOrWhiteSpace(AliasName) && AliasName.Trim() != SelectedAlias;

    private bool CanRemove() => SelectedAlias is not null;

    private bool CanRebind() => SelectedAlias is not null && RebindTarget is not null;

    [RelayCommand]
    private Task ReloadAsync() => RefreshAsync();

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAliasAsync()
    {
        if (Selected is not { } device)
        {
            return;
        }

        var alias = AliasName.Trim();
        var known = KnownIds();
        await ChangeAsync(() => runtime.Access.SetAlias(ToolCategories.Audio, device.Id, alias, known), $"別名「{alias}」を登録しました");
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAliasAsync()
    {
        if (SelectedAlias is not { } old)
        {
            return;
        }

        var alias = AliasName.Trim();
        var known = KnownIds();
        if (await ConfirmAsync("別名を変更しますか?", $"「{old}」を指定している呼び出しは対象が見つからなくなります。新しい別名は「{alias}」です。", "変更"))
        {
            await ChangeAsync(() => runtime.Access.RenameAlias(ToolCategories.Audio, old, alias, known), $"別名を「{alias}」に変更しました");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAliasAsync()
    {
        if (SelectedAlias is { } alias && await ConfirmAsync("別名を削除しますか?", $"「{alias}」を指定している呼び出しは対象が見つからなくなります。", "削除"))
        {
            await ChangeAsync(() => runtime.Access.RemoveAlias(ToolCategories.Audio, alias), $"別名「{alias}」を削除しました");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRebind))]
    private async Task RebindAliasAsync()
    {
        if (SelectedAlias is { } alias && RebindTarget is { } target
            && await ConfirmAsync("別名を別の機器に再紐付けしますか?", $"今後「{alias}」は「{target.Name}」({target.Direction}) を指します。\n{target.Id}", "再紐付け"))
        {
            await ChangeAsync(() => runtime.Access.RebindAlias(ToolCategories.Audio, alias, target.Id), $"別名「{alias}」を「{target.Name}」に再紐付けしました");
        }
    }

    private Task ChangeAsync(Action change, string success) => RunAsync(async () =>
    {
        ClearMessage();
        await Task.Run(change);
        AliasName = "";
        await LoadAsync();
        if (!HasMessage)
        {
            Notify(InfoBarSeverity.Success, success, "次の呼び出しから反映されます。");
        }
    }, "別名を変更できませんでした");
}

/// <summary>One row of the device page: an endpoint, or an id that only exists because aliases still point to it.</summary>
public sealed record DeviceItem(string Id, string Name, string Direction, string State, string Default, string Level, string Operations, bool Found, IReadOnlyList<string> Aliases)
{
    public string Summary => $"{Direction} · {State}{(Default.Length > 0 ? " · 既定" : "")}";
    public string AliasText => Aliases.Count == 0 ? "" : "別名: " + string.Join(", ", Aliases);
    public string DefaultText => Default.Length == 0 ? "—" : Default;

    public static IReadOnlyList<DeviceItem> Build(AudioDeviceListData? list, IReadOnlyList<DeviceAlias> aliases)
    {
        var items = new List<DeviceItem>();
        foreach (var d in list?.Devices ?? [])
        {
            items.Add(new DeviceItem(
                d.Id,
                d.Name,
                d.Direction == AudioDirection.Output ? "出力" : "入力",
                StateLabel(d.State),
                string.Join("、", d.DefaultRoles.Select(RoleLabel)),
                d.Volume is { } v ? $"音量 {v}{(d.Muted == true ? "（ミュート）" : "")}" : "—",
                string.Join(", ", d.Operations),
                Found: true,
                d.Aliases));
        }

        var missing = list is null ? "状態を取得できません"
            : list.FailedCount > 0 || list.Truncated ? "見つかりません（一部の機器を取得できていません）"
            : "見つかりません";
        var known = items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in aliases.Where(a => !known.Contains(a.DeviceId)).GroupBy(a => a.DeviceId, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new DeviceItem(group.Key, "（登録済みの機器）", "—", missing, "", "—", "—", Found: false, [.. group.Select(a => a.Alias)]));
        }

        return items;
    }

    private static string StateLabel(AudioDeviceState state) => state switch
    {
        AudioDeviceState.Active => "接続中",
        AudioDeviceState.Disabled => "無効",
        AudioDeviceState.Unplugged => "未接続",
        _ => "存在しない",
    };

    private static string RoleLabel(AudioRole role) => role switch
    {
        AudioRole.Console => "コンソール",
        AudioRole.Multimedia => "マルチメディア",
        _ => "通信",
    };
}
