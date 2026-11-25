using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Sysacta.Core.Auditing;
using Sysacta.Infrastructure;
using Wpf.Ui.Controls;

namespace Sysacta.App.ViewModels;

public sealed partial class LogsViewModel(SysactaRuntime runtime) : PageViewModel
{
    private const int DisplayLimit = 1000;
    private const int ExportLimit = 100_000;

    public static IReadOnlyList<string> OutcomeOptions { get; } =
        ["すべての結果", .. Enum.GetValues<AuditOutcome>().Select(AuditRow.OutcomeLabel)];

    public ObservableCollection<AuditRow> Events { get; } = [];

    public ObservableCollection<string> ToolOptions { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _outcomeIndex;

    [ObservableProperty]
    private int _toolIndex;

    [ObservableProperty]
    private DateTime? _fromDate;

    [ObservableProperty]
    private DateTime? _toDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AuditRow? _selected;

    [ObservableProperty]
    private bool _hasEvents;

    [ObservableProperty]
    private string _resultSummary = "";

    [ObservableProperty]
    private double? _retentionDays;

    public bool HasSelection => Selected is not null;

    public override async Task RefreshAsync()
    {
        RetentionDays = runtime.Settings.AuditRetentionDays;
        if (ToolOptions.Count == 0)
        {
            ToolOptions.Add("すべてのツール");
            foreach (var name in runtime.Registry.All.Select(t => t.Name).Order(StringComparer.Ordinal))
            {
                ToolOptions.Add(name);
            }
        }

        await SearchAsync();
    }

    private AuditQuery BuildQuery(int limit) => new(
        Text: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
        Outcome: OutcomeIndex > 0 ? Enum.GetValues<AuditOutcome>()[OutcomeIndex - 1] : null,
        ToolName: ToolIndex > 0 && ToolIndex < ToolOptions.Count ? ToolOptions[ToolIndex] : null,
        From: FromDate is { } f ? new DateTimeOffset(f.Date, TimeZoneInfo.Local.GetUtcOffset(f.Date)) : null,
        To: ToDate is { } t ? new DateTimeOffset(t.Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(t.Date)) : null,
        Limit: limit);

    [RelayCommand]
    private Task SearchAsync() => RunAsync(async () =>
    {
        var query = BuildQuery(DisplayLimit);
        var events = await Task.Run(() => runtime.Audit.Query(query));
        Events.Clear();
        foreach (var e in events)
        {
            Events.Add(new AuditRow(e));
        }

        HasEvents = Events.Count > 0;
        ResultSummary = events.Count >= DisplayLimit ? $"新しい順に {DisplayLimit} 件を表示(条件で絞り込めます)" : $"{events.Count} 件";
    }, "ログを読み込めませんでした");

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        OutcomeIndex = 0;
        ToolIndex = 0;
        FromDate = null;
        ToDate = null;
        SearchCommand.Execute(null);
    }

    [RelayCommand]
    private Task ExportCsvAsync() => ExportAsync("CSV ファイル (*.csv)|*.csv", "csv", (events, stream) =>
    {
        // UTF-8 with BOM so spreadsheet applications detect the encoding.
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        AuditLog.ExportCsv(events, writer);
    });

    [RelayCommand]
    private Task ExportJsonAsync() => ExportAsync("JSON ファイル (*.json)|*.json", "json", AuditLog.ExportJson);

    private Task ExportAsync(string filter, string extension, Action<IReadOnlyList<AuditEvent>, Stream> write) => RunAsync(async () =>
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = extension,
            FileName = $"sysacta-audit-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var query = BuildQuery(ExportLimit);
        var path = dialog.FileName;
        var count = await Task.Run(() =>
        {
            var events = runtime.Audit.Query(query);
            using var stream = File.Create(path);
            write(events, stream);
            return events.Count;
        });
        Notify(InfoBarSeverity.Success, $"{count} 件を書き出しました", path);
    }, "書き出しに失敗しました");

    [RelayCommand]
    private Task SaveRetentionAsync() => RunAsync(async () =>
    {
        var days = RetentionDays is { } d && d == Math.Floor(d) ? (int)d : -1;
        if (await runtime.SaveSettingsAsync(s => s with { AuditRetentionDays = days }, restartIfRunning: false) is { } error)
        {
            Notify(InfoBarSeverity.Error, "保持期間を保存できませんでした", error);
            return;
        }

        var removed = await Task.Run(() => runtime.Audit.Purge(days, DateTimeOffset.UtcNow));
        Notify(InfoBarSeverity.Success, $"保持期間を {days} 日に設定しました", removed > 0 ? $"期限を過ぎた {removed} 件を削除しました。" : "");
        await SearchAsync();
    });
}
