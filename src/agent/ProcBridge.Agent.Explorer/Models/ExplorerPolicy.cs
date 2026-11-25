using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>
/// Permission profile set by the owner of machine B for one paired machine A (Plan section 14).
/// The Agent is the side that decides; nothing machine A sends can widen it.
/// </summary>
[MessagePackObject]
public sealed class ExplorerPolicy
{
    /// <summary>Folders machine A may browse. Empty means no file access at all.</summary>
    [Key(0)]
    public IReadOnlyList<string> FileRoots { get; set; } = [];

    /// <summary>Registry keys machine A may browse, e.g. <c>HKCU\Software\Vendor</c>. Empty means none.</summary>
    [Key(1)]
    public IReadOnlyList<string> RegistryRoots { get; set; } = [];

    [Key(2)]
    public ExplorerPermissions Permissions { get; set; } = ExplorerPermissions.None;
}
