using System.Net.WebSockets;
using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.ViewModels.Tunnel;
using ProcBridge.Transport.Relay.Interfaces;
using ProcBridge.Transport.Relay.Models;
using ProcBridge.Transport.Secure.Interfaces;

namespace ProcBridge.Transport.Relay;

/// <summary>
/// Machine A side of pairing through the relay (plan 9): finds machine B by the invite code it shows (the relay only gets
/// the lookup key derived from it, <c>POST api/tunnel/pairing-sessions</c>), then opens lane 0 of that session. Give it to
/// <c>PairingClient</c>: the Noise XX handshake and the 6-digit code run inside the lane like on the LAN, and the
/// HostId the relay named must match the key the handshake proves (<see cref="ExpectedPeerFingerprint"/>).
/// One connection only: the relay slot is single use.
/// </summary>
public sealed class RelayPairingConnector : IPeerHintConnector, IDisposable
{
    private readonly RelayEndpoint _relay;
    private readonly string _lookupKey;
    private readonly IRelaySocketFactory _sockets;
    private readonly IRelayApi _api;
    private readonly HttpClient? _ownedHttp;
    private int _used;

    /// <param name="inviteCode">The code machine B shows (<c>1234-5678</c>; spaces and dashes are ignored).</param>
    /// <param name="sockets">Null for <see cref="ClientWebSocketFactory"/>.</param>
    /// <param name="http">HTTP client for the REST call, not owned; null creates one on <see cref="RelayEndpoint.BaseUrl"/>.</param>
    /// <exception cref="ArgumentException">The code is not <see cref="RelayPairingCode.Length"/> digits.</exception>
    public RelayPairingConnector(RelayEndpoint relay, string inviteCode, IRelaySocketFactory? sockets = null, HttpClient? http = null)
    {
        if (!RelayPairingCode.TryNormalize(inviteCode, out string code))
            throw new ArgumentException($"The relay pairing code has {RelayPairingCode.Length} digits.", nameof(inviteCode));
        _relay = relay;
        _lookupKey = RelayPairingCode.DeriveLookupKey(code);
        _sockets = sockets ?? new ClientWebSocketFactory();
        if (http is null)
        {
            _ownedHttp = new HttpClient { BaseAddress = relay.BaseUrl, Timeout = TimeSpan.FromSeconds(30) };
            http = _ownedHttp;
        }
        _api = new RelayApiImplement(http);
    }

    /// <summary>Stored as the paired machine's address: machines paired this way are reached through this relay.</summary>
    public string Description => RelayAddress(_relay);

    /// <summary>HostId the relay named for the slot, once connected.</summary>
    public string? ExpectedPeerFingerprint { get; private set; }

    /// <summary>The address stored for a machine paired through <paramref name="relay"/>: <c>relay https://relay.example/</c>.</summary>
    public static string RelayAddress(RelayEndpoint relay) => $"relay {relay.BaseUrl}";

    /// <summary>The relay URL of an address written by <see cref="RelayAddress"/>; null for a LAN address.</summary>
    public static Uri? TryParseRelayAddress(string? address)
        => address is not null && address.StartsWith("relay ", StringComparison.Ordinal)
           && Uri.TryCreate(address["relay ".Length..], UriKind.Absolute, out Uri? url) ? url : null;

    /// <exception cref="IOException">No slot for this code (wrong, expired or used), relay unreachable or refused the lane.</exception>
    public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
            throw new InvalidOperationException("A relay pairing code finds machine B once; ask machine B for a new code.");
        TunnelPairingSessionVM session;
        try
        {
            session = await _api.Tunnel.OpenPairingSessionAsync(new OpenPairingSessionRequestVM { LookupKey = _lookupKey }, cancellationToken).ConfigureAwait(false);
        }
        catch (RelayApiException e) when (e.Code == "PAIRING_SLOT_NOT_FOUND")
        {
            throw new IOException("No machine is waiting for this pairing code on the relay (wrong, expired or already used).", e);
        }
        catch (RelayApiException e)
        {
            throw new IOException($"Relay refused the pairing lookup: {e.Code} {e.Message}", e);
        }
        catch (HttpRequestException e)
        {
            throw new IOException($"Relay unreachable: {e.Message}", e);
        }
        ExpectedPeerFingerprint = session.HostId;

        Uri uri = _relay.WebSocketUri($"{TunnelProtocol.DataPath}?{TunnelProtocol.SessionIdQuery}={session.SessionId}&{TunnelProtocol.LaneQuery}=0");
        try
        {
            WebSocket socket = await _sockets.ConnectAsync(uri, session.Ticket, cancellationToken).ConfigureAwait(false);
            return new WebSocketLaneStream(socket);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or InvalidOperationException)
        {
            throw new IOException($"Relay refused the pairing lane: {e.Message}", e);
        }
    }

    public void Dispose() => _ownedHttp?.Dispose();
}
