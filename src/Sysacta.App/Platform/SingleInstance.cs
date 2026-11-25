using System.Security.Principal;

namespace Sysacta.App.Platform;

/// <summary>
/// One instance per Windows user session. A second launch signals the first one to show its window and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        _registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Returns null (after signalling the running instance) when another instance already owns the session.</summary>
    public static SingleInstance? TryAcquire(Action onActivate)
    {
        // "Local\" = per logon session; the user SID keeps fast-user-switching sessions and other users apart.
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        var mutex = new Mutex(initiallyOwned: true, $@"Local\Sysacta-{sid}", out var createdNew);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Sysacta-{sid}-activate");
        if (!createdNew)
        {
            activate.Set();
            activate.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, activate, onActivate);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
