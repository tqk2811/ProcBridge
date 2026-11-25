using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Tests.Fakes;

internal sealed class FakeDialogs : IUserDialogs
{
    public bool ConfirmAnswer { get; set; }

    public List<string> Questions { get; } = [];

    public List<string> Errors { get; } = [];

    public string? LocalFolder { get; set; }

    public string? RemoteFolder { get; set; }

    public PairedMachine? RemoteFolderMachine { get; private set; }

    public bool Confirm(string title, string message)
    {
        Questions.Add(message);
        return ConfirmAnswer;
    }

    public void ShowError(string title, string message) => Errors.Add(message);

    public string? PickLocalFolder(string? initialPath) => LocalFolder;

    public string? PickRemoteFolder(PairedMachine machine, string? initialPath)
    {
        RemoteFolderMachine = machine;
        return RemoteFolder;
    }
}
