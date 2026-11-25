using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class FileStatResponse : ExplorerResponse
{
    [Key(2)]
    public FileEntry? Entry { get; set; }
}
