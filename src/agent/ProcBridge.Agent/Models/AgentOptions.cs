using System.Net;
using ProcBridge.Protocol;

namespace ProcBridge.Agent.Models;

public sealed record AgentOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    /// <summary>TCP port; 0 picks a free one (tests).</summary>
    public int Port { get; init; } = ProtocolInfo.DefaultPort;

    public string ServerName { get; init; } = Environment.MachineName;

    public int MaxFrameSize { get; init; } = 1024 * 1024 + 64 * 1024;

    /// <summary>Largest chunk served by one FsRead; must leave room for the header inside a frame.</summary>
    public int MaxReadSize { get; init; } = 1024 * 1024;

    /// <summary>Requests handled at once on one connection; more wait, which applies back pressure on the socket.</summary>
    public int MaxConcurrentRequestsPerConnection { get; init; } = 16;

    /// <summary>Longest a response may take to be written; a client that stops reading is dropped after it.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time a new connection has to send Hello.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Buffer of the share watcher (<c>ReadDirectoryChangesW</c>); events beyond it are lost and reported as a rescan.</summary>
    public int WatchBufferSize { get; init; } = 64 * 1024;

    /// <summary>How long changes are collected before one notification carries them all.</summary>
    public TimeSpan WatchBatchDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Notifications queued for one connection; a client that falls further behind gets a rescan instead.</summary>
    public int NotificationQueueCapacity { get; init; } = 256;

    /// <summary>Most entries one FsTree walk returns before it reports <c>Truncated</c>.</summary>
    public int MaxTreeEntries { get; init; } = 500_000;

    /// <summary>Depth used when an FsTree request leaves it to the server.</summary>
    public int DefaultTreeDepth { get; init; } = 64;

    /// <summary>Unfinished FsTree walks one connection may hold at once.</summary>
    public int MaxTreeWalksPerConnection { get; init; } = 4;
}
