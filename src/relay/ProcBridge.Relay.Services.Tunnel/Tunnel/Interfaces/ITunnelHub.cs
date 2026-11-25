using System.Net.WebSockets;

using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel.Interfaces;

/// <summary>
/// Pairs the two ends of a tunnel session and copies opaque binary frames between them. The relay never parses
/// payloads (end-to-end encrypted above) and stores nothing beyond the live channels and sessions in memory.
/// </summary>
public interface ITunnelHub
{
    /// <summary>
    /// Runs the control channel of a host until it closes. The host first proves its identity key with a Noise XX
    /// handshake; the channel is then registered under HostId = fingerprint of that key. A newer proven channel of the
    /// same HostId replaces the older one. Throws <c>ServiceException</c> (before accepting nothing) when the address has
    /// too many control channels.
    /// </summary>
    Task RunControlChannelAsync(WebSocket socket, string clientAddress, CancellationToken cancellationToken);

    /// <summary>True when <paramref name="clientAddress"/> may open one more control channel (checked before the upgrade).</summary>
    bool CanOpenControlChannel(string clientAddress);

    /// <summary>Opens a session to the host registered as <paramref name="hostId"/> and notifies it.</summary>
    Task<TunnelSessionVM> OpenSessionAsync(string hostId, string clientAddress, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the pairing slot registered under <paramref name="lookupKey"/> (single use; 404 when none or expired), opens
    /// a session to its host and tells the host its slot was claimed.
    /// </summary>
    Task<TunnelPairingSessionVM> OpenPairingSessionAsync(string lookupKey, string clientAddress, CancellationToken cancellationToken);

    /// <summary>Validates ticket and lane and reserves the slot; throws <c>ServiceException</c> when not allowed.</summary>
    TunnelLaneReservation ReserveLane(Guid sessionId, string ticket, int lane, string clientAddress);

    /// <summary>Gives back a reservation whose WebSocket upgrade did not happen.</summary>
    void ReleaseLane(TunnelLaneReservation reservation);

    /// <summary>Waits for the other end of the lane, then forwards frames both ways until either end closes.</summary>
    Task RunLaneAsync(TunnelLaneReservation reservation, WebSocket socket, CancellationToken cancellationToken);
}
