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

    /// <summary>Runs <paramref name="work"/> with the busy flag set and converts failures into an error message.</summary>
    protected async Task RunAsync(Func<Task> work, string failureTitle = "操作に失敗しました")
    {
        IsBusy = true;
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            Notify(InfoBarSeverity.Error, failureTitle, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One audit record formatted for display.</summary>
public sealed record AuditRow(AuditEvent Event)
{
    public string Time => Event.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string Tool => Event.ToolName ?? "—";
    public string Client => Event.ClientName ?? (Event.Source == "http" ? "(未認証)" : "—");
    public string Outcome => OutcomeLabel(Event.Outcome);
    public string ErrorCode => Event.ErrorCode ?? "";
    public string Duration => Event.DurationMs is { } d ? $"{d} ms" : "";
    public string Source => Event.Source switch { "mcp" => "MCP", "local_console" => "管理コンソール", "http" => "HTTP", var s => s };
    public string Arguments => Event.Arguments ?? "(記録対象外)";
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
