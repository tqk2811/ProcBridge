using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class FileReadResponse : ExplorerResponse
{
    [Key(2)]
    public byte[] Data { get; set; } = [];

    [Key(3)]
    public long FileLength { get; set; }
}
