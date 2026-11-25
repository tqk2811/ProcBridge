using System.Security.Cryptography;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Agent.Models;
using ProcBridge.Agent.Wpf.Core.Models;
using ProcBridge.Agent.Wpf.Core.ViewModels;
using ProcBridge.Agent.Wpf.Tests.Fakes;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Agent.Wpf.Tests;

[TestClass]
public sealed class MainViewModelTests
{
    private readonly FakeAgentHost _host = new();
    private readonly PairedMachineStore _paired = new();
    private readonly ExplorerPolicyStore _policies = new();
    private readonly MemorySettingsStore _settings = new();
    private readonly FakeDialogService _dialogs = new();
    private DateTime _now = new(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc);

    private MainViewModel Create() => new(_host, _paired, _policies, _settings, _dialogs, isElevated: false, () => _now, new Random(1));

    private PairedMachine Pair(string name) => _paired.Add(RandomNumberGenerator.GetBytes(32), name);

    [TestMethod]
    public void Revoke_Confirmed_RemovesFromStoreAndDropsTheProfile()
    {
        PairedMachine machine = Pair("a");
        _policies.Set(machine.Fingerprint, new ExplorerPolicy { Permissions = ExplorerPermissions.FileRead });
        MainViewModel viewModel = Create();

        viewModel.RevokeCommand.Execute(viewModel.PairedMachines.Single());

        Assert.AreEqual(1, _dialogs.ConfirmCalls);
        Assert.IsNull(_paired.Find(machine.Fingerprint));
        Assert.AreEqual(ExplorerPermissions.None, _policies.Get(machine.Fingerprint).Permissions);
        Assert.IsEmpty(viewModel.PairedMachines);
    }

    [TestMethod]
    public void Revoke_NotConfirmed_KeepsTheMachine()
    {
        PairedMachine machine = Pair("a");
        _dialogs.ConfirmResult = false;
        MainViewModel viewModel = Create();

        viewModel.Revoke(viewModel.PairedMachines.Single());

        Assert.IsNotNull(_paired.Find(machine.Fingerprint));
        Assert.HasCount(1, viewModel.PairedMachines);
    }

    [TestMethod]
    public void PairingDialogClosedWithoutDecision_RejectsTheRequest()
    {
        MainViewModel viewModel = Create();
        var prompt = new FakePairingPrompt();

        _host.RaisePairing(prompt);

        Assert.HasCount(1, _dialogs.PairingDialogs);
        Assert.AreEqual(1, prompt.RejectCalls);
        Assert.AreEqual(0, prompt.AcceptCalls);
    }

    [TestMethod]
    public void PairingWithWrongFingerprintChoice_IsNeverAccepted()
    {
        _dialogs.OnPairing = dialog =>
        {
            string correct = FakePairingPrompt.Fingerprint.Split('-')[dialog.HiddenGroupNumber - 1];
            dialog.Choose(dialog.Choices.First(x => x != correct));
            dialog.AcceptCommand.Execute(null);
        };
        MainViewModel viewModel = Create();
        var prompt = new FakePairingPrompt();

        _host.RaisePairing(prompt);

        Assert.AreEqual(0, prompt.AcceptCalls);
        Assert.AreEqual(1, prompt.RejectCalls);
    }

    [TestMethod]
    public void SecondPairingRequestWhileTheDialogIsOpen_IsRejected()
    {
        var second = new FakePairingPrompt();
        _dialogs.OnPairing = _ =>
        {
            if (_dialogs.PairingDialogs.Count == 1)
                _host.RaisePairing(second);
        };
        MainViewModel viewModel = Create();

        _host.RaisePairing(new FakePairingPrompt());

        Assert.HasCount(1, _dialogs.PairingDialogs);
        Assert.AreEqual(1, second.RejectCalls);
    }

    [TestMethod]
    public async Task SuccessfulPairing_ReloadsTheListAndTurnsPairingOff()
    {
        _dialogs.OnPairing = dialog =>
        {
            dialog.Choose(FakePairingPrompt.Fingerprint.Split('-')[dialog.HiddenGroupNumber - 1]);
            dialog.Accept();
        };
        MainViewModel viewModel = Create();
        viewModel.AllowPairing = true;
        var prompt = new FakePairingPrompt();
        _host.RaisePairing(prompt);
        Assert.AreEqual(1, prompt.AcceptCalls);

        Pair("machine-a"); // What the transport does once machine A confirmed the code.
        prompt.CompletionSource.SetResult(true);

        for (int i = 0; i < 100 && viewModel.AllowPairing; i++)
            await Task.Delay(20);
        Assert.IsFalse(viewModel.AllowPairing);
        Assert.IsFalse(_host.AllowPairing);
        Assert.HasCount(1, viewModel.PairedMachines);
    }

