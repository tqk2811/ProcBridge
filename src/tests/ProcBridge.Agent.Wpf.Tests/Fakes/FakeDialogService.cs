using ProcBridge.Agent.Wpf.Core.Interfaces;
using ProcBridge.Agent.Wpf.Core.ViewModels;

namespace ProcBridge.Agent.Wpf.Tests.Fakes;

internal sealed class FakeDialogService : IDialogService
{
    /// <summary>What the "user" does in the pairing dialog; null closes it without a decision.</summary>
    public Action<PairingViewModel>? OnPairing { get; set; }

    public List<PairingViewModel> PairingDialogs { get; } = [];

    public bool ConfirmResult { get; set; } = true;

    public int ConfirmCalls { get; private set; }

    public string? FolderToPick { get; set; }

    public ExplorerPolicyViewModel? LastPolicyEditor { get; private set; }

    public void ShowPairing(PairingViewModel viewModel)
    {
        PairingDialogs.Add(viewModel);
        OnPairing?.Invoke(viewModel);
    }

    public bool EditExplorerPolicy(ExplorerPolicyViewModel viewModel)
    {
        LastPolicyEditor = viewModel;
        return false;
    }

    public bool Confirm(string title, string message)
    {
        ConfirmCalls++;
        return ConfirmResult;
    }

    public string? PickFolder(string title) => FolderToPick;
}
