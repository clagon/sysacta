using System.Collections.Concurrent;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Notification;

namespace Sysacta.Tests;

/// <summary>In-memory notification adapter shared by Core.Tests and Mcp.Tests. Never shows a notification.</summary>
public sealed class FakeNotifications : IDesktopNotifications
{
    public ToolAvailability Availability { get; set; } = ToolAvailability.Available;

    /// <summary>Notifications asked of the "OS", in order.</summary>
    public ConcurrentQueue<(string Title, string Body)> Sent { get; } = new();

    private int _prepared;

    public int Prepared => Volatile.Read(ref _prepared);

    /// <summary>Runs while preparing (before the start point); may throw.</summary>
    public Action? OnPrepare { get; set; }

    /// <summary>Runs inside the send (after the start point); may block or throw.</summary>
    public Action? OnSend { get; set; }

    public ToolAvailability GetAvailability() => Availability;

    public Action Prepare(string title, string body)
    {
        Interlocked.Increment(ref _prepared);
        OnPrepare?.Invoke();
        return () =>
        {
            Sent.Enqueue((title, body));
            OnSend?.Invoke();
        };
    }
}
