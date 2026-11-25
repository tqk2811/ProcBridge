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

    /// <summary>Time a new connection has to send Hello.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
