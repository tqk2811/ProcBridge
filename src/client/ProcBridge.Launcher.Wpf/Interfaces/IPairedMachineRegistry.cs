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
}
