using ProcBridge.Relay.Services.Tunnel.Enums;

namespace ProcBridge.Relay.Services.Tunnel.Models;

/// <summary>
/// A lane slot validated and reserved BEFORE the WebSocket upgrade is accepted, so a bad ticket or a busy lane is
/// answered with a plain HTTP error. Must be passed to <c>ITunnelHub.RunLaneAsync</c> or released (exactly once).
/// </summary>
public sealed class TunnelLaneReservation
{
    private int _finished;

    internal TunnelLaneReservation(TunnelSession session, TunnelLanePair pair, TunnelSide side, int lane, string clientAddress)
    {
        Session = session;
        Pair = pair;
        Side = side;
        Lane = lane;
        ClientAddress = clientAddress;
    }

    internal TunnelSession Session { get; }
    internal TunnelLanePair Pair { get; }
    public TunnelSide Side { get; }
    public int Lane { get; }
    public Guid SessionId => Session.Id;

    /// <summary>Address that holds this lane, for the per-address lane limit.</summary>
    internal string ClientAddress { get; }

    /// <summary>True only on the first call: the per-address count is given back once.</summary>
    internal bool TryFinish() => Interlocked.Exchange(ref _finished, 1) == 0;
}
