using System.Windows;
using Microsoft.Win32;
using ProcBridge.Agent.Wpf.Core.Interfaces;
using ProcBridge.Agent.Wpf.Core.ViewModels;
using ProcBridge.Agent.Wpf.Views;

namespace ProcBridge.Agent.Wpf.Services;

/// <summary>Opens the app's dialogs, owned by the main window (or centered on screen while it is hidden in the tray).</summary>
public sealed class WpfDialogService(Window owner) : IDialogService
{
    public void ShowPairing(PairingViewModel viewModel)
    {
        // A pairing request may arrive while the window sits in the tray: bring it back so the dialog is seen.
        if (!owner.IsVisible)
            owner.Show();
        if (owner.WindowState == WindowState.Minimized)
            owner.WindowState = WindowState.Normal;
        owner.Activate();
        new PairingWindow(viewModel) { Owner = owner }.ShowDialog();
    }

    public bool EditExplorerPolicy(ExplorerPolicyViewModel viewModel)
        => new ExplorerPolicyWindow(viewModel) { Owner = owner }.ShowDialog() == true;

    public bool Confirm(string title, string message)
        => MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }
}
