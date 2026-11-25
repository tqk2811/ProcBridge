using System.Windows;
using ProcBridge.Agent.Wpf.Core.ViewModels;

namespace ProcBridge.Agent.Wpf.Views;

/// <summary>Pairing dialog; closing it without a decision rejects the request.</summary>
public partial class PairingWindow : Window
{
    private readonly PairingViewModel _viewModel;

    public PairingWindow(PairingViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) =>
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.Reject();
        };
    }

    // May come from a background thread (the request timed out).
    private void OnCloseRequested() => Dispatcher.BeginInvoke(() =>
    {
        if (IsLoaded)
            Close();
    });
}
