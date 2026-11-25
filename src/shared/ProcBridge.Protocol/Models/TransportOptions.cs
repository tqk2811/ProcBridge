namespace ProcBridge.Protocol.Models;

/// <summary>Limits and timeouts shared by both ends of the transport.</summary>
public sealed record TransportOptions
{
    /// <summary>Largest frame body (before protection). One read chunk plus header must fit.</summary>
    public int MaxFrameSize { get; init; } = 1024 * 1024 + 64 * 1024;

    /// <summary>Size of one read chunk; a file larger than this is split and fetched in parallel.</summary>
    public int ReadChunkSize { get; init; } = 256 * 1024;

    /// <summary>Number of parallel TCP connections the Broker keeps to one Agent.</summary>
    public int ConnectionCount { get; init; } = 4;

    /// <summary>Chunk requests in flight for one file download.</summary>
    public int MaxParallelChunks { get; init; } = 8;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times an idempotent request is resent after its connection broke.</summary>
    public int MaxRetries { get; init; } = 2;
}
