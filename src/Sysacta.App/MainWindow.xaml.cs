using System.ComponentModel;
using System.Windows;
using Sysacta.App.Platform;
using Sysacta.App.ViewModels;
using Sysacta.App.Views;

namespace Sysacta.App;

/// <summary>Management console window. Closing hides it to the tray; only <see cref="CloseForExit"/> really closes.</summary>
public partial class MainWindow
{
    private readonly PageProvider _pages;
    private bool _allowClose;
    private bool _navigated;

    internal MainWindow(SysactaRuntime runtime)
    {
        InitializeComponent();
        AdminBanner.IsOpen = Privilege.IsElevated;
        if (Privilege.IsElevated)
        {
            Title = "Sysacta (管理者)";
            TitleBar.Title = "Sysacta — 管理者";
        }

        // Pages are created once and reused; each refreshes from the database when shown.
        _pages = new PageProvider(
            new OverviewPage(new OverviewViewModel(runtime)),
            new ConnectionPage(new ConnectionViewModel(runtime)),
            new ToolsPage(new ToolsViewModel(runtime)),
            new ClientsPage(new ClientsViewModel(runtime)),
            new LogsPage(new LogsViewModel(runtime)),
            new GeneralPage(new GeneralViewModel(runtime, () => this)));
        Navigation.SetServiceProvider(_pages);
    }

    internal void ShowAndActivate(Type? page)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (page is not null)
        {
            Navigation.Navigate(page);
            _navigated = true;
        }
        else if (!_navigated)
        {
            Navigation.Navigate(typeof(OverviewPage));
            _navigated = true;
        }
    }

    internal void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Keep running in the tray.
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private sealed class PageProvider(params object[] pages) : IServiceProvider
    {
        public object? GetService(Type serviceType) => pages.FirstOrDefault(serviceType.IsInstanceOfType);
    }
}
