using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sysacta.Core.Auditing;
using Wpf.Ui.Controls;

namespace Sysacta.App.ViewModels;

/// <summary>
/// Base for page view models: busy flag and one inline feedback message (shown in an InfoBar at the top of the page).
/// Long or blocking work (SQLite, server start) must run via <see cref="RunAsync"/> so the UI thread never blocks.
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasMessage;

    [ObservableProperty]
    private string _messageTitle = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private InfoBarSeverity _messageSeverity;

    public abstract Task RefreshAsync();

    protected void Notify(InfoBarSeverity severity, string title, string message = "")
    {
        MessageSeverity = severity;
        MessageTitle = title;
        Message = message;
        HasMessage = true;
    }

    protected void ClearMessage() => HasMessage = false;

    /// <summary>
    /// Runs <paramref name="work"/> with the busy flag set and converts failures into an error message.
    /// Returns false when it failed.
    /// </summary>
    protected async Task<bool> RunAsync(Func<Task> work, string failureTitle = "操作に失敗しました")
    {
        IsBusy = true;
        try
        {
            await work();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Thrown by Sysacta's own stores and validation with user-facing text (no paths or internals).
            Notify(InfoBarSeverity.Error, failureTitle, ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is System.IO.IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Notify(InfoBarSeverity.Error, failureTitle, SafeMessage(ex));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fixed user-facing text for OS / file / database failures. Their own messages can contain local paths or SQL,
    /// which must not be shown (AGENTS.md rule 5); the type name helps a bug report.
    /// </summary>
    public static string SafeMessage(Exception ex) => (ex switch
    {
        UnauthorizedAccessException or System.Security.SecurityException => "アクセスが拒否されました。ファイルや設定の権限を確認してください。",
        Microsoft.Data.Sqlite.SqliteException => "データベースの操作に失敗しました。しばらくしてからやり直してください。",
        _ => "ファイルまたはデータベースにアクセスできませんでした。",
    }) + $" ({ex.GetType().Name})";
}

/// <summary>
/// Persists a control's value (toggle, combo box) so that the stored value always ends up equal to the last value
/// shown, even when the user changes it again while a save is still running: saves for one control never overlap, and
/// a change made meanwhile is saved afterwards. On failure the view is reloaded from the store.
/// </summary>
internal sealed class LatestValueSaver<T>(Func<T> current, Func<T, Task<bool>> save, Func<Task> reload)
{
    private bool _saving;

    /// <summary>A save is running; the shown value must not be replaced by a (possibly stale) read.</summary>
    public bool IsSaving => _saving;

    /// <summary>Call from the property-changed hook on the UI thread.</summary>
    public async Task ChangedAsync()
    {
        if (_saving)
        {
            return; // the running loop picks up the new value
        }

        _saving = true;
        var failed = false;
        try
        {
            while (true)
            {
                var value = current();
                if (!await save(value))
                {
                    failed = true;
                    break;
                }

                if (EqualityComparer<T>.Default.Equals(current(), value))
                {
                    break;
                }
            }
        }
        finally
        {
            // Released exactly once, by the invocation that set it, before any reload: a save started while the reload
            // runs owns the flag from then on and must not have it cleared by this (older) invocation.
            _saving = false;
        }

        if (failed)
        {
            await reload();
        }
    }
}

/// <summary>One audit record formatted for display.</summary>
public sealed record AuditRow(AuditEvent Event)
{
    public string Time => Event.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string Tool => Event.ToolName ?? "—";
    public string Client => Event.ClientName ?? (Event.Source == "http" ? "(未認証)" : "—");
    /// <summary>Aggregated records show how many rejections they stand for, so they never read as one attempt.</summary>
    public string Outcome => Event.AggregateCount is { } n ? $"{OutcomeLabel(Event.Outcome)} ×{n}" : OutcomeLabel(Event.Outcome);
    public string ErrorCode => Event.ErrorCode ?? "";
    public string Duration => Event.DurationMs is { } d ? $"{d} ms" : "";
    public string Source => Event.Source switch { "mcp" => "MCP", "local_console" => "管理コンソール", "http" => "HTTP", var s => s };
    public string Arguments => Event.AggregateCount is { } n
        ? $"集約記録: この時刻から1分間に、個別には記録しなかった同じ理由の拒否が {n} 件ありました(接続元・内容は記録しません)。"
        : Event.Arguments ?? "(記録対象外)";
    public string Remote => Event.RemoteAddress ?? "—";

    public static string OutcomeLabel(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "成功",
        AuditOutcome.Error => "失敗",
        AuditOutcome.Denied => "拒否",
        AuditOutcome.AuthFailed => "認証失敗",
        AuditOutcome.Pending => "未確定",
        AuditOutcome.Unknown => "結果不明",
        AuditOutcome.Partial => "一部成功",
        _ => outcome.ToString(),
    };
}
