using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>
/// The profile machine B keeps for the calling machine A. Informational only: machine A uses it to hide operations it
/// would be refused; the Agent still checks every request against the same profile.
/// </summary>
[MessagePackObject]
public sealed class ExplorerPolicyResponse : ExplorerResponse
{
    [Key(2)]
    public ExplorerPermissions Permissions { get; set; }

    [Key(3)]
    public IReadOnlyList<string> FileRoots { get; set; } = [];

    [Key(4)]
    public IReadOnlyList<string> RegistryRoots { get; set; } = [];
}
