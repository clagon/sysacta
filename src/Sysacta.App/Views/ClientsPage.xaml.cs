using System.Windows.Controls;
using Sysacta.App.ViewModels;

namespace Sysacta.App.Views;

public partial class ClientsPage : Page
{
    public ClientsPage(ClientsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }
}
