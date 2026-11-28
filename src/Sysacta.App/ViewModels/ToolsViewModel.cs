using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Wpf.Ui.Controls;

namespace Sysacta.App.ViewModels;

public sealed partial class ToolsViewModel(SysactaRuntime runtime) : PageViewModel
{
    private static readonly JsonSerializerOptions IndentedJson = new(ToolJson.Options) { WriteIndented = true };

    public ObservableCollection<ToolCategoryGroup> Categories { get; } = [];

    /// <summary>
    /// Builds the list once and afterwards only updates switches in place, so a save that is still running is neither
    /// overwritten by a stale read nor detached from the switch shown (each switch keeps a single saver).
    /// </summary>
    public override async Task RefreshAsync()
    {
        var states = await Task.Run(() => runtime.Registry.All.ToDictionary(
            t => t.Name,
            t => (Enabled: runtime.Access.IsToolEnabled(t.Name), Availability: t.GetAvailability())));
        if (Categories.Count > 0)
        {
            foreach (var item in Categories.SelectMany(c => c.Items))
            {
                var state = states[item.Name];
                item.ApplyStoredValue(state.Enabled);
                item.ApplyAvailability(state.Availability);
            }

            return;
        }

        foreach (var group in runtime.Registry.All.GroupBy(t => t.Descriptor.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var items = group.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t =>
            {
                var state = states[t.Name];
                return new ToolItem(this, t, state.Enabled, state.Availability);
            });
            Categories.Add(new ToolCategoryGroup(CategoryLabel(group.Key), [.. items]));
        }
    }

    internal Task<bool> SetEnabledAsync(ToolItem item, bool enabled) => RunAsync(async () =>
    {
        await Task.Run(() => runtime.Access.SetToolEnabled(item.Name, enabled));
        Notify(InfoBarSeverity.Success, enabled ? $"{item.Name} を有効にしました" : $"{item.Name} を無効にしました", "すべてのクライアントに即時に反映されます。");
    });

    /// <summary>Runs a read-only tool, or sends the fixed test notification, through the same executor as MCP (validated, globally authorized and audited).</summary>
    internal async Task TestAsync(ToolItem item)
    {
        await RunAsync(async () =>
        {
            // The executor starts with synchronous SQLite reads (authorization) and audit writes: keep them off the UI thread.
            var result = await Task.Run(() => runtime.Executor.ExecuteAsync(Caller.LocalConsole, item.Name, item.TestArguments, CancellationToken.None));
            item.TestResult = JsonSerializer.Serialize(result, IndentedJson);
            item.HasTestResult = true;
        });
    }

    private static string CategoryLabel(string category) => category switch
    {
        ToolCategories.Audio => "オーディオ",
        ToolCategories.Display => "ディスプレイ",
        ToolCategories.Media => "メディア",
        ToolCategories.Power => "電源",
        ToolCategories.System => "システム",
        ToolCategories.Apps => "アプリ・プロセス",
        ToolCategories.Network => "ネットワーク",
        ToolCategories.Bluetooth => "Bluetooth",
        ToolCategories.Notification => "通知",
        _ => category,
    };
}

public sealed partial class ToolCategoryGroup(string name, IReadOnlyList<ToolItem> items) : ObservableObject
{
    public string Name { get; } = name;

    public IReadOnlyList<ToolItem> Items { get; } = items;

    // Setting IsEnabled saves through each item's LatestValueSaver. Tools that are off by default (screen loss, lost
    // connection) and experimental tools are only ever enabled one by one.
    [RelayCommand]
    private void EnableAll()
    {
        foreach (var item in Items.Where(i => i.IsAvailable && !i.IsExperimental && !i.IsOffByDefault))
        {
            item.IsEnabled = true;
        }
    }

    [RelayCommand]
    private void DisableAll()
    {
        foreach (var item in Items)
        {
            item.IsEnabled = false;
        }
    }
}

public sealed partial class ToolItem : ObservableObject
{
    private readonly ToolsViewModel _owner;
    private readonly LatestValueSaver<bool> _saver;
    private readonly bool _readOnly;
    /// <summary>The console test has no argument input, so tools with a required argument need fixed ones.</summary>
    private readonly bool _needsArguments;

