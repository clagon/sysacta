using System.Windows;
using System.Windows.Controls;
using Sysacta.App.ViewModels;
using Sysacta.App.Views;

namespace Sysacta.App.Tests;

/// <summary>
/// XAML errors (unknown properties, missing resources, bad enum values) only surface at runtime. These tests parse
/// and lay out every page and the main window without touching the database (view models are not refreshed).
/// </summary>
public class XamlSmokeTests
{
    private static void OnSta(Action action) => UiThread.Run(action);

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(1100, 760));
        element.Arrange(new Rect(0, 0, 1100, 760));
        element.UpdateLayout();
    }

    [Fact]
    public void All_pages_and_main_window_load()
    {
        OnSta(() =>
        {
            Page[] pages =
            [
                new OverviewPage(new OverviewViewModel(null!)),
                new ConnectionPage(new ConnectionViewModel(null!)),
                new ToolsPage(new ToolsViewModel(null!)),
                new ClientsPage(new ClientsViewModel(null!)),
                new LogsPage(new LogsViewModel(null!)),
                new GeneralPage(new GeneralViewModel(null!, () => null!)),
            ];
            foreach (var page in pages)
            {
                Layout(page);
            }

            var window = new MainWindow(null!);
            Layout((FrameworkElement)window.Content);
            window.CloseForExit();
        });
    }
}

/// <summary>Creates the real notification-area icon and follows server state changes (Windows only).</summary>
public class TrayIconTests
{
    [Fact]
    public async Task Tray_icon_is_created_and_tracks_server_state()
    {
        var host = TestHost.Create();
        var tray = UiThread.Run(() => new TrayIcon(host, () => "http://127.0.0.1:1/mcp", () => Task.FromResult<string?>(null), _ => { }, () => { }));
        try
        {
            var created = UiThread.Run(() => tray.IsCreated);
            Assert.SkipWhen(!created && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "No notification area in this CI session.");
            Assert.True(created);
            Assert.Equal("停止中", UiThread.Run(() => tray.StatusText));

            await host.StartAsync(new Sysacta.Mcp.ServerEndpointOptions { Port = 0 }, TestContext.Current.CancellationToken);
            await WaitForStatus(tray, s => s.StartsWith("実行中", StringComparison.Ordinal));

            await host.StopAsync();
            await WaitForStatus(tray, s => s == "停止中");
        }
        finally
        {
            UiThread.Run(tray.Dispose);
            await host.DisposeAsync();
        }
    }

    private static async Task WaitForStatus(TrayIcon tray, Func<string, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition(UiThread.Run(() => tray.StatusText) ?? ""))
        {
            Assert.True(DateTime.UtcNow < deadline, "Tray status did not update.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>An MCP host with no tools and in-memory stores (no SQLite).</summary>
internal static class TestHost
{
    public static Sysacta.Mcp.McpServerHost Create()
    {
        var store = new NoAccess();
        var audit = new NoAudit();
        var executor = new Sysacta.Core.Tools.ToolExecutor(new Sysacta.Core.Tools.ToolRegistry([]), new Sysacta.Core.Security.AccessPolicy(store, TimeProvider.System), audit, TimeProvider.System);
        return new Sysacta.Mcp.McpServerHost(executor, new Sysacta.Core.Security.TokenAuthenticator(store, TimeProvider.System), audit, TimeProvider.System, "test");
    }

    private sealed class NoAccess : Sysacta.Core.Security.IAccessStore
    {
        public bool IsToolEnabled(string toolName) => false;
        public Sysacta.Core.Security.ClientRecord? GetClient(string clientId) => null;
        public (Sysacta.Core.Security.ClientRecord Client, string TokenHash)? FindClientByTokenHash(string tokenHash) => null;
        public bool IsClientToolAllowed(string clientId, string toolName) => false;
        public void RecordClientUse(string clientId, DateTimeOffset at)
        {
        }
    }

    private sealed class NoAudit : Sysacta.Core.Auditing.IAuditLog
    {
        public void Write(Sysacta.Core.Auditing.AuditEvent auditEvent)
        {
        }
    }
}

/// <summary>One long-lived STA thread owning the WPF <see cref="Application"/> (only one per process is allowed).</summary>
internal static class UiThread
{
    private static readonly Lazy<System.Windows.Threading.Dispatcher> Dispatcher = new(() =>
    {
        System.Windows.Threading.Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            // Never instantiate App: WPF would run App.OnStartup when the dispatcher starts, opening the real
            // %LOCALAPPDATA% database and starting a server. Load the same resources into a plain Application instead.
            _ = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
                Resources = new ResourceDictionary
                {
                    MergedDictionaries =
                    {
                        new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light },
                        new Wpf.Ui.Markup.ControlsDictionary(),
                        new ResourceDictionary { Source = new Uri("pack://application:,,,/Sysacta;component/Styles.xaml") },
                    },
                },
            };
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    public static void Run(Action action) => Run(() =>
    {
        action();
        return 0;
    });

    public static T Run<T>(Func<T> func)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        Exception? failure = null;
        var result = Dispatcher.Value.Invoke(() =>
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                failure = ex;
                return default!;
            }
        });
        return failure is null ? result : throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
