using ProcBridge.Relay.Services.Common.Security;
using ProcBridge.Relay.Services.Tunnel.Enums;

namespace ProcBridge.Relay.Services.Tunnel;

/// <summary>One client-to-host session: two tickets and up to <c>TunnelProtocol.MaxLanes</c> parallel lanes.</summary>
internal sealed class TunnelSession
{
    private readonly object _lock = new();
    private readonly Dictionary<int, TunnelLanePair> _lanes = new();
    private readonly string _clientTicket;
    private readonly string _hostTicket;
    private DateTime _acceptLanesUntil;

    public TunnelSession(Guid id, Guid shareId, Guid clientDeviceId, Guid hostDeviceId, string clientTicket, string hostTicket, DateTime acceptLanesUntil)
    {
        Id = id;
        ShareId = shareId;
        ClientDeviceId = clientDeviceId;
        HostDeviceId = hostDeviceId;
        _clientTicket = clientTicket;
        _hostTicket = hostTicket;
        _acceptLanesUntil = acceptLanesUntil;
    }

    public Guid Id { get; }
    public Guid ShareId { get; }
    public Guid ClientDeviceId { get; }
    public Guid HostDeviceId { get; }

    public TunnelSide? MatchTicket(string ticket)
    {
        if (SecretTokens.FixedTimeEquals(ticket, _clientTicket))
            return TunnelSide.Client;
        if (SecretTokens.FixedTimeEquals(ticket, _hostTicket))
            return TunnelSide.Host;
        return null;
    }

    /// <summary>New lanes are accepted while any lane is open, or until the acceptance deadline.</summary>
    public bool AcceptsLanes(DateTime now)
    {
        lock (_lock)
            return _lanes.Count > 0 || now < _acceptLanesUntil;
    }

    /// <summary>Expired and idle: safe to forget.</summary>
    public bool IsDead(DateTime now)
    {
        lock (_lock)
            return _lanes.Count == 0 && now >= _acceptLanesUntil;
    }

    /// <returns>The pair with <paramref name="side"/> reserved, or <c>null</c> when that side of the lane is taken.</returns>
    public TunnelLanePair? TryReserve(int lane, TunnelSide side)
    {
        lock (_lock)
        {
            if (!_lanes.TryGetValue(lane, out var pair))
            {
                pair = new TunnelLanePair();
                _lanes.Add(lane, pair);
            }
            return pair.TryReserve(side) ? pair : null;
        }
    }

    /// <summary>Aborts every lane (revocation); the session then dies once the lanes are gone.</summary>
    public void AbortAll()
    {
        lock (_lock)
        {
            _acceptLanesUntil = DateTime.MinValue;
            foreach (var pair in _lanes.Values)
                pair.Abort();
        }
    }

    /// <summary>Forgets a finished (or abandoned) lane so the index can be reused; restarts the grace window when it was the last one.</summary>
    public void RemoveLane(int lane, TunnelLanePair pair, DateTime now, TimeSpan grace)
    {
        lock (_lock)
        {
            if (_lanes.TryGetValue(lane, out var current) && ReferenceEquals(current, pair))
            {
                _lanes.Remove(lane);
                if (_lanes.Count == 0)
                    _acceptLanesUntil = now + grace;
            }
        }
    }
}
