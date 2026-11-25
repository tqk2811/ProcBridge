using ProcBridge.Launcher.Models;
using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Launcher.Wpf.Services;
using ProcBridge.Launcher.Wpf.Tests.Fakes;
using ProcBridge.Launcher.Wpf.ViewModels;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Tests;

/// <summary>Relay part of a profile: only the relay URL; the HostId is the fingerprint of the paired machine.</summary>
[TestClass]
public sealed class RelayProfileTests
{
    private static readonly PairedMachine MachineB = FakeRegistry.Machine("B", "FING-ERPR-INTB", "10.0.0.2:5000");

    private static LaunchProfile Sample() => new()
    {
        Name = "remote",
        MachineFingerprint = MachineB.Fingerprint,
        ShareName = "src",
        ShareRoot = @"D:\src",
        Mappings = [new FolderMapping { VirtualRoot = @"V:\Proj", RemotePath = @"D:\src\proj" }],
        Program = "cmd.exe",
        UseRelay = true,
        RelayUrl = "https://relay.example/",
    };

    [TestMethod]
    public void OptionsFactory_WithRelay_SetsRelayAndKeepsTheFingerprint()
    {
        LauncherOptions options = new LaunchOptionsFactory(@"C:\pb").Create(Sample(), MachineB);

        Assert.IsNotNull(options.Relay);
        Assert.AreEqual(new Uri("https://relay.example/"), options.Relay.Url);
        Assert.AreEqual(MachineB.Fingerprint, options.AgentFingerprint);
        Assert.AreEqual("relay.example", options.AgentHost);
    }

    [TestMethod]
    public void OptionsFactory_RelayOff_UsesTheLanAddress()
    {
        LauncherOptions options = new LaunchOptionsFactory(@"C:\pb").Create(Sample() with { UseRelay = false }, MachineB);

        Assert.IsNull(options.Relay);
        Assert.AreEqual("10.0.0.2", options.AgentHost);
    }

    [TestMethod]
    public void OptionsFactory_RelayWithBadUrl_IsRefused()
    {
        LaunchProfile profile = Sample() with { RelayUrl = "relay" };

        var e = Assert.ThrowsExactly<ProfileValidationException>(() => new LaunchOptionsFactory(@"C:\pb").Create(profile, MachineB));

        Assert.HasCount(1, e.Errors);
    }

    [TestMethod]
    public void OptionsFactory_MachinePairedThroughTheRelay_UsesItsRelayWhenTheProfileHasNone()
    {
        PairedMachine relayPaired = FakeRegistry.Machine("B", "FING-ERPR-INTB", "relay https://paired.example/");
        var factory = new LaunchOptionsFactory(@"C:\pb");

        LauncherOptions relayOff = factory.Create(Sample() with { UseRelay = false }, relayPaired);
        LauncherOptions blankUrl = factory.Create(Sample() with { RelayUrl = " " }, relayPaired);
        LauncherOptions ownRelay = factory.Create(Sample(), relayPaired);

        Assert.AreEqual(new Uri("https://paired.example/"), relayOff.Relay!.Url);
        Assert.AreEqual(new Uri("https://paired.example/"), blankUrl.Relay!.Url);
        Assert.AreEqual(new Uri("https://relay.example/"), ownRelay.Relay!.Url);
    }

    [TestMethod]
    public void ProfileViewModel_PickingAMachinePairedThroughTheRelay_TurnsTheRelayOnWithItsUrl()
    {
        PairedMachine relayPaired = FakeRegistry.Machine("R", "RELA-YPAI-RED0", "relay https://paired.example/");
        PairedMachine[] machines = [MachineB, relayPaired];
        var viewModel = new ProfileViewModel(Sample() with { UseRelay = false, RelayUrl = string.Empty }, new FakeDialogs(),
            fingerprint => machines.FirstOrDefault(x => x.Fingerprint == fingerprint));
        Assert.IsFalse(viewModel.UseRelay, "A LAN machine keeps the relay off.");

        viewModel.MachineFingerprint = relayPaired.Fingerprint;

        Assert.IsTrue(viewModel.UseRelay);
        Assert.AreEqual("https://paired.example/", viewModel.RelayUrl);
        Assert.IsNotNull(new LaunchOptionsFactory(@"C:\pb").Create(viewModel.ToModel(), relayPaired).Relay);
    }

    [TestMethod]
    public void ProfileViewModel_LoadingAProfileOfARelayMachine_DoesNotChangeIt()
    {
        PairedMachine relayPaired = FakeRegistry.Machine("R", "RELA-YPAI-RED0", "relay https://paired.example/");
        LaunchProfile saved = Sample() with { MachineFingerprint = relayPaired.Fingerprint, UseRelay = false, RelayUrl = string.Empty };
        var changes = new List<string?>();

        var loaded = new ProfileViewModel(saved, new FakeDialogs(), _ => relayPaired);
        loaded.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.IsFalse(loaded.ToModel().UseRelay);
        Assert.AreEqual(string.Empty, loaded.ToModel().RelayUrl);
        Assert.IsEmpty(changes);
        // The run still goes through the machine's relay.
        Assert.AreEqual(new Uri("https://paired.example/"), new LaunchOptionsFactory(@"C:\pb").Create(loaded.ToModel(), relayPaired).Relay!.Url);
    }

    [TestMethod]
    public void ProfileViewModel_PickingTheRelayMachine_KeepsAnOwnRelay()
    {
        PairedMachine relayPaired = FakeRegistry.Machine("R", "RELA-YPAI-RED0", "relay https://paired.example/");
        PairedMachine[] machines = [MachineB, relayPaired];
        var viewModel = new ProfileViewModel(Sample(), new FakeDialogs(), fingerprint => machines.FirstOrDefault(x => x.Fingerprint == fingerprint));

        viewModel.MachineFingerprint = relayPaired.Fingerprint;

        Assert.AreEqual("https://relay.example/", viewModel.RelayUrl);
    }

    [TestMethod]
    public void ProfileViewModel_RoundTripsRelayFields()
    {
        var viewModel = new ProfileViewModel(Sample(), new FakeDialogs(), _ => MachineB);
        viewModel.RelayUrl = " https://other.example/ ";

        LaunchProfile saved = viewModel.ToModel();

        Assert.IsTrue(saved.UseRelay);
        Assert.AreEqual("https://other.example/", saved.RelayUrl);
    }
}
