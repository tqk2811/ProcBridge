using ProcBridge.Launcher.Wpf.Tests.Fakes;
using ProcBridge.Launcher.Wpf.ViewModels;

namespace ProcBridge.Launcher.Wpf.Tests;

/// <summary>Paired machines and the pairing flow (fingerprints shown, 6 digit code typed by the user).</summary>
[TestClass]
public sealed class MachinesTests
{
    [TestMethod]
    public async Task Pairing_ShowsBothFingerprints_SendsTheCode_AndListsTheNewMachine()
    {
        var registry = new FakeRegistry();
        var machines = new MachinesViewModel(registry, new FakeDialogs());
        PairingViewModel pairing = machines.Pairing;
        Assert.AreEqual(registry.OwnFingerprint, pairing.OwnFingerprint);
        pairing.Address = "10.1.2.3:7000";

        Task started = pairing.StartCommand.ExecuteAsync();
        Assert.IsTrue(pairing.IsWaitingForCode);
        Assert.AreEqual(FakeRegistry.AgentFingerprint, pairing.AgentFingerprint);

        pairing.Code = "12a456";
        pairing.ConfirmCodeCommand.Execute(null);
        Assert.IsTrue(pairing.IsWaitingForCode, "a code that is not 6 digits is not sent");

        pairing.Code = "123456";
        pairing.ConfirmCodeCommand.Execute(null);
        await started;

        Assert.AreEqual("10.1.2.3", registry.LastHost);
        Assert.AreEqual(7000, registry.LastPort);
        Assert.AreEqual("123456", registry.CodeReceived);
        Assert.AreEqual("B-new", machines.Machines.Single().Name);
        Assert.IsFalse(pairing.IsBusy);
    }

    [TestMethod]
    public async Task Pairing_Cancel_StopsWaitingForTheCode()
    {
        var registry = new FakeRegistry();
        var machines = new MachinesViewModel(registry, new FakeDialogs());
        machines.Pairing.Address = "host";

        Task started = machines.Pairing.StartCommand.ExecuteAsync();
        machines.Pairing.CancelCommand.Execute(null);
        await started;

        Assert.IsEmpty(machines.Machines);
        Assert.IsFalse(machines.Pairing.IsBusy);
        StringAssert.Contains(machines.Pairing.StatusText, "huỷ");
    }

    [TestMethod]
    public void Revoke_AsksFirst()
    {
        var registry = new FakeRegistry();
        registry.Machines.Add(FakeRegistry.Machine("B", "FP"));
        var dialogs = new FakeDialogs();
        var machines = new MachinesViewModel(registry, dialogs) { };
        machines.SelectedMachine = machines.Machines[0];

        machines.RevokeCommand.Execute(null);
        Assert.HasCount(1, machines.Machines);

        dialogs.ConfirmAnswer = true;
        machines.RevokeCommand.Execute(null);
        Assert.IsEmpty(machines.Machines);
        Assert.IsEmpty(registry.Machines);
    }
}
