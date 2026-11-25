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
    public void ProfileViewModel_RoundTripsRelayFields()
    {
        var viewModel = new ProfileViewModel(Sample(), new FakeDialogs(), _ => MachineB);
        viewModel.RelayUrl = " https://other.example/ ";

        LaunchProfile saved = viewModel.ToModel();

        Assert.IsTrue(saved.UseRelay);
        Assert.AreEqual("https://other.example/", saved.RelayUrl);
    }
}
