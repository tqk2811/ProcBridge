using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>Folder picker of the profile editor: the Remote Explorer tree of one machine B. Borrows the explorer.</summary>
public sealed class RemoteFolderPickerViewModel
{
    public RemoteFolderPickerViewModel(PairedMachine machine, IRemoteExplorer explorer, string? initialPath)
    {
        Title = $"Chọn thư mục trên {machine.Name} ({machine.Address})";
        InitialPath = initialPath;
        Tree = new RemoteFolderTreeViewModel(explorer);
    }

    public string Title { get; }

    /// <summary>Folder chosen last time, shown as a hint.</summary>
    public string? InitialPath { get; }

    public RemoteFolderTreeViewModel Tree { get; }
}
