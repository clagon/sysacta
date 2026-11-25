using System.Windows;
using System.Windows.Threading;
using Sysacta.App.Platform;
using Sysacta.Infrastructure;

namespace Sysacta.App;

/// <summary>
/// Process lifecycle: single instance → open database → tray → optional window → optional server start.
/// Closing the window only hides it; <see cref="ExitAsync"/> (tray "終了") stops the server and releases resources.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(6);

    private SingleInstance? _instance;
    private SysactaRuntime? _runtime;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private DispatcherTimer? _maintenance;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = SingleInstance.TryAcquire(() => Dispatcher.BeginInvoke(() => ShowMainWindow()));
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            // Keep the tray app alive; never show stack traces or messages (they may contain paths). The type name
            // alone helps a bug report.
            args.Handled = true;
            System.Windows.MessageBox.Show($"予期しないエラーが発生しました。操作をやり直してください。\n({args.Exception.GetType().Name})", "Sysacta", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        try
        {
            _runtime = await Task.Run(() => new SysactaRuntime(SysactaDatabase.DefaultDirectory));
        }
        catch (Exception ex)
        {
            // InvalidOperationException carries Sysacta's own text (e.g. "database schema is newer"); other messages may
            // contain paths and are replaced.
            var detail = ex is InvalidOperationException ? ex.Message : ViewModels.PageViewModel.SafeMessage(ex);
            System.Windows.MessageBox.Show($"Sysacta を起動できませんでした。\n\n{detail}", "Sysacta", MessageBoxButton.OK, MessageBoxImage.Error);
            _instance.Dispose();
            Shutdown(1);
            return;
        }

        // Tray first: it is the only way to reach a hidden app, so later (cosmetic) failures must not prevent it.
        _tray = new TrayIcon(_runtime, ShowMainWindow, () => _ = ExitAsync());
        _window = new MainWindow(_runtime);
        try
        {
            ThemeController.Apply(_runtime.Settings.Theme, _window);
        }
        catch (InvalidOperationException)
        {
            // Theme is cosmetic; keep starting with the default theme.
        }

        if (!e.Args.Contains(AutoStart.MinimizedArgument, StringComparer.OrdinalIgnoreCase))
        {
            ShowMainWindow();
        }

        if (_runtime.Settings.StartServerOnLaunch && await _runtime.StartServerAsync() is { } error)
        {
            _tray.ShowWarning("MCPサーバーを起動できませんでした", error);
        }

        _maintenance = new DispatcherTimer { Interval = MaintenanceInterval };
        _maintenance.Tick += (_, _) => PurgeAuditLog();
        _maintenance.Start();
        PurgeAuditLog();
    }

    /// <summary>Shows the console, optionally on a specific page.</summary>
    public void ShowMainWindow(Type? page = null)
    {
        if (_window is null || _exiting)
        {
            return;
        }

        _window.ShowAndActivate(page);
    }

    public async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _maintenance?.Stop();
        _window?.CloseForExit();
        _tray?.Dispose();
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
        }

        _instance?.Dispose();
        Shutdown();
    }

    private void PurgeAuditLog()
    {
        if (_runtime is not { } runtime)
        {
            return;
        }

        var days = runtime.Settings.AuditRetentionDays;
        _ = Task.Run(() =>
        {
            try
            {
                runtime.Audit.Purge(days, DateTimeOffset.UtcNow);
            }
            catch (Exception)
            {
                // Retried on the next maintenance tick.
            }
        });
    }
}
