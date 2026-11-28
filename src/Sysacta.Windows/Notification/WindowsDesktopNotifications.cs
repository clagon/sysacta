using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Notification;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Sysacta.Windows.Notification;

/// <summary>
/// Windows.UI.Notifications for an unpackaged desktop app. Windows requires a Start menu shortcut carrying the
/// AppUserModelID; the app creates it for the current user at its own startup (never because of a tool call). No toast
/// activator, launch argument or event handler is registered, so clicking a notification runs nothing of its content.
/// The WinRT objects used here are agile and created per call; there is nothing to dispose.
/// </summary>
public sealed class WindowsDesktopNotifications : IDesktopNotifications
{
    /// <summary>Fixed sender identity. An installer must use the same id and shortcut path.</summary>
    public const string AppUserModelId = "Sysacta.Desktop";

    private const string Template = """<toast><visual><binding template="ToastGeneric"><text/><text/></binding></visual></toast>""";

    private readonly string _shortcut;
    private readonly string? _executable;
    private volatile Registration _state = Registration.Pending;
    private int _registering;

    public WindowsDesktopNotifications()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Sysacta.lnk"), Environment.ProcessPath)
    {
    }

    /// <summary>For tests: a shortcut location outside the Start menu registers nothing with Windows.</summary>
    internal WindowsDesktopNotifications(string shortcut, string? executable)
    {
        _shortcut = shortcut;
        _executable = executable;
    }

    private enum Registration
    {
        Pending,
        Registered,
        Conflict,
        Failed,
    }

    public ToolAvailability GetAvailability() => _state switch
    {
        Registration.Conflict => ToolAvailability.Unavailable(ErrorCodes.NotSupported, "The Start menu shortcut named Sysacta does not belong to this copy of Sysacta, so notifications are not registered. It was left unchanged."),
        Registration.Failed => ToolAvailability.Unavailable(ErrorCodes.NotSupported, "Notification registration failed when Sysacta started."),
        _ => ToolAvailability.Available,
    };

    /// <summary>Registers once on a dedicated STA thread (shell COM). A hang or failure affects only this tool.</summary>
    public void RegisterInBackground()
    {
        if (Interlocked.Exchange(ref _registering, 1) != 0)
        {
            return;
        }

        var thread = new Thread(Register) { IsBackground = true, Name = "Sysacta notification registration" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal void Register()
    {
        try
        {
            if (_executable is null)
            {
                _state = Registration.Failed;
            }
            else
            {
                _state = ShellShortcut.Ensure(_shortcut, _executable, AppUserModelId) ? Registration.Registered : Registration.Conflict;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _state = Registration.Failed;
        }
    }

    public Action Prepare(string title, string body)
    {
        if (_state == Registration.Pending)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "Notification registration has not finished. Nothing was sent.", retryable: true);
        }

        try
        {
            // The shortcut can be replaced or removed after startup: check it again (read-only, never repaired here).
            if (_state != Registration.Registered || !OnStaThread(() => ShellShortcut.IsRegistration(_shortcut, _executable!, AppUserModelId)))
            {
                throw new ToolFailureException(ErrorCodes.NotSupported, "The Sysacta notification registration is missing or was changed. Restart Sysacta to check it again. Nothing was sent.");
            }

            var notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            if (ReadSetting(notifier) is { } setting && setting != NotificationSetting.Enabled)
            {
                throw new ToolFailureException(ErrorCodes.NotificationsDisabled, "Windows notifications are turned off for Sysacta, for this user or by policy. Nothing was sent.");
            }

            var toast = new ToastNotification(BuildPayload(title, body));
            return () => notifier.Show(toast);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or ToolFailureException))
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows notification could not be prepared. Nothing was sent.", retryable: true);
        }
    }

    /// <summary>
    /// Shell COM on its own STA thread, as at registration. A hang blocks only this call, which holds the send lock, so
    /// at most one such thread is stuck; the executor times the call out and refuses a late start.
    /// </summary>
    private static bool OnStaThread(Func<bool> read)
    {
        var result = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = read();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }
        })
        { IsBackground = true, Name = "Sysacta notification registration check" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return failure is null ? result : throw new InvalidOperationException("The registration could not be read.", failure);
    }

    /// <summary>The text goes in as DOM text nodes of a fixed template, so it can never add markup or attributes.</summary>
    internal static XmlDocument BuildPayload(string title, string body)
    {
        var xml = new XmlDocument();
        xml.LoadXml(Template);
        var texts = xml.GetElementsByTagName("text");
        texts[0].AppendChild(xml.CreateTextNode(title));
        texts[1].AppendChild(xml.CreateTextNode(body));
        return xml;
    }

    /// <summary>
    /// null when Windows cannot report it (it can fail for an app that has not shown a notification yet); an unknown
    /// setting is not treated as disabled.
    /// </summary>
    private static NotificationSetting? ReadSetting(ToastNotifier notifier)
    {
        try
        {
            return notifier.Setting;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
