using ProcBridge.Broker.Models;
using ProcBridge.Protocol;

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

    /// <summary>Target command line, passed to CreateProcess as is (cmd.exe has its own quoting rules).</summary>
    public required string CommandLine { get; init; }
}
