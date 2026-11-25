using System.Windows;
using Microsoft.Win32;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.ViewModels;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Views;

/// <summary>Production <see cref="IUserDialogs"/>: message boxes, the folder dialog and the Remote Explorer picker window.</summary>
public sealed class WpfUserDialogs : IUserDialogs
{
    private readonly Window _owner;
    private readonly IRemoteExplorerFactory _explorers;

    public WpfUserDialogs(Window owner, IRemoteExplorerFactory explorers)
    {
        _owner = owner;
        _explorers = explorers;
    }

    public bool Confirm(string title, string message)
        => MessageBox.Show(_owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowError(string title, string message)
        => MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public string? PickLocalFolder(string? initialPath)
    {
        var dialog = new OpenFolderDialog();
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            dialog.InitialDirectory = initialPath;
        return dialog.ShowDialog(_owner) == true ? dialog.FolderName : null;
    }

    public string? PickRemoteFolder(PairedMachine machine, string? initialPath)
    {
        IRemoteExplorer explorer = _explorers.Create(machine);
        try
        {
            var picker = new RemoteFolderPickerViewModel(machine, explorer, initialPath);
            var window = new RemoteFolderPickerWindow { Owner = _owner, DataContext = picker };
            _ = picker.Tree.LoadAsync();
            return window.ShowDialog() == true ? picker.Tree.SelectedPath : null;
        }
        finally
        {
            // Fire and forget: closing the pooled connections must not block the UI thread.
            _ = Task.Run(() => explorer.DisposeAsync().AsTask());
        }
    }
}
