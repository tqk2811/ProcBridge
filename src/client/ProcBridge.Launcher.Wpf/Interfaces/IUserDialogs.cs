using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Interfaces;

/// <summary>Windows the view models open; faked in tests.</summary>
public interface IUserDialogs
{
    /// <summary>Asks a yes/no question; true when the user confirms.</summary>
    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    /// <summary>Folder on machine A; null when cancelled.</summary>
    string? PickLocalFolder(string? initialPath);

    /// <summary>Folder on <paramref name="machine"/> picked in the Remote Explorer tree; null when cancelled.</summary>
    string? PickRemoteFolder(PairedMachine machine, string? initialPath);
}
