using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>Response of write operations that return nothing besides the status.</summary>
[MessagePackObject]
public sealed class ExplorerAckResponse : ExplorerResponse
{
}
