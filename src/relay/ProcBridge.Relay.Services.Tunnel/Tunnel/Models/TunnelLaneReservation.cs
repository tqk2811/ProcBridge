using ProcBridge.Relay.Services.Tunnel.Enums;

namespace ProcBridge.Relay.Services.Tunnel.Models;

/// <summary>
/// A lane slot validated and reserved BEFORE the WebSocket upgrade is accepted, so a bad ticket or a busy lane is
/// answered with a plain HTTP error. Must be passed to <c>ITunnelHub.RunLaneAsync</c> or released.
/// </summary>
public sealed class TunnelLaneReservation
{
    internal TunnelLaneReservation(TunnelSession session, TunnelLanePair pair, TunnelSide side, int lane)
    {
        Session = session;
        Pair = pair;
        Side = side;
        Lane = lane;
    }

    internal TunnelSession Session { get; }
    internal TunnelLanePair Pair { get; }
    public TunnelSide Side { get; }
    public int Lane { get; }
    public Guid SessionId => Session.Id;
}
