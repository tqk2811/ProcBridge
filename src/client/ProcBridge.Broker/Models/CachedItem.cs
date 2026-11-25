using ProcBridge.Protocol.Models;

namespace ProcBridge.Broker.Models;

/// <summary>A remote file or directory present in the local cache.</summary>
/// <param name="Entry">Metadata on machine B the cached copy matches.</param>
/// <param name="LocalPath">Full DOS path of the cached file, or of the placeholder folder for a directory.</param>
/// <param name="RangeId">Non-zero when the copy is a sparse file still downloaded by ranges (M8): reads go through
/// <see cref="FileCache.FetchRangeAsync"/> with this id first.</param>
public sealed record CachedItem(FsEntry Entry, string LocalPath, uint RangeId = 0);
