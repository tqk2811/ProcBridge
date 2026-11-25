using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Launcher.Wpf.Tests.Fakes;
using ProcBridge.Launcher.Wpf.ViewModels;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Tests;

/// <summary>Shares of machine B in the profile editor: picked from the list with their folder, folders checked at once.</summary>
[TestClass]
public sealed class ShareListTests
{
    private static readonly PairedMachine MachineB = FakeRegistry.Machine("B", "FING-ERPR-INTB", "10.0.0.2:5000");

    private static readonly ShareEntry Src = new() { Name = "src", RootPath = @"D:\src", Writable = true };

    private static readonly ShareEntry Tools = new() { Name = "tools", RootPath = @"E:\tools" };

    private sealed class Factory(FakeRemoteExplorer explorer) : IRemoteExplorerFactory
    {
        public PairedMachine? Machine { get; private set; }

        public IRemoteExplorer Create(PairedMachine machine)
        {
            Machine = machine;
            return explorer;
        }
    }

    private static (ProfileViewModel Profile, FakeRemoteExplorer Explorer, FakeDialogs Dialogs) Create(LaunchProfile profile)
    {
        var explorer = new FakeRemoteExplorer { Shares = [Tools, Src] };
        var dialogs = new FakeDialogs();
        return (new ProfileViewModel(profile, dialogs, _ => MachineB, new Factory(explorer)), explorer, dialogs);
    }

    private static LaunchProfile Empty() => new() { Name = "p", MachineFingerprint = MachineB.Fingerprint };

    [TestMethod]
    public async Task LoadShares_ListsThem_AndPickingOneFillsTheNameAndTheFolder()
    {
        (ProfileViewModel profile, FakeRemoteExplorer explorer, _) = Create(Empty());

        await profile.LoadSharesCommand.ExecuteAsync();

        CollectionAssert.AreEqual(new[] { "src", "tools" }, profile.Shares.Select(x => x.Name).ToArray());
        Assert.IsTrue(explorer.Disposed);
        StringAssert.Contains(profile.ShareStatusText, "2 chia sẻ");
        profile.SelectedShare = Tools;
        Assert.AreEqual("tools", profile.ShareName);
        Assert.AreEqual(@"E:\tools", profile.ShareRoot);
        // Typing a known name picks its folder too.
        profile.ShareName = "SRC";
        Assert.AreEqual(@"D:\src", profile.ShareRoot);
        Assert.AreSame(Src, profile.SelectedShare);
    }

    [TestMethod]
    public async Task LoadShares_CorrectsAWrongRootOfTheSavedShare()
    {
        (ProfileViewModel profile, _, _) = Create(Empty() with { ShareName = "src", ShareRoot = @"D:\wrong" });

        await profile.LoadSharesCommand.ExecuteAsync();

        Assert.AreEqual(@"D:\src", profile.ShareRoot);
        Assert.AreEqual(@"D:\src", profile.ToModel().ShareRoot);
    }

    [TestMethod]
    public async Task LoadShares_UnknownSavedShare_IsReported()
    {
        (ProfileViewModel profile, _, _) = Create(Empty() with { ShareName = "gone", ShareRoot = @"D:\gone" });

        await profile.LoadSharesCommand.ExecuteAsync();

        StringAssert.Contains(profile.ShareStatusText, "không có chia sẻ 'gone'");
        Assert.AreEqual(@"D:\gone", profile.ShareRoot);
    }

    [TestMethod]
    public async Task LoadShares_OnAnOldAgent_FallsBackToTypingTheShare()
    {
        (ProfileViewModel profile, FakeRemoteExplorer explorer, _) = Create(Empty() with { ShareName = "src", ShareRoot = @"D:\src" });
        explorer.SharesFailure = new ProtocolStatusException(NtStatus.NotImplemented, "Message ExplorerSysShares is not supported.");

        await profile.LoadSharesCommand.ExecuteAsync();

        Assert.IsEmpty(profile.Shares);
        StringAssert.Contains(profile.ShareStatusText, "bằng tay");
        Assert.AreEqual(@"D:\src", profile.ShareRoot);
    }

