using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class FileEntry
{
    [Key(0)]
    public required string Name { get; init; }

    [Key(1)]
    public required string FullPath { get; init; }

    [Key(2)]
    public bool IsDirectory { get; init; }

    /// <summary>Junction, symbolic link or other reparse point. Following it is still checked against the roots.</summary>
    [Key(3)]
    public bool IsReparsePoint { get; init; }

    [Key(4)]
    public long Size { get; init; }

    /// <summary>Raw <see cref="System.IO.FileAttributes"/> value.</summary>
    [Key(5)]
    public int Attributes { get; init; }

    [Key(6)]
    public DateTime CreationTimeUtc { get; init; }

    [Key(7)]
    public DateTime LastWriteTimeUtc { get; init; }
}
