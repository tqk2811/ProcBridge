namespace ProcBridge.Broker.Models;

/// <summary>A write-back found the file changed on machine B (plan section 10).</summary>
/// <param name="RemotePath">Share path that was written back.</param>
/// <param name="ConflictPath">Share path machine A's content was saved to (KeepBoth policy).</param>
public sealed record WriteConflict(string RemotePath, string ConflictPath);
