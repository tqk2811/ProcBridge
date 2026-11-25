using ProcBridge.Agent.Wpf.Core.ViewModels;

namespace ProcBridge.Agent.Wpf.Core.Interfaces;

/// <summary>Windows the ViewModels open; the WPF app implements it, tests fake it.</summary>
public interface IDialogService
{
    /// <summary>Shows the pairing dialog modally; returns once it closed.</summary>
    void ShowPairing(PairingViewModel viewModel);

    /// <summary>Shows the Explorer profile editor modally; true when the profile was saved.</summary>
    bool EditExplorerPolicy(ExplorerPolicyViewModel viewModel);

    /// <summary>Asks a yes/no question.</summary>
    bool Confirm(string title, string message);

    /// <summary>Lets the user pick a folder; null when cancelled.</summary>
    string? PickFolder(string title);
}
