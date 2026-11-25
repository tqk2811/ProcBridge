using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class DrivesResponse : ExplorerResponse
{
    /// <summary>Drives that hold at least one allowed root.</summary>
    [Key(2)]
    public IReadOnlyList<DriveEntry> Drives { get; set; } = [];

    /// <summary>The allowed roots, the starting points for browsing.</summary>
    [Key(3)]
    public IReadOnlyList<string> Roots { get; set; } = [];
}
