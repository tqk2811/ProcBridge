using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>Renames or moves a file or directory inside the share. Response: <see cref="FsStatResponse"/> of the new name.</summary>
[MessagePackObject]
public sealed class FsRenameRequest
{
    [Key(0)] public required string Path { get; init; }
    [Key(1)] public required string NewPath { get; init; }
    /// <summary>Replace an existing file at <see cref="NewPath"/> (never a directory).</summary>
    [Key(2)] public bool ReplaceIfExists { get; init; }
}
