namespace Sysacta.Core.Tools.Notification;

/// <summary>The OS desktop notification API under Sysacta's own fixed sender identity.</summary>
public interface IDesktopNotifications
{
    /// <summary>Cheap and free of side effects: reports the result of the app's own startup registration.</summary>
    ToolAvailability GetAvailability();

    /// <summary>
    /// Checks that a notification can be sent now and builds it from plain text (never parsed as markup), without
    /// showing anything. The returned action asks the OS once to show it; returning means only that the API accepted
    /// the request. Throws <see cref="ToolFailureException"/> when nothing can be sent.
    /// </summary>
    Action Prepare(string title, string body);
}
