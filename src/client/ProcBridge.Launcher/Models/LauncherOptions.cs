using ProcBridge.Broker.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Launcher.Models;

/// <summary>What the Launcher runs: one Broker session (Agent, share, virtual folders) and the target command.</summary>
public sealed record LauncherOptions
{
    public required string AgentHost { get; init; }

    public int AgentPort { get; init; } = ProtocolInfo.DefaultPort;

    public required string ShareName { get; init; }

    public required IReadOnlyList<VirtualMapping> Mappings { get; init; }

    /// <summary>Cache folder; null for the Broker's default (<c>%LOCALAPPDATA%\ProcBridge\cache\&lt;session&gt;</c>).</summary>
    public string? CacheRoot { get; init; }

    /// <summary>ProcBridge.Spawn of the target's architecture.</summary>
    public required string SpawnPath { get; init; }

    /// <summary>
    /// Sets the variables that keep shared build servers (MSBuild server and node reuse, VBCSCompiler) out of the run:
    /// a server started inside the tree would outlive the run and keep the Job (and the Broker) alive, or be reused by
    /// a later build outside ProcBridge with the virtual folders it saw.
    /// </summary>
    public bool DisableBuildServers { get; init; }

    /// <summary>Identity, paired Agents and the Agent's key; null only with <see cref="AllowPlaintextTransport"/>.</summary>
    public BrokerSecurity? Security { get; init; }

    /// <summary>Development only (<c>--plaintext</c>): talk unencrypted frames to an Agent started with <c>--plaintext</c>.</summary>
    public bool AllowPlaintextTransport { get; init; }

    /// <summary><c>--agent-fingerprint</c>: which paired Agent to trust when several were paired at the same address.</summary>
    public string? AgentFingerprint { get; init; }

    /// <summary><c>--data</c>: folder of the identity key and paired machine list; null for <c>%LOCALAPPDATA%\ProcBridge\launcher</c>.</summary>
    public string? DataDirectory { get; init; }
    /// <summary>Write-through (<see cref="BrokerOptions.WriteThrough"/>).</summary>
    public bool WriteThrough { get; init; }

    /// <summary>What a write-back does when the file changed on machine B (<see cref="BrokerOptions.ConflictPolicy"/>).</summary>
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.KeepBoth;

    /// <summary><c>--relay</c>: reach the Agent through a relay instead of <see cref="AgentHost"/>:<see cref="AgentPort"/>.</summary>
    public LauncherRelay? Relay { get; init; }

    /// <summary>Target command line, passed to CreateProcess as is (cmd.exe has its own quoting rules).</summary>
    public required string CommandLine { get; init; }

    /// <summary><c>--cwd</c>: starting folder of the target (it must exist on machine A); null to inherit the Launcher's.</summary>
    public string? WorkingDirectory { get; init; }
}
