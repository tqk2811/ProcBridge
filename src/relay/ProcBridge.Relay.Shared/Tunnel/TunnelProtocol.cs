namespace ProcBridge.Relay.Shared.Tunnel;

/// <summary>
/// Wire constants of the relay tunnel. The relay only pairs two WebSocket lanes and copies binary frames between
/// them; payloads are end-to-end encrypted by the layer above and never parsed here.
/// </summary>
public static class TunnelProtocol
{
    /// <summary>Data lane: <c>GET /tunnel?sessionId=..&amp;lane=0</c> + <c>Authorization: Bearer pbt_...</c> ticket (WebSocket upgrade).</summary>
    public const string DataPath = "/tunnel";

    /// <summary>Control channel of a host device: <c>GET /tunnel/control</c> + device bearer token (WebSocket upgrade).</summary>
    public const string ControlPath = "/tunnel/control";

    public const string SessionIdQuery = "sessionId";
    /// <summary>Legacy: ticket in the URL; the relay only accepts it when <c>Relay:Tunnel:AllowTicketInQuery</c> is on.</summary>
    public const string TicketQuery = "ticket";
    public const string LaneQuery = "lane";

    /// <summary>Upper bound of parallel lanes per session (decision 15.1: many connections per session).</summary>
    public const int MaxLanes = 16;

    /// <summary>Control message type pushed to the host when a client opens a session to one of its shares.</summary>
    public const string SessionOpenedMessage = "sessionOpened";

    /// <summary>Control message type pushed to the host when a client lane waits for the host end of that lane.</summary>
    public const string LaneRequestedMessage = "laneRequested";
}
