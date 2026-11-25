using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class DirectoryListResponse : ExplorerResponse
{
    [Key(2)]
    public string? Path { get; set; }

    [Key(3)]
    public IReadOnlyList<FileEntry> Entries { get; set; } = [];
}
