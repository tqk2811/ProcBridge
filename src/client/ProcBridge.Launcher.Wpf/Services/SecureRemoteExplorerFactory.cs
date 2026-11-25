using ProcBridge.Crypto.Identity;
using ProcBridge.Explorer.Client;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Transport.Relay;
using ProcBridge.Transport.Relay.Models;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Interfaces;
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
        // A machine paired through the relay has no LAN address: reach it through that relay by its HostId (fingerprint).
        IStreamConnector connector;
        if (RelayPairingConnector.TryParseRelayAddress(machine.Address) is { } relayUrl)
            connector = new RelayStreamConnector(new RelayEndpoint(relayUrl), machine.Fingerprint);
        else
        {
            (string host, int port) = LauncherCommandLine.ParseAgent(machine.Address);
            connector = new TcpStreamConnector(host, port);
        }
        var factory = new SecureConnectionFactory(connector, _identity, _pairedMachines, machine.PublicKey, Transport);
        return new SecureRemoteExplorer(factory, new ExplorerClient(factory, Transport, "ProcBridge.Launcher.Wpf"));
    }
}