    public ToolItem(ToolsViewModel owner, SysactaTool tool, bool enabled, ToolAvailability? precomputedAvailability = null)
    {
        _owner = owner;
        _isEnabled = enabled;
        _readOnly = tool.Descriptor.ReadOnly;
        _needsArguments = tool.InputSchema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0;
        _saver = new LatestValueSaver<bool>(() => IsEnabled, value => owner.SetEnabledAsync(this, value), owner.RefreshAsync);
        Name = tool.Name;
        Title = tool.Descriptor.Title;
        Description = tool.Descriptor.Description;
        IsExperimental = tool.Descriptor.DefaultPolicy == DefaultPolicy.Experimental;
        IsOffByDefault = tool.Descriptor.DefaultPolicy == DefaultPolicy.Off;
        Caution = CautionFor(tool.Name);
        if (tool.Name == "notification_send")
        {
            // The only state-changing console test: fixed harmless text, and the button says that it really sends.
            TestArguments = JsonSerializer.SerializeToElement(new { title = "Sysacta テスト通知", body = "管理画面から送ったテスト通知です。" });
            TestLabel = "テスト通知を送る";
            TestToolTip = "実際に Windows の通知を 1 件送ります (監査ログに記録されます)";
        }
        PolicyLabel = tool.Descriptor.DefaultPolicy switch
        {
            DefaultPolicy.On => tool.Descriptor.ReadOnly ? "既定で有効・読み取り" : "既定で有効・状態変更",
            DefaultPolicy.Off => "既定で無効・注意が必要な操作",
            _ => "実験的・非公開APIを使用",
        };
        ApplyAvailability(precomputedAvailability ?? tool.GetAvailability());
    }

    public JsonElement? TestArguments { get; }
    public string TestLabel { get; } = "動作確認";
    public string TestToolTip { get; } = "読み取り専用ツールを管理コンソールから実行します (監査ログに記録されます)";

    public string Name { get; }
    public string Title { get; }
    public string Description { get; }
    public string PolicyLabel { get; }
    public bool IsExperimental { get; }
    public bool IsOffByDefault { get; }

    /// <summary>What the local user must be able to do before enabling the tool (physical recovery), or "".</summary>
    public string Caution { get; }

    public bool HasCaution => Caution.Length > 0;

    private static string CautionFor(string tool) => tool switch
    {
        "display_set_mode" => "注意: 画面が映らなくなることがあります。対象はプライマリ以外のディスプレイだけです。元に戻すには、トレイメニューまたはデバイス画面の「元に戻す」を使います（Sysacta の起動中の直近 1 件のみ）。Windows の設定には保存されないため、サインアウトでも元に戻ります。",
        "display_set_power" => "注意: オフにしたモニターは DDC/CI に応答しなくなったり Windows から消えたりして、Sysacta からはオンに戻せないことがあります。モニターの電源ボタンで戻せることを確認してから有効にしてください。すべての画面をオフにすると、物理操作でしか戻せません。",
        "display_set_input" => "注意: 切り替え先に信号がないと画面が映らず、Sysacta から戻せないことがあります。モニターのメニュー（OSD）で入力を戻せることを確認してから有効にしてください。入力名は MCCS の定義で、本体の端子表示と一致しないことがあります。",
        "notification_send" => "注意: 通知の内容は Windows の通知センターに残り、ロック画面や画面共有中の画面に表示されることがあります。保持・同期・表示は Windows の設定に従います。",
        "power_set_plan" => "注意: 電源プランの変更は Windows に保存され、再起動後も残ります。性能・消費電力・自動スリープまでの時間が変わります。元に戻すには、Windows の設定または MCP から元のプランを指定し直します。",
        "power_lock" => "注意: Windows がロックされ、PC の前で使うにはサインインし直す必要があります。MCP の権限は変わりません。",
        "power_sleep" => "注意: PC がスリープし、MCP の接続が切れます。復帰には PC の前での操作やウェイク設定が必要で、Sysacta からは起こせません。未保存の作業は保存してから有効にしてください。",
        "power_shutdown" => "注意: PC が停止し、MCP の接続が切れます。電源を入れ直すには PC の前での操作が必要です。保存していない作業があるアプリは停止を止めることがあります。",
        "power_restart" => "注意: PC が再起動し、MCP の接続が切れます。Sysacta はサインイン時の自動起動が有効な場合だけ再開します。保存していない作業があるアプリは再起動を止めることがあります。",
        _ => "",
    };

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string _availabilityText = "";

    [ObservableProperty]
    private bool _canTest;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _testResult = "";

    [ObservableProperty]
    private bool _hasTestResult;

    private bool _applyingStoredValue;

    internal void ApplyAvailability(ToolAvailability availability)
    {
        IsAvailable = availability.IsAvailable;
        AvailabilityText = availability.IsAvailable ? ""
            : Name == "notification_send" ? "通知を登録できないため利用できません。スタートメニューの「Sysacta」がこの Sysacta のものでない場合（別の場所にあるコピーが登録したものを含む）は、そのコピーを使うか、ショートカットを取り除いて Sysacta を再起動してください。"
            : $"この環境では利用できません: {availability.Reason}";
        CanTest = (TestArguments is not null || (_readOnly && !_needsArguments)) && availability.IsAvailable;
    }

    /// <summary>Shows the stored value without saving it again; ignored while this switch's own save is running.</summary>
    internal void ApplyStoredValue(bool enabled)
    {
        if (_saver.IsSaving)
        {
            return;
        }

        _applyingStoredValue = true;
        IsEnabled = enabled;
        _applyingStoredValue = false;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_applyingStoredValue)
        {
            _ = _saver.ChangedAsync();
        }
    }

    [RelayCommand]
    private Task TestAsync() => _owner.TestAsync(this);
}
