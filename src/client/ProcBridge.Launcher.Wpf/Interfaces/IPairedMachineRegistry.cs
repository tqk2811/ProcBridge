using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Interfaces;

/// <summary>Paired machines B of this machine A and the pairing of a new one.</summary>
public interface IPairedMachineRegistry
{
    /// <summary>Fingerprint of this machine, shown so the user of machine B can compare it.</summary>
    string OwnFingerprint { get; }

    IReadOnlyList<PairedMachine> List();

    bool Revoke(string fingerprint);

    /// <param name="askCode">Gets the Agent fingerprint; returns the 6 digit code the user read on machine B.</param>
    Task<PairedMachine> PairAsync(string host, int port, Func<string, CancellationToken, Task<string>> askCode, CancellationToken cancellationToken);

    /// <summary>
    /// Pairs with a machine B this machine only reaches through <paramref name="relayUrl"/>: finds it by the relay
    /// pairing code it shows (<paramref name="relayCode"/>, 8 digits), then the same exchange as <see cref="PairAsync"/>.
    /// </summary>
    Task<PairedMachine> PairThroughRelayAsync(Uri relayUrl, string relayCode, Func<string, CancellationToken, Task<string>> askCode, CancellationToken cancellationToken);
}
