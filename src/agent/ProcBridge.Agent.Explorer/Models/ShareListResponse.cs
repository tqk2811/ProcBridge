using MessagePack;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>
/// The shares any paired machine A may open with <c>Hello</c>. Never refused: knowing a share is what a run needs anyway,
/// and the Explorer profile only governs browsing outside the shares.
/// </summary>
[MessagePackObject]
public sealed class ShareListResponse : ExplorerResponse
{
    [Key(2)]
    public IReadOnlyList<ShareEntry> Shares { get; set; } = [];
}
