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

    /// <summary>
    /// Least credit window for file data in flight per direction over the whole pool (see Transport.CreditWindow); the
    /// window starts at <see cref="ReadChunkSize"/> × <see cref="MaxParallelChunks"/> and follows the link between these bounds.
    /// </summary>
    public long DataWindowMin { get; init; } = 512 * 1024;

    /// <summary>Largest credit window, for fast links with a long round trip.</summary>
    public long DataWindowMax { get; init; } = 16 * 1024 * 1024;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Time the secure handshake (or the join of a session) may take once the stream is open; a silent peer is given up on after it.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>After a failed connect, how long a busy pool leaves that slot alone while another connection is alive.</summary>
    public TimeSpan ConnectBackoff { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A connection that received nothing for this long is probed with <see cref="Enums.MessageType.Ping"/>; a request that
    /// times out probes at once. <see cref="TimeSpan.Zero"/> turns keepalive off.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a probe waits for any frame from the peer before the connection is declared dead (its pending requests
    /// then fail as connection failures and idempotent ones are resent on another connection).
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How many times an idempotent request is resent after its connection broke.</summary>
    public int MaxRetries { get; init; } = 2;
}
