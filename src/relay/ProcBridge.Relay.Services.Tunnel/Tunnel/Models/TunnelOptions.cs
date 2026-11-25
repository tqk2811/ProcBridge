namespace ProcBridge.Relay.Services.Tunnel.Models;

/// <summary>Configuration section <c>Relay:Tunnel</c>.</summary>
public sealed class TunnelOptions
{
    public const string SectionName = "Relay:Tunnel";

    /// <summary>
    /// How long a session accepts NEW lanes after it is opened, and again after its last lane closes (grace to
    /// reconnect). While at least one lane is open, new lanes are always accepted.
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the first end of a lane waits for the other end before the relay drops it.</summary>
    public TimeSpan PairTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>After one direction of a lane closed, how long the other direction may take to finish its close.</summary>
    public TimeSpan CloseGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Also accept the lane ticket as <c>?ticket=</c> in the URL (old peers). Off by default: URLs are logged by proxies
    /// and servers, headers normally are not.
    /// </summary>
    public bool AllowTicketInQuery { get; set; }

    /// <summary>Receive buffer per lane direction (bytes).</summary>
    public int BufferSize { get; set; } = 64 * 1024;
}
