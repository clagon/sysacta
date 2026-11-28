using System.Windows.Controls;
using Sysacta.App.ViewModels;

namespace Sysacta.App.Views;

public partial class AppsPage : Page
{
    public AppsPage(AppsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }
}
