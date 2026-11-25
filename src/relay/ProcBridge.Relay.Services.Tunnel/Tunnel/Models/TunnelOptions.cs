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

    /// <summary>How long a host may take for the Noise handshake that proves its HostId on the control channel.</summary>
    public TimeSpan ControlHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Open control channels (proven or still in the handshake) per client address.</summary>
    public int MaxControlChannelsPerAddress { get; set; } = 16;

    /// <summary>Live sessions per HostId: someone who knows a HostId cannot make that host keep unlimited tickets.</summary>
    public int MaxSessionsPerHost { get; set; } = 64;

    /// <summary>Live sessions opened from one client address.</summary>
    public int MaxSessionsPerAddress { get; set; } = 64;

    /// <summary>Open or reserved lanes per client address (both ends count).</summary>
    public int MaxLanesPerAddress { get; set; } = 256;

    /// <summary>
    /// How long a pairing slot (host waiting to be found by its invite code) stays open. Short on purpose: the code is
    /// only 8 digits. The host opens a new slot with a new code when it expires.
    /// </summary>
    public TimeSpan PairingSlotLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Failed pairing lookups (no slot for the key) per minute over the whole relay. Past it every lookup gets 429 until
    /// the minute ends: a guesser spread over many addresses still cannot try more codes than this.
    /// </summary>
    public int MaxFailedPairingLookupsPerMinute { get; set; } = 1000;

    /// <summary><c>pairingSlot</c> messages one control channel may send per minute; more are rejected.</summary>
    public int MaxPairingSlotRequestsPerMinute { get; set; } = 30;
}
