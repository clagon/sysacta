using System.Windows.Controls;
using Sysacta.App.ViewModels;

namespace Sysacta.App.Views;

public partial class GeneralPage : Page
{
    public GeneralPage(GeneralViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }
}
