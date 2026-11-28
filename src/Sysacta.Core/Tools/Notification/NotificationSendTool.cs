using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Sysacta.Core.Tools.Notification;

public sealed class NotificationSendArgs
{
    [Description("Single-line plain text title, 1-64 UTF-16 code units (an emoji usually counts as 2 or more). Not blank.")]
    [StringLength(NotificationSendTool.MaxTitleLength, MinimumLength = 1)]
    [NotificationText]
    public required string Title { get; init; }

    [Description("Plain text body, 1-256 UTF-16 code units; line breaks (LF or CRLF) are allowed. Not blank.")]
    [StringLength(NotificationSendTool.MaxBodyLength, MinimumLength = 1)]
    [NotificationText(multiline: true)]
    public required string Body { get; init; }
}

public sealed record NotificationSendData(
    [property: Description("true when the Windows API accepted the notification; false when nothing was sent; null when unknown. Never proof that it was displayed or seen.")] bool? Accepted,
    [property: Description("Set only when refused by the rate limit: milliseconds until a send may be attempted again.")] long? RetryAfterMs);

/// <summary>
/// Sends one plain-text desktop notification. One instance per process: it holds the rate limit shared by every client
/// and the local console (an MCP host restart or a permission change does not reset it; a process restart does).
/// </summary>
public sealed class NotificationSendTool(IDesktopNotifications notifications, TimeProvider time) : SysactaTool<NotificationSendArgs, NotificationSendData>
{
    public const int MaxTitleLength = 64;
    public const int MaxBodyLength = 256;
    public const int MaxPerWindow = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    /// <summary>Monotonic timestamps of the latest OS send starts, oldest first (at most <see cref="MaxPerWindow"/>).</summary>
    private readonly Queue<long> _starts = new(MaxPerWindow);
    private long _lastStart;

    public override ToolDescriptor Descriptor { get; } = new("notification_send", ToolCategories.Notification, "Send desktop notification",
        "Shows a Windows desktop notification from Sysacta with a plain-text title and body (no markup, links, images, sounds or actions). Never send secrets, credentials or personal data: the text may stay in the notification center and appear on the lock screen or shared screens. status=success with verification=accepted means only that Windows accepted the request, not that it was displayed or seen (focus assist, notification settings or the lock screen can suppress it). Rate limited across all clients to 5 per 60 seconds with at least 2 seconds between sends; a refused request returns BUSY with retry_after_ms and is not queued. Do not resend when status=unknown, it may show twice.",
        DefaultPolicy.On, ReadOnly: false, AuditedArguments: []);

    public override ToolAvailability GetAvailability() => notifications.GetAvailability();

    // One lock for all sends: the quota check and the start below cannot race, and a hung native call keeps it.
    protected override ValueTask<ToolTarget> ResolveTargetAsync(NotificationSendArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ToolTarget(["notification:send"], null));

    protected override Task<ToolOutcome<NotificationSendData>> ExecuteAsync(NotificationSendArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        if (RetryAfter() is { } wait)
        {
            return Task.FromResult(ToolOutcome.Error(new NotificationSendData(false, (long)Math.Ceiling(wait.TotalMilliseconds)),
                ErrorCodes.Busy, "The notification rate limit was reached. Nothing was sent.", retryable: true));
        }

        var send = notifications.Prepare(args.Title, args.Body.ReplaceLineEndings("\n"));
        var unknown = new NotificationSendData(null, null);
        ReportProgress(execution, unknown);
        cancellationToken.ThrowIfCancellationRequested();
        execution.BeginSideEffect();
        // The quota is consumed by every started send, whatever happens next. A refused start consumes nothing.
        Record();
        try
        {
            send();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never retried: the notification may have been submitted. The exception text may contain the payload.
            return Task.FromResult(ToolOutcome.Unknown(unknown, ErrorCodes.OsApiError, "Windows did not confirm the notification. It may or may not be shown."));
        }

        var accepted = new NotificationSendData(true, null);
        ReportAccepted(execution, accepted);
        return Task.FromResult(new ToolOutcome<NotificationSendData>(accepted, Verification.Accepted));
    }

    /// <summary>How long until another send may start, or null when one may start now.</summary>
    private TimeSpan? RetryAfter()
    {
        while (_starts.TryPeek(out var oldest) && time.GetElapsedTime(oldest) >= Window)
        {
            _starts.Dequeue();
        }

        if (_starts.Count == 0)
        {
            return null;
        }

        var wait = MinInterval - time.GetElapsedTime(_lastStart);
        if (_starts.Count >= MaxPerWindow)
        {
            var windowWait = Window - time.GetElapsedTime(_starts.Peek());
            wait = windowWait > wait ? windowWait : wait;
        }

        return wait > TimeSpan.Zero ? wait : null;
    }

    private void Record()
    {
        _lastStart = time.GetTimestamp();
        _starts.Enqueue(_lastStart);
    }
}
