using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Tests.Fakes;

/// <summary>Paired machines in memory; pairing asks the code like the real client and pairs when it is "123456".</summary>
internal sealed class FakeRegistry : IPairedMachineRegistry
{
    public const string AgentFingerprint = "AGEN-TFIN-GERP-RINT";

    public List<PairedMachine> Machines { get; } = [];

    public string? LastHost { get; private set; }

    public int LastPort { get; private set; }

    public string? CodeReceived { get; private set; }

    public string OwnFingerprint => "OWNF-INGE-RPRI-NT00";

    public static PairedMachine Machine(string name, string fingerprint, string address = "10.0.0.2:5000")
        => new() { Name = name, Fingerprint = fingerprint, Address = address, PublicKey = new byte[32] };

    public IReadOnlyList<PairedMachine> List() => Machines.ToArray();

    public bool Revoke(string fingerprint) => Machines.RemoveAll(x => x.Fingerprint == fingerprint) > 0;

    public async Task<PairedMachine> PairAsync(string host, int port, Func<string, CancellationToken, Task<string>> askCode, CancellationToken cancellationToken)
    {
        LastHost = host;
        LastPort = port;
        CodeReceived = await askCode(AgentFingerprint, cancellationToken);
        if (CodeReceived != "123456")
            throw new InvalidOperationException("code mismatch");
        PairedMachine machine = Machine("B-new", AgentFingerprint, $"{host}:{port}");
        Machines.Add(machine);
        return machine;
    }

    public Uri? LastRelayUrl { get; private set; }

    public string? LastRelayCode { get; private set; }

    public async Task<PairedMachine> PairThroughRelayAsync(Uri relayUrl, string relayCode, Func<string, CancellationToken, Task<string>> askCode, CancellationToken cancellationToken)
    {
        LastRelayUrl = relayUrl;
        LastRelayCode = relayCode;
        CodeReceived = await askCode(AgentFingerprint, cancellationToken);
        if (CodeReceived != "123456")
            throw new InvalidOperationException("code mismatch");
        PairedMachine machine = Machine("B-relay", AgentFingerprint, $"relay {relayUrl}");
        Machines.Add(machine);
        return machine;
    }
}
