using System.Windows.Controls;
using Sysacta.App.ViewModels;

namespace Sysacta.App.Views;

public partial class LogsPage : Page
{
    public LogsPage(LogsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }
}
