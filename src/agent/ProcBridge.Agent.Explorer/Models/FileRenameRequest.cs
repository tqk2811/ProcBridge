using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>Renames or moves a file or directory inside the allowed roots. Never overwrites.</summary>
[MessagePackObject]
public sealed class FileRenameRequest
{
    [Key(0)]
    public required string Path { get; init; }

    [Key(1)]
    public required string NewPath { get; init; }
}
