using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysacta.Core.Devices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;
using Sysacta.Core.Tools.Display;
using Wpf.Ui.Controls;

namespace Sysacta.App.ViewModels;

/// <summary>One device category of the page. Aliases are unique per category.</summary>
public sealed record DeviceCategory(string Id, string Label);

/// <summary>
/// Audio devices, displays and their aliases. The lists come from <see cref="AudioDeviceCatalog"/> and
/// <see cref="DisplayCatalog"/>, the same code as audio_list_devices / display_list_devices. Display values are read
/// only for the selected display. Aliases are changed only here (never through MCP); nothing is re-bound automatically.
/// </summary>
public sealed partial class DevicesViewModel : PageViewModel
{
    private const int MaxDevices = 512;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly SysactaRuntime runtime;
    private Task? _load;
    private bool _loadAgain;
    private bool _reading;
    private DeviceItem? _read;

    public DevicesViewModel(SysactaRuntime runtime)
    {
        this.runtime = runtime;
        _category = Categories[0];
    }

    public IReadOnlyList<DeviceCategory> Categories { get; } =
        [new(ToolCategories.Audio, "オーディオ"), new(ToolCategories.Display, "ディスプレイ")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private DeviceCategory _category;

    public string EmptyText => $"{Category.Label}デバイスがありません。";

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    [ObservableProperty]
    private bool _hasDevices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(RebindTargets))]
    [NotifyCanExecuteChangedFor(nameof(AddAliasCommand), nameof(RenameAliasCommand), nameof(RemoveAliasCommand), nameof(RebindAliasCommand))]
    private DeviceItem? _selected;

    /// <summary>"現在値" of the selected device; for a display, filled in after reading it.</summary>
    [ObservableProperty]
    private string _selectedValues = "";

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

    /// <summary>Connected or known devices of the category an alias can be moved to, chosen by the user.</summary>
    public IReadOnlyList<DeviceItem> RebindTargets => [.. Devices.Where(d => d.Found && d.Id != Selected?.Id)];

    public override Task RefreshAsync()
    {
        ClearMessage();
        return RunAsync(LoadAsync, "デバイスを読み込めませんでした");
    }

    partial void OnCategoryChanged(DeviceCategory value)
    {
        // Rows of the other category must never be edited under this one.
        Devices.Clear();
        HasDevices = false;
        Selected = null;
        OnPropertyChanged(nameof(RebindTargets));
        if (runtime is not null)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>At most one listing runs; requests made meanwhile are served by one more pass.</summary>
    private Task LoadAsync()
    {
        if (_load is { IsCompleted: false })
        {
            _loadAgain = true;
            return _load;
        }

        return _load = LoadPassesAsync();
    }

    private async Task LoadPassesAsync()
    {
        do
        {
            _loadAgain = false;
            var category = Category.Id;
            if (category == ToolCategories.Audio)
            {
                var (list, aliases) = await Task.Run(() => Listed(category, () => AudioDeviceCatalog.List(runtime.AudioEndpoints, runtime.Access, null, MaxDevices)));
                if (!_loadAgain && Category.Id == category)
                {
                    Show(list, aliases);
                }
            }
            else
            {
                var (list, aliases) = await Task.Run(() => Listed(category, () => DisplayCatalog.List(runtime.Displays, runtime.Access)));
                if (!_loadAgain && Category.Id == category)
                {
                    ShowDisplays(list, aliases);
                }
            }
        }
        while (_loadAgain);
    }

    private (T? List, IReadOnlyList<DeviceAlias> Aliases) Listed<T>(string category, Func<T> list)
        where T : class
    {
        var stored = runtime.Access.List(category);
        try
        {
            return (list(), stored);
        }
        catch (ToolFailureException)
        {
            return (null, stored); // registered aliases stay manageable
        }
    }

    /// <param name="list">Null when the enumeration failed.</param>
    internal void Show(AudioDeviceListData? list, IReadOnlyList<DeviceAlias> aliases)
    {
        Replace(DeviceItem.Build(list, aliases));
        if (list is null)
        {
            Notify(InfoBarSeverity.Error, "オーディオデバイスを取得できませんでした", "登録済みの別名のみを表示しています。Windows Audio サービスの状態を確認して再読み込みしてください。");
        }
        else if (list.FailedCount > 0 || list.Truncated || list.Devices.Any(d => d.ErrorCode is not null))
        {
            Notify(InfoBarSeverity.Warning, "一部の情報を取得できませんでした", $"取得できなかった機器 {list.FailedCount} 件 / 検出 {list.TotalCount} 件{(list.Truncated ? "（表示件数の上限を超えています）" : "")}");
        }
    }

    /// <param name="list">Null when the enumeration failed.</param>
    internal void ShowDisplays(DisplayDeviceListData? list, IReadOnlyList<DeviceAlias> aliases)
    {
        Replace(DeviceItem.BuildDisplays(list, aliases));
        if (list is null)
        {
            Notify(InfoBarSeverity.Error, "ディスプレイを取得できませんでした", "登録済みの別名のみを表示しています。リモート接続中やロック中でないことを確認して再読み込みしてください。");
        }
        else if (list.UnidentifiedCount > 0 || list.Truncated || list.Devices.Any(d => d.ErrorCode is not null))
        {
            Notify(InfoBarSeverity.Warning, "一部の情報を取得できませんでした", $"識別できなかったディスプレイ {list.UnidentifiedCount} 件 / 検出 {list.TotalCount} 件{(list.Truncated ? "（表示件数の上限を超えています）" : "")}");
        }
    }

    private void Replace(IReadOnlyList<DeviceItem> items)
    {
        var selectedId = Selected?.Id;
        var selectedAlias = SelectedAlias;
        _read = null;
        Devices.Clear();
        foreach (var item in items)
        {
            Devices.Add(item);
        }

        HasDevices = Devices.Count > 0;
        Selected = Devices.FirstOrDefault(d => d.Id == selectedId);
        SelectedAlias = Selected?.Aliases.FirstOrDefault(a => a == selectedAlias);
        OnPropertyChanged(nameof(RebindTargets));
    }

    partial void OnSelectedChanged(DeviceItem? value)
    {
        SelectedAlias = null;
        AliasName = "";
        RebindTarget = null;
        SelectedValues = value?.Level ?? "";
        if (runtime is not null)
        {
            _ = ReadSelectedDisplayAsync();
        }
    }

    /// <summary>
    /// Reads the selected display's values in the background. Only one read runs at a time, so a monitor that does not
    /// answer neither blocks the window nor piles up queries; the latest selection is read afterwards.
    /// </summary>
    private async Task ReadSelectedDisplayAsync()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        try
        {
            while (Selected is { Found: true, IsDisplay: true } item && !ReferenceEquals(item, _read))
            {
                SelectedValues = $"{item.Level}\n輝度・コントラストを取得中…";
                var work = Task.Run(() => ReadDisplay(item.Id));
                if (await Task.WhenAny(work, Task.Delay(ReadTimeout)) != work && ReferenceEquals(item, Selected))
                {
                    SelectedValues = $"{item.Level}\nモニターが応答していません。";
                }

                var text = await work;
                _read = item;
                if (ReferenceEquals(item, Selected))
                {
                    SelectedValues = $"{item.Level}\n{text}";
                }
            }
        }
        finally
        {
            _reading = false;
        }
    }

    private string ReadDisplay(string id)
    {
        using var timeout = new CancellationTokenSource(ReadTimeout);
        try
        {
            return string.Join('\n', Enum.GetValues<DisplayFeature>().Select(f => FeatureText(f, DisplayFeatureState.Read(runtime.Displays, id, f, timeout.Token))));
        }
        catch (OperationCanceledException)
        {
            return "輝度・コントラストの取得がタイムアウトしました。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "輝度・コントラストを取得できませんでした。";
        }
    }

    internal static string FeatureText(DisplayFeature feature, DisplayFeatureState state)
    {
        var label = feature == DisplayFeature.Brightness ? "輝度" : "コントラスト";
        return state.Support switch
        {
            DisplaySupport.Supported => $"{label}: {state.Level}（{(state.Method == DisplayControlMethod.Wmi ? "内蔵パネル・Windows制御" : "モニター本体・DDC/CI")}、値域 {state.NativeRange!.Min}〜{state.NativeRange.Max}）",
            DisplaySupport.NotSupported => $"{label}: 非対応（{state.ErrorCode}）",
            _ => $"{label}: 取得できません（{state.ErrorCode}）",
        };
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
        var category = Category.Id;
        var known = KnownIds();
        await ChangeAsync(() => runtime.Access.SetAlias(category, device.Id, alias, known), $"別名「{alias}」を登録しました");
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAliasAsync()
    {
        if (SelectedAlias is not { } old)
        {
            return;
        }

        var alias = AliasName.Trim();
        var category = Category.Id;
        var known = KnownIds();
        if (await ConfirmAsync("別名を変更しますか?", $"「{old}」を指定している呼び出しは対象が見つからなくなります。新しい別名は「{alias}」です。", "変更"))
        {
            await ChangeAsync(() => runtime.Access.RenameAlias(category, old, alias, known), $"別名を「{alias}」に変更しました");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAliasAsync()
    {
        var category = Category.Id;
        if (SelectedAlias is { } alias && await ConfirmAsync("別名を削除しますか?", $"「{alias}」を指定している呼び出しは対象が見つからなくなります。", "削除"))
        {
            await ChangeAsync(() => runtime.Access.RemoveAlias(category, alias), $"別名「{alias}」を削除しました");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRebind))]
    private async Task RebindAliasAsync()
    {
        var category = Category.Id;
        if (SelectedAlias is { } alias && RebindTarget is { } target
            && await ConfirmAsync("別名を別の機器に再紐付けしますか?", $"今後「{alias}」は「{target.Name}」({target.Direction}) を指します。\n{target.Id}", "再紐付け"))
        {
            await ChangeAsync(() => runtime.Access.RebindAlias(category, alias, target.Id), $"別名「{alias}」を「{target.Name}」に再紐付けしました");
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

/// <summary>One row of the device page: a device, or an id that only exists because aliases still point to it.</summary>
public sealed record DeviceItem(string Id, string Name, string Direction, string State, string Default, string Level, string Operations, bool Found, IReadOnlyList<string> Aliases)
{
    public bool IsDisplay { get; init; }
    public string Summary => $"{Direction} · {State}{(Default.Length == 0 ? "" : IsDisplay ? " · プライマリ" : " · 既定")}{(IsDisplay ? $" · …{Id[Math.Max(0, Id.Length - 6)..]}" : "")}";
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

        return AddMissing(items, aliases, list is null, list is { FailedCount: > 0 } or { Truncated: true }, isDisplay: false);
    }

    public static IReadOnlyList<DeviceItem> BuildDisplays(DisplayDeviceListData? list, IReadOnlyList<DeviceAlias> aliases)
    {
        var items = (list?.Devices ?? []).Select(d => new DeviceItem(
            d.Id,
            d.Name,
            $"{KindLabel(d.Kind)}（{d.Connection}）",
            d.IsCloned ? "接続中（複製表示）" : "接続中",
            d.IsPrimary ? "プライマリ" : "",
            d.CurrentMode is { } m
                ? $"{m.Width}×{m.Height}{(m.RefreshNumerator is { } n && m.RefreshDenominator is { } r ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $" @ {(double)n / r:0.##} Hz") : "")}"
                : "表示モードを取得できません",
            string.Join(", ", d.Operations),
            Found: true,
            d.Aliases)
        { IsDisplay = true }).ToList();

        return AddMissing(items, aliases, list is null, list is { UnidentifiedCount: > 0 } or { Truncated: true }, isDisplay: true);
    }

    private static List<DeviceItem> AddMissing(List<DeviceItem> items, IReadOnlyList<DeviceAlias> aliases, bool failed, bool incomplete, bool isDisplay)
    {
        var missing = failed ? "状態を取得できません"
            : incomplete ? "見つかりません（一部の機器を取得できていません）"
            : "見つかりません";
        var known = items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in aliases.Where(a => !known.Contains(a.DeviceId)).GroupBy(a => a.DeviceId, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new DeviceItem(group.Key, "（登録済みの機器）", "—", missing, "", "—", "—", Found: false, [.. group.Select(a => a.Alias)]) { IsDisplay = isDisplay });
        }

        return items;
    }

    private static string KindLabel(DisplayKind kind) => kind switch
    {
        DisplayKind.Internal => "内蔵",
        DisplayKind.External => "外部",
        DisplayKind.Virtual => "仮想",
        _ => "種類不明",
    };

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