    [TestMethod]
    public void Start_WithInvalidPort_DoesNotStartTheHost()
    {
        MainViewModel viewModel = Create();
        viewModel.Port = "70000";

        viewModel.StartCommand.Execute(null);

        Assert.AreEqual(0, _host.StartCalls);
        Assert.IsFalse(viewModel.IsRunning);
        Assert.IsNotNull(viewModel.ErrorText);
    }

    [TestMethod]
    public void Start_WithMissingShareFolder_DoesNotStartTheHost()
    {
        _settings.Settings = new AgentUiSettings { Shares = [new ShareSettings { Name = "src", Path = @"Z:\does\not\exist\" + Guid.NewGuid() }] };
        MainViewModel viewModel = Create();

        viewModel.Start();

        Assert.AreEqual(0, _host.StartCalls);
        Assert.IsNotNull(viewModel.ErrorText);
    }

    [TestMethod]
    public async Task StartAndStop_PassSharesWithTheWriteFlag_AndSaveTheSettings()
    {
        string folder = Directory.CreateTempSubdirectory("pb-agent-wpf-").FullName;
        try
        {
            _dialogs.FolderToPick = folder;
            MainViewModel viewModel = Create();
            viewModel.AddShareCommand.Execute(null);
            viewModel.Shares.Single().AllowWrite = true;
            viewModel.Port = "47999";

            viewModel.Start();

            Assert.IsTrue(viewModel.IsRunning);
            Assert.AreEqual(47999, _host.Port);
            Assert.IsTrue(_host.StartedShares!.Single().AllowWrite);
            Assert.AreEqual(47999, _settings.Settings.Port);
            Assert.AreEqual(folder, _settings.Settings.Shares.Single().Path);
            Assert.IsTrue(_settings.Settings.Shares.Single().AllowWrite);

            viewModel.Shares.Single().Name = "renamed";
            Assert.IsTrue(viewModel.SharesNeedRestart);

            await viewModel.StopCommand.ExecuteAsync();
            Assert.IsFalse(viewModel.IsRunning);
            Assert.IsFalse(_host.IsRunning);
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [TestMethod]
    public void Refresh_GroupsConnections_AndRecordsLastSeen()
    {
        PairedMachine machine = Pair("a");
        MainViewModel viewModel = Create();
        _host.Connections.Add(new AgentConnectionInfo { RemoteAddress = "10.0.0.2:5000", PeerFingerprint = machine.Fingerprint, ConnectedAtUtc = _now });
        _host.Connections.Add(new AgentConnectionInfo { RemoteAddress = "10.0.0.2:5001", PeerFingerprint = machine.Fingerprint, ConnectedAtUtc = _now });
        _host.Connections.Add(new AgentConnectionInfo { RemoteAddress = "10.0.0.9:6000", ConnectedAtUtc = _now });

        viewModel.Refresh();

        Assert.HasCount(2, viewModel.Connections);
        ConnectedMachineViewModel connected = viewModel.Connections.Single(x => x.Fingerprint == machine.Fingerprint);
        Assert.AreEqual(2, connected.ConnectionCount);
        Assert.AreEqual("10.0.0.2", connected.RemoteAddresses);
        Assert.AreEqual(2, viewModel.PairedMachines.Single().ConnectionCount);
        Assert.AreEqual(_now, _settings.Settings.LastSeenUtc[machine.Fingerprint]);

        _host.Connections.Clear();
        _now = _now.AddHours(1);
        viewModel.Refresh();

        Assert.IsEmpty(viewModel.Connections);
        Assert.AreEqual(_now.AddHours(-1), viewModel.PairedMachines.Single().LastSeenUtc);
    }

    [TestMethod]
    public void EditExplorerPolicy_OpensTheEditorForThatMachine()
    {
        PairedMachine machine = Pair("a");
        MainViewModel viewModel = Create();

        viewModel.EditExplorerPolicyCommand.Execute(viewModel.PairedMachines.Single());

        Assert.AreEqual(machine.Fingerprint, _dialogs.LastPolicyEditor?.Fingerprint);
    }
}
