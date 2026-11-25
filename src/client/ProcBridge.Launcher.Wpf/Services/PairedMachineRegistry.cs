using ProcBridge.Crypto.Identity;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Services;

/// <summary><see cref="IPairedMachineRegistry"/> over the store shared with the console Launcher; borrows both objects.</summary>
public sealed class PairedMachineRegistry : IPairedMachineRegistry
{
    private readonly IdentityKey _identity;
    private readonly PairedMachineStore _store;

    public PairedMachineRegistry(IdentityKey identity, PairedMachineStore store)
    {
        _identity = identity;
        _store = store;
    }

    public string OwnFingerprint => _identity.Fingerprint;

    public IReadOnlyList<PairedMachine> List() => _store.List();

    public bool Revoke(string fingerprint) => _store.Revoke(fingerprint);

    public Task<PairedMachine> PairAsync(string host, int port, Func<string, CancellationToken, Task<string>> askCode, CancellationToken cancellationToken)
        => new PairingClient(_identity, _store).PairAsync(new TcpStreamConnector(host, port), askCode, cancellationToken);
}