    [TestMethod]
    public void PickRemote_OutsideTheShare_IsRefusedAtOnce()
    {
        (ProfileViewModel profile, _, FakeDialogs dialogs) = Create(Empty() with
        {
            ShareName = "src",
            ShareRoot = @"D:\src",
            Mappings = [new FolderMapping { VirtualRoot = @"V:\p", RemotePath = @"D:\src\p" }],
        });
        dialogs.RemoteFolder = @"D:\other\p";

        profile.PickRemotePathCommand.Execute(profile.Mappings[0]);

        Assert.AreEqual(@"D:\src\p", profile.Mappings[0].RemotePath);
        StringAssert.Contains(dialogs.Errors.Single(), @"D:\other\p");
    }

    [TestMethod]
    public async Task PickRemote_WithoutShare_PicksTheShareHoldingTheFolder()
    {
        (ProfileViewModel profile, _, FakeDialogs dialogs) = Create(Empty() with { Mappings = [new FolderMapping { VirtualRoot = @"V:\t" }] });
        await profile.LoadSharesCommand.ExecuteAsync();
        dialogs.RemoteFolder = @"E:\tools\bin";

        profile.PickRemotePathCommand.Execute(profile.Mappings[0]);

        Assert.AreEqual("tools", profile.ShareName);
        Assert.AreEqual(@"E:\tools", profile.ShareRoot);
        Assert.AreEqual(@"E:\tools\bin", profile.Mappings[0].RemotePath);
        Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task PickRemote_WithoutShare_InNoShareOfMachineB_IsRefused()
    {
        (ProfileViewModel profile, _, FakeDialogs dialogs) = Create(Empty() with { Mappings = [new FolderMapping { VirtualRoot = @"V:\t" }] });
        await profile.LoadSharesCommand.ExecuteAsync();
        dialogs.RemoteFolder = @"C:\Windows";

        profile.PickRemotePathCommand.Execute(profile.Mappings[0]);

        Assert.AreEqual(string.Empty, profile.Mappings[0].RemotePath);
        StringAssert.Contains(dialogs.Errors.Single(), "không nằm trong chia sẻ nào");
    }

    [TestMethod]
    public async Task ChangingTheMachine_ClearsTheSharesOfThePreviousOne()
    {
        (ProfileViewModel profile, _, _) = Create(Empty());
        await profile.LoadSharesCommand.ExecuteAsync();
        profile.ShareName = "src";
        Assert.IsNotNull(profile.SelectedShare);

        profile.MachineFingerprint = "OTHE-RMAC-HINE";

        Assert.IsEmpty(profile.Shares);
        Assert.IsNull(profile.ShareStatusText);
        Assert.IsNull(profile.SelectedShare);
    }

    [TestMethod]
    public async Task LoadShares_AnswerArrivingAfterTheMachineChanged_IsDropped()
    {
        (ProfileViewModel profile, FakeRemoteExplorer explorer, _) = Create(Empty());
        explorer.SharesGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task loading = profile.LoadSharesAsync();

        profile.MachineFingerprint = "OTHE-RMAC-HINE";
        explorer.SharesGate.SetResult();
        await loading;

        Assert.IsEmpty(profile.Shares);
        Assert.IsNull(profile.ShareStatusText);
        Assert.AreEqual(string.Empty, profile.ShareRoot);
    }

    [TestMethod]
    public async Task ShareWithoutFolder_KeepsTheRootTypedByTheUser()
    {
        (ProfileViewModel profile, FakeRemoteExplorer explorer, _) = Create(Empty() with { ShareRoot = @"D:\typed" });
        explorer.Shares = [new ShareEntry { Name = "src", RootPath = string.Empty }];

        await profile.LoadSharesCommand.ExecuteAsync();

        Assert.AreEqual("src", profile.ShareName);
        Assert.AreEqual(@"D:\typed", profile.ShareRoot);
    }

    [TestMethod]
    public void LoadShares_WithoutExplorer_IsDisabled()
    {
        var profile = new ProfileViewModel(Empty(), new FakeDialogs(), _ => MachineB);

        Assert.IsFalse(profile.LoadSharesCommand.CanExecute(null));
    }
}
