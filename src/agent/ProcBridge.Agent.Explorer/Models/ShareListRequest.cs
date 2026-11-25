using MessagePack;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>Asks which shares the Agent serves (name and folder on machine B), to pick one for a run profile.</summary>
[MessagePackObject]
public sealed class ShareListRequest
{
}
