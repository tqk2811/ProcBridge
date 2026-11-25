using System.Net.WebSockets;

using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel.Interfaces;

/// <summary>
/// Pairs the two ends of a tunnel session and copies opaque binary frames between them. The relay never parses
/// payloads (end-to-end encrypted above). Does NOT check share permissions: the caller does that first.
/// </summary>
public interface ITunnelHub
{
    /// <summary>Runs the control channel of a host device until it closes. A newer channel of the same device replaces the older one.</summary>
    Task RunControlChannelAsync(Guid deviceId, WebSocket socket, CancellationToken cancellationToken);

    /// <summary>Opens a session from <paramref name="clientDeviceId"/> to a share served by <paramref name="hostDeviceId"/> and notifies the host.</summary>
    Task<TunnelSessionVM> OpenSessionAsync(
        Guid clientDeviceId,
        string? clientPublicKey,
        Guid shareId,
        Guid hostDeviceId,
        string? hostPublicKey,
        CancellationToken cancellationToken);

    /// <summary>Validates ticket and lane and reserves the slot; throws <c>ServiceException</c> when not allowed.</summary>
    TunnelLaneReservation ReserveLane(Guid sessionId, string ticket, int lane);

    /// <summary>Gives back a reservation whose WebSocket upgrade did not happen.</summary>
    void ReleaseLane(TunnelLaneReservation reservation);

    /// <summary>Waits for the other end of the lane, then forwards frames both ways until either end closes.</summary>
    Task RunLaneAsync(TunnelLaneReservation reservation, WebSocket socket, CancellationToken cancellationToken);
}
