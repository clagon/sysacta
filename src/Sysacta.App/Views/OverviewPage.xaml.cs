using System.Windows.Controls;
using System.Windows.Threading;
using Sysacta.App.ViewModels;

namespace Sysacta.App.Views;

public partial class OverviewPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public OverviewPage(OverviewViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Live status only while the page is visible.
        _timer.Tick += async (_, _) => await viewModel.RefreshAsync();
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await viewModel.RefreshAsync();
        };
        Unloaded += (_, _) => _timer.Stop();
    }
}
