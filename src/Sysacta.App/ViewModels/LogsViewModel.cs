using System.Collections.ObjectModel;
using System.Globalization;
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

    public static IReadOnlyList<string> KindOptions { get; } = ["すべての記録", "個別の記録", "集約された記録"];

    public ObservableCollection<AuditRow> Events { get; } = [];

    public ObservableCollection<string> ToolOptions { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _outcomeIndex;

    [ObservableProperty]
    private int _toolIndex;

    [ObservableProperty]
    private int _kindIndex;

    /// <summary>Storage use, limits and whether records were evicted or could not be written (SEC-06).</summary>
    [ObservableProperty]
    private string _storageSummary = "";

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
            foreach (var name in runtime.Registry.AllNames.Order(StringComparer.Ordinal))
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
        From: DayStart(FromDate, 0, TimeZoneInfo.Local),
        To: DayStart(ToDate, 1, TimeZoneInfo.Local),
        Limit: limit,
        Aggregated: KindIndex switch { 1 => false, 2 => true, _ => null });

    /// <summary>
    /// Local midnight of <paramref name="date"/> + <paramref name="days"/>, using the UTC offset of that midnight itself
    /// (not of the selected day): on a daylight-saving change the two differ by an hour.
    /// </summary>
    internal static DateTimeOffset? DayStart(DateTime? date, int days, TimeZoneInfo zone) =>
        date is { } d ? new DateTimeOffset(d.Date.AddDays(days), zone.GetUtcOffset(d.Date.AddDays(days))) : null;

    [RelayCommand]
    private Task SearchAsync() => RunAsync(async () =>
    {
        var query = BuildQuery(DisplayLimit);
        var (events, status) = await Task.Run(() => (runtime.Audit.Query(query), runtime.Audit.GetStatus()));
        StorageSummary = FormatStorage(status);
        Events.Clear();
        foreach (var e in events)
        {
            Events.Add(new AuditRow(e));
        }

        HasEvents = Events.Count > 0;
        ResultSummary = events.Count >= DisplayLimit ? $"新しい順に {DisplayLimit} 件を表示(条件で絞り込めます)" : $"{events.Count} 件";
    }, "ログを読み込めませんでした");

    internal static string FormatStorage(AuditStatus s)
    {
        static string MiB(long bytes) => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.CurrentCulture);
        var text = new StringBuilder();
        text.Append(CultureInfo.CurrentCulture,
            $"保存量 {s.Rows:N0} / {s.Limits.MaxRows:N0} 件、{MiB(s.Bytes)} / {MiB(s.Limits.MaxBytes)} MiB (うち状態変更用に {s.Limits.ReservedRows:N0} 件を予約)。");
        text.Append(s.EvictedCount > 0
            ? string.Create(CultureInfo.CurrentCulture, $"上限を超えたため古い記録 {s.EvictedCount:N0} 件を削除しました(最終 {s.LastEvictedAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm})。")
            : "上限による削除はありません。");
        if (s.FailedWrites > 0)
        {
            text.Append(CultureInfo.CurrentCulture, $"起動後に {s.FailedWrites:N0} 件を記録できませんでした。");
        }

        text.Append("拒否が多いときは、1分ごとの件数に集約して記録します。");
        text.Append(CultureInfo.CurrentCulture,
            $"DB ファイル {MiB(s.FileBytes)} MiB (未使用 {MiB(s.FreeBytes)} MiB、設定・権限を含む)、WAL {MiB(s.WalBytes)} MiB。ファイルは定期保守で必要なときだけ縮めます。");
        return text.ToString();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        OutcomeIndex = 0;
        ToolIndex = 0;
        KindIndex = 0;
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
