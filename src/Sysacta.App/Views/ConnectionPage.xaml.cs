using System.Windows.Controls;
using Sysacta.App.ViewModels;

namespace Sysacta.App.Views;

public partial class ConnectionPage : Page
{
    public ConnectionPage(ConnectionViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }
}
