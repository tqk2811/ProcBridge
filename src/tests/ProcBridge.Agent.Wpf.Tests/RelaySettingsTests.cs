using ProcBridge.Agent.Wpf.Core.Models;
using ProcBridge.Agent.Wpf.Core.ViewModels;
using ProcBridge.Agent.Wpf.Tests.Fakes;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Agent.Wpf.Tests;

/// <summary>Relay section of the Agent app: passed to the host on start, token saved only DPAPI-protected.</summary>
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
    public void Start_WithRelay_PassesItToTheHost_AndSavesTheTokenProtected()
    {
        string folder = NewShareFolder();
        try
        {
            _settings.Settings = new AgentUiSettings { Shares = [new ShareSettings { Name = "src", Path = folder }] };
            MainViewModel viewModel = Create();
            viewModel.RelayUrl = " https://relay.example/ ";
            viewModel.RelayToken = "pbd_bimat";

            viewModel.Start();

            Assert.IsNull(viewModel.ErrorText);
            Assert.IsNotNull(_host.StartedRelay);
            Assert.AreEqual(new Uri("https://relay.example/"), _host.StartedRelay.BaseUrl);
            Assert.AreEqual("pbd_bimat", _host.StartedRelay.DeviceToken);
            Assert.AreEqual("https://relay.example/", _settings.Settings.RelayUrl);
            Assert.IsNotNull(_settings.Settings.RelayTokenProtected);
            Assert.DoesNotContain("pbd_bimat", _settings.Settings.RelayTokenProtected);
            Assert.AreEqual("relay: đã kết nối", viewModel.RelayState);

            // Read back by a new window.
            Assert.AreEqual("pbd_bimat", Create().RelayToken);
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
            Assert.IsNull(_settings.Settings.RelayTokenProtected);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void Start_WithRelayButNoTokenOrBadUrl_DoesNotStart()
    {
        string folder = NewShareFolder();
        try
        {
            _settings.Settings = new AgentUiSettings { Shares = [new ShareSettings { Name = "src", Path = folder }] };
            MainViewModel viewModel = Create();
            viewModel.RelayUrl = "https://relay.example/";

            viewModel.Start();
            Assert.IsFalse(viewModel.IsRunning);
            Assert.IsNotNull(viewModel.ErrorText);

            viewModel.RelayUrl = "ftp://relay.example/";
            viewModel.RelayToken = "pbd_x";
            viewModel.Start();
            Assert.IsFalse(viewModel.IsRunning);
            Assert.AreEqual(0, _host.StartCalls);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
