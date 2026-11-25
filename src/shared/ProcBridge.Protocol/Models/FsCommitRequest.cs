using MessagePack;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Protocol.Models;

/// <summary>
/// Ends an upload: the staging file, cut to <see cref="Size"/>, replaces the file at <see cref="Path"/> when its
/// version still is <see cref="ExpectedVersion"/> (the version machine A started from). Otherwise
/// <see cref="Policy"/> decides. Response: <see cref="FsCommitResponse"/>.
/// </summary>
[MessagePackObject]
public sealed class FsCommitRequest
{
    [Key(0)] public required string Path { get; init; }
    [Key(1)] public required string UploadId { get; init; }
    [Key(2)] public required long Size { get; init; }
    /// <summary>Null skips the check (the client knows no version, e.g. the file was gone).</summary>
    [Key(3)] public FsVersion? ExpectedVersion { get; init; }
    /// <summary>Last write time to give the file (FILETIME UTC), null for the time of the commit.</summary>
    [Key(4)] public long? LastWriteTime { get; init; }
    [Key(5)] public ConflictPolicy Policy { get; init; }
    /// <summary>
    /// Held handle (<see cref="FsOpenHandleRequest"/>, M8) of the writer whose content this is, 0 when unknown: a commit
    /// of a held file writes through it, in place. Handle ids are unique in the share, so the hold may live on another
    /// connection.
    /// </summary>
    [Key(6)] public ulong HoldId { get; init; }
}
