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

    /// <summary>Runs a read-only tool through the same executor as MCP (validated, globally authorized and audited).</summary>
    internal async Task TestAsync(ToolItem item)
    {
        await RunAsync(async () =>
        {
            // The executor starts with synchronous SQLite reads (authorization) and audit writes: keep them off the UI thread.
            var result = await Task.Run(() => runtime.Executor.ExecuteAsync(Caller.LocalConsole, item.Name, null, CancellationToken.None));
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

    // Setting IsEnabled saves through each item's LatestValueSaver.
    [RelayCommand]
    private void EnableAll()
    {
        foreach (var item in Items.Where(i => i.IsAvailable && !i.IsExperimental))
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
    /// <summary>The console test sends no arguments, so tools with a required argument cannot be tested there.</summary>
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
        PolicyLabel = tool.Descriptor.DefaultPolicy switch
        {
            DefaultPolicy.On => tool.Descriptor.ReadOnly ? "既定で有効・読み取り" : "既定で有効・状態変更",
            DefaultPolicy.Off => "既定で無効・注意が必要な操作",
            _ => "実験的・非公開APIを使用",
        };
        ApplyAvailability(precomputedAvailability ?? tool.GetAvailability());
    }

    public string Name { get; }
    public string Title { get; }
    public string Description { get; }
    public string PolicyLabel { get; }
    public bool IsExperimental { get; }

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
        AvailabilityText = availability.IsAvailable ? "" : $"この環境では利用できません: {availability.Reason}";
        CanTest = _readOnly && !_needsArguments && availability.IsAvailable;
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
