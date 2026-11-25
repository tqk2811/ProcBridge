using ProcBridge.Agent.Wpf.Core.Models;
using ProcBridge.Agent.Wpf.Core.ViewModels;
using ProcBridge.Agent.Wpf.Tests.Fakes;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Agent.Wpf.Tests;

/// <summary>Relay section of the Agent app: only the URL, passed to the host on start and saved (the relay has no accounts).</summary>
[TestClass]
public sealed class RelaySettingsTests
{
    private readonly FakeAgentHost _host = new();
    private readonly MemorySettingsStore _settings = new();

    private MainViewModel Create() => new(_host, new PairedMachineStore(), new ExplorerPolicyStore(), _settings, new FakeDialogService(), isElevated: false);

    private static string NewShareFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "pb-agent-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [TestMethod]
    public void Start_WithRelay_PassesTheUrlToTheHost_AndSavesIt()
    {
        string folder = NewShareFolder();
        try
        {
            _settings.Settings = new AgentUiSettings { Shares = [new ShareSettings { Name = "src", Path = folder }] };
            MainViewModel viewModel = Create();
            viewModel.RelayUrl = " https://relay.example/ ";

            viewModel.Start();

            Assert.IsNull(viewModel.ErrorText);
            Assert.IsNotNull(_host.StartedRelay);
            Assert.AreEqual(new Uri("https://relay.example/"), _host.StartedRelay.BaseUrl);
            Assert.AreEqual("https://relay.example/", _settings.Settings.RelayUrl);
            Assert.AreEqual("relay: đã kết nối", viewModel.RelayState);
            Assert.AreEqual("https://relay.example/", Create().RelayUrl);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task RelayPairingCode_ShownWhilePairingIsOnThroughTheRelay()
    {
        string folder = NewShareFolder();
        try
        {
            _settings.Settings = new AgentUiSettings { Shares = [new ShareSettings { Name = "src", Path = folder }] };
            _host.RelayPairingCodeValue = "1234-5678";
            MainViewModel viewModel = Create();
            viewModel.RelayUrl = "https://relay.example/";
            viewModel.Start();
            Assert.IsNull(viewModel.RelayPairingCode, "Pairing off: no code.");

            viewModel.AllowPairing = true;
            Assert.AreEqual("1234-5678", viewModel.RelayPairingCode);

            // The code rotates after each use or expiry: the window timer picks the new one up.
            _host.RelayPairingCodeValue = "8765-4321";
            viewModel.Refresh();
            Assert.AreEqual("8765-4321", viewModel.RelayPairingCode);

            viewModel.AllowPairing = false;
            Assert.IsNull(viewModel.RelayPairingCode);
            viewModel.AllowPairing = true;
            await viewModel.StopAsync();
            Assert.IsNull(viewModel.RelayPairingCode, "Stopped: no code.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void Start_WithoutRelayUrl_ServesLanOnly()
    {
        string folder = NewShareFolder();
        try
        {
            _settings.Settings = new AgentUiSettings { Shares = [new ShareSettings { Name = "src", Path = folder }] };
            MainViewModel viewModel = Create();

            viewModel.Start();

            Assert.IsTrue(viewModel.IsRunning);
            Assert.IsNull(_host.StartedRelay);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void Start_WithBadRelayUrl_DoesNotStart()
    {
        string folder = NewShareFolder();
        try
        {
            _settings.Settings = new AgentUiSettings { Shares = [new ShareSettings { Name = "src", Path = folder }] };
            MainViewModel viewModel = Create();
            viewModel.RelayUrl = "ftp://relay.example/";

            viewModel.Start();

            Assert.IsFalse(viewModel.IsRunning);
            Assert.IsNotNull(viewModel.ErrorText);
            Assert.AreEqual(0, _host.StartCalls);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
