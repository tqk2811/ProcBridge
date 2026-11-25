using System.Net.WebSockets;

namespace ProcBridge.Transport.Relay.Interfaces;

/// <summary>
/// Opens WebSockets to the relay. The default is <see cref="ClientWebSocketFactory"/>; tests plug in the in-process
/// test server, or a recorder to check what the relay sees.
/// </summary>
public interface IRelaySocketFactory
{
    /// <param name="bearerToken">Sent as <c>Authorization: Bearer</c>: the session ticket on a data lane; null on the control channel.</param>
    Task<WebSocket> ConnectAsync(Uri uri, string? bearerToken, CancellationToken cancellationToken);
}
