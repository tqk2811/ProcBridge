using ProcBridge.Crypto.Identity;
using ProcBridge.Explorer.Client;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Services;

/// <summary>Opens encrypted Remote Explorers with this machine's identity; borrows the identity key and the store.</summary>
public sealed class SecureRemoteExplorerFactory : IRemoteExplorerFactory
{
    private static readonly TransportOptions Transport = new() { ConnectTimeout = TimeSpan.FromSeconds(10), RequestTimeout = TimeSpan.FromSeconds(30) };

    private readonly IdentityKey _identity;
    private readonly PairedMachineStore _pairedMachines;

    public SecureRemoteExplorerFactory(IdentityKey identity, PairedMachineStore pairedMachines)
    {
        _identity = identity;
        _pairedMachines = pairedMachines;
    }

    public IRemoteExplorer Create(PairedMachine machine)
    {
        (string host, int port) = LauncherCommandLine.ParseAgent(machine.Address);
        var factory = new SecureConnectionFactory(new TcpStreamConnector(host, port), _identity, _pairedMachines, machine.PublicKey, Transport);
        return new SecureRemoteExplorer(factory, new ExplorerClient(factory, Transport, "ProcBridge.Launcher.Wpf"));
    }
}
