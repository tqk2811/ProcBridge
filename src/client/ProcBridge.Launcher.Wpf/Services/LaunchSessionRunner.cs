using System.Runtime.Versioning;
using ProcBridge.Broker.Models;
using ProcBridge.Crypto.Identity;
using ProcBridge.Launcher.Models;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Services;

/// <summary>
/// Runs through the same <see cref="LaunchSession"/> as the console Launcher, with the same trust rule: the Agent must
/// be paired (looked up by <see cref="LauncherOptions.AgentFingerprint"/>). Borrows the identity key and the store.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LaunchSessionRunner : ILaunchRunner
{
    private readonly IdentityKey _identity;
    private readonly PairedMachineStore _pairedMachines;

    public LaunchSessionRunner(IdentityKey identity, PairedMachineStore pairedMachines)
    {
        _identity = identity;
        _pairedMachines = pairedMachines;
    }

    public Task<int> RunAsync(LauncherOptions options, Action<string> outputLine, CancellationToken cancellationToken)
    {
        PairedMachine agent = (options.AgentFingerprint is null ? null : _pairedMachines.Find(options.AgentFingerprint))
            ?? throw new InvalidOperationException($"Máy B {options.AgentFingerprint} chưa được ghép cặp.");
        LauncherOptions secured = options with
        {
            Security = new BrokerSecurity { Identity = _identity, PairedMachines = _pairedMachines, AgentPublicKey = agent.PublicKey },
        };
        return new LaunchSession(secured, outputLine).RunAsync(cancellationToken);
    }
}
