using MessagePack;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>Asks which profile machine B keeps for the calling machine A (what the Remote Explorer may offer).</summary>
[MessagePackObject]
public sealed class ExplorerPolicyRequest
{
}
