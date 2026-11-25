using ProcBridge.Launcher.Models;
using ProcBridge.Launcher.Wpf.Helpers;
using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Launcher.Wpf.Services;
using ProcBridge.Launcher.Wpf.Tests.Fakes;
using ProcBridge.Launcher.Wpf.ViewModels;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Tests;

/// <summary>Relay part of a profile: turned into <c>--relay</c> options, token kept DPAPI-protected.</summary>
[TestClass]
public sealed class RelayProfileTests
{
    private static readonly PairedMachine MachineB = FakeRegistry.Machine("B", "FING-ERPR-INTB", "10.0.0.2:5000");
    private static readonly Guid ShareId = Guid.Parse("0b6f6d2e-1111-4c4c-9a9a-123456789abc");

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
        RelayShareId = ShareId.ToString(),
        RelayTokenProtected = RelayTokenProtection.Protect("pbd_bimat"),
    };

    [TestMethod]
    public void OptionsFactory_WithRelay_SetsRelayAndKeepsTheFingerprint()
    {
        LauncherOptions options = new LaunchOptionsFactory(@"C:\pb").Create(Sample(), MachineB);

        Assert.IsNotNull(options.Relay);
        Assert.AreEqual(new Uri("https://relay.example/"), options.Relay.Url);
        Assert.AreEqual("pbd_bimat", options.Relay.DeviceToken);
        Assert.AreEqual(ShareId, options.Relay.ShareId);
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
    public void OptionsFactory_RelayWithBadFields_ListsEveryProblem()
    {
        LaunchProfile profile = Sample() with { RelayUrl = "relay", RelayShareId = "abc", RelayTokenProtected = null };

        var e = Assert.ThrowsExactly<ProfileValidationException>(() => new LaunchOptionsFactory(@"C:\pb").Create(profile, MachineB));

        Assert.HasCount(3, e.Errors);
    }

    [TestMethod]
    public void ProfileViewModel_RoundTripsRelayFields_TokenNeverInClear()
    {
        var viewModel = new ProfileViewModel(Sample(), new FakeDialogs(), _ => MachineB);
        Assert.AreEqual("pbd_bimat", viewModel.RelayToken);

        viewModel.RelayToken = "pbd_moi";
        LaunchProfile saved = viewModel.ToModel();

        Assert.IsTrue(saved.UseRelay);
        Assert.AreEqual(ShareId.ToString(), saved.RelayShareId);
        Assert.IsNotNull(saved.RelayTokenProtected);
        Assert.DoesNotContain("pbd_moi", saved.RelayTokenProtected);
        Assert.AreEqual("pbd_moi", RelayTokenProtection.Unprotect(saved.RelayTokenProtected));
    }
}
