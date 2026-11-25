using System.Windows;
using ProcBridge.Agent.Wpf.Core.ViewModels;

namespace ProcBridge.Agent.Wpf.Views;

/// <summary>Explorer profile editor; DialogResult is true once the profile was saved.</summary>
public partial class ExplorerPolicyWindow : Window
{
    public ExplorerPolicyWindow(ExplorerPolicyViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += saved => DialogResult = saved;
    }
}
